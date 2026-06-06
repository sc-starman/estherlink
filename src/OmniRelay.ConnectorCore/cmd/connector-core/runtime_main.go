package main

import (
	"context"
	"database/sql"
	"encoding/json"
	"errors"
	"flag"
	"fmt"
	"io"
	"math"
	"net"
	"os"
	"os/signal"
	"path/filepath"
	"regexp"
	"strconv"
	"strings"
	"sync"
	"syscall"
	"time"

	"github.com/omnirelay/connector-core/internal/accounting"
	"github.com/omnirelay/connector-core/internal/clients"
	"github.com/omnirelay/connector-core/internal/protocol"
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
	_ "modernc.org/sqlite"
)

type runOptions struct {
	configPath      string
	metadataPath    string
	accountingDB    string
	statePath       string
	lockPath        string
	panelGroup      string
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
	clientID       string
	username       string
	enabled        int
	totalBytes     int64
	expiryUnixMS   int64
	speedLimitKbps int64
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
	LimiterPolicyUsers int    `json:"limiterPolicyUsers"`
	LimiterActiveUsers int    `json:"limiterActiveUsers"`
	LimiterBypassCount int64  `json:"limiterBypassCount"`
	LimiterLastError   string `json:"limiterLastError"`
}

type cycleResult struct {
	sampledClients     int
	updatedClients     int
	enforcementActions int
	shouldSync         bool
}

