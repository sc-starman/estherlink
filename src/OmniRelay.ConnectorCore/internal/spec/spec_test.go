package spec

import (
	"strings"
	"testing"
)

const validSpec = `{
  "apiVersion": "omnirelay.io/v1alpha1",
  "kind": "Gateway",
  "relayId": "e4ccc282a1004b62ad2cda5770d6e32d",
  "release": {"channel": "stable", "connectorCoreVersion": "1.0.0"},
  "gateway": {"type": "remote", "protocol": "vless_tls_singbox", "publicPort": 443, "connectorMode": "full_tunnel"},
  "tunnel": {"frpServerPort": 7000, "frpAuthToken": "0123456789abcdef0123456789abcdef"},
  "panel": {"port": 3054, "publicHost": "panel.example.com"}
}`

func TestParseValidSpec(t *testing.T) {
	value, err := Parse([]byte(validSpec))
	if err != nil {
		t.Fatalf("Parse() error = %v", err)
	}
	if value.RelayID != "e4ccc282a1004b62ad2cda5770d6e32d" {
		t.Fatalf("unexpected relay id: %s", value.RelayID)
	}
}

func TestParseRejectsUnknownField(t *testing.T) {
	content := strings.Replace(validSpec, `"kind": "Gateway",`, `"kind": "Gateway", "unexpected": true,`, 1)
	_, err := Parse([]byte(content))
	if err == nil || !strings.Contains(err.Error(), "unknown field") {
		t.Fatalf("expected unknown field error, got %v", err)
	}
}

func TestParseRejectsUnsafeRelayID(t *testing.T) {
	content := strings.Replace(validSpec, "e4ccc282a1004b62ad2cda5770d6e32d", "../../unsafe", 1)
	_, err := Parse([]byte(content))
	if err == nil || !strings.Contains(err.Error(), "relayId") {
		t.Fatalf("expected relay id error, got %v", err)
	}
}

func TestParseRejectsPortConflict(t *testing.T) {
	content := strings.Replace(validSpec, `"port": 3054`, `"port": 443`, 1)
	_, err := Parse([]byte(content))
	if err == nil || !strings.Contains(err.Error(), "conflicts") {
		t.Fatalf("expected conflict error, got %v", err)
	}
}

func TestParseAppliesTunnelProbeDefaults(t *testing.T) {
	value, err := Parse([]byte(validSpec))
	if err != nil {
		t.Fatal(err)
	}
	if value.Tunnel.BackendHost != "127.0.0.1" || value.Tunnel.BackendPort != 15000 || len(value.Tunnel.ProbeURLs) != 3 {
		t.Fatalf("unexpected tunnel defaults: %+v", value.Tunnel)
	}
}

func TestParseRejectsInvalidTunnelProbeURL(t *testing.T) {
	content := strings.Replace(validSpec, `"panel": {"port": 3054, "publicHost": "panel.example.com"}`, `"tunnel": {"probeUrls": ["file:///etc/passwd"]}, "panel": {"port": 3054, "publicHost": "panel.example.com"}`, 1)
	_, err := Parse([]byte(content))
	if err == nil || !strings.Contains(err.Error(), "probeUrls") {
		t.Fatalf("expected probe URL error, got %v", err)
	}
}

func TestParseAppliesOpenVPNNetworkDefault(t *testing.T) {
	content := strings.Replace(validSpec, `"protocol": "vless_tls_singbox"`, `"protocol": "openvpn_tcp_singbox"`, 1)
	content = strings.Replace(content, `"panel": {"port": 3054, "publicHost": "panel.example.com"}`, `"openvpn": {"publicHost": "vpn.example.com"}, "panel": {"port": 3054, "publicHost": "panel.example.com"}`, 1)
	value, err := Parse([]byte(content))
	if err != nil {
		t.Fatal(err)
	}
	if value.OpenVPN.Network != "10.29.0.0/24" {
		t.Fatalf("unexpected OpenVPN network default: %q", value.OpenVPN.Network)
	}
}

func TestParseRejectsUnsafeOpenVPNNetwork(t *testing.T) {
	content := strings.Replace(validSpec, `"protocol": "vless_tls_singbox"`, `"protocol": "openvpn_tcp_singbox"`, 1)
	content = strings.Replace(content, `"panel": {"port": 3054, "publicHost": "panel.example.com"}`, `"openvpn": {"network": "10.29.0.0/31", "publicHost": "vpn.example.com"}, "panel": {"port": 3054, "publicHost": "panel.example.com"}`, 1)
	_, err := Parse([]byte(content))
	if err == nil || !strings.Contains(err.Error(), "openvpn.network") {
		t.Fatalf("expected OpenVPN network error, got %v", err)
	}
}

func TestParseRejectsPartialOpenVPNSharedAssets(t *testing.T) {
	content := strings.Replace(validSpec, `"protocol": "vless_tls_singbox"`, `"protocol": "openvpn_tcp_singbox"`, 1)
	content = strings.Replace(content, `"panel": {"port": 3054, "publicHost": "panel.example.com"}`, `"openvpn": {"publicHost": "vpn.example.com", "sharedCaCertFile": "/tmp/ca.crt"}, "panel": {"port": 3054, "publicHost": "panel.example.com"}`, 1)
	_, err := Parse([]byte(content))
	if err == nil || !strings.Contains(err.Error(), "shared asset paths") {
		t.Fatalf("expected partial OpenVPN asset error, got %v", err)
	}
}

func TestParseRequiresStrongIPSecPreSharedKey(t *testing.T) {
	content := strings.Replace(validSpec, `"protocol": "vless_tls_singbox"`, `"protocol": "ipsec_l2tp_singbox"`, 1)
	_, err := Parse([]byte(content))
	if err == nil || !strings.Contains(err.Error(), "preSharedKey") {
		t.Fatalf("expected IPsec PSK validation error, got %v", err)
	}
}
