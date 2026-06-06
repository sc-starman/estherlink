package main

import (
	"database/sql"
	"fmt"
	"os"
	"path/filepath"
	"testing"

	"github.com/omnirelay/connector-core/internal/accounting"
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
