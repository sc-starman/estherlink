package clients

import (
	"database/sql"
	"encoding/json"
	"os"
	"path/filepath"
	"testing"

	"github.com/omnirelay/connector-core/internal/accounting"
	_ "modernc.org/sqlite"
)

func TestSyncVlessUsersChangesOnlyWhenEffectiveUsersChange(t *testing.T) {
	for _, protocolID := range []string{"vless_plain_singbox", "vless_tls_singbox"} {
		t.Run(protocolID, func(t *testing.T) {
			testSyncVlessUsersChangesOnlyWhenEffectiveUsersChange(t, protocolID)
		})
	}
}

func testSyncVlessUsersChangesOnlyWhenEffectiveUsersChange(t *testing.T, protocolID string) {
	root := t.TempDir()
	configPath := filepath.Join(root, "config.json")
	initial := `{"inbounds":[{"type":"vless","users":[{"uuid":"old","name":"old"}]}]}`
	if err := os.WriteFile(configPath, []byte(initial), 0o600); err != nil {
		t.Fatal(err)
	}
	db := testDB(t)
	insertClient(t, db, "b", protocolID, "", true)
	insertClient(t, db, "a", protocolID, "", true)
	insertClient(t, db, "disabled", protocolID, "", false)

	first, err := Sync(SyncOptions{ConfigPath: configPath, Database: db, ProtocolID: protocolID})
	if err != nil {
		t.Fatal(err)
	}
	if !first.Changed || first.ActiveUsers != 2 {
		t.Fatalf("unexpected first result: %+v", first)
	}
	second, err := Sync(SyncOptions{ConfigPath: configPath, Database: db, ProtocolID: protocolID})
	if err != nil {
		t.Fatal(err)
	}
	if second.Changed {
		t.Fatal("second sync should not rewrite unchanged users")
	}
}

func TestSyncShadowTLSUpdatesBothManagedInbounds(t *testing.T) {
	root := t.TempDir()
	configPath := filepath.Join(root, "config.json")
	initial := `{"inbounds":[{"type":"shadowtls","users":[]},{"type":"shadowsocks","users":[]}]}`
	if err := os.WriteFile(configPath, []byte(initial), 0o600); err != nil {
		t.Fatal(err)
	}
	db := testDB(t)
	insertClient(t, db, "client", "shadowtls_v3_shadowsocks_singbox", "secret", true)
	result, err := Sync(SyncOptions{ConfigPath: configPath, Database: db, ProtocolID: "shadowtls_v3_shadowsocks_singbox"})
	if err != nil {
		t.Fatal(err)
	}
	if !result.Changed {
		t.Fatal("expected changed config")
	}
	raw, err := os.ReadFile(configPath)
	if err != nil {
		t.Fatal(err)
	}
	var config map[string]any
	if err := json.Unmarshal(raw, &config); err != nil {
		t.Fatal(err)
	}
	for _, rawInbound := range config["inbounds"].([]any) {
		users := rawInbound.(map[string]any)["users"].([]any)
		if len(users) != 1 {
			t.Fatalf("expected one user in each managed inbound: %s", raw)
		}
	}
}

func TestSyncRejectsEnabledPasswordClientWithoutSecret(t *testing.T) {
	root := t.TempDir()
	configPath := filepath.Join(root, "config.json")
	initial := `{"inbounds":[{"type":"trojan","users":[]}]}`
	if err := os.WriteFile(configPath, []byte(initial), 0o600); err != nil {
		t.Fatal(err)
	}
	db := testDB(t)
	insertClient(t, db, "client", "trojan_singbox", "", true)
	if _, err := Sync(SyncOptions{ConfigPath: configPath, Database: db, ProtocolID: "trojan_singbox"}); err == nil {
		t.Fatal("expected enabled client without secret to be rejected")
	}
}

func testDB(t *testing.T) *sql.DB {
	t.Helper()
	db, err := sql.Open("sqlite", ":memory:")
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { _ = db.Close() })
	if err := accounting.Migrate(db); err != nil {
		t.Fatal(err)
	}
	return db
}

func insertClient(t *testing.T, db *sql.DB, id string, protocolID string, secret string, enabled bool) {
	t.Helper()
	enabledValue := 0
	if enabled {
		enabledValue = 1
	}
	_, err := db.Exec(`INSERT INTO clients(client_id,protocol_id,email,username,auth_secret,enabled,created_at,updated_at)
VALUES(?,?,?,?,?,?,0,0)`, id, protocolID, id, id, secret, enabledValue)
	if err != nil {
		t.Fatal(err)
	}
}
