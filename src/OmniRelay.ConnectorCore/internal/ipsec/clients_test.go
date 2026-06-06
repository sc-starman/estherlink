package ipsec

import (
	"database/sql"
	"os"
	"path/filepath"
	"strings"
	"testing"

	"github.com/omnirelay/connector-core/internal/accounting"
	_ "modernc.org/sqlite"
)

func TestSyncClientsRendersRelayOwnedChapSecrets(t *testing.T) {
	root := t.TempDir()
	db, err := sql.Open("sqlite", filepath.Join(root, "accounting.db"))
	if err != nil {
		t.Fatal(err)
	}
	defer db.Close()
	if err := accounting.Migrate(db); err != nil {
		t.Fatal(err)
	}
	if _, err := db.Exec(`INSERT INTO clients(client_id,protocol_id,email,username,auth_username,auth_secret,enabled,created_at,updated_at)
VALUES('client','ipsec_l2tp_singbox','client','client','l2tp-user','secret',1,0,0)`); err != nil {
		t.Fatal(err)
	}
	output := filepath.Join(root, "ipsec", "chap-secrets")
	result, err := SyncClients(db, "ipsec_l2tp_singbox", output)
	if err != nil || !result.Changed || result.ActiveUsers != 1 {
		t.Fatalf("unexpected sync result: %+v, err=%v", result, err)
	}
	content, err := os.ReadFile(output)
	if err != nil {
		t.Fatal(err)
	}
	if !strings.Contains(string(content), `"l2tp-user" l2tpd "secret" *`) {
		t.Fatalf("unexpected chap secrets: %s", content)
	}
}
