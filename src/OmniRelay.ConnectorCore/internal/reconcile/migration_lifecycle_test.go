package reconcile

import (
	"os"
	"path/filepath"
	"runtime"
	"testing"
)

func TestFinalizeMigrationRemovesOnlyObsoleteLegacyPaths(t *testing.T) {
	root := t.TempDir()
	relayID := testSpec().RelayID
	options := MigrateOptions{ApplyOptions: ApplyOptions{
		ConfigRoot: filepath.Join(root, "etc"), TransactionRoot: filepath.Join(root, "transactions"),
		SystemdRoot: filepath.Join(root, "systemd"),
	}, LegacyBinaryRoot: filepath.Join(root, "sbin")}
	obsolete := filepath.Join(options.LegacyBinaryRoot, "omnirelay-gatewayctl-"+relayID)
	managed := filepath.Join(options.SystemdRoot, "omnirelay-omnipanel-"+relayID+".service")
	for _, path := range []string{obsolete, managed} {
		if err := os.MkdirAll(filepath.Dir(path), 0o700); err != nil {
			t.Fatal(err)
		}
		if err := os.WriteFile(path, []byte("test"), 0o600); err != nil {
			t.Fatal(err)
		}
	}
	if _, err := FinalizeMigration(relayID, options); err != nil {
		t.Fatal(err)
	}
	if _, err := os.Stat(obsolete); !os.IsNotExist(err) {
		t.Fatal("obsolete legacy path was not removed")
	}
	if _, err := os.Stat(managed); err != nil {
		t.Fatal("shared managed unit was incorrectly removed")
	}
}

func TestRollbackMigrationRestoresLatestSnapshot(t *testing.T) {
	root := t.TempDir()
	gatewaySpec := testSpec()
	options := MigrateOptions{ApplyOptions: ApplyOptions{
		ConfigRoot: filepath.Join(root, "etc"), TransactionRoot: filepath.Join(root, "transactions"),
		SystemdRoot: filepath.Join(root, "systemd"), NginxRoot: filepath.Join(root, "nginx"), DNSMasqRoot: filepath.Join(root, "dnsmasq"),
	}, LegacyBinaryRoot: filepath.Join(root, "sbin"), PanelAppRoot: filepath.Join(root, "apps"), NginxEnabledRoot: filepath.Join(root, "nginx-enabled")}
	legacy := filepath.Join(options.LegacyBinaryRoot, "omnirelay-gatewayctl-"+gatewaySpec.RelayID)
	legacyPanel := filepath.Join(options.PanelAppRoot, gatewaySpec.RelayID, "omnipanel", "current", "server.js")
	legacyNginx := filepath.Join(options.NginxRoot, "omnirelay-omnipanel-"+gatewaySpec.RelayID+".conf")
	legacyNginxEnabled := filepath.Join(options.NginxEnabledRoot, filepath.Base(legacyNginx))
	for _, path := range []string{legacy, legacyPanel, legacyNginx} {
		if err := os.MkdirAll(filepath.Dir(path), 0o700); err != nil {
			t.Fatal(err)
		}
		if err := os.WriteFile(path, []byte("legacy"), 0o600); err != nil {
			t.Fatal(err)
		}
	}
	if err := os.MkdirAll(filepath.Dir(legacyNginxEnabled), 0o700); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(legacyNginxEnabled, []byte("legacy-enabled"), 0o600); err != nil {
		t.Fatal(err)
	}
	if _, err := Migrate(gatewaySpec, options); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(legacy, []byte("changed"), 0o600); err != nil {
		t.Fatal(err)
	}
	result, err := RollbackMigration(gatewaySpec.RelayID, options)
	if err != nil {
		t.Fatal(err)
	}
	content, err := os.ReadFile(legacy)
	if err != nil || string(content) != "legacy" {
		t.Fatalf("legacy binary was not restored: %q %v", content, err)
	}
	if _, err := os.Stat(filepath.Join(options.ConfigRoot, "relays", gatewaySpec.RelayID, "gateway", "spec.json")); !os.IsNotExist(err) {
		t.Fatal("new managed specification remained after rollback")
	}
	if content, err := os.ReadFile(legacyPanel); err != nil || string(content) != "legacy" {
		t.Fatalf("legacy panel release was not restored: %q %v", content, err)
	}
	if content, err := os.ReadFile(legacyNginxEnabled); err != nil || string(content) != "legacy-enabled" {
		t.Fatalf("legacy nginx enabled site was not restored: %q %v", content, err)
	}
	if len(result.RestartUnits) != 1 || result.RestartUnits[0] != "nginx.service" {
		t.Fatalf("unexpected rollback restart units: %+v", result.RestartUnits)
	}
}

