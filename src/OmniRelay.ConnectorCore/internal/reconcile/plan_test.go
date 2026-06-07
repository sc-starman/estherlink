package reconcile

import (
	"reflect"
	"slices"
	"strings"
	"testing"

	"github.com/omnirelay/connector-core/internal/spec"
)

func TestBuildPlanIsDeterministic(t *testing.T) {
	gatewaySpec := spec.GatewaySpec{
		RelayID: "e4ccc282a1004b62ad2cda5770d6e32d",
		Gateway: spec.Gateway{Protocol: "openvpn_tcp_singbox"},
	}
	first := BuildPlan(gatewaySpec)
	second := BuildPlan(gatewaySpec)
	if !reflect.DeepEqual(first, second) {
		t.Fatal("plan is not deterministic")
	}
	if first.Runtime != "openvpn" {
		t.Fatalf("unexpected runtime: %s", first.Runtime)
	}
}

func TestBuildPlanIncludesPanelRuntimePackages(t *testing.T) {
	gatewaySpec := spec.GatewaySpec{
		RelayID: "e4ccc282a1004b62ad2cda5770d6e32d",
		Gateway: spec.Gateway{Protocol: "vless_tls_singbox"},
		Panel:   spec.PanelSpec{Port: 3054},
	}
	packages := BuildPlan(gatewaySpec).RequiredPackages
	for _, expected := range []string{"nginx", "nodejs", "sqlite3", "sudo"} {
		if !slices.Contains(packages, expected) {
			t.Fatalf("panel package %q missing from plan: %+v", expected, packages)
		}
	}
}

func TestBuildPlanListsIPSecSecretPathWithoutSecretValue(t *testing.T) {
	gatewaySpec := spec.GatewaySpec{
		RelayID:   "e4ccc282a1004b62ad2cda5770d6e32d",
		Gateway:   spec.Gateway{Protocol: "ipsec_l2tp_singbox"},
		IPSecL2TP: spec.IPSecL2TPSpec{PreSharedKey: "must-not-appear-in-plan"},
	}
	plan := BuildPlan(gatewaySpec)
	foundSecretPath := false
	for _, action := range plan.Actions {
		if action.Target == "/etc/omnirelay/relays/e4ccc282a1004b62ad2cda5770d6e32d/gateway/ipsec-l2tp/ipsec.secrets" {
			foundSecretPath = true
		}
		if strings.Contains(action.Target, "must-not-appear") || strings.Contains(action.Change, "must-not-appear") {
			t.Fatal("plan exposed secret value")
		}
	}
	if !foundSecretPath {
		t.Fatal("plan did not include managed IPsec secrets path")
	}
}
