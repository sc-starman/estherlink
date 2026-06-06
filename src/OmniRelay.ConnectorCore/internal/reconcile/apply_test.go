package reconcile

import (
	"errors"
	"os"
	"path/filepath"
	"runtime"
	"strings"
	"testing"
	"time"

	panelconfig "github.com/omnirelay/connector-core/internal/panel"
	"github.com/omnirelay/connector-core/internal/spec"
)

func TestApplyPersistsSpecAndIsIdempotent(t *testing.T) {
	root := t.TempDir()
	gatewaySpec := testSpec()
	options := ApplyOptions{
		ConfigRoot:      filepath.Join(root, "etc"),
		TransactionRoot: filepath.Join(root, "transactions"),
		SystemdRoot:     filepath.Join(root, "systemd"),
		Now:             func() time.Time { return time.Date(2026, 6, 4, 10, 0, 0, 0, time.UTC) },
	}
	first, err := Apply(gatewaySpec, options)
	if err != nil {
		t.Fatalf("first Apply() error = %v", err)
	}
	if !first.Changed {
		t.Fatal("first apply should change spec")
	}
	second, err := Apply(gatewaySpec, options)
	if err != nil {
		t.Fatalf("second Apply() error = %v", err)
	}
	if second.Changed {
		t.Fatal("second apply should be idempotent")
	}
	info, err := os.Stat(first.SpecPath)
	if err != nil {
		t.Fatal(err)
	}
	if runtime.GOOS != "windows" && info.Mode().Perm() != 0o600 {
		t.Fatalf("spec mode = %o", info.Mode().Perm())
	}
	if len(first.ChangedFiles) != 10 {
		t.Fatalf("expected spec, manifest, connector config, metadata, and six systemd units to change, got %d", len(first.ChangedFiles))
	}
}

func TestApplyRejectsConcurrentOperation(t *testing.T) {
	root := t.TempDir()
	gatewaySpec := testSpec()
	transactionRoot := filepath.Join(root, "transactions")
	lockPath := filepath.Join(transactionRoot, "locks", gatewaySpec.RelayID+".lock")
	if err := os.MkdirAll(filepath.Dir(lockPath), 0o700); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(lockPath, []byte("busy"), 0o600); err != nil {
		t.Fatal(err)
	}
	_, err := Apply(gatewaySpec, ApplyOptions{ConfigRoot: filepath.Join(root, "etc"), TransactionRoot: transactionRoot})
	if err == nil || !strings.Contains(err.Error(), "another gateway operation") {
		t.Fatalf("expected lock conflict, got %v", err)
	}
}

func TestApplyRollsBackPersistedSpecWhenValidationFails(t *testing.T) {
	root := t.TempDir()
	gatewaySpec := testSpec()
	options := ApplyOptions{
		ConfigRoot:      filepath.Join(root, "etc"),
		TransactionRoot: filepath.Join(root, "transactions"),
		SystemdRoot:     filepath.Join(root, "systemd"),
	}
	first, err := Apply(gatewaySpec, options)
	if err != nil {
		t.Fatal(err)
	}
	original, err := os.ReadFile(first.SpecPath)
	if err != nil {
		t.Fatal(err)
	}
	gatewaySpec.Gateway.PublicPort = 8443
	options.ValidatePersisted = func(string) error { return errors.New("injected validation failure") }
	if _, err := Apply(gatewaySpec, options); err == nil {
		t.Fatal("expected apply failure")
	}
	restored, err := os.ReadFile(first.SpecPath)
	if err != nil {
		t.Fatal(err)
	}
	if string(restored) != string(original) {
		t.Fatal("spec was not restored after failed apply")
	}
}

func TestApplyRollsBackAllManagedFilesWhenPostWriteValidationFails(t *testing.T) {
	root := t.TempDir()
	gatewaySpec := testSpec()
	options := ApplyOptions{
		ConfigRoot: filepath.Join(root, "etc"), TransactionRoot: filepath.Join(root, "transactions"),
		SystemdRoot: filepath.Join(root, "systemd"), DNSMasqRoot: filepath.Join(root, "dnsmasq"),
	}
	first, err := Apply(gatewaySpec, options)
	if err != nil {
		t.Fatal(err)
	}
	unitPath := filepath.Join(options.SystemdRoot, "omnirelay-connector-"+gatewaySpec.RelayID+".service")
	original, err := os.ReadFile(unitPath)
	if err != nil {
		t.Fatal(err)
	}
	gatewaySpec.Gateway.PublicPort = 8443
	rollbackCalls := 0
	options.AfterWrite = func() error { return errors.New("injected daemon-reload failure") }
	options.AfterRollback = func() error {
		rollbackCalls++
		return nil
	}
	if _, err := Apply(gatewaySpec, options); err == nil {
		t.Fatal("expected apply failure")
	}
	if rollbackCalls != 1 {
		t.Fatalf("expected one rollback post-action, got %d", rollbackCalls)
	}
	restored, err := os.ReadFile(unitPath)
	if err != nil {
		t.Fatal(err)
	}
	if string(restored) != string(original) {
		t.Fatalf("systemd unit was not restored after failed apply; first transaction=%s", first.TransactionID)
	}
}

