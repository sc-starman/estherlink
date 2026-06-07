package systemd

import (
	"reflect"
	"strings"
	"testing"

	"github.com/omnirelay/connector-core/internal/spec"
)

func TestRenderGatewayUnitsIsDeterministicAndUsesNativeCommands(t *testing.T) {
	gatewaySpec := spec.GatewaySpec{RelayID: "e4ccc282a1004b62ad2cda5770d6e32d"}
	first := RenderGatewayUnits(gatewaySpec, Options{})
	second := RenderGatewayUnits(gatewaySpec, Options{})
	if !reflect.DeepEqual(first, second) {
		t.Fatal("systemd rendering is not deterministic")
	}
	if len(first) != 6 {
		t.Fatalf("unexpected unit count: %d", len(first))
	}
	combined := ""
	for _, unit := range first {
		combined += unit.Content
	}
	for _, expected := range []string{
		"connector-core accounting sync --relay-id e4ccc282a1004b62ad2cda5770d6e32d",
		"connector-core clock sync --relay-id e4ccc282a1004b62ad2cda5770d6e32d",
		"omnirelay-connector-e4ccc282a1004b62ad2cda5770d6e32d.service",
	} {
		if !strings.Contains(combined, expected) {
			t.Fatalf("rendered units missing %q", expected)
		}
	}
	if strings.Contains(combined, "gatewayctl") || strings.Contains(combined, "tunnelctl") || strings.Contains(combined, "/bin/bash") {
		t.Fatal("rendered units contain a legacy command path")
	}
}

func TestRenderGatewayUnitsAddsOpenVPNDaemonAndNativeEnforcer(t *testing.T) {
	gatewaySpec := spec.GatewaySpec{
		RelayID: "e4ccc282a1004b62ad2cda5770d6e32d",
		Gateway: spec.Gateway{Protocol: "openvpn_tcp_singbox"},
	}
	units := RenderGatewayUnits(gatewaySpec, Options{})
	if len(units) != 11 {
		t.Fatalf("unexpected OpenVPN unit count: %d", len(units))
	}
	combined := ""
	for _, unit := range units {
		combined += unit.Content
	}
	for _, expected := range []string{
		"/usr/sbin/openvpn --config /etc/omnirelay/relays/e4ccc282a1004b62ad2cda5770d6e32d/gateway/openvpn/server.conf",
		"connector-core openvpn enforce --relay-id e4ccc282a1004b62ad2cda5770d6e32d",
		"connector-core openvpn limits --relay-id e4ccc282a1004b62ad2cda5770d6e32d --action apply",
		"connector-core firewall apply --relay-id e4ccc282a1004b62ad2cda5770d6e32d",
	} {
		if !strings.Contains(combined, expected) {
			t.Fatalf("rendered OpenVPN units missing %q", expected)
		}
	}
	if strings.Contains(combined, "gatewayctl") || strings.Contains(combined, "/bin/bash") {
		t.Fatal("rendered OpenVPN units contain a legacy command path")
	}
}

func TestRenderGatewayUnitsAddsTransactionalIPSecActivation(t *testing.T) {
	gatewaySpec := spec.GatewaySpec{
		RelayID: "e4ccc282a1004b62ad2cda5770d6e32d",
		Gateway: spec.Gateway{Protocol: "ipsec_l2tp_singbox"},
	}
	units := RenderGatewayUnits(gatewaySpec, Options{})
	if len(units) != 10 {
		t.Fatalf("unexpected IPsec/L2TP unit count: %d", len(units))
	}
	combined := ""
	for _, unit := range units {
		combined += unit.Content
	}
	for _, expected := range []string{
		"connector-core ipsec activate --relay-id e4ccc282a1004b62ad2cda5770d6e32d",
		"connector-core ipsec deactivate --relay-id e4ccc282a1004b62ad2cda5770d6e32d",
		"connector-core ipsec enforce --relay-id e4ccc282a1004b62ad2cda5770d6e32d",
		"connector-core firewall apply --relay-id e4ccc282a1004b62ad2cda5770d6e32d",
	} {
		if !strings.Contains(combined, expected) {
			t.Fatalf("rendered IPsec/L2TP units missing %q", expected)
		}
	}
}

func TestRenderGatewayUnitsAddsPanelServiceWhenEnabled(t *testing.T) {
	gatewaySpec := spec.GatewaySpec{
		RelayID: "e4ccc282a1004b62ad2cda5770d6e32d",
		Panel:   spec.PanelSpec{Port: 3054},
	}
	units := RenderGatewayUnits(gatewaySpec, Options{})
	if len(units) != 7 {
		t.Fatalf("unexpected panel-enabled unit count: %d", len(units))
	}
	combined := ""
	for _, unit := range units {
		combined += unit.Content
	}
	if !strings.Contains(combined, "omnirelay-omnipanel-e4ccc282a1004b62ad2cda5770d6e32d.service") ||
		!strings.Contains(combined, "EnvironmentFile=/etc/omnirelay/relays/e4ccc282a1004b62ad2cda5770d6e32d/gateway/panel/panel.env") ||
		!strings.Contains(combined, "ExecStart=/usr/bin/node /opt/omnirelay/relays/e4ccc282a1004b62ad2cda5770d6e32d/omnipanel/current/server.js") {
		t.Fatalf("panel service is incomplete: %s", combined)
	}
	if strings.Contains(combined, "ExecStartPre=") || strings.Contains(combined, "panel activate") {
		t.Fatalf("panel service must not self-activate during every restart: %s", combined)
	}
	panelUnit := unitContent(t, units, "omnirelay-omnipanel-e4ccc282a1004b62ad2cda5770d6e32d.service")
	if strings.Contains(panelUnit, "NoNewPrivileges=true") {
		t.Fatalf("panel service must allow its restricted sudoers command: %s", panelUnit)
	}
	connectorUnit := unitContent(t, units, "omnirelay-connector-e4ccc282a1004b62ad2cda5770d6e32d.service")
	if !strings.Contains(connectorUnit, "NoNewPrivileges=true") {
		t.Fatalf("connector service should keep NoNewPrivileges: %s", connectorUnit)
	}
}

func unitContent(t *testing.T, units []Unit, name string) string {
	t.Helper()
	for _, unit := range units {
		if unit.Name == name {
			return unit.Content
		}
	}
	t.Fatalf("unit %s not rendered", name)
	return ""
}
