package openvpn

import (
	"context"
	"database/sql"
	"path/filepath"
	"strings"
	"testing"

	"github.com/omnirelay/connector-core/internal/accounting"
	"github.com/omnirelay/connector-core/internal/host"
	_ "modernc.org/sqlite"
)

type recordingLimitRunner struct {
	commands []string
}

func (runner *recordingLimitRunner) Run(_ context.Context, name string, args ...string) error {
	runner.commands = append(runner.commands, name+" "+strings.Join(args, " "))
	return nil
}

func TestApplySpeedLimitsGeneratesDeterministicTCCommands(t *testing.T) {
	root := t.TempDir()
	db, err := sql.Open("sqlite", filepath.Join(root, "accounting.db"))
	if err != nil {
		t.Fatal(err)
	}
	defer db.Close()
	if err := accounting.Migrate(db); err != nil {
		t.Fatal(err)
	}
	if _, err := db.Exec(`INSERT INTO clients(client_id,protocol_id,email,username,auth_username,enabled,speed_limit_kbps,created_at,updated_at)
VALUES('client-1','openvpn_tcp_singbox','u','u','user',1,512,0,0)`); err != nil {
		t.Fatal(err)
	}
	if _, err := hostWriteMap(filepath.Join(root, "client-ip-map.json"), map[string]string{"user": "10.29.0.2"}); err != nil {
		t.Fatal(err)
	}
	runner := &recordingLimitRunner{}
	result, err := ApplySpeedLimits(context.Background(), db, "openvpn_tcp_singbox", "e4ccc282a1004b62ad2cda5770d6e32d", root, runner)
	if err != nil || result.AppliedClients != 1 {
		t.Fatalf("unexpected result: %+v err=%v", result, err)
	}
	combined := strings.Join(runner.commands, "\n")
	for _, expected := range []string{"rate 512kbit", "match ip dst 10.29.0.2/32", "match ip src 10.29.0.2/32"} {
		if !strings.Contains(combined, expected) {
			t.Fatalf("missing command %q:\n%s", expected, combined)
		}
	}
}

func hostWriteMap(path string, _ map[string]string) (bool, error) {
	content := []byte("{\"user\":\"10.29.0.2\"}\n")
	return host.WriteFileAtomic(path, content, 0o600)
}
