//go:build linux

package main

import (
	"context"
	"database/sql"
	"encoding/base64"
	"encoding/json"
	"errors"
	"flag"
	"fmt"
	"io"
	"net"
	"os"
	"os/exec"
	"os/signal"
	"os/user"
	"path/filepath"
	"regexp"
	"strconv"
	"strings"
	"sync"
	"syscall"
	"time"

	"github.com/sagernet/sing-box"
	"github.com/sagernet/sing-box/adapter"
	"github.com/sagernet/sing-box/include"
	"github.com/sagernet/sing-box/option"
	satomic "github.com/sagernet/sing/common/atomic"
	"github.com/sagernet/sing/common/buf"
	"github.com/sagernet/sing/common/bufio"
	sbjson "github.com/sagernet/sing/common/json"
	M "github.com/sagernet/sing/common/metadata"
	N "github.com/sagernet/sing/common/network"
	"golang.zx2c4.com/wireguard/wgctrl/wgtypes"
	_ "modernc.org/sqlite"
)

const (
	defaultConfigPath      = "/etc/omnirelay/gateway/connector/config.json"
	defaultMetadataPath    = "/etc/omnirelay/gateway/metadata.json"
	defaultAccountingDB    = "/etc/omnirelay/gateway/connector/accounting.db"
	defaultStatePath       = "/etc/omnirelay/gateway/connector/connector_core_state.json"
	defaultLockPath        = "/run/omnirelay-accounting-sync.lock"
	defaultSyncCommand     = "/usr/local/sbin/omnirelay-gatewayctl sync-clients"
	defaultPanelGroup      = "omnigateway"
	defaultCycleInterval   = 30
	defaultSyncTimeoutSecs = 90
)

var fullTunnelProtocols = map[string]struct{}{
	"shadowsocks_singbox":              {},
	"vless_plain_singbox":              {},
	"vless_reality_singbox":            {},
	"shadowtls_v3_shadowsocks_singbox": {},
}

type runOptions struct {
	configPath      string
	metadataPath    string
	accountingDB    string
	statePath       string
	lockPath        string
	panelGroup      string
	syncCommand     string
	intervalSeconds int
}

type metadataAccounting struct {
	Source      string `json:"source"`
	ClientsFile string `json:"clientsFile"`
}

type metadataDocument struct {
	ActiveProtocol string             `json:"active_protocol"`
	Accounting     metadataAccounting `json:"accounting"`
}

type clientPolicy struct {
	clientID     string
	username     string
	enabled      int
	totalBytes   int64
	expiryUnixMS int64
}

type coreState struct {
	OK                 bool   `json:"ok"`
	LastSyncUTC        string `json:"lastSyncUtc"`
	LastError          string `json:"lastError"`
	Source             string `json:"source"`
	ProtocolID         string `json:"protocolId"`
	SampledClients     int    `json:"sampledClients"`
	UpdatedClients     int    `json:"updatedClients"`
	EnforcementActions int    `json:"enforcementActions"`
}

type cycleResult struct {
	sampledClients     int
	updatedClients     int
	enforcementActions int
	shouldSync         bool
}

type userCounter struct {
	read  *satomic.Int64
	write *satomic.Int64
}

type statsTracker struct {
	mu    sync.Mutex
	users map[string]userCounter
}

func newStatsTracker() *statsTracker {
	return &statsTracker{
		users: make(map[string]userCounter),
	}
}

func (t *statsTracker) loadOrCreate(user string) userCounter {
	counter, ok := t.users[user]
	if ok {
		return counter
	}
	counter = userCounter{
		read:  &satomic.Int64{},
		write: &satomic.Int64{},
	}
	t.users[user] = counter
	return counter
}

func (t *statsTracker) RoutedConnection(_ context.Context, conn net.Conn, metadata adapter.InboundContext, _ adapter.Rule, _ adapter.Outbound) net.Conn {
	user := strings.TrimSpace(metadata.User)
	if user == "" {
		return conn
	}
	t.mu.Lock()
	counter := t.loadOrCreate(user)
	t.mu.Unlock()
	return bufio.NewInt64CounterConn(conn, []*satomic.Int64{counter.read}, []*satomic.Int64{counter.write})
}

