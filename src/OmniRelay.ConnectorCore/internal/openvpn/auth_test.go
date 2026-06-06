package openvpn

import (
	"database/sql"
	"os"
	"path/filepath"
	"testing"

	"github.com/omnirelay/connector-core/internal/accounting"
	_ "modernc.org/sqlite"
)

func TestAuthenticateUsesAccountingEligibility(t *testing.T) {
	root := t.TempDir()
	db, err := sql.Open("sqlite", filepath.Join(root, "accounting.db"))
	if err != nil {
		t.Fatal(err)
	}
	defer db.Close()
	if err := accounting.Migrate(db); err != nil {
		t.Fatal(err)
	}
	if _, err := db.Exec(`INSERT INTO clients(client_id,protocol_id,username,auth_username,auth_secret,enabled,created_at,updated_at)
VALUES('client-1','openvpn_tcp_singbox','user','user','secret',1,0,0)`); err != nil {
		t.Fatal(err)
	}
	credentials := filepath.Join(root, "credentials")
	if err := os.WriteFile(credentials, []byte("user\nsecret\n"), 0o600); err != nil {
		t.Fatal(err)
	}
	result, err := Authenticate(db, "openvpn_tcp_singbox", credentials)
	if err != nil || !result.Allowed || result.ClientID != "client-1" {
		t.Fatalf("unexpected auth result: %+v, err=%v", result, err)
	}
	if _, err := db.Exec(`UPDATE clients SET enabled=0 WHERE client_id='client-1'`); err != nil {
		t.Fatal(err)
	}
	result, err = Authenticate(db, "openvpn_tcp_singbox", credentials)
	if err != nil || result.Allowed || result.ReasonCode != "client_disabled" {
		t.Fatalf("unexpected disabled auth result: %+v, err=%v", result, err)
	}
}
