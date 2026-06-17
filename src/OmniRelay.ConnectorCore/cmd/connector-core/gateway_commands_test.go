package main

import (
	"database/sql"
	"encoding/base64"
	"fmt"
	"io"
	"net"
	"os"
	"path/filepath"
	"regexp"
	"strings"
	"sync/atomic"
	"testing"
	"time"

	"github.com/omnirelay/connector-core/internal/accounting"
	openvpnapi "github.com/omnirelay/connector-core/internal/openvpn"
)

func TestRunClientsSyncInfersProtocolFromPersistedSpec(t *testing.T) {
	root := t.TempDir()
	relayID := "e4ccc282a1004b62ad2cda5770d6e32d"
	gatewayRoot := filepath.Join(root, "relays", relayID, "gateway")
	connectorRoot := filepath.Join(gatewayRoot, "connector")
	if err := os.MkdirAll(connectorRoot, 0o755); err != nil {
		t.Fatal(err)
	}
	writeTestFile(t, filepath.Join(gatewayRoot, "spec.json"), fmt.Sprintf(`{
  "apiVersion": "omnirelay.io/v1alpha1",
  "kind": "Gateway",
  "relayId": %q,
  "gateway": {
    "type": "remote",
    "protocol": "vless_tls_singbox",
    "publicPort": 443,
    "connectorMode": "full_tunnel"
  },
  "tunnel": {"frpServerPort": 7000, "frpAuthToken": "0123456789abcdef0123456789abcdef"}
}`, relayID))
	configPath := filepath.Join(connectorRoot, "config.json")
	writeTestFile(t, configPath, `{
  "inbounds": [{
    "type": "vless",
    "tag": "vless-in",
    "listen": "127.0.0.1",
    "listen_port": 30443,
    "users": []
  }],
  "outbounds": [{"type": "direct", "tag": "direct"}],
  "route": {"final": "direct"}
}`)
	dbPath := filepath.Join(connectorRoot, "accounting.db")
	db, err := sql.Open("sqlite", dbPath)
	if err != nil {
		t.Fatal(err)
	}
	if err := accounting.Migrate(db); err != nil {
		t.Fatal(err)
	}
	_, err = db.Exec(`INSERT INTO clients(client_id,protocol_id,email,username,enabled,created_at,updated_at)
VALUES(?,?,?,?,1,0,0)`, "dbe0861d-2aa1-4b49-bcaa-56c9c8570d9c", "vless_tls_singbox", "user@example.com", "user@example.com")
	if err != nil {
		t.Fatal(err)
	}
	if err := db.Close(); err != nil {
		t.Fatal(err)
	}

	err = runClientsSync([]string{
		"--relay-id", relayID,
		"--config-root", root,
		"--lock-file", filepath.Join(root, "accounting.lock"),
		"--json",
	})
	if err != nil {
		t.Fatal(err)
	}
	content, err := os.ReadFile(configPath)
	if err != nil {
		t.Fatal(err)
	}
	if !containsString(string(content), "dbe0861d-2aa1-4b49-bcaa-56c9c8570d9c") {
		t.Fatalf("synced config does not contain active VLESS client: %s", content)
	}
}

func TestSeedInitialClientCreatesVLESSClientOnce(t *testing.T) {
	db, err := sql.Open("sqlite", ":memory:")
	if err != nil {
		t.Fatal(err)
	}
	defer db.Close()
	if err := accounting.Migrate(db); err != nil {
		t.Fatal(err)
	}
	if err := seedInitialClient(db, "vless_tls_singbox"); err != nil {
		t.Fatal(err)
	}
	if err := seedInitialClient(db, "vless_tls_singbox"); err != nil {
		t.Fatal(err)
	}
	var count int
	var clientID, email, username, authUsername, authSecret string
	if err := db.QueryRow(`SELECT COUNT(1), client_id, email, username, auth_username, auth_secret FROM clients WHERE protocol_id='vless_tls_singbox'`).Scan(&count, &clientID, &email, &username, &authUsername, &authSecret); err != nil {
		t.Fatal(err)
	}
	if count != 1 {
		t.Fatalf("expected one seeded VLESS client, got %d", count)
	}
	if !regexp.MustCompile(`^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$`).MatchString(clientID) {
		t.Fatalf("seeded VLESS client id is not a UUIDv4: %q", clientID)
	}
	if email != "omni-client@local" || username != "omni-client@local" || authUsername != "" || authSecret == "" {
		t.Fatalf("unexpected VLESS seed values: email=%q username=%q authUsername=%q authSecret=%q", email, username, authUsername, authSecret)
	}
}