func (t *statsTracker) RoutedPacketConnection(_ context.Context, conn N.PacketConn, metadata adapter.InboundContext, _ adapter.Rule, _ adapter.Outbound) N.PacketConn {
	user := strings.TrimSpace(metadata.User)
	if user == "" {
		return conn
	}
	t.mu.Lock()
	counter := t.loadOrCreate(user)
	t.mu.Unlock()
	return bufio.NewInt64CounterPacketConn(conn, []*satomic.Int64{counter.read}, []*satomic.Int64{counter.write})
}

func (t *statsTracker) DrainByUser() map[string]int64 {
	out := make(map[string]int64)
	t.mu.Lock()
	defer t.mu.Unlock()
	for user, counter := range t.users {
		upload := counter.read.Swap(0)
		download := counter.write.Swap(0)
		total := upload + download
		if total > 0 {
			out[user] = total
		}
	}
	return out
}

func (t *statsTracker) Reset() {
	t.mu.Lock()
	defer t.mu.Unlock()
	t.users = make(map[string]userCounter)
}

type trackedConnection struct {
	id         string
	user       string
	conn       net.Conn
	packetConn N.PacketConn
}

type connTracker struct {
	mu    sync.Mutex
	next  uint64
	items map[string]*trackedConnection
}

func newConnTracker() *connTracker {
	return &connTracker{
		items: make(map[string]*trackedConnection),
	}
}

func (t *connTracker) nextID() string {
	t.mu.Lock()
	defer t.mu.Unlock()
	t.next++
	return strconv.FormatUint(t.next, 10)
}

func (t *connTracker) track(item *trackedConnection) {
	t.mu.Lock()
	defer t.mu.Unlock()
	t.items[item.id] = item
}

func (t *connTracker) untrack(id string) {
	t.mu.Lock()
	defer t.mu.Unlock()
	delete(t.items, id)
}

func (t *connTracker) RoutedConnection(_ context.Context, conn net.Conn, metadata adapter.InboundContext, _ adapter.Rule, _ adapter.Outbound) net.Conn {
	id := t.nextID()
	item := &trackedConnection{
		id:   id,
		user: strings.TrimSpace(metadata.User),
		conn: conn,
	}
	t.track(item)
	return &wrappedConn{Conn: conn, tracker: t, id: id}
}

func (t *connTracker) RoutedPacketConnection(_ context.Context, conn N.PacketConn, metadata adapter.InboundContext, _ adapter.Rule, _ adapter.Outbound) N.PacketConn {
	id := t.nextID()
	item := &trackedConnection{
		id:         id,
		user:       strings.TrimSpace(metadata.User),
		packetConn: conn,
	}
	t.track(item)
	return &wrappedPacketConn{PacketConn: conn, tracker: t, id: id}
}

func (t *connTracker) ActiveByUser() map[string]int {
	t.mu.Lock()
	defer t.mu.Unlock()
	out := make(map[string]int)
	for _, item := range t.items {
		user := strings.TrimSpace(item.user)
		if user == "" {
			continue
		}
		out[user]++
	}
	return out
}

func (t *connTracker) CloseByUsers(users map[string]struct{}) int {
	if len(users) == 0 {
		return 0
	}
	toClose := make([]*trackedConnection, 0)
	t.mu.Lock()
	for id, item := range t.items {
		if _, ok := users[item.user]; ok {
			toClose = append(toClose, item)
			delete(t.items, id)
		}
	}
	t.mu.Unlock()
	closed := 0
	for _, item := range toClose {
		if item.conn != nil {
			_ = item.conn.Close()
		}
		if item.packetConn != nil {
			_ = item.packetConn.Close()
		}
		closed++
	}
	return closed
}

func (t *connTracker) Reset() {
	t.mu.Lock()
	defer t.mu.Unlock()
	for _, item := range t.items {
		if item.conn != nil {
			_ = item.conn.Close()
		}
		if item.packetConn != nil {
			_ = item.packetConn.Close()
		}
	}
	t.items = make(map[string]*trackedConnection)
}

