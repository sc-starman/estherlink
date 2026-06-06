package accounting

import (
	"database/sql"
	"os"
	"path/filepath"
	"strconv"
	"testing"
	"time"

	_ "modernc.org/sqlite"
)

func TestCollectOpenVPNStatusUsesUsernameAndCounterDeltas(t *testing.T) {
	db := testCollectorDatabase(t)
	insertCollectorClient(t, db, "client-1", "openvpn_tcp_singbox", "shared-cn", "alice", true)
	statusPath := filepath.Join(t.TempDir(), "status.log")
	writeOpenVPNStatus(t, statusPath, 100, 200)

	first, err := Collect(CollectInput{
		Database: db, ProtocolID: "openvpn_tcp_singbox", Source: "openvpn_status",
		OpenVPNStatusPath: statusPath, Now: time.Unix(100, 0),
	})
	if err != nil {
		t.Fatal(err)
	}
	if first.UsageDeltas["client-1"] != 0 || first.ActiveConnections["client-1"] != 1 {
		t.Fatalf("unexpected first sample: %+v", first)
	}

	writeOpenVPNStatus(t, statusPath, 160, 260)
	second, err := Collect(CollectInput{
		Database: db, ProtocolID: "openvpn_tcp_singbox", Source: "openvpn_status",
		OpenVPNStatusPath: statusPath, Now: time.Unix(110, 0),
	})
	if err != nil {
		t.Fatal(err)
	}
	if second.UsageDeltas["client-1"] != 120 {
		t.Fatalf("expected 120 byte delta, got %+v", second)
	}
}

func TestCollectPPPFallsBackForSingleEnabledUnknownUser(t *testing.T) {
	db := testCollectorDatabase(t)
	insertCollectorClient(t, db, "client-1", "ipsec_l2tp_singbox", "alice", "alice", true)
	root := t.TempDir()
	interfaceRoot := filepath.Join(root, "ppp0", "statistics")
	if err := os.MkdirAll(interfaceRoot, 0o755); err != nil {
		t.Fatal(err)
	}
	writeNumber(t, filepath.Join(interfaceRoot, "rx_bytes"), "100")
	writeNumber(t, filepath.Join(interfaceRoot, "tx_bytes"), "200")
	sessions := filepath.Join(t.TempDir(), "sessions.tsv")
	if err := os.WriteFile(sessions, []byte("ppp0\t__unknown__\n"), 0o600); err != nil {
		t.Fatal(err)
	}
	if _, err := Collect(CollectInput{
		Database: db, ProtocolID: "ipsec_l2tp_singbox", Source: "ipsec_ppp",
		PPPSessionsPath: sessions, SysClassNetRoot: root, Now: time.Unix(100, 0),
	}); err != nil {
		t.Fatal(err)
	}
	writeNumber(t, filepath.Join(interfaceRoot, "rx_bytes"), "150")
	writeNumber(t, filepath.Join(interfaceRoot, "tx_bytes"), "250")
	result, err := Collect(CollectInput{
		Database: db, ProtocolID: "ipsec_l2tp_singbox", Source: "ipsec_ppp",
		PPPSessionsPath: sessions, SysClassNetRoot: root, Now: time.Unix(110, 0),
	})
	if err != nil {
		t.Fatal(err)
	}
	if result.UsageDeltas["client-1"] != 100 || result.ActiveConnections["client-1"] != 1 {
		t.Fatalf("unexpected PPP sample: %+v", result)
	}
}

func TestCollectCounterResetChargesNewCounterOnly(t *testing.T) {
	db := testCollectorDatabase(t)
	if delta, err := counterDelta(db, "source", "key", 100, time.Unix(1, 0)); err != nil || delta != 0 {
		t.Fatalf("unexpected first delta %d: %v", delta, err)
	}
	if delta, err := counterDelta(db, "source", "key", 150, time.Unix(2, 0)); err != nil || delta != 50 {
		t.Fatalf("unexpected increasing delta %d: %v", delta, err)
	}
	if delta, err := counterDelta(db, "source", "key", 20, time.Unix(3, 0)); err != nil || delta != 20 {
		t.Fatalf("unexpected reset delta %d: %v", delta, err)
	}
}

func testCollectorDatabase(t *testing.T) *sql.DB {
	t.Helper()
	db, err := sql.Open("sqlite", filepath.Join(t.TempDir(), "accounting.db"))
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { _ = db.Close() })
	if err := Migrate(db); err != nil {
		t.Fatal(err)
	}
	return db
}

func insertCollectorClient(t *testing.T, db *sql.DB, clientID, protocolID, username, authUsername string, enabled bool) {
	t.Helper()
	enabledValue := 0
	if enabled {
		enabledValue = 1
	}
	_, err := db.Exec(`INSERT INTO clients(client_id,protocol_id,email,username,auth_username,auth_secret,enabled,created_at,updated_at)
VALUES(?,?,?,?,?,'secret',?,?,?)`, clientID, protocolID, clientID+"@test", username, authUsername, enabledValue, 1, 1)
	if err != nil {
		t.Fatal(err)
	}
}

func writeOpenVPNStatus(t *testing.T, path string, received int, sent int) {
	t.Helper()
	content := "TITLE,OpenVPN 2.6\n" +
		"HEADER,CLIENT_LIST,Common Name,Real Address,Virtual Address,Bytes Received,Bytes Sent,Connected Since,Username\n" +
		"CLIENT_LIST,shared-cn,198.51.100.1:1234,10.29.0.2," +
		fmtInt(received) + "," + fmtInt(sent) + ",2026-01-01 00:00:00,alice\n"
	if err := os.WriteFile(path, []byte(content), 0o600); err != nil {
		t.Fatal(err)
	}
}

func writeNumber(t *testing.T, path string, value string) {
	t.Helper()
	if err := os.WriteFile(path, []byte(value+"\n"), 0o600); err != nil {
		t.Fatal(err)
	}
}

func fmtInt(value int) string {
	return strconv.Itoa(value)
}
