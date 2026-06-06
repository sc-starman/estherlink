package accounting

import (
	"database/sql"
	"testing"

	_ "modernc.org/sqlite"
)

func TestMigrateUpgradesLegacyClientsTable(t *testing.T) {
	db, err := sql.Open("sqlite", ":memory:")
	if err != nil {
		t.Fatal(err)
	}
	defer db.Close()
	if _, err := db.Exec(`CREATE TABLE clients (
client_id TEXT PRIMARY KEY, protocol_id TEXT NOT NULL, username TEXT NOT NULL,
enabled INTEGER NOT NULL DEFAULT 1, total_bytes_limit INTEGER NOT NULL DEFAULT 0,
expiry_unix_ms INTEGER NOT NULL DEFAULT 0, created_at INTEGER NOT NULL, updated_at INTEGER NOT NULL
);`); err != nil {
		t.Fatal(err)
	}
	if err := Migrate(db); err != nil {
		t.Fatalf("Migrate() error = %v", err)
	}
	for _, column := range []string{"email", "auth_username", "auth_secret", "speed_limit_kbps"} {
		var count int
		if err := db.QueryRow(`SELECT COUNT(*) FROM pragma_table_info('clients') WHERE name=?`, column).Scan(&count); err != nil {
			t.Fatal(err)
		}
		if count != 1 {
			t.Fatalf("missing migrated column %s", column)
		}
	}
	if err := Migrate(db); err != nil {
		t.Fatalf("second Migrate() error = %v", err)
	}
}