func shouldUntrack(err error) bool {
	if err == nil {
		return false
	}
	if errors.Is(err, io.EOF) {
		return true
	}
	var netErr net.Error
	if errors.As(err, &netErr) {
		return !netErr.Temporary()
	}
	return true
}

type wrappedConn struct {
	net.Conn
	tracker *connTracker
	id      string
	once    sync.Once
}

func (w *wrappedConn) untrack() {
	w.once.Do(func() {
		w.tracker.untrack(w.id)
	})
}

func (w *wrappedConn) Read(b []byte) (int, error) {
	n, err := w.Conn.Read(b)
	if shouldUntrack(err) {
		w.untrack()
	}
	return n, err
}

func (w *wrappedConn) Write(b []byte) (int, error) {
	n, err := w.Conn.Write(b)
	if shouldUntrack(err) {
		w.untrack()
	}
	return n, err
}

func (w *wrappedConn) Close() error {
	w.untrack()
	return w.Conn.Close()
}

func (w *wrappedConn) Upstream() any {
	return w.Conn
}

type wrappedPacketConn struct {
	N.PacketConn
	tracker *connTracker
	id      string
	once    sync.Once
}

func (w *wrappedPacketConn) untrack() {
	w.once.Do(func() {
		w.tracker.untrack(w.id)
	})
}

func (w *wrappedPacketConn) ReadPacket(buffer *buf.Buffer) (M.Socksaddr, error) {
	dest, err := w.PacketConn.ReadPacket(buffer)
	if shouldUntrack(err) {
		w.untrack()
	}
	return dest, err
}

func (w *wrappedPacketConn) WritePacket(buffer *buf.Buffer, destination M.Socksaddr) error {
	err := w.PacketConn.WritePacket(buffer, destination)
	if shouldUntrack(err) {
		w.untrack()
	}
	return err
}

func (w *wrappedPacketConn) Close() error {
	w.untrack()
	return w.PacketConn.Close()
}

func (w *wrappedPacketConn) Upstream() any {
	return w.PacketConn
}

type runtimeDaemon struct {
	opts         runOptions
	stats        *statsTracker
	conns        *connTracker
	boxMu        sync.Mutex
	box          *box.Box
	syncMu       sync.Mutex
	syncInFlight bool
	asyncErr     string
}

func newRuntimeDaemon(opts runOptions) *runtimeDaemon {
	return &runtimeDaemon{
		opts:  opts,
		stats: newStatsTracker(),
		conns: newConnTracker(),
	}
}

func (d *runtimeDaemon) setAsyncErr(message string) {
	d.syncMu.Lock()
	defer d.syncMu.Unlock()
	d.asyncErr = message
}

func (d *runtimeDaemon) getAsyncErr() string {
	d.syncMu.Lock()
	defer d.syncMu.Unlock()
	return d.asyncErr
}

func (d *runtimeDaemon) createBox() (*box.Box, error) {
	ctx := box.Context(
		context.Background(),
		include.InboundRegistry(),
		include.OutboundRegistry(),
		include.EndpointRegistry(),
	)
	options, err := loadOptions(ctx, d.opts.configPath)
	if err != nil {
		return nil, err
	}
	instance, err := box.New(box.Options{
		Context: ctx,
		Options: options,
	})
	if err != nil {
		return nil, err
	}
	instance.Router().AppendTracker(d.stats)
	instance.Router().AppendTracker(d.conns)
	if err := instance.Start(); err != nil {
		_ = instance.Close()
		return nil, err
	}
	return instance, nil
}

func (d *runtimeDaemon) reloadBox() error {
	d.boxMu.Lock()
	old := d.box
	d.box = nil
	d.boxMu.Unlock()
	if old != nil {
		_ = old.Close()
	}
	d.stats.Reset()
	d.conns.Reset()
	newBox, err := d.createBox()
	if err != nil {
		return err
	}
	d.boxMu.Lock()
	d.box = newBox
	d.boxMu.Unlock()
	return nil
}

func (d *runtimeDaemon) closeBox() {
	d.boxMu.Lock()
	old := d.box
	d.box = nil
	d.boxMu.Unlock()
	if old != nil {
		_ = old.Close()
	}
	d.conns.Reset()
	d.stats.Reset()
}

