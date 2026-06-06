package status

import (
	"context"
	"testing"

	"github.com/omnirelay/connector-core/internal/spec"
)

type fakeSystemd map[string]string

func (fakeSystemd) DaemonReload(context.Context) error          { return nil }
func (fakeSystemd) EnableNow(context.Context, ...string) error  { return nil }
func (fakeSystemd) Start(context.Context, ...string) error      { return nil }
func (fakeSystemd) Restart(context.Context, ...string) error    { return nil }
func (fakeSystemd) Stop(context.Context, ...string) error       { return nil }
func (fakeSystemd) DisableNow(context.Context, ...string) error { return nil }
func (f fakeSystemd) IsActive(_ context.Context, unit string) (string, error) {
	if state := f[unit]; state != "" {
		return state, nil
	}
	return "inactive", nil
}

func TestCollectReportsUnitFailureWithoutProbe(t *testing.T) {
	relayID := "e4ccc282a1004b62ad2cda5770d6e32d"
	result := Collect(context.Background(), spec.GatewaySpec{
		RelayID: relayID, Gateway: spec.Gateway{Protocol: "vless_tls_singbox"},
	}, Options{Systemd: fakeSystemd{
		"omnirelay-gateway-" + relayID + ".target":    "active",
		"omnirelay-connector-" + relayID + ".service": "failed",
	}})
	if result.Healthy || result.ReasonCode != "gateway_units_not_active" {
		t.Fatalf("unexpected result: %+v", result)
	}
}

func TestCollectRequiresOpenVPNAndPanelServicesWhenConfigured(t *testing.T) {
	relayID := "e4ccc282a1004b62ad2cda5770d6e32d"
	states := fakeSystemd{
		"omnirelay-gateway-" + relayID + ".target":    "active",
		"omnirelay-connector-" + relayID + ".service": "active",
		"omnirelay-openvpn-" + relayID + ".service":   "active",
		"omnirelay-omnipanel-" + relayID + ".service": "active",
		"omnirelay-firewall-" + relayID + ".service":  "active",
		"omnirelay-accounting-" + relayID + ".timer":  "active",
		"omnirelay-clock-sync-" + relayID + ".timer":  "active",
		"nginx.service":   "active",
		"dnsmasq.service": "active",
	}
	gatewaySpec := spec.GatewaySpec{
		RelayID: relayID, Gateway: spec.Gateway{Protocol: "openvpn_tcp_singbox"}, Panel: spec.PanelSpec{Port: 3054},
	}
	result := Collect(context.Background(), gatewaySpec, Options{Systemd: states})
	if !result.Healthy || result.OpenVPNState != "active" || result.PanelState != "active" {
		t.Fatalf("unexpected healthy result: %+v", result)
	}
	states["omnirelay-openvpn-"+relayID+".service"] = "failed"
	result = Collect(context.Background(), gatewaySpec, Options{Systemd: states})
	if result.Healthy || result.ReasonCode != "openvpn_not_active" {
		t.Fatalf("unexpected OpenVPN failure result: %+v", result)
	}
}
