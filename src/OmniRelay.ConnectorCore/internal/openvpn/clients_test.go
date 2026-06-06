package openvpn

import (
	"database/sql"
	"os"
	"path/filepath"
	"testing"

	"github.com/omnirelay/connector-core/internal/accounting"
	_ "modernc.org/sqlite"
)

func TestSyncClientsRendersStableCCDAndRemovesStaleIdentity(t *testing.T) {
	root := t.TempDir()
	db, err := sql.Open("sqlite", filepath.Join(root, "accounting.db"))
	if err != nil {
		t.Fatal(err)
	}
	defer db.Close()
	if err := accounting.Migrate(db); err != nil {
		t.Fatal(err)
	}
	for _, identity := range []string{"alpha", "beta"} {
		if _, err := db.Exec(`INSERT INTO clients(client_id,protocol_id,email,username,auth_username,enabled,created_at,updated_at)
VALUES(?,?,?,?,?,1,0,0)`, identity, "openvpn_tcp_singbox", identity, identity, identity); err != nil {
			t.Fatal(err)
		}
	}
	first, err := SyncClients(db, "openvpn_tcp_singbox", "10.29.0.0/29", filepath.Join(root, "openvpn"))
	if err != nil || !first.Changed || first.ActiveUsers != 2 {
		t.Fatalf("unexpected first sync: %+v, err=%v", first, err)
	}
	second, err := SyncClients(db, "openvpn_tcp_singbox", "10.29.0.0/29", filepath.Join(root, "openvpn"))
	if err != nil || second.Changed {
		t.Fatalf("second sync should be unchanged: %+v, err=%v", second, err)
	}
	if _, err := db.Exec(`UPDATE clients SET enabled=0 WHERE client_id='beta'`); err != nil {
		t.Fatal(err)
	}
	third, err := SyncClients(db, "openvpn_tcp_singbox", "10.29.0.0/29", filepath.Join(root, "openvpn"))
	if err != nil || !third.Changed || third.ActiveUsers != 1 {
		t.Fatalf("unexpected third sync: %+v, err=%v", third, err)
	}
	if _, err := os.Stat(filepath.Join(root, "openvpn", "ccd", "beta")); !os.IsNotExist(err) {
		t.Fatalf("stale CCD file was not removed: %v", err)
	}
}