func (d *runtimeDaemon) triggerSyncCommand() {
	if strings.TrimSpace(d.opts.syncCommand) == "" {
		return
	}
	d.syncMu.Lock()
	if d.syncInFlight {
		d.syncMu.Unlock()
		return
	}
	d.syncInFlight = true
	d.syncMu.Unlock()

	go func() {
		defer func() {
			d.syncMu.Lock()
			d.syncInFlight = false
			d.syncMu.Unlock()
		}()
		ctx, cancel := context.WithTimeout(context.Background(), defaultSyncTimeoutSecs*time.Second)
		defer cancel()
		cmd := exec.CommandContext(ctx, "/usr/bin/env", "bash", "-lc", d.opts.syncCommand)
		output, err := cmd.CombinedOutput()
		if err != nil {
			message := strings.TrimSpace(string(output))
			if message == "" {
				message = err.Error()
			}
			if len(message) > 280 {
				message = message[:280]
			}
			d.setAsyncErr("sync_command_failed:" + message)
			return
		}
		d.setAsyncErr("")
	}()
}

func (d *runtimeDaemon) runCycle() coreState {
	now := time.Now().UTC()
	state := coreState{
		OK:          true,
		LastSyncUTC: now.Format(time.RFC3339),
		Source:      "connector_tracker",
	}

	deltas := d.stats.DrainByUser()
	activeCounts := d.conns.ActiveByUser()

	metadata, err := readMetadata(d.opts.metadataPath)
	if err != nil {
		state.OK = false
		state.LastError = "metadata_read_failed:" + err.Error()
		return state
	}
	state.ProtocolID = metadata.ActiveProtocol

	if !isFullTunnelProtocol(metadata.ActiveProtocol) || metadata.Accounting.Source != "connector_tracker" {
		state.LastError = d.getAsyncErr()
		return state
	}

	var result cycleResult
	err = withFileLock(d.opts.lockPath, func() error {
		var applyErr error
		result, applyErr = d.applyConnectorTrackerCycle(now, metadata, deltas, activeCounts)
		return applyErr
	})
	if err != nil {
		state.OK = false
		state.LastError = "cycle_failed:" + err.Error()
		return state
	}

	state.SampledClients = result.sampledClients
	state.UpdatedClients = result.updatedClients
	state.EnforcementActions = result.enforcementActions
	state.LastError = d.getAsyncErr()

	if result.shouldSync {
		d.triggerSyncCommand()
	}
	return state
}

