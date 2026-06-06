package reconcile

import (
	"sort"

	"github.com/omnirelay/connector-core/internal/protocol"
	"github.com/omnirelay/connector-core/internal/spec"
	"github.com/omnirelay/connector-core/internal/systemd"
)

type Action struct {
	Kind   string `json:"kind"`
	Target string `json:"target"`
	Change string `json:"change"`
}

type Plan struct {
	RelayID          string   `json:"relayId"`
	Protocol         string   `json:"protocol"`
	Runtime          string   `json:"runtime"`
	RequiredPackages []string `json:"requiredPackages"`
	Actions          []Action `json:"actions"`
}

func BuildPlan(gatewaySpec spec.GatewaySpec) Plan {
	definition, _ := protocol.Lookup(gatewaySpec.Gateway.Protocol)
	packages := append([]string{"ca-certificates", "curl", "iptables"}, definition.RequiredPackages...)
	if gatewaySpec.Panel.Port > 0 {
		packages = append(packages, "nginx", "nodejs", "sudo")
		if gatewaySpec.Panel.TLSEnabled && gatewaySpec.Panel.TLSMode == "certbot" {
			packages = append(packages, "certbot")
		}
	}
	packages = uniqueSorted(packages)

	actions := []Action{
		{Kind: "directory", Target: "/etc/omnirelay/relays/" + gatewaySpec.RelayID + "/gateway", Change: "reconcile"},
		{Kind: "file", Target: "/etc/omnirelay/relays/" + gatewaySpec.RelayID + "/gateway/spec.json", Change: "write_if_changed"},
		{Kind: "file", Target: "/etc/omnirelay/relays/" + gatewaySpec.RelayID + "/gateway/managed-files.json", Change: "write_if_changed"},
		{Kind: "runtime", Target: definition.Runtime, Change: "reconcile"},
	}
	gatewayRoot := "/etc/omnirelay/relays/" + gatewaySpec.RelayID + "/gateway"
	switch definition.Runtime {
	case "singbox":
		actions = appendFileActions(actions, gatewayRoot+"/connector/config.json", gatewayRoot+"/metadata.json")
	case "openvpn":
		actions = appendFileActions(actions,
			gatewayRoot+"/connector/config.json", gatewayRoot+"/metadata.json",
			gatewayRoot+"/openvpn/server.conf", gatewayRoot+"/openvpn/runtime.json",
			"/etc/dnsmasq.d/omnirelay-openvpn-"+gatewaySpec.RelayID+".conf",
		)
	case "ipsec_l2tp":
		actions = appendFileActions(actions,
			gatewayRoot+"/connector/config.json", gatewayRoot+"/metadata.json",
			gatewayRoot+"/ipsec-l2tp/ipsec.conf", gatewayRoot+"/ipsec-l2tp/ipsec.secrets",
			gatewayRoot+"/ipsec-l2tp/xl2tpd.conf", gatewayRoot+"/ipsec-l2tp/ppp-options",
			gatewayRoot+"/ipsec-l2tp/runtime.json",
			"/etc/dnsmasq.d/omnirelay-ipsec-l2tp-"+gatewaySpec.RelayID+".conf",
		)
	}
	if gatewaySpec.Panel.Port > 0 {
		actions = appendFileActions(actions,
			gatewayRoot+"/panel/panel.env",
			"/etc/nginx/sites-available/omnirelay-omnipanel-"+gatewaySpec.RelayID+".conf",
		)
	}
	for _, unit := range systemd.RenderGatewayUnits(gatewaySpec, systemd.Options{}) {
		actions = append(actions, Action{Kind: "systemd_unit", Target: unit.Name, Change: "write_if_changed"})
	}
	sort.Slice(actions, func(i, j int) bool {
		if actions[i].Kind == actions[j].Kind {
			return actions[i].Target < actions[j].Target
		}
		return actions[i].Kind < actions[j].Kind
	})

	return Plan{
		RelayID:          gatewaySpec.RelayID,
		Protocol:         definition.ID,
		Runtime:          definition.Runtime,
		RequiredPackages: packages,
		Actions:          actions,
	}
}

func appendFileActions(actions []Action, targets ...string) []Action {
	for _, target := range targets {
		actions = append(actions, Action{Kind: "file", Target: target, Change: "write_if_changed"})
	}
	return actions
}

func uniqueSorted(values []string) []string {
	seen := make(map[string]struct{}, len(values))
	result := make([]string, 0, len(values))
	for _, value := range values {
		if _, ok := seen[value]; ok {
			continue
		}
		seen[value] = struct{}{}
		result = append(result, value)
	}
	sort.Strings(result)
	return result
}
