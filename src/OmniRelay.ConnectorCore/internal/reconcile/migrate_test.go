package reconcile

import (
	"os"
	"path/filepath"
	"testing"
	"time"
)

func TestMigrateSnapshotsLegacyStateBeforeApply(t *testing.T) {
	root := t.TempDir()
	gatewaySpec := testSpec()
	options := MigrateOptions{ApplyOptions: ApplyOptions{
		ConfigRoot: filepath.Join(root, "etc"), TransactionRoot: filepath.Join(root, "transactions"),
		SystemdRoot: filepath.Join(root, "systemd"), NginxRoot: filepath.Join(root, "nginx"), DNSMasqRoot: filepath.Join(root, "dnsmasq"),
	}, LegacyBinaryRoot: filepath.Join(root, "sbin")}
	legacyRelayFile := filepath.Join(options.ConfigRoot, "relays", gatewaySpec.RelayID, "gateway", "legacy-state.json")
	legacyBinary := filepath.Join(options.LegacyBinaryRoot, "omnirelay-gatewayctl-"+gatewaySpec.RelayID)
	legacyUnit := filepath.Join(options.SystemdRoot, "omnirelay-legacy-"+gatewaySpec.RelayID+".service")
	for _, path := range []string{legacyRelayFile, legacyBinary, legacyUnit} {
		if err := os.MkdirAll(filepath.Dir(path), 0o700); err != nil {
			t.Fatal(err)
		}
		if err := os.WriteFile(path, []byte("legacy"), 0o600); err != nil {
			t.Fatal(err)
		}
	}
	result, err := Migrate(gatewaySpec, options)
	if err != nil {
		t.Fatal(err)
	}
	if result.SnapshotFiles != 3 || !result.Apply.Changed {
		t.Fatalf("unexpected migration result: %+v", result)
	}
	if _, err := os.Stat(result.LegacySnapshotPath); err != nil {
		t.Fatalf("migration snapshot is missing: %v", err)
	}
	if content, err := os.ReadFile(legacyBinary); err != nil || string(content) != "legacy" {
		t.Fatalf("migration removed legacy state before acceptance: %q %v", content, err)
	}
}

func TestPruneMigrationBackupsKeepsRetentionWindow(t *testing.T) {
	root := t.TempDir()
	relayID := testSpec().RelayID
	now := time.Date(2026, 6, 4, 10, 0, 0, 0, time.UTC)
	backupRoot := filepath.Join(root, "migration-backups", relayID)
	old := filepath.Join(backupRoot, now.Add(-15*24*time.Hour).Format("20060102T150405.000000000Z"))
	recent := filepath.Join(backupRoot, now.Add(-13*24*time.Hour).Format("20060102T150405.000000000Z"))
	for _, path := range []string{old, recent} {
		if err := os.MkdirAll(path, 0o700); err != nil {
			t.Fatal(err)
		}
	}
	if err := pruneMigrationBackups(relayID, root, now, 14*24*time.Hour); err != nil {
		t.Fatal(err)
	}
	if _, err := os.Stat(old); !os.IsNotExist(err) {
		t.Fatal("expired migration backup was not pruned")
	}
	if _, err := os.Stat(recent); err != nil {
		t.Fatal("recent migration backup was pruned")
	}
}

func TestSnapshotLegacyStateIncludesIPSecGlobalFiles(t *testing.T) {
	root := t.TempDir()
	gatewaySpec := testSpec()
	gatewaySpec.Gateway.Protocol = "ipsec_l2tp_singbox"
	gatewaySpec.Gateway.PublicPort = 1701
	gatewaySpec.Gateway.ConnectorMode = "internal_tunnel"
	gatewaySpec.IPSecL2TP.Network = "10.39.0.0/24"
	gatewaySpec.IPSecL2TP.PreSharedKey = "a-strong-test-pre-shared-key"
	options := MigrateOptions{ApplyOptions: ApplyOptions{
		ConfigRoot: filepath.Join(root, "etc-omnirelay"), TransactionRoot: filepath.Join(root, "transactions"),
		SystemdRoot: filepath.Join(root, "systemd"), NginxRoot: filepath.Join(root, "nginx"), DNSMasqRoot: filepath.Join(root, "dnsmasq"),
	}, LegacyBinaryRoot: filepath.Join(root, "sbin"), GlobalRoot: filepath.Join(root, "etc")}
	for _, path := range []string{
		filepath.Join(options.GlobalRoot, "ipsec.conf"),
		filepath.Join(options.GlobalRoot, "ppp", "chap-secrets"),
	} {
		if err := os.MkdirAll(filepath.Dir(path), 0o700); err != nil {
			t.Fatal(err)
		}
		if err := os.WriteFile(path, []byte("legacy"), 0o600); err != nil {
			t.Fatal(err)
		}
	}
	snapshotRoot := filepath.Join(root, "snapshot")
	count, err := snapshotLegacyState(gatewaySpec, options, snapshotRoot)
	if err != nil {
		t.Fatal(err)
	}
	if count != 2 {
		t.Fatalf("expected two snapshotted global files, got %d", count)
	}
	for _, name := range []string{"ipsec.conf", "chap-secrets"} {
		matches, err := filepath.Glob(filepath.Join(snapshotRoot, "*-"+name))
		if err != nil || len(matches) != 1 {
			t.Fatalf("snapshot does not contain %s: %v %+v", name, err, matches)
		}
	}
}