func (d *runtimeDaemon) applyConnectorTrackerCycle(now time.Time, metadata metadataDocument, deltas map[string]int64, activeCounts map[string]int) (cycleResult, error) {
	result := cycleResult{}

	db, err := sql.Open("sqlite", d.opts.accountingDB)
	if err != nil {
		return result, err
	}
	defer db.Close()

	if err := ensureAccountingSchema(db); err != nil {
		return result, err
	}

	policies, err := loadClientPolicies(db, metadata.ActiveProtocol)
	if err != nil {
		return result, err
	}

	userToClient := make(map[string]string)
	for _, policy := range policies {
		userToClient[policy.clientID] = policy.clientID
		if policy.username != "" {
			userToClient[policy.username] = policy.clientID
		}
	}

	deltaByClient := make(map[string]int64)
	for user, bytes := range deltas {
		if bytes <= 0 {
			continue
		}
		clientID, ok := userToClient[user]
		if !ok || clientID == "" {
			continue
		}
		deltaByClient[clientID] += bytes
	}
	result.sampledClients = len(deltaByClient)

	activeByClient := make(map[string]int)
	for user, count := range activeCounts {
		clientID, ok := userToClient[user]
		if !ok || clientID == "" {
			continue
		}
		activeByClient[clientID] += count
	}

	tx, err := db.BeginTx(context.Background(), nil)
	if err != nil {
		return result, err
	}
	defer func() {
		_ = tx.Rollback()
	}()

	for clientID, bytes := range deltaByClient {
		if bytes <= 0 {
			continue
		}
		_, err = tx.Exec(
			`INSERT INTO usage_totals(client_id,used_bytes,updated_at)
			 VALUES(?,?,?)
			 ON CONFLICT(client_id) DO UPDATE SET
			   used_bytes=usage_totals.used_bytes + excluded.used_bytes,
			   updated_at=excluded.updated_at`,
			clientID,
			bytes,
			now.Unix(),
		)
		if err != nil {
			return result, err
		}
	}

	for _, policy := range policies {
		_, err = tx.Exec(
			`INSERT OR REPLACE INTO connection_counters(client_id,active_connections,last_seen_at)
			 VALUES(?,?,?)`,
			policy.clientID,
			activeByClient[policy.clientID],
			now.Unix(),
		)
		if err != nil {
			return result, err
		}
	}

	rows, err := tx.Query(
		`SELECT c.client_id, c.enabled, c.total_bytes_limit, c.expiry_unix_ms,
		        COALESCE(u.used_bytes, 0) AS used_bytes, COALESCE(e.disabled_reason, '') AS disabled_reason
		   FROM clients c
		   LEFT JOIN usage_totals u ON u.client_id = c.client_id
		   LEFT JOIN enforcement_state e ON e.client_id = c.client_id
		  WHERE c.protocol_id = ?`,
		metadata.ActiveProtocol,
	)
	if err != nil {
		return result, err
	}
	defer rows.Close()

	enableChanges := make(map[string]bool)
	disableUsers := make(map[string]struct{})
	nowMS := now.UnixMilli()

	for rows.Next() {
		var (
			clientID       string
			enabled        int
			totalBytes     int64
			expiryUnixMS   int64
			usedBytes      int64
			disabledReason string
		)
		if err := rows.Scan(&clientID, &enabled, &totalBytes, &expiryUnixMS, &usedBytes, &disabledReason); err != nil {
			return result, err
		}

		reason := ""
		if expiryUnixMS > 0 && nowMS >= expiryUnixMS {
			reason = "expired"
		} else if totalBytes > 0 && usedBytes >= totalBytes {
			reason = "quota_exceeded"
		}

		if reason != "" {
			if enabled != 0 {
				_, err = tx.Exec(`UPDATE clients SET enabled=0, updated_at=? WHERE client_id=?`, now.Unix(), clientID)
				if err != nil {
					return result, err
				}
				enableChanges[clientID] = false
				result.updatedClients++
			}
			_, err = tx.Exec(
				`INSERT OR REPLACE INTO enforcement_state(client_id,disabled_reason,disabled_at,updated_at)
				 VALUES(?,?,?,?)`,
				clientID,
				reason,
				now.Unix(),
				now.Unix(),
			)
			if err != nil {
				return result, err
			}
			disableUsers[clientID] = struct{}{}
			continue
		}

		if enabled == 0 && (disabledReason == "expired" || disabledReason == "quota_exceeded") {
			_, err = tx.Exec(`UPDATE clients SET enabled=1, updated_at=? WHERE client_id=?`, now.Unix(), clientID)
			if err != nil {
				return result, err
			}
			enableChanges[clientID] = true
			result.updatedClients++
		}
		_, err = tx.Exec(
			`INSERT OR REPLACE INTO enforcement_state(client_id,disabled_reason,disabled_at,updated_at)
			 VALUES(?,?,?,?)`,
			clientID,
			"",
			0,
			now.Unix(),
		)
		if err != nil {
			return result, err
		}
	}
	if err := rows.Err(); err != nil {
		return result, err
	}

	if err := tx.Commit(); err != nil {
		return result, err
	}

	if len(enableChanges) > 0 && strings.TrimSpace(metadata.Accounting.ClientsFile) != "" {
		changed, fileErr := updateClientEnableFlags(metadata.Accounting.ClientsFile, enableChanges)
		if fileErr != nil {
			return result, fileErr
		}
		if changed {
			result.shouldSync = true
		}
	}

	closed := d.conns.CloseByUsers(disableUsers)
	result.enforcementActions = closed

	if err := repairAccountingPermissions(d.opts.accountingDB, d.opts.panelGroup); err != nil {
		return result, err
	}
	return result, nil
}

