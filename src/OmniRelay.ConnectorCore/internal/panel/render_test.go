package panel

import (
	"strings"
	"testing"

	"github.com/omnirelay/connector-core/internal/spec"
)

func TestRenderPanelEnvironmentAndNginx(t *testing.T) {
	gatewaySpec := spec.GatewaySpec{
		RelayID: "e4ccc282a1004b62ad2cda5770d6e32d",
		Gateway: spec.Gateway{Protocol: "vless_tls_singbox"},
		Panel: spec.PanelSpec{
			Port: 3054, Username: "admin", Password: `sec"ret`, Domain: "panel.example.com",
			PublicHost: "panel.example.com", DomainOnly: true, TLSEnabled: true, CertFile: "/cert.pem", KeyFile: "/key.pem",
		},
	}
	environment, err := RenderEnvironment(gatewaySpec, Options{})
	if err != nil {
		t.Fatal(err)
	}
	nginx, err := RenderNginx(gatewaySpec, Options{})
	if err != nil {
		t.Fatal(err)
	}
	for _, pair := range []struct {
		content []byte
		value   string
	}{
		{environment, `OMNIPANEL_AUTH_PASSWORD="sec\"ret"`},
		{environment, `SINGBOX_RELOAD_COMMAND="sudo /usr/local/bin/connector-core clients sync --relay-id e4ccc282a1004b62ad2cda5770d6e32d --json"`},
		{nginx, "listen 3054 ssl;"},
		{nginx, "server_name panel.example.com;"},
		{nginx, "proxy_pass http://127.0.0.1:"},
	} {
		if !strings.Contains(string(pair.content), pair.value) {
			t.Fatalf("rendered panel content missing %q: %s", pair.value, pair.content)
		}
	}
}

func TestRenderNginxRejectsDomainOnlyWithoutDomain(t *testing.T) {
	_, err := RenderNginx(spec.GatewaySpec{Panel: spec.PanelSpec{Port: 3054, DomainOnly: true}}, Options{})
	if err == nil {
		t.Fatal("expected domain-only validation error")
	}
}
