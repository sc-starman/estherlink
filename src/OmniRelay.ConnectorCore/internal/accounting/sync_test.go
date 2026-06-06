package accounting

import (
	"database/sql"
	"testing"
	"time"

	_ "modernc.org/sqlite"
)

func TestSyncDisablesQuotaExceededClientAndReenablesAfterLimitChange(t *testing.T) {
	db := syncTestDB(t)
	_, err := db.Exec(`INSERT INTO clients(client_id,protocol_id,email,username,enabled,total_bytes_limit,created_at,updated_at)
VALUES('client','vless_tls_singbox','client','client',1,100,0,0)`)
	if err != nil {
		t.Fatal(err)
	}
	now := time.Date(2026, 6, 4, 10, 0, 0, 0, time.UTC)
	first, err := Sync(SyncInput{
		Database: db, ProtocolID: "vless_tls_singbox", Now: now,
		UsageDeltas: map[string]int64{"client": 100},
	})
	if err != nil {
		t.Fatal(err)
	}
	if first.EnableChanges["client"] || len(first.DisableClients) != 1 {
		t.Fatalf("unexpected first result: %+v", first)
	}
	if _, err := db.Exec(`UPDATE clients SET total_bytes_limit=200 WHERE client_id='client'`); err != nil {
		t.Fatal(err)
	}
	second, err := Sync(SyncInput{Database: db, ProtocolID: "vless_tls_singbox", Now: now.Add(time.Minute)})
	if err != nil {
		t.Fatal(err)
	}
	if enabled, ok := second.EnableChanges["client"]; !ok || !enabled {
		t.Fatalf("client was not re-enabled: %+v", second)
	}
}

func TestSyncDisablesExpiredClientWithoutUsageDelta(t *testing.T) {
	db := syncTestDB(t)
	_, err := db.Exec(`INSERT INTO clients(client_id,protocol_id,email,username,enabled,expiry_unix_ms,created_at,updated_at)
VALUES('client','trojan_singbox','client','client',1,1000,0,0)`)
	if err != nil {
		t.Fatal(err)
	}
	result, err := Sync(SyncInput{
		Database: db, ProtocolID: "trojan_singbox", Now: time.UnixMilli(1001),
	})
	if err != nil {
		t.Fatal(err)
	}
	if result.UpdatedClients != 1 || len(result.DisableClients) != 1 {
		t.Fatalf("unexpected result: %+v", result)
	}
}

func syncTestDB(t *testing.T) *sql.DB {
	t.Helper()
	db, err := sql.Open("sqlite", ":memory:")
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { _ = db.Close() })
	if err := Migrate(db); err != nil {
		t.Fatal(err)
	}
	return db
}