func TestRollbackMigrationRestoresClockUnitsToSystemdRoot(t *testing.T) {
	root := t.TempDir()
	gatewaySpec := testSpec()
	options := MigrateOptions{ApplyOptions: ApplyOptions{
		ConfigRoot: filepath.Join(root, "etc"), TransactionRoot: filepath.Join(root, "transactions"),
		SystemdRoot: filepath.Join(root, "systemd"), NginxRoot: filepath.Join(root, "nginx"), DNSMasqRoot: filepath.Join(root, "dnsmasq"),
	}, LegacyBinaryRoot: filepath.Join(root, "sbin"), PanelAppRoot: filepath.Join(root, "apps"), NginxEnabledRoot: filepath.Join(root, "nginx-enabled")}
	clockUnit := filepath.Join(options.SystemdRoot, "omnirelay-clock-sync-"+gatewaySpec.RelayID+".service")
	if err := os.MkdirAll(filepath.Dir(clockUnit), 0o700); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(clockUnit, []byte("legacy-clock-unit"), 0o600); err != nil {
		t.Fatal(err)
	}
	if _, err := Migrate(gatewaySpec, options); err != nil {
		t.Fatal(err)
	}
	if err := os.Remove(clockUnit); err != nil {
		t.Fatal(err)
	}
	if _, err := RollbackMigration(gatewaySpec.RelayID, options); err != nil {
		t.Fatal(err)
	}
	if content, err := os.ReadFile(clockUnit); err != nil || string(content) != "legacy-clock-unit" {
		t.Fatalf("clock unit was not restored to systemd root: %q %v", content, err)
	}
	wrongPath := filepath.Join(options.LegacyBinaryRoot, filepath.Base(clockUnit))
	if _, err := os.Stat(wrongPath); !os.IsNotExist(err) {
		t.Fatalf("clock unit was incorrectly restored under legacy binary root: %s", wrongPath)
	}
}

func TestRollbackMigrationRestoresEnabledSiteSymlink(t *testing.T) {
	if runtime.GOOS == "windows" {
		t.Skip("Windows symlink creation requires developer mode or elevated privileges")
	}
	root := t.TempDir()
	gatewaySpec := testSpec()
	options := MigrateOptions{ApplyOptions: ApplyOptions{
		ConfigRoot: filepath.Join(root, "etc"), TransactionRoot: filepath.Join(root, "transactions"),
		SystemdRoot: filepath.Join(root, "systemd"), NginxRoot: filepath.Join(root, "nginx"), DNSMasqRoot: filepath.Join(root, "dnsmasq"),
	}, LegacyBinaryRoot: filepath.Join(root, "sbin"), PanelAppRoot: filepath.Join(root, "apps"), NginxEnabledRoot: filepath.Join(root, "nginx-enabled")}
	legacyNginx := filepath.Join(options.NginxRoot, "omnirelay-omnipanel-"+gatewaySpec.RelayID+".conf")
	enabled := filepath.Join(options.NginxEnabledRoot, filepath.Base(legacyNginx))
	for _, directory := range []string{options.NginxRoot, options.NginxEnabledRoot} {
		if err := os.MkdirAll(directory, 0o700); err != nil {
			t.Fatal(err)
		}
	}
	if err := os.WriteFile(legacyNginx, []byte("legacy"), 0o600); err != nil {
		t.Fatal(err)
	}
	if err := os.Symlink(legacyNginx, enabled); err != nil {
		t.Fatal(err)
	}
	if _, err := Migrate(gatewaySpec, options); err != nil {
		t.Fatal(err)
	}
	if err := os.Remove(enabled); err != nil {
		t.Fatal(err)
	}
	if err := os.Symlink("/wrong/site", enabled); err != nil {
		t.Fatal(err)
	}
	if _, err := RollbackMigration(gatewaySpec.RelayID, options); err != nil {
		t.Fatal(err)
	}
	if target, err := os.Readlink(enabled); err != nil || target != legacyNginx {
		t.Fatalf("legacy enabled-site symlink was not restored: %q %v", target, err)
	}
}

func TestRollbackRestartUnitsIncludesOnlyAffectedSharedDaemons(t *testing.T) {
	root := t.TempDir()
	options := MigrateOptions{ApplyOptions: ApplyOptions{
		NginxRoot: filepath.Join(root, "nginx"), DNSMasqRoot: filepath.Join(root, "dnsmasq"),
	}, GlobalRoot: filepath.Join(root, "etc"), PanelAppRoot: filepath.Join(root, "apps"), NginxEnabledRoot: filepath.Join(root, "nginx-enabled")}
	units := rollbackRestartUnits([]string{
		filepath.Join(options.DNSMasqRoot, "omnirelay-ipsec-l2tp-test.conf"),
		filepath.Join(options.GlobalRoot, "ipsec.secrets"),
		filepath.Join(options.GlobalRoot, "ppp", "chap-secrets"),
		filepath.Join(root, "unrelated"),
	}, options)
	expected := []string{"dnsmasq.service", "strongswan-starter.service", "xl2tpd.service"}
	if len(units) != len(expected) {
		t.Fatalf("unexpected shared restart units: %+v", units)
	}
	for index := range expected {
		if units[index] != expected[index] {
			t.Fatalf("unexpected shared restart units: %+v", units)
		}
	}
}