func TestSeedInitialClientUsesOpenVPNDefaultsAndSkipsSharedProtocols(t *testing.T) {
	db, err := sql.Open("sqlite", ":memory:")
	if err != nil {
		t.Fatal(err)
	}
	defer db.Close()
	if err := accounting.Migrate(db); err != nil {
		t.Fatal(err)
	}
	if err := seedInitialClient(db, "mixed_singbox"); err != nil {
		t.Fatal(err)
	}
	var sharedCount int
	if err := db.QueryRow(`SELECT COUNT(1) FROM clients WHERE protocol_id='mixed_singbox'`).Scan(&sharedCount); err != nil {
		t.Fatal(err)
	}
	if sharedCount != 0 {
		t.Fatalf("shared protocol should not seed per-client rows, got %d", sharedCount)
	}
	if err := seedInitialClient(db, "openvpn_tcp_singbox"); err != nil {
		t.Fatal(err)
	}
	var username, authUsername, authSecret string
	if err := db.QueryRow(`SELECT username, auth_username, auth_secret FROM clients WHERE protocol_id='openvpn_tcp_singbox'`).Scan(&username, &authUsername, &authSecret); err != nil {
		t.Fatal(err)
	}
	if username != "ovpn_client" || authUsername != "ovpn_client" || len(authSecret) < 20 {
		t.Fatalf("unexpected OpenVPN seed values: username=%q authUsername=%q authSecret=%q", username, authUsername, authSecret)
	}
}

func TestSeedInitialClientUsesValidLengthShadowsocks2022Secret(t *testing.T) {
	db, err := sql.Open("sqlite", ":memory:")
	if err != nil {
		t.Fatal(err)
	}
	defer db.Close()
	if err := accounting.Migrate(db); err != nil {
		t.Fatal(err)
	}
	if err := seedInitialClient(db, "shadowsocks_singbox"); err != nil {
		t.Fatal(err)
	}
	var authSecret string
	if err := db.QueryRow(`SELECT auth_secret FROM clients WHERE protocol_id='shadowsocks_singbox'`).Scan(&authSecret); err != nil {
		t.Fatal(err)
	}
	decoded, err := base64.StdEncoding.DecodeString(authSecret)
	if err != nil {
		t.Fatalf("seeded shadowsocks secret %q is not standard base64: %v", authSecret, err)
	}
	if len(decoded) != 16 {
		t.Fatalf("seeded shadowsocks secret decodes to %d bytes, want 16 (required for 2022-blake3-aes-128-gcm)", len(decoded))
	}
}

func TestRunClientsSyncDispatchesOpenVPNRuntime(t *testing.T) {
	root := t.TempDir()
	relayID := "e4ccc282a1004b62ad2cda5770d6e32d"
	gatewayRoot := filepath.Join(root, "relays", relayID, "gateway")
	connectorRoot := filepath.Join(gatewayRoot, "connector")
	if err := os.MkdirAll(connectorRoot, 0o755); err != nil {
		t.Fatal(err)
	}
	writeTestFile(t, filepath.Join(gatewayRoot, "spec.json"), fmt.Sprintf(`{
  "apiVersion": "omnirelay.io/v1alpha1",
  "kind": "Gateway",
  "relayId": %q,
  "gateway": {
    "type": "remote",
    "protocol": "openvpn_tcp_singbox",
    "publicPort": 443,
    "connectorMode": "internal_tunnel"
  },
  "tunnel": {"frpServerPort": 7000, "frpAuthToken": "0123456789abcdef0123456789abcdef"},
  "openvpn": {"network": "10.29.0.0/24", "publicHost": "vpn.example.com"}
}`, relayID))
	openVPNRoot := filepath.Join(gatewayRoot, "openvpn")
	if err := os.MkdirAll(openVPNRoot, 0o700); err != nil {
		t.Fatal(err)
	}
	for name, content := range map[string]string{
		"ca.crt": "-----BEGIN CERTIFICATE-----\nca\n", "client-shared.crt": "-----BEGIN CERTIFICATE-----\ncert\n",
		"client-shared.key": "-----BEGIN PRIVATE KEY-----\nkey\n", "ta.key": "-----BEGIN OpenVPN Static key V1-----\nkey\n",
	} {
		writeTestFile(t, filepath.Join(openVPNRoot, name), content)
	}
	dbPath := filepath.Join(connectorRoot, "accounting.db")
	db, err := sql.Open("sqlite", dbPath)
	if err != nil {
		t.Fatal(err)
	}
	if err := accounting.Migrate(db); err != nil {
		t.Fatal(err)
	}
	if _, err := db.Exec(`INSERT INTO clients(client_id,protocol_id,email,username,auth_username,auth_secret,enabled,created_at,updated_at)
VALUES('client','openvpn_tcp_singbox','user@example.com','user','ovpn-user','secret',1,0,0)`); err != nil {
		t.Fatal(err)
	}
	if err := db.Close(); err != nil {
		t.Fatal(err)
	}
	if err := runClientsSync([]string{
		"--relay-id", relayID, "--config-root", root,
		"--lock-file", filepath.Join(root, "accounting.lock"), "--json",
	}); err != nil {
		t.Fatal(err)
	}
	ccd, err := os.ReadFile(filepath.Join(gatewayRoot, "openvpn", "ccd", "ovpn-user"))
	if err != nil {
		t.Fatal(err)
	}
	if !containsString(string(ccd), "ifconfig-push 10.29.0.2 255.255.255.0") {
		t.Fatalf("unexpected OpenVPN CCD: %s", ccd)
	}
}

