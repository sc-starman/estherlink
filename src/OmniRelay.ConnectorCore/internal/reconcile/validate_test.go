package reconcile

import (
	"os"
	"path/filepath"
	"strings"
	"testing"
)

func TestValidateRuntimeAssetsRejectsMissingOpenVPNPKI(t *testing.T) {
	gatewaySpec := testSpec()
	gatewaySpec.Gateway.Protocol = "openvpn_tcp_singbox"
	err := ValidateRuntimeAssets(gatewaySpec, t.TempDir())
	if err == nil || !strings.Contains(err.Error(), "ca.crt") {
		t.Fatalf("expected missing OpenVPN PKI error, got %v", err)
	}
}

func TestValidateRuntimeAssetsAcceptsManagedOpenVPNPKI(t *testing.T) {
	root := t.TempDir()
	gatewaySpec := testSpec()
	gatewaySpec.Gateway.Protocol = "openvpn_tcp_singbox"
	openVPNRoot := filepath.Join(root, "relays", gatewaySpec.RelayID, "gateway", "openvpn")
	for _, name := range []string{"ca.crt", "client-shared.crt", "client-shared.key", "ta.key"} {
		path := filepath.Join(openVPNRoot, name)
		if err := os.MkdirAll(filepath.Dir(path), 0o700); err != nil {
			t.Fatal(err)
		}
		if err := os.WriteFile(path, []byte("asset"), 0o600); err != nil {
			t.Fatal(err)
		}
	}
	if err := ValidateRuntimeAssets(gatewaySpec, root); err != nil {
		t.Fatal(err)
	}
}