func updateClientEnableFlags(path string, changes map[string]bool) (bool, error) {
	raw, err := os.ReadFile(path)
	if err != nil {
		if errors.Is(err, os.ErrNotExist) {
			return false, nil
		}
		return false, err
	}
	var payload []map[string]any
	if err := json.Unmarshal(raw, &payload); err != nil {
		return false, err
	}
	changed := false
	for _, item := range payload {
		rawID, ok := item["id"]
		if !ok {
			continue
		}
		id, _ := rawID.(string)
		target, hit := changes[id]
		if !hit {
			continue
		}
		current := true
		if v, ok := item["enable"]; ok {
			switch value := v.(type) {
			case bool:
				current = value
			case float64:
				current = value != 0
			case string:
				current = strings.EqualFold(value, "true") || value == "1"
			}
		}
		if current != target {
			item["enable"] = target
			changed = true
		}
	}
	if !changed {
		return false, nil
	}
	serialized, err := json.MarshalIndent(payload, "", "  ")
	if err != nil {
		return false, err
	}
	serialized = append(serialized, '\n')
	tmp := path + ".tmp"
	if err := os.WriteFile(tmp, serialized, 0o640); err != nil {
		return false, err
	}
	if err := os.Rename(tmp, path); err != nil {
		return false, err
	}
	return true, nil
}

func loadClientPolicies(db *sql.DB, protocolID string) ([]clientPolicy, error) {
	rows, err := db.Query(
		`SELECT client_id, username, enabled, total_bytes_limit, expiry_unix_ms
		   FROM clients
		  WHERE protocol_id = ?`,
		protocolID,
	)
	if err != nil {
		return nil, err
	}
	defer rows.Close()

	var out []clientPolicy
	for rows.Next() {
		var row clientPolicy
		if err := rows.Scan(&row.clientID, &row.username, &row.enabled, &row.totalBytes, &row.expiryUnixMS); err != nil {
			return nil, err
		}
		out = append(out, row)
	}
	return out, rows.Err()
}

func loadOptions(ctx context.Context, path string) (option.Options, error) {
	content, err := os.ReadFile(path)
	if err != nil {
		return option.Options{}, err
	}
	return sbjson.UnmarshalExtendedContext[option.Options](ctx, content)
}

func withFileLock(lockPath string, fn func() error) error {
	if err := os.MkdirAll(filepath.Dir(lockPath), 0o755); err != nil {
		return err
	}
	handle, err := os.OpenFile(lockPath, os.O_CREATE|os.O_RDWR, 0o640)
	if err != nil {
		return err
	}
	defer handle.Close()
	if err := syscall.Flock(int(handle.Fd()), syscall.LOCK_EX); err != nil {
		return err
	}
	defer func() {
		_ = syscall.Flock(int(handle.Fd()), syscall.LOCK_UN)
	}()
	return fn()
}

func readMetadata(path string) (metadataDocument, error) {
	var out metadataDocument
	content, err := os.ReadFile(path)
	if err != nil {
		return out, err
	}
	if err := json.Unmarshal(content, &out); err != nil {
		return out, err
	}
	return out, nil
}

func isFullTunnelProtocol(protocolID string) bool {
	_, ok := fullTunnelProtocols[strings.TrimSpace(protocolID)]
	return ok
}