func TestApplyOpenVPNRendersNativeManagedStateIdempotently(t *testing.T) {
	root := t.TempDir()
	gatewaySpec := testSpec()
	gatewaySpec.Gateway.Protocol = "openvpn_tcp_singbox"
	gatewaySpec.Gateway.ConnectorMode = "internal_tunnel"
	gatewaySpec.OpenVPN.Network = "10.29.0.0/24"
	gatewaySpec.OpenVPN.PublicHost = "vpn.example.com"
	options := ApplyOptions{
		ConfigRoot: filepath.Join(root, "etc"), TransactionRoot: filepath.Join(root, "transactions"),
		SystemdRoot: filepath.Join(root, "systemd"), DNSMasqRoot: filepath.Join(root, "dnsmasq"), PPPHookRoot: filepath.Join(root, "ppp"),
	}
	first, err := Apply(gatewaySpec, options)
	if err != nil {
		t.Fatal(err)
	}
	if len(first.ChangedFiles) != 18 {
		t.Fatalf("expected 18 OpenVPN managed files, got %d: %+v", len(first.ChangedFiles), first.ChangedFiles)
	}
	gatewayRoot := filepath.Join(options.ConfigRoot, "relays", gatewaySpec.RelayID, "gateway")
	for _, path := range []string{
		filepath.Join(gatewayRoot, "connector", "config.json"),
		filepath.Join(gatewayRoot, "openvpn", "server.conf"),
		filepath.Join(gatewayRoot, "openvpn", "runtime.json"),
		filepath.Join(options.SystemdRoot, "omnirelay-openvpn-"+gatewaySpec.RelayID+".service"),
		filepath.Join(options.SystemdRoot, "omnirelay-openvpn-enforce-"+gatewaySpec.RelayID+".timer"),
	} {
		if _, err := os.Stat(path); err != nil {
			t.Fatalf("managed OpenVPN path missing: %s: %v", path, err)
		}
	}
	serverConfig, err := os.ReadFile(filepath.Join(gatewayRoot, "openvpn", "server.conf"))
	if err != nil {
		t.Fatal(err)
	}
	if strings.Contains(string(serverConfig), "/bin/bash") || strings.Contains(string(serverConfig), "gatewayctl") {
		t.Fatalf("OpenVPN config contains legacy command path: %s", serverConfig)
	}
	if !strings.Contains(string(serverConfig), "connector-core openvpn authenticate --relay-id "+gatewaySpec.RelayID) {
		t.Fatalf("OpenVPN config does not use native authentication: %s", serverConfig)
	}
	second, err := Apply(gatewaySpec, options)
	if err != nil {
		t.Fatal(err)
	}
	if second.Changed {
		t.Fatalf("second OpenVPN apply should be idempotent: %+v", second.ChangedFiles)
	}
}

func TestApplyIPSecRendersRelayOwnedDesiredStateWithoutGlobalMutation(t *testing.T) {
	root := t.TempDir()
	gatewaySpec := testSpec()
	gatewaySpec.Gateway.Protocol = "ipsec_l2tp_singbox"
	gatewaySpec.Gateway.PublicPort = 1701
	gatewaySpec.Gateway.ConnectorMode = "internal_tunnel"
	gatewaySpec.IPSecL2TP.Network = "10.39.0.0/24"
	gatewaySpec.IPSecL2TP.PreSharedKey = "a-strong-test-pre-shared-key"
	options := ApplyOptions{
		ConfigRoot: filepath.Join(root, "etc"), TransactionRoot: filepath.Join(root, "transactions"),
		SystemdRoot: filepath.Join(root, "systemd"), DNSMasqRoot: filepath.Join(root, "dnsmasq"), PPPHookRoot: filepath.Join(root, "ppp"),
	}
	result, err := Apply(gatewaySpec, options)
	if err != nil {
		t.Fatal(err)
	}
	if len(result.ChangedFiles) != 22 {
		t.Fatalf("expected 22 IPsec/L2TP managed files, got %d: %+v", len(result.ChangedFiles), result.ChangedFiles)
	}
	ipsecRoot := filepath.Join(options.ConfigRoot, "relays", gatewaySpec.RelayID, "gateway", "ipsec-l2tp")
	for _, name := range []string{"ipsec.conf", "ipsec.secrets", "xl2tpd.conf", "ppp-options", "runtime.json"} {
		if _, err := os.Stat(filepath.Join(ipsecRoot, name)); err != nil {
			t.Fatalf("managed IPsec path missing: %s: %v", name, err)
		}
	}
	if _, err := os.Stat(filepath.Join(root, "etc", "ipsec.secrets")); !errors.Is(err, os.ErrNotExist) {
		t.Fatalf("apply must not mutate global ipsec.secrets before ownership cutover: %v", err)
	}
	secrets, err := os.ReadFile(filepath.Join(ipsecRoot, "ipsec.secrets"))
	if err != nil {
		t.Fatal(err)
	}
	if !strings.Contains(string(secrets), "a-strong-test-pre-shared-key") {
		t.Fatal("relay-owned IPsec secrets file was not rendered")
	}
}