type limiterSnapshot struct {
	policyUsers  int
	limitedUsers int
	bypassCount  int64
	lastError    string
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

type userRateGate struct {
	kbps satomic.Int64

	readMu   sync.Mutex
	readNext time.Time

	writeMu   sync.Mutex
	writeNext time.Time
}

func (g *userRateGate) setKbps(v int64) {
	if v < 0 {
		v = 0
	}
	g.kbps.Store(v)
}

func (g *userRateGate) throttleRead(bytes int) {
	g.throttle(bytes, true)
}

func (g *userRateGate) throttleWrite(bytes int) {
	g.throttle(bytes, false)
}

func (g *userRateGate) throttle(bytes int, readDir bool) {
	if bytes <= 0 {
		return
	}
	kbps := g.kbps.Load()
	if kbps <= 0 {
		return
	}
	durationNs := int64(math.Ceil((float64(bytes) * 8 * float64(time.Second)) / (float64(kbps) * 1000.0)))
	if durationNs < 1 {
		durationNs = 1
	}
	duration := time.Duration(durationNs)

	var mu *sync.Mutex
	var next *time.Time
	if readDir {
		mu = &g.readMu
		next = &g.readNext
	} else {
		mu = &g.writeMu
		next = &g.writeNext
	}

	mu.Lock()
	now := time.Now()
	start := now
	if next.After(now) {
		start = *next
	}
	wait := start.Sub(now)
	*next = start.Add(duration)
	mu.Unlock()

	if wait > 0 {
		time.Sleep(wait)
	}
}

type speedLimitedConn struct {
	net.Conn
	gate *userRateGate
}

func (c *speedLimitedConn) Read(b []byte) (int, error) {
	n, err := c.Conn.Read(b)
	if n > 0 {
		c.gate.throttleRead(n)
	}
	return n, err
}

func (c *speedLimitedConn) Write(b []byte) (int, error) {
	if len(b) > 0 {
		c.gate.throttleWrite(len(b))
	}
	return c.Conn.Write(b)
}

func (c *speedLimitedConn) Upstream() any {
	return c.Conn
}

type speedLimitedPacketConn struct {
	N.PacketConn
	gate *userRateGate
}

func (c *speedLimitedPacketConn) ReadPacket(buffer *buf.Buffer) (M.Socksaddr, error) {
	destination, err := c.PacketConn.ReadPacket(buffer)
	if err == nil && buffer != nil {
		c.gate.throttleRead(buffer.Len())
	}
	return destination, err
}

func (c *speedLimitedPacketConn) WritePacket(buffer *buf.Buffer, destination M.Socksaddr) error {
	if buffer != nil {
		c.gate.throttleWrite(buffer.Len())
	}
	return c.PacketConn.WritePacket(buffer, destination)
}

func (c *speedLimitedPacketConn) Upstream() any {
	return c.PacketConn
}

type speedLimiter struct {
	mu           sync.Mutex
	users        map[string]*userRateGate
	policyUsers  int
	limitedUsers int
	lastError    string
	protocolID   string

	warnMu    sync.Mutex
	warnUntil map[string]time.Time

	bypassCount satomic.Int64
}

func newSpeedLimiter() *speedLimiter {
	return &speedLimiter{
		users:     make(map[string]*userRateGate),
		warnUntil: make(map[string]time.Time),
	}
}

func (l *speedLimiter) snapshot() limiterSnapshot {
	l.mu.Lock()
	snap := limiterSnapshot{
		policyUsers:  l.policyUsers,
		limitedUsers: l.limitedUsers,
		lastError:    l.lastError,
	}
	l.mu.Unlock()
	snap.bypassCount = l.bypassCount.Load()
	return snap
}

func (l *speedLimiter) setProtocolID(protocolID string) {
	l.mu.Lock()
	defer l.mu.Unlock()
	l.protocolID = strings.TrimSpace(protocolID)
}

func (l *speedLimiter) applyPolicies(policies []clientPolicy) error {
	rates := make(map[string]int64)
	limitedUsers := 0
	for _, policy := range policies {
		rate := policy.speedLimitKbps
		if rate < 0 {
			rate = 0
		}

		keys := make([]string, 0, 2)
		if clientID := strings.TrimSpace(policy.clientID); clientID != "" {
			keys = append(keys, clientID)
		}
		if username := strings.TrimSpace(policy.username); username != "" {
			keys = append(keys, username)
		}
		if rate > 0 && len(keys) == 0 {
			return fmt.Errorf("speed_limit_identity_missing")
		}
		for _, key := range keys {
			if existing, ok := rates[key]; ok && existing != rate {
				return fmt.Errorf("speed_limit_identity_conflict:%s", key)
			}
			rates[key] = rate
		}
		if rate > 0 {
			limitedUsers++
		}
	}

	l.mu.Lock()
	defer l.mu.Unlock()
	for _, gate := range l.users {
		gate.setKbps(0)
	}
	for user, rate := range rates {
		gate, ok := l.users[user]
		if !ok {
			gate = &userRateGate{}
			l.users[user] = gate
		}
		gate.setKbps(rate)
	}
	l.policyUsers = len(rates)
	l.limitedUsers = limitedUsers
	l.lastError = ""
	return nil
}

func (l *speedLimiter) setError(message string) {
	l.mu.Lock()
	defer l.mu.Unlock()
	l.lastError = strings.TrimSpace(message)
}

func (l *speedLimiter) findGate(user string) (*userRateGate, bool) {
	l.mu.Lock()
	defer l.mu.Unlock()
	gate, ok := l.users[user]
	return gate, ok
}

func (l *speedLimiter) warnBypass(user string, reason string) {
	key := reason + ":" + user
	now := time.Now()
	l.warnMu.Lock()
	nextAllowed, ok := l.warnUntil[key]
	if ok && now.Before(nextAllowed) {
		l.warnMu.Unlock()
		l.bypassCount.Add(1)
		return
	}
	l.warnUntil[key] = now.Add(30 * time.Second)
	l.warnMu.Unlock()
	l.bypassCount.Add(1)
	l.mu.Lock()
	protocolID := l.protocolID
	l.mu.Unlock()
	fmt.Fprintf(os.Stderr, "WARN: speed_limit_bypass protocol=%q reason=%s user=%q\n", protocolID, reason, user)
}

func (l *speedLimiter) RoutedConnection(_ context.Context, conn net.Conn, metadata adapter.InboundContext, _ adapter.Rule, _ adapter.Outbound) net.Conn {
	user := strings.TrimSpace(metadata.User)
	if user == "" {
		l.warnBypass("", "empty_user")
		return conn
	}
	gate, ok := l.findGate(user)
	if !ok {
		l.warnBypass(user, "unknown_user")
		return conn
	}
	return &speedLimitedConn{Conn: conn, gate: gate}
}

func (l *speedLimiter) RoutedPacketConnection(_ context.Context, conn N.PacketConn, metadata adapter.InboundContext, _ adapter.Rule, _ adapter.Outbound) N.PacketConn {
	user := strings.TrimSpace(metadata.User)
	if user == "" {
		l.warnBypass("", "empty_user")
		return conn
	}
	gate, ok := l.findGate(user)
	if !ok {
		l.warnBypass(user, "unknown_user")
		return conn
	}
	return &speedLimitedPacketConn{PacketConn: conn, gate: gate}
}

type runtimeDaemon struct {
	opts    runOptions
	limiter *speedLimiter
	stats   *statsTracker
	conns   *connTracker
	boxMu   sync.Mutex
	box     *box.Box
}

func newRuntimeDaemon(opts runOptions) *runtimeDaemon {
	return &runtimeDaemon{
		opts:    opts,
		limiter: newSpeedLimiter(),
		stats:   newStatsTracker(),
		conns:   newConnTracker(),
	}
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
	instance.Router().AppendTracker(d.limiter)
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
	d.limiter.setProtocolID(metadata.ActiveProtocol)

	if !isFullTunnelProtocol(metadata.ActiveProtocol) || metadata.Accounting.Source != "connector_tracker" {
		_ = d.limiter.applyPolicies(nil)
		snapshot := d.limiter.snapshot()
		state.LimiterPolicyUsers = snapshot.policyUsers
		state.LimiterActiveUsers = snapshot.limitedUsers
		state.LimiterBypassCount = snapshot.bypassCount
		state.LimiterLastError = snapshot.lastError
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
	snapshot := d.limiter.snapshot()
	state.LimiterPolicyUsers = snapshot.policyUsers
	state.LimiterActiveUsers = snapshot.limitedUsers
	state.LimiterBypassCount = snapshot.bypassCount
	state.LimiterLastError = snapshot.lastError
	if result.shouldSync {
		var syncResult clients.SyncResult
		err = withFileLock(d.opts.lockPath, func() error {
			var syncErr error
			syncResult, syncErr = d.syncClients(metadata.ActiveProtocol)
			return syncErr
		})
		if err != nil {
			state.OK = false
			state.LastError = "client_sync_failed:" + err.Error()
			return state
		}
		if syncResult.Changed {
			if err := d.reloadBox(); err != nil {
				state.OK = false
				state.LastError = "client_sync_reload_failed:" + err.Error()
			}
		}
	}
	return state
}

func (d *runtimeDaemon) syncClients(protocolID string) (clients.SyncResult, error) {
	db, err := sql.Open("sqlite", d.opts.accountingDB)
	if err != nil {
		return clients.SyncResult{}, err
	}
	defer db.Close()
	if err := accounting.Migrate(db); err != nil {
		return clients.SyncResult{}, err
	}
	return clients.Sync(clients.SyncOptions{
		ConfigPath: d.opts.configPath,
		Database:   db,
		ProtocolID: protocolID,
		Validate:   validateConfigContent,
	})
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
	if err := d.limiter.applyPolicies(policies); err != nil {
		d.limiter.setError("policy_apply_failed:" + err.Error())
		return result, fmt.Errorf("limiter_policy_apply_failed:%w", err)
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

	accountingResult, err := accounting.Sync(accounting.SyncInput{
		Database: db, ProtocolID: metadata.ActiveProtocol, Now: now,
		UsageDeltas: deltaByClient, ActiveConnections: activeByClient,
	})
	if err != nil {
		return result, err
	}
	result.updatedClients = accountingResult.UpdatedClients
	enableChanges := accountingResult.EnableChanges
	disableUsers := make(map[string]struct{})
	for _, clientID := range accountingResult.DisableClients {
		disableUsers[clientID] = struct{}{}
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
	if len(enableChanges) > 0 {
		if definition, ok := protocol.Lookup(strings.TrimSpace(metadata.ActiveProtocol)); ok && definition.PerClient {
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
		`SELECT client_id, username, enabled, total_bytes_limit, expiry_unix_ms, COALESCE(speed_limit_kbps, 0)
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
		if err := rows.Scan(&row.clientID, &row.username, &row.enabled, &row.totalBytes, &row.expiryUnixMS, &row.speedLimitKbps); err != nil {
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

func validateConfigContent(content []byte) error {
	ctx := box.Context(
		context.Background(),
		include.InboundRegistry(),
		include.OutboundRegistry(),
		include.EndpointRegistry(),
	)
	options, err := sbjson.UnmarshalExtendedContext[option.Options](ctx, content)
	if err != nil {
		return err
	}
	instance, err := box.New(box.Options{Context: ctx, Options: options})
	if err != nil {
		return err
	}
	return instance.Close()
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
	definition, ok := protocol.Lookup(strings.TrimSpace(protocolID))
	return ok && definition.Runtime == "singbox"
}

func ensureAccountingSchema(db *sql.DB) error {
	return accounting.Migrate(db)
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
	_ = flags.String("sync-command", "", "deprecated; client synchronization is handled internally")
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

func usage() {
	fmt.Fprintln(os.Stderr, "Usage:")
	modernUsage()
	fmt.Fprintln(os.Stderr, "  connector-core run [--config path] [--metadata path] [--accounting-db path] [--state-file path]")
	fmt.Fprintln(os.Stderr, "  connector-core check [--config path]")
	fmt.Fprintln(os.Stderr, "  connector-core tunnel run --config <path> --state-file <path> [--strict-config]")
	fmt.Fprintln(os.Stderr, "  connector-core tunnel check --config <path> [--strict-config]")
	fmt.Fprintln(os.Stderr, "  connector-core frps run --config <path> --state-file <path> [--strict-config]")
	fmt.Fprintln(os.Stderr, "  connector-core frps check --config <path> [--strict-config]")
}

func main() {
	if len(os.Args) < 2 {
		usage()
		os.Exit(2)
	}
	if handled, err := runModernCommand(os.Args[1:]); handled {
		if err != nil {
			message := strings.TrimSpace(err.Error())
			message = regexp.MustCompile(`\s+`).ReplaceAllString(message, " ")
			fmt.Fprintln(os.Stderr, "ERROR:", message)
			os.Exit(exitCodeForError(err))
		}
		return
	}
	var err error
	switch os.Args[1] {
	case "run":
		err = runCommand(os.Args[2:])
	case "check":
		err = checkCommand(os.Args[2:])
	case "tunnel":
		if len(os.Args) < 3 {
			usage()
			os.Exit(2)
		}
		switch os.Args[2] {
		case "run":
			err = runFrpTunnelCommand(os.Args[3:])
		case "check":
			err = checkFrpTunnelCommand(os.Args[3:])
		default:
			usage()
			os.Exit(2)
		}
	case "frps":
		if len(os.Args) < 3 {
			usage()
			os.Exit(2)
		}
		switch os.Args[2] {
		case "run":
			err = runFrpsCommand(os.Args[3:])
		case "check":
			err = checkFrpsCommand(os.Args[3:])
		default:
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