func ensureAccountingSchema(db *sql.DB) error {
	_, err := db.Exec(`
PRAGMA busy_timeout=5000;
PRAGMA journal_mode=WAL;
PRAGMA synchronous=NORMAL;
CREATE TABLE IF NOT EXISTS clients (
  client_id TEXT PRIMARY KEY,
  protocol_id TEXT NOT NULL,
  username TEXT NOT NULL,
  enabled INTEGER NOT NULL DEFAULT 1,
  total_bytes_limit INTEGER NOT NULL DEFAULT 0,
  expiry_unix_ms INTEGER NOT NULL DEFAULT 0,
  created_at INTEGER NOT NULL,
  updated_at INTEGER NOT NULL
);
CREATE TABLE IF NOT EXISTS usage_totals (
  client_id TEXT PRIMARY KEY,
  used_bytes INTEGER NOT NULL DEFAULT 0,
  updated_at INTEGER NOT NULL
);
CREATE TABLE IF NOT EXISTS connection_counters (
  client_id TEXT PRIMARY KEY,
  active_connections INTEGER NOT NULL DEFAULT 0,
  last_seen_at INTEGER NOT NULL DEFAULT 0
);
CREATE TABLE IF NOT EXISTS enforcement_state (
  client_id TEXT PRIMARY KEY,
  disabled_reason TEXT NOT NULL DEFAULT '',
  disabled_at INTEGER NOT NULL DEFAULT 0,
  updated_at INTEGER NOT NULL DEFAULT 0
);
CREATE TABLE IF NOT EXISTS sampler_state (
  source TEXT NOT NULL,
  state_key TEXT NOT NULL,
  last_value INTEGER NOT NULL DEFAULT 0,
  updated_at INTEGER NOT NULL DEFAULT 0,
  PRIMARY KEY (source, state_key)
);
CREATE TABLE IF NOT EXISTS sampler_sessions (
  source TEXT NOT NULL,
  session_key TEXT NOT NULL,
  client_id TEXT NOT NULL DEFAULT '',
  upload_bytes INTEGER NOT NULL DEFAULT 0,
  download_bytes INTEGER NOT NULL DEFAULT 0,
  last_seen_at INTEGER NOT NULL DEFAULT 0,
  closed_at INTEGER NOT NULL DEFAULT 0,
  PRIMARY KEY (source, session_key)
);
`)
	return err
}

func panelGroupGID(group string) (int, error) {
	info, err := user.LookupGroup(group)
	if err != nil {
		return 0, err
	}
	return strconv.Atoi(info.Gid)
}

func repairAccountingPermissions(dbPath string, panelGroup string) error {
	gid, err := panelGroupGID(panelGroup)
	if err != nil {
		return nil
	}
	dbDir := filepath.Dir(dbPath)
	if err := os.MkdirAll(dbDir, 0o2770); err != nil {
		return err
	}
	if err := os.Chown(dbDir, 0, gid); err != nil {
		return err
	}
	if err := os.Chmod(dbDir, 0o2770); err != nil {
		return err
	}

	for _, target := range []string{dbPath, dbPath + "-wal", dbPath + "-shm"} {
		if _, err := os.Stat(target); err != nil {
			continue
		}
		if err := os.Chown(target, 0, gid); err != nil {
			return err
		}
		if err := os.Chmod(target, 0o660); err != nil {
			return err
		}
	}
	return nil
}

func writeState(path string, accountingDB string, panelGroup string, state coreState) error {
	if err := os.MkdirAll(filepath.Dir(path), 0o755); err != nil {
		return err
	}
	payload, err := json.Marshal(state)
	if err != nil {
		return err
	}
	payload = append(payload, '\n')
	tmp := path + ".tmp"
	if err := os.WriteFile(tmp, payload, 0o644); err != nil {
		return err
	}
	if err := os.Rename(tmp, path); err != nil {
		return err
	}
	_ = repairAccountingPermissions(accountingDB, panelGroup)
	return nil
}