func TestApplyPanelRendersSecretEnvironmentAndNginxIdempotently(t *testing.T) {
	root := t.TempDir()
	gatewaySpec := testSpec()
	gatewaySpec.Panel = spec.PanelSpec{
		Port: 3054, Username: "admin", Password: "panel-secret", Domain: "panel.example.com",
		PublicHost: "panel.example.com", DomainOnly: true,
	}
	options := ApplyOptions{
		ConfigRoot: filepath.Join(root, "etc"), TransactionRoot: filepath.Join(root, "transactions"),
		SystemdRoot: filepath.Join(root, "systemd"), NginxRoot: filepath.Join(root, "nginx"), DNSMasqRoot: filepath.Join(root, "dnsmasq"),
		SudoersRoot: filepath.Join(root, "sudoers"),
	}
	first, err := Apply(gatewaySpec, options)
	if err != nil {
		t.Fatal(err)
	}
	if len(first.ChangedFiles) != 14 {
		t.Fatalf("expected 14 panel-enabled managed files, got %d: %+v", len(first.ChangedFiles), first.ChangedFiles)
	}
	panelEnv := filepath.Join(options.ConfigRoot, "relays", gatewaySpec.RelayID, "gateway", "panel", "panel.env")
	environment, err := os.ReadFile(panelEnv)
	if err != nil {
		t.Fatal(err)
	}
	if !strings.Contains(string(environment), `OMNIPANEL_AUTH_PASSWORD="panel-secret"`) {
		t.Fatal("panel environment did not contain configured credentials")
	}
	if runtime.GOOS != "windows" {
		info, err := os.Stat(panelEnv)
		if err != nil {
			t.Fatal(err)
		}
		if info.Mode().Perm() != 0o600 {
			t.Fatalf("panel environment mode = %o", info.Mode().Perm())
		}
	}
	nginx, err := os.ReadFile(filepath.Join(options.NginxRoot, "omnirelay-omnipanel-"+gatewaySpec.RelayID+".conf"))
	if err != nil {
		t.Fatal(err)
	}
	if strings.Contains(string(nginx), "panel-secret") || !strings.Contains(string(nginx), "listen 3054;") {
		t.Fatalf("unexpected nginx content: %s", nginx)
	}
	second, err := Apply(gatewaySpec, options)
	if err != nil {
		t.Fatal(err)
	}
	if second.Changed {
		t.Fatalf("second panel apply should be idempotent: %+v", second.ChangedFiles)
	}
}

func TestApplyPersistsManagedPanelTLSPaths(t *testing.T) {
	root := t.TempDir()
	certSource := filepath.Join(root, "upload.crt")
	keySource := filepath.Join(root, "upload.key")
	if err := os.WriteFile(certSource, []byte("-----BEGIN CERTIFICATE-----\ntest\n-----END CERTIFICATE-----\n"), 0o600); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(keySource, []byte("-----BEGIN PRIVATE KEY-----\ntest\n-----END PRIVATE KEY-----\n"), 0o600); err != nil {
		t.Fatal(err)
	}
	gatewaySpec := testSpec()
	gatewaySpec.Panel = spec.PanelSpec{
		Port: 3054, Username: "admin", Password: "panel-secret", PublicHost: "panel.example.com",
		TLSEnabled: true, TLSMode: "uploaded", CertFile: certSource, KeyFile: keySource,
	}
	options := ApplyOptions{
		ConfigRoot: filepath.Join(root, "etc"), TransactionRoot: filepath.Join(root, "transactions"),
		SystemdRoot: filepath.Join(root, "systemd"), NginxRoot: filepath.Join(root, "nginx"), DNSMasqRoot: filepath.Join(root, "dnsmasq"),
		SudoersRoot: filepath.Join(root, "sudoers"),
	}
	result, err := Apply(gatewaySpec, options)
	if err != nil {
		t.Fatal(err)
	}
	persisted, err := spec.LoadFile(result.SpecPath)
	if err != nil {
		t.Fatal(err)
	}
	expectedCert, expectedKey := panelconfig.ManagedTLSPaths(filepath.Join(filepath.Dir(result.SpecPath), "panel"))
	if persisted.Panel.CertFile != expectedCert || persisted.Panel.KeyFile != expectedKey {
		t.Fatalf("persisted panel TLS paths are not managed: cert=%q key=%q", persisted.Panel.CertFile, persisted.Panel.KeyFile)
	}
}

