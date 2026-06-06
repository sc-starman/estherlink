package openvpn

import (
	"database/sql"
	"os"
	"path/filepath"
	"runtime"
	"strings"
	"testing"

	"github.com/omnirelay/connector-core/internal/accounting"
	_ "modernc.org/sqlite"
)

func TestSyncExportsIsIdempotentAndRemovesDisabledClient(t *testing.T) {
	root := t.TempDir()
	for name, content := range map[string]string{
		"ca.crt":            "-----BEGIN CERTIFICATE-----\nca\n",
		"client-shared.crt": "-----BEGIN CERTIFICATE-----\ncert\n",
		"client-shared.key": "-----BEGIN PRIVATE KEY-----\nkey\n",
		"ta.key":            "-----BEGIN OpenVPN Static key V1-----\nkey\n",
	} {
		if err := os.WriteFile(filepath.Join(root, name), []byte(content), 0o600); err != nil {
			t.Fatal(err)
		}
	}
	db, err := sql.Open("sqlite", filepath.Join(root, "accounting.db"))
	if err != nil {
		t.Fatal(err)
	}
	defer db.Close()
	if err := accounting.Migrate(db); err != nil {
		t.Fatal(err)
	}
	if _, err := db.Exec(`INSERT INTO clients(client_id,protocol_id,email,username,auth_username,auth_secret,enabled,created_at,updated_at)
VALUES('client-1','openvpn_tcp_singbox','user','user','user','secret',1,0,0)`); err != nil {
		t.Fatal(err)
	}
	first, err := SyncExports(db, "openvpn_tcp_singbox", "vpn.example.com", 443, root)
	if err != nil || !first.Changed {
		t.Fatalf("unexpected first export sync: %+v err=%v", first, err)
	}
	profilePath := filepath.Join(root, "exports", "client-1.ovpn")
	content, err := os.ReadFile(profilePath)
	if err != nil || !strings.Contains(string(content), "remote vpn.example.com 443") {
		t.Fatalf("invalid profile: %v %s", err, content)
	}
	if runtime.GOOS != "windows" {
		info, _ := os.Stat(profilePath)
		if info.Mode().Perm() != 0o600 {
			t.Fatalf("profile mode is %o", info.Mode().Perm())
		}
	}
	second, err := SyncExports(db, "openvpn_tcp_singbox", "vpn.example.com", 443, root)
	if err != nil || second.Changed {
		t.Fatalf("second sync should be unchanged: %+v err=%v", second, err)
	}
	if _, err := db.Exec(`UPDATE clients SET enabled=0 WHERE client_id='client-1'`); err != nil {
		t.Fatal(err)
	}
	if result, err := SyncExports(db, "openvpn_tcp_singbox", "vpn.example.com", 443, root); err != nil || !result.Changed {
		t.Fatalf("disable sync failed: %+v err=%v", result, err)
	}
	if _, err := os.Stat(profilePath); !os.IsNotExist(err) {
		t.Fatalf("disabled client profile remains: %v", err)
	}
}
