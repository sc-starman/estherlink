package ipsec

import (
	"database/sql"
	"os"
	"path/filepath"
	"testing"

	"github.com/omnirelay/connector-core/internal/accounting"
	_ "modernc.org/sqlite"
)

type recordingKiller struct {
	pids []int
}

func (k *recordingKiller) Kill(pid int) error {
	k.pids = append(k.pids, pid)
	return nil
}

func TestEnforceSessionsKillsDisabledPPPUser(t *testing.T) {
	db := enforceDatabase(t)
	_, err := db.Exec(`INSERT INTO clients(client_id,protocol_id,email,username,auth_username,auth_secret,enabled,created_at,updated_at)
VALUES('client-1','ipsec_l2tp_singbox','a@test','alice','alice','secret',0,1,1)`)
	if err != nil {
		t.Fatal(err)
	}
	sessions := filepath.Join(t.TempDir(), "sessions.tsv")
	if err := os.WriteFile(sessions, []byte("ppp0\talice\n"), 0o600); err != nil {
		t.Fatal(err)
	}
	procRoot := t.TempDir()
	processRoot := filepath.Join(procRoot, "123")
	if err := os.MkdirAll(processRoot, 0o755); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(processRoot, "cmdline"), []byte("/usr/sbin/pppd\x00call\x00ppp0\x00"), 0o600); err != nil {
		t.Fatal(err)
	}
	killer := &recordingKiller{}
	result, err := EnforceSessions(EnforceOptions{
		Database: db, ProtocolID: "ipsec_l2tp_singbox", SessionsPath: sessions, ProcRoot: procRoot, Killer: killer,
	})
	if err != nil {
		t.Fatal(err)
	}
	if result.Disconnected != 1 || len(killer.pids) != 1 || killer.pids[0] != 123 {
		t.Fatalf("unexpected enforcement result: %+v pids=%v", result, killer.pids)
	}
}

func enforceDatabase(t *testing.T) *sql.DB {
	t.Helper()
	db, err := sql.Open("sqlite", filepath.Join(t.TempDir(), "accounting.db"))
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { _ = db.Close() })
	if err := accounting.Migrate(db); err != nil {
		t.Fatal(err)
	}
	return db
}