func TestRunAccountingSyncUpdatesOpenVPNUsageAndDisablesQuotaExceededClient(t *testing.T) {
	root := t.TempDir()
	relayID := "e4ccc282a1004b62ad2cda5770d6e32d"
	gatewayRoot := filepath.Join(root, "relays", relayID, "gateway")
	connectorRoot := filepath.Join(gatewayRoot, "connector")
	if err := os.MkdirAll(connectorRoot, 0o755); err != nil {
		t.Fatal(err)
	}
	writeTestFile(t, filepath.Join(gatewayRoot, "spec.json"), fmt.Sprintf(`{
  "apiVersion": "omnirelay.io/v1alpha1",
  "kind": "Gateway",
  "relayId": %q,
  "gateway": {
    "type": "remote",
    "protocol": "openvpn_tcp_singbox",
    "publicPort": 443,
    "connectorMode": "internal_tunnel"
  },
  "tunnel": {"frpServerPort": 7000, "frpAuthToken": "0123456789abcdef0123456789abcdef"},
  "openvpn": {"network": "10.29.0.0/24", "publicHost": "vpn.example.com"}
}`, relayID))
	openVPNRoot := filepath.Join(gatewayRoot, "openvpn")
	if err := os.MkdirAll(openVPNRoot, 0o700); err != nil {
		t.Fatal(err)
	}
	for name, content := range map[string]string{
		"ca.crt":            "-----BEGIN CERTIFICATE-----\nca\n",
		"client-shared.crt": "-----BEGIN CERTIFICATE-----\ncert\n",
		"client-shared.key": "-----BEGIN PRIVATE KEY-----\nkey\n",
		"ta.key":            "-----BEGIN OpenVPN Static key V1-----\nkey\n",
	} {
		writeTestFile(t, filepath.Join(openVPNRoot, name), content)
	}
	dbPath := filepath.Join(connectorRoot, "accounting.db")
	db, err := sql.Open("sqlite", dbPath)
	if err != nil {
		t.Fatal(err)
	}
	if err := accounting.Migrate(db); err != nil {
		t.Fatal(err)
	}
	if _, err := db.Exec(`INSERT INTO clients(client_id,protocol_id,email,username,auth_username,auth_secret,enabled,total_bytes_limit,created_at,updated_at)
VALUES('client','openvpn_tcp_singbox','user@example.com','user@example.com','ovpn-user','secret',1,100,0,0)`); err != nil {
		t.Fatal(err)
	}
	if err := db.Close(); err != nil {
		t.Fatal(err)
	}
	statusPath := filepath.Join(root, "openvpn-status.log")
	killCount := &atomic.Int32{}
	stopManagement := startOpenVPNManagementStub(t, relayID, killCount)
	defer stopManagement()
	writeOpenVPNStatusForGatewayCommandTest(t, statusPath, 100, 100)
	if err := runAccountingSync([]string{
		"--relay-id", relayID,
		"--config-root", root,
		"--database", dbPath,
		"--openvpn-status", statusPath,
		"--lock-file", filepath.Join(root, "accounting.lock"),
		"--json",
	}); err != nil {
		t.Fatal(err)
	}
	writeOpenVPNStatusForGatewayCommandTest(t, statusPath, 150, 150)
	if err := runAccountingSync([]string{
		"--relay-id", relayID,
		"--config-root", root,
		"--database", dbPath,
		"--openvpn-status", statusPath,
		"--lock-file", filepath.Join(root, "accounting.lock"),
		"--json",
	}); err != nil {
		t.Fatal(err)
	}
	writeOpenVPNStatusForGatewayCommandTest(t, statusPath, 150, 150)
	if err := runAccountingSync([]string{
		"--relay-id", relayID,
		"--config-root", root,
		"--database", dbPath,
		"--openvpn-status", statusPath,
		"--lock-file", filepath.Join(root, "accounting.lock"),
		"--json",
	}); err != nil {
		t.Fatal(err)
	}
	db, err = sql.Open("sqlite", dbPath)
	if err != nil {
		t.Fatal(err)
	}
	defer db.Close()
	var usedBytes int64
	var enabled int
	var disabledReason string
	if err := db.QueryRow(`SELECT used_bytes FROM usage_totals WHERE client_id='client'`).Scan(&usedBytes); err != nil {
		t.Fatal(err)
	}
	if usedBytes != 100 {
		t.Fatalf("unexpected recorded usage: %d", usedBytes)
	}
	if err := db.QueryRow(`SELECT enabled FROM clients WHERE client_id='client'`).Scan(&enabled); err != nil {
		t.Fatal(err)
	}
	if enabled != 0 {
		t.Fatalf("expected client to be disabled after quota exceed, got enabled=%d", enabled)
	}
	if err := db.QueryRow(`SELECT disabled_reason FROM enforcement_state WHERE client_id='client'`).Scan(&disabledReason); err != nil {
		t.Fatal(err)
	}
	if disabledReason != "quota_exceeded" {
		t.Fatalf("unexpected disabled reason: %q", disabledReason)
	}
	if got := killCount.Load(); got != 2 {
		t.Fatalf("expected two OpenVPN disconnect attempts after quota exceed, got %d", got)
	}
}

