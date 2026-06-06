package panel

import (
	"context"
	"errors"
	"os"
	"path/filepath"
	"runtime"
	"strings"
	"testing"
)

type panelRunner struct {
	commands []string
	failTest bool
}

func (runner *panelRunner) Run(_ context.Context, name string, args ...string) error {
	runner.commands = append(runner.commands, name+" "+strings.Join(args, " "))
	if runner.failTest && name == "nginx" {
		return errors.New("invalid nginx")
	}
	return nil
}

func TestEnableNginxSiteRollsBackFailedValidation(t *testing.T) {
	if runtime.GOOS == "windows" {
		t.Skip("symlink test requires Windows developer mode or elevated privileges")
	}
	root := t.TempDir()
	available := filepath.Join(root, "available", "site.conf")
	enabled := filepath.Join(root, "enabled", "site.conf")
	if err := os.MkdirAll(filepath.Dir(available), 0o755); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(available, []byte("server {}"), 0o644); err != nil {
		t.Fatal(err)
	}
	runner := &panelRunner{failTest: true}
	if _, err := EnableNginxSite(context.Background(), available, enabled, runner); err == nil {
		t.Fatal("expected nginx validation failure")
	}
	if _, err := os.Lstat(enabled); !os.IsNotExist(err) {
		t.Fatalf("failed nginx site was not rolled back: %v", err)
	}
	runner.failTest = false
	changed, err := EnableNginxSite(context.Background(), available, enabled, runner)
	if err != nil || !changed {
		t.Fatalf("site enable failed: changed=%v err=%v", changed, err)
	}
}
