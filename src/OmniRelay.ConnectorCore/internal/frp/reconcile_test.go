package frp

import (
	"os"
	"path/filepath"
	"strings"
	"testing"

	"github.com/omnirelay/connector-core/internal/host"
	"github.com/omnirelay/connector-core/internal/spec"
)

func TestReconcileWritesSharedConfigAndRejectsConflicts(t *testing.T) {
	root := t.TempDir()
	options := ReconcileOptions{
		ConfigRoot: filepath.Join(root, "etc"), SystemdRoot: filepath.Join(root, "systemd"),
		StateRoot: filepath.Join(root, "state"), ConnectorBinary: "/connector-core",
	}
	writeSpec(t, options.ConfigRoot, gatewaySpec("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", 7000, "0123456789abcdef0123456789abcdef"))
	first, err := Reconcile(options)
	if err != nil {
		t.Fatal(err)
	}
	if !first.Active || !first.Changed {
		t.Fatalf("unexpected first reconcile: %+v", first)
	}
	content, err := os.ReadFile(first.ConfigPath)
	if err != nil {
		t.Fatal(err)
	}
	if strings.Contains(string(content), "relay") || !strings.Contains(string(content), "bindPort = 7000") {
		t.Fatalf("unexpected FRPS config: %s", content)
	}
	writeSpec(t, options.ConfigRoot, gatewaySpec("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", 7001, "fedcba9876543210fedcba9876543210"))
	if _, err := Reconcile(options); err == nil || !strings.Contains(err.Error(), "conflicts") {
		t.Fatalf("expected conflict, got %v", err)
	}
}

func TestReconcileRemovesGlobalStateWhenNoRemoteSpecsRemain(t *testing.T) {
	root := t.TempDir()
	options := ReconcileOptions{ConfigRoot: filepath.Join(root, "etc"), SystemdRoot: filepath.Join(root, "systemd")}
	writeSpec(t, options.ConfigRoot, gatewaySpec("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", 7000, "0123456789abcdef0123456789abcdef"))
	first, err := Reconcile(options)
	if err != nil {
		t.Fatal(err)
	}
	if err := os.Remove(filepath.Join(options.ConfigRoot, "relays", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "gateway", "spec.json")); err != nil {
		t.Fatal(err)
	}
	result, err := Reconcile(options)
	if err != nil {
		t.Fatal(err)
	}
	if result.Active || len(result.RemovedPaths) != 2 {
		t.Fatalf("unexpected cleanup result: %+v", result)
	}
	if _, err := os.Stat(first.ConfigPath); !os.IsNotExist(err) {
		t.Fatalf("FRPS config still exists: %v", err)
	}
}

func gatewaySpec(relayID string, port int, token string) spec.GatewaySpec {
	return spec.GatewaySpec{
		APIVersion: spec.APIVersion, Kind: spec.Kind, RelayID: relayID,
		Gateway: spec.Gateway{Type: "remote", Protocol: "vless_tls_singbox", PublicPort: 443, ConnectorMode: "full_tunnel"},
		Tunnel:  spec.TunnelSpec{BackendPort: 15000, FRPServerPort: port, FRPAuthToken: token},
	}
}

func writeSpec(t *testing.T, root string, gatewaySpec spec.GatewaySpec) {
	t.Helper()
	gatewaySpec.ApplyDefaults()
	content, err := gatewaySpec.CanonicalJSON()
	if err != nil {
		t.Fatal(err)
	}
	path := filepath.Join(root, "relays", gatewaySpec.RelayID, "gateway", "spec.json")
	if _, err := host.WriteFileAtomic(path, append(content, '\n'), 0o600); err != nil {
		t.Fatal(err)
	}
}
