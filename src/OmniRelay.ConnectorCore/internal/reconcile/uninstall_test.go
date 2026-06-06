package reconcile

import (
	"errors"
	"os"
	"path/filepath"
	"testing"
)

func TestUninstallRemovesOnlyManagedFiles(t *testing.T) {
	root := t.TempDir()
	options := ApplyOptions{
		ConfigRoot: filepath.Join(root, "etc"), TransactionRoot: filepath.Join(root, "transactions"),
		SystemdRoot: filepath.Join(root, "systemd"), NginxRoot: filepath.Join(root, "nginx"),
	}
	gatewaySpec := testSpec()
	applied, err := Apply(gatewaySpec, options)
	if err != nil {
		t.Fatal(err)
	}
	unrelated := filepath.Join(options.SystemdRoot, "administrator.service")
	if err := os.WriteFile(unrelated, []byte("keep"), 0o644); err != nil {
		t.Fatal(err)
	}
	result, err := Uninstall(gatewaySpec.RelayID, options)
	if err != nil {
		t.Fatal(err)
	}
	if len(result.DeletedFiles) != len(applied.ChangedFiles) {
		t.Fatalf("deleted %d files, expected %d", len(result.DeletedFiles), len(applied.ChangedFiles))
	}
	for _, path := range result.DeletedFiles {
		if _, err := os.Stat(path); !errors.Is(err, os.ErrNotExist) {
			t.Fatalf("managed path still exists: %s: %v", path, err)
		}
	}
	if content, err := os.ReadFile(unrelated); err != nil || string(content) != "keep" {
		t.Fatalf("unrelated file changed: %q, %v", content, err)
	}
}

func TestUninstallRollsBackWhenPostActionFails(t *testing.T) {
	root := t.TempDir()
	options := ApplyOptions{
		ConfigRoot: filepath.Join(root, "etc"), TransactionRoot: filepath.Join(root, "transactions"),
		SystemdRoot: filepath.Join(root, "systemd"), NginxRoot: filepath.Join(root, "nginx"),
	}
	gatewaySpec := testSpec()
	applied, err := Apply(gatewaySpec, options)
	if err != nil {
		t.Fatal(err)
	}
	options.AfterWrite = func() error { return errors.New("injected reload failure") }
	if _, err := Uninstall(gatewaySpec.RelayID, options); err == nil {
		t.Fatal("expected uninstall failure")
	}
	for _, path := range applied.ChangedFiles {
		if _, err := os.Stat(path); err != nil {
			t.Fatalf("managed path was not restored: %s: %v", path, err)
		}
	}
}