func TestApplyProtocolChangeRemovesOnlyStaleManagedFiles(t *testing.T) {
	root := t.TempDir()
	options := ApplyOptions{
		ConfigRoot: filepath.Join(root, "etc"), TransactionRoot: filepath.Join(root, "transactions"),
		SystemdRoot: filepath.Join(root, "systemd"), NginxRoot: filepath.Join(root, "nginx"), DNSMasqRoot: filepath.Join(root, "dnsmasq"),
	}
	openVPNSpec := testSpec()
	openVPNSpec.Gateway.Protocol = "openvpn_tcp_singbox"
	openVPNSpec.Gateway.ConnectorMode = "internal_tunnel"
	openVPNSpec.OpenVPN.Network = "10.29.0.0/24"
	openVPNSpec.OpenVPN.PublicHost = "vpn.example.com"
	if _, err := Apply(openVPNSpec, options); err != nil {
		t.Fatal(err)
	}
	unrelated := filepath.Join(options.SystemdRoot, "administrator.service")
	if err := os.WriteFile(unrelated, []byte("keep"), 0o644); err != nil {
		t.Fatal(err)
	}
	singBoxSpec := testSpec()
	result, err := Apply(singBoxSpec, options)
	if err != nil {
		t.Fatal(err)
	}
	if len(result.DeletedFiles) != 8 {
		t.Fatalf("expected OpenVPN server/runtime, dnsmasq config, and five units to be deleted, got %+v", result.DeletedFiles)
	}
	for _, path := range result.DeletedFiles {
		if _, err := os.Stat(path); !errors.Is(err, os.ErrNotExist) {
			t.Fatalf("stale managed path still exists: %s: %v", path, err)
		}
	}
	if content, err := os.ReadFile(unrelated); err != nil || string(content) != "keep" {
		t.Fatalf("unrelated administrator file changed: %q, %v", content, err)
	}
}

func TestApplyRejectsTamperedManagedManifestPath(t *testing.T) {
	root := t.TempDir()
	options := ApplyOptions{
		ConfigRoot: filepath.Join(root, "etc"), TransactionRoot: filepath.Join(root, "transactions"),
		SystemdRoot: filepath.Join(root, "systemd"), NginxRoot: filepath.Join(root, "nginx"),
	}
	gatewaySpec := testSpec()
	if _, err := Apply(gatewaySpec, options); err != nil {
		t.Fatal(err)
	}
	manifestPath := filepath.Join(options.ConfigRoot, "relays", gatewaySpec.RelayID, "gateway", "managed-files.json")
	content := `{"apiVersion":"omnirelay.io/managed-files/v1","relayId":"` + gatewaySpec.RelayID + `","paths":["` + filepath.ToSlash(filepath.Join(root, "..", "unsafe")) + `"]}`
	if err := os.WriteFile(manifestPath, []byte(content), 0o600); err != nil {
		t.Fatal(err)
	}
	if _, err := Apply(gatewaySpec, options); err == nil || !strings.Contains(err.Error(), "unsafe path") {
		t.Fatalf("expected unsafe manifest rejection, got %v", err)
	}
}

func testSpec() spec.GatewaySpec {
	return spec.GatewaySpec{
		APIVersion: spec.APIVersion,
		Kind:       spec.Kind,
		RelayID:    "e4ccc282a1004b62ad2cda5770d6e32d",
		Release:    spec.ReleaseSpec{Channel: "stable"},
		Gateway:    spec.Gateway{Type: "remote", Protocol: "vless_tls_singbox", PublicPort: 443, ConnectorMode: "full_tunnel"},
		Tunnel:     spec.TunnelSpec{FRPServerPort: 7000, FRPAuthToken: "0123456789abcdef0123456789abcdef"},
	}
}