func runCommand(args []string) error {
	flags := flag.NewFlagSet("run", flag.ContinueOnError)
	opts := runOptions{}
	flags.StringVar(&opts.configPath, "config", defaultConfigPath, "sing-box config path")
	flags.StringVar(&opts.metadataPath, "metadata", defaultMetadataPath, "gateway metadata path")
	flags.StringVar(&opts.accountingDB, "accounting-db", defaultAccountingDB, "unified accounting sqlite path")
	flags.StringVar(&opts.statePath, "state-file", defaultStatePath, "connector runtime state json path")
	flags.StringVar(&opts.lockPath, "lock-file", defaultLockPath, "shared accounting lock file path")
	flags.IntVar(&opts.intervalSeconds, "interval-sec", defaultCycleInterval, "sampling and enforcement interval in seconds")
	flags.StringVar(&opts.syncCommand, "sync-command", defaultSyncCommand, "command executed when enforcement updates clients file")
	flags.StringVar(&opts.panelGroup, "panel-group", defaultPanelGroup, "panel group for DB permissions")
	if err := flags.Parse(args); err != nil {
		return err
	}
	if opts.intervalSeconds < 5 {
		opts.intervalSeconds = 5
	}

	daemon := newRuntimeDaemon(opts)
	if err := daemon.reloadBox(); err != nil {
		initial := coreState{
			OK:          false,
			LastSyncUTC: time.Now().UTC().Format(time.RFC3339),
			LastError:   "runtime_start_failed:" + err.Error(),
		}
		_ = writeState(opts.statePath, opts.accountingDB, opts.panelGroup, initial)
		return err
	}
	defer daemon.closeBox()

	state := daemon.runCycle()
	if err := writeState(opts.statePath, opts.accountingDB, opts.panelGroup, state); err != nil {
		return err
	}

	ticker := time.NewTicker(time.Duration(opts.intervalSeconds) * time.Second)
	defer ticker.Stop()

	signals := make(chan os.Signal, 4)
	signal.Notify(signals, syscall.SIGINT, syscall.SIGTERM, syscall.SIGHUP)
	defer signal.Stop(signals)

	for {
		select {
		case sig := <-signals:
			if sig == syscall.SIGHUP {
				err := daemon.reloadBox()
				reloadState := coreState{
					OK:          err == nil,
					LastSyncUTC: time.Now().UTC().Format(time.RFC3339),
					LastError:   "",
				}
				if err != nil {
					reloadState.LastError = "reload_failed:" + err.Error()
				}
				_ = writeState(opts.statePath, opts.accountingDB, opts.panelGroup, reloadState)
				continue
			}
			return nil
		case <-ticker.C:
			cycleState := daemon.runCycle()
			if err := writeState(opts.statePath, opts.accountingDB, opts.panelGroup, cycleState); err != nil {
				return err
			}
		}
	}
}

func checkCommand(args []string) error {
	flags := flag.NewFlagSet("check", flag.ContinueOnError)
	configPath := flags.String("config", defaultConfigPath, "sing-box config path")
	if err := flags.Parse(args); err != nil {
		return err
	}
	ctx := box.Context(
		context.Background(),
		include.InboundRegistry(),
		include.OutboundRegistry(),
		include.EndpointRegistry(),
	)
	options, err := loadOptions(ctx, *configPath)
	if err != nil {
		return err
	}
	instance, err := box.New(box.Options{Context: ctx, Options: options})
	if err != nil {
		return err
	}
	return instance.Close()
}

func generateRealityKeyPair() error {
	privateKey, err := wgtypes.GeneratePrivateKey()
	if err != nil {
		return err
	}
	publicKey := privateKey.PublicKey()
	fmt.Printf("PrivateKey: %s\n", base64.RawURLEncoding.EncodeToString(privateKey[:]))
	fmt.Printf("PublicKey: %s\n", base64.RawURLEncoding.EncodeToString(publicKey[:]))
	return nil
}

func usage() {
	fmt.Fprintln(os.Stderr, "Usage:")
	fmt.Fprintln(os.Stderr, "  connector-core run [--config path] [--metadata path] [--accounting-db path] [--state-file path]")
	fmt.Fprintln(os.Stderr, "  connector-core check [--config path]")
	fmt.Fprintln(os.Stderr, "  connector-core generate reality-keypair")
}

func main() {
	if len(os.Args) < 2 {
		usage()
		os.Exit(2)
	}
	var err error
	switch os.Args[1] {
	case "run":
		err = runCommand(os.Args[2:])
	case "check":
		err = checkCommand(os.Args[2:])
	case "generate":
		if len(os.Args) >= 3 && os.Args[2] == "reality-keypair" {
			err = generateRealityKeyPair()
		} else {
			usage()
			os.Exit(2)
		}
	default:
		usage()
		os.Exit(2)
	}
	if err != nil {
		message := strings.TrimSpace(err.Error())
		message = regexp.MustCompile(`\s+`).ReplaceAllString(message, " ")
		fmt.Fprintln(os.Stderr, "ERROR:", message)
		os.Exit(1)
	}
}
