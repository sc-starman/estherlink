package singbox

import (
	"encoding/json"
	"strings"
	"testing"
)

func TestRenderAllSupportedProtocols(t *testing.T) {
	protocols := []string{
		"vless_plain_singbox",
		"vless_tls_singbox",
		"mixed_singbox",
		"socks_singbox",
		"http_singbox",
		"hysteria2_singbox",
		"trojan_singbox",
		"naive_singbox",
		"shadowsocks_singbox",
		"shadowtls_v3_shadowsocks_singbox",
	}
	for _, protocolID := range protocols {
		t.Run(protocolID, func(t *testing.T) {
			spec := completeRenderSpec(protocolID)
			first, err := Render(spec)
			if err != nil {
				t.Fatal(err)
			}
			second, err := Render(spec)
			if err != nil {
				t.Fatal(err)
			}
			if string(first) != string(second) {
				t.Fatal("rendering is not deterministic")
			}
			var config map[string]any
			if err := json.Unmarshal(first, &config); err != nil {
				t.Fatal(err)
			}
			if len(config["inbounds"].([]any)) == 0 {
				t.Fatal("rendered config has no inbounds")
			}
		})
	}
}

func TestRenderPlainVLESSForcesTLSAndFlowOff(t *testing.T) {
	spec := completeRenderSpec("vless_plain_singbox")
	spec.TLS = TLS{Enabled: true, ServerName: "relay.example.com", CertFile: "/tmp/cert", KeyFile: "/tmp/key"}
	spec.VLESSFlow = "xtls-rprx-vision"
	content, err := Render(spec)
	if err != nil {
		t.Fatal(err)
	}
	var config map[string]any
	if err := json.Unmarshal(content, &config); err != nil {
		t.Fatal(err)
	}
	inbound := config["inbounds"].([]any)[0].(map[string]any)
	if inbound["tls"].(map[string]any)["enabled"] != false {
		t.Fatal("plain VLESS must force TLS off")
	}
	user := inbound["users"].([]any)[0].(map[string]any)
	if _, exists := user["flow"]; exists {
		t.Fatal("plain VLESS must not render VLESS flow")
	}
}

func TestRenderVLESSOnlyAddsFlowWhenTLSEnabled(t *testing.T) {
	spec := completeRenderSpec("vless_tls_singbox")
	spec.TLS.Enabled = false
	spec.TLS.CertFile = ""
	spec.TLS.KeyFile = ""
	content, err := Render(spec)
	if err != nil {
		t.Fatal(err)
	}
	var config map[string]any
	if err := json.Unmarshal(content, &config); err != nil {
		t.Fatal(err)
	}
	inbound := config["inbounds"].([]any)[0].(map[string]any)
	user := inbound["users"].([]any)[0].(map[string]any)
	if _, exists := user["flow"]; exists {
		t.Fatal("VLESS flow must not be rendered when TLS is disabled")
	}
}

func TestRenderRejectsMissingPasswordClientSecret(t *testing.T) {
	spec := completeRenderSpec("trojan_singbox")
	spec.Users[0].Secret = ""
	if _, err := Render(spec); err == nil {
		t.Fatal("expected missing per-client secret to be rejected")
	}
}

func TestValidateRenderedMixedConfig(t *testing.T) {
	content, err := Render(completeRenderSpec("mixed_singbox"))
	if err != nil {
		t.Fatal(err)
	}
	if err := Validate(content); err != nil {
		t.Fatal(err)
	}
}

func TestRenderInternalTunnelProducesValidatedRedirectConfig(t *testing.T) {
	content, err := RenderInternalTunnel(15001, 46087, DNS{})
	if err != nil {
		t.Fatal(err)
	}
	if err := Validate(content); err != nil {
		t.Fatalf("internal tunnel config did not validate: %v\n%s", err, content)
	}
	if !strings.Contains(string(content), `"type": "redirect"`) || !strings.Contains(string(content), `"listen_port": 46087`) {
		t.Fatalf("unexpected internal tunnel config: %s", content)
	}
	if !strings.Contains(string(content), `"tag": "dns-in"`) || !strings.Contains(string(content), `"tag": "dns-out"`) {
		t.Fatalf("internal tunnel config is missing DNS routing: %s", content)
	}
}

func TestInternalRedirectPortIsStableAndValid(t *testing.T) {
	first := InternalRedirectPort("e4ccc282a1004b62ad2cda5770d6e32d")
	second := InternalRedirectPort("e4ccc282a1004b62ad2cda5770d6e32d")
	if first != second || first < 30000 || first >= 50000 {
		t.Fatalf("unexpected redirect port: %d / %d", first, second)
	}
}

func completeRenderSpec(protocolID string) RenderSpec {
	return RenderSpec{
		ProtocolID:  protocolID,
		PublicPort:  30443,
		BackendPort: 15001,
		Users: []User{{
			ID:     "dbe0861d-2aa1-4b49-bcaa-56c9c8570d9c",
			Secret: "client-secret",
		}},
		Proxy: Credential{Username: "proxy-user", Password: "proxy-secret"},
		TLS: TLS{
			Enabled: false, ServerName: "relay.example.com",
		},
		VLESSFlow: "xtls-rprx-vision",
		Hysteria2: Hysteria2{
			UpMbps: 100, DownMbps: 100, ObfsPassword: "obfs",
		},
		Naive:                     Naive{Network: "tcp", QUICCC: "bbr"},
		ShadowsocksServerPassword: "server-secret",
		ShadowTLS: ShadowTLS{
			CamouflageServer: "www.cloudflare.com:443",
			StrictMode:       true,
		},
	}
}
