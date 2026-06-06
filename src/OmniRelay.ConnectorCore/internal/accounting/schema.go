package accounting

import (
	"database/sql"
	"fmt"
)

const CurrentSchemaVersion = 1

func Migrate(db *sql.DB) error {
	if _, err := db.Exec(`
PRAGMA busy_timeout=5000;
PRAGMA journal_mode=WAL;
PRAGMA synchronous=NORMAL;
CREATE TABLE IF NOT EXISTS clients (
  client_id TEXT PRIMARY KEY,
  protocol_id TEXT NOT NULL,
  email TEXT NOT NULL DEFAULT '',
  username TEXT NOT NULL,
  auth_username TEXT NOT NULL DEFAULT '',
  auth_secret TEXT NOT NULL DEFAULT '',
  enabled INTEGER NOT NULL DEFAULT 1,
  total_bytes_limit INTEGER NOT NULL DEFAULT 0,
  speed_limit_kbps INTEGER NOT NULL DEFAULT 0,
  expiry_unix_ms INTEGER NOT NULL DEFAULT 0,
  created_at INTEGER NOT NULL,
  updated_at INTEGER NOT NULL
);
CREATE TABLE IF NOT EXISTS usage_totals (
  client_id TEXT PRIMARY KEY,
  used_bytes INTEGER NOT NULL DEFAULT 0,
  updated_at INTEGER NOT NULL
);
CREATE TABLE IF NOT EXISTS connection_counters (
  client_id TEXT PRIMARY KEY,
  active_connections INTEGER NOT NULL DEFAULT 0,
  last_seen_at INTEGER NOT NULL DEFAULT 0
);
CREATE TABLE IF NOT EXISTS enforcement_state (
  client_id TEXT PRIMARY KEY,
  disabled_reason TEXT NOT NULL DEFAULT '',
  disabled_at INTEGER NOT NULL DEFAULT 0,
  updated_at INTEGER NOT NULL DEFAULT 0
);
CREATE TABLE IF NOT EXISTS sampler_state (
  source TEXT NOT NULL,
  state_key TEXT NOT NULL,
  last_value INTEGER NOT NULL DEFAULT 0,
  updated_at INTEGER NOT NULL DEFAULT 0,
  PRIMARY KEY (source, state_key)
);
CREATE TABLE IF NOT EXISTS sampler_sessions (
  source TEXT NOT NULL,
  session_key TEXT NOT NULL,
  client_id TEXT NOT NULL DEFAULT '',
  upload_bytes INTEGER NOT NULL DEFAULT 0,
  download_bytes INTEGER NOT NULL DEFAULT 0,
  last_seen_at INTEGER NOT NULL DEFAULT 0,
  closed_at INTEGER NOT NULL DEFAULT 0,
  PRIMARY KEY (source, session_key)
);
CREATE TABLE IF NOT EXISTS schema_migrations (
  version INTEGER PRIMARY KEY,
  applied_at INTEGER NOT NULL
);
`); err != nil {
		return err
	}
	for _, column := range []struct {
		name       string
		definition string
	}{
		{"email", "TEXT NOT NULL DEFAULT ''"},
		{"auth_username", "TEXT NOT NULL DEFAULT ''"},
		{"auth_secret", "TEXT NOT NULL DEFAULT ''"},
		{"speed_limit_kbps", "INTEGER NOT NULL DEFAULT 0"},
	} {
		if err := ensureColumn(db, "clients", column.name, column.definition); err != nil {
			return err
		}
	}
	if _, err := db.Exec(`CREATE INDEX IF NOT EXISTS idx_clients_protocol_email ON clients(protocol_id,email);
CREATE INDEX IF NOT EXISTS idx_clients_protocol_auth_username ON clients(protocol_id,auth_username);
INSERT OR IGNORE INTO schema_migrations(version,applied_at) VALUES(?,unixepoch());`, CurrentSchemaVersion); err != nil {
		return err
	}
	return nil
}

func ensureColumn(db *sql.DB, table string, name string, definition string) error {
	rows, err := db.Query("PRAGMA table_info(" + table + ")")
	if err != nil {
		return err
	}
	defer rows.Close()
	for rows.Next() {
		var cid int
		var columnName, columnType string
		var notNull, primaryKey int
		var defaultValue any
		if err := rows.Scan(&cid, &columnName, &columnType, &notNull, &defaultValue, &primaryKey); err != nil {
			return err
		}
		if columnName == name {
			return nil
		}
	}
	if err := rows.Err(); err != nil {
		return err
	}
	_, err = db.Exec(fmt.Sprintf("ALTER TABLE %s ADD COLUMN %s %s", table, name, definition))
	return err
}