func writeTestFile(t *testing.T, path string, content string) {
	t.Helper()
	if err := os.WriteFile(path, []byte(content), 0o600); err != nil {
		t.Fatal(err)
	}
}

func containsString(content string, expected string) bool {
	for index := 0; index+len(expected) <= len(content); index++ {
		if content[index:index+len(expected)] == expected {
			return true
		}
	}
	return false
}

func writeOpenVPNStatusForGatewayCommandTest(t *testing.T, path string, received int, sent int) {
	t.Helper()
	content := "TITLE,OpenVPN 2.6\n" +
		"HEADER,CLIENT_LIST,Common Name,Real Address,Virtual Address,Bytes Received,Bytes Sent,Connected Since,Username\n" +
		"CLIENT_LIST,ovpn-user,198.51.100.1:1234,10.29.0.2," +
		fmt.Sprintf("%d,%d,2026-01-01 00:00:00,ovpn-user\n", received, sent)
	if err := os.WriteFile(path, []byte(content), 0o600); err != nil {
		t.Fatal(err)
	}
}

func startOpenVPNManagementStub(t *testing.T, relayID string, killCount *atomic.Int32) func() {
	t.Helper()
	listener, err := net.Listen("tcp", fmt.Sprintf("127.0.0.1:%d", openvpnapi.ManagementPort(relayID)))
	if err != nil {
		t.Fatal(err)
	}
	done := make(chan struct{})
	go func() {
		defer close(done)
		for {
			connection, acceptErr := listener.Accept()
			if acceptErr != nil {
				return
			}
			func() {
				defer connection.Close()
				_ = connection.SetReadDeadline(time.Now().Add(500 * time.Millisecond))
				buffer := make([]byte, 4096)
				count, _ := connection.Read(buffer)
				command := string(buffer[:count])
				switch {
				case strings.Contains(command, "status 3"):
					_, _ = io.WriteString(connection, ">INFO:OpenVPN Management Interface\nHEADER,CLIENT_LIST,Common Name,Real Address,Virtual Address,Username,Client ID\nCLIENT_LIST,ovpn-user,198.51.100.1:1234,10.29.0.2,ovpn-user,7\nEND\n")
				case strings.Contains(command, "client-kill 7"):
					if killCount != nil {
						killCount.Add(1)
					}
					_, _ = io.WriteString(connection, "SUCCESS: client-kill\nEND\n")
				default:
					_, _ = io.WriteString(connection, "END\n")
				}
			}()
		}
	}()
	return func() {
		_ = listener.Close()
		<-done
	}
}
