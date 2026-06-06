package systemd

import (
	"fmt"
	"path"
	"sort"
	"strings"

	"github.com/omnirelay/connector-core/internal/spec"
)

type Unit struct {
	Name    string `json:"name"`
	Content string `json:"content"`
	Enable  bool   `json:"enable"`
	Start   bool   `json:"start"`
}

type Options struct {
	ConnectorBinary       string
	ConfigRoot            string
	AccountingIntervalSec int
}

func RenderGatewayUnits(gatewaySpec spec.GatewaySpec, options Options) []Unit {
	options = defaults(options)
	relayID := gatewaySpec.RelayID
	gatewayRoot := path.Join(options.ConfigRoot, "relays", relayID, "gateway")
	connectorService := "omnirelay-connector-" + relayID + ".service"
	accountingService := "omnirelay-accounting-" + relayID + ".service"
	accountingTimer := "omnirelay-accounting-" + relayID + ".timer"
	clockService := "omnirelay-clock-sync-" + relayID + ".service"
	clockTimer := "omnirelay-clock-sync-" + relayID + ".timer"
	target := "omnirelay-gateway-" + relayID + ".target"
	targetWants := []string{connectorService, accountingTimer, clockTimer}
	if gatewaySpec.Gateway.Type == "remote" {
		targetWants = append(targetWants, "omnirelay-frps.service")
	}

	units := []Unit{
		{
			Name: connectorService, Enable: true, Start: true,
			Content: renderConnectorService(relayID, gatewayRoot, options),
		},
		{
			Name: accountingService,
			Content: renderOneshotService(
				"OmniRelay accounting reconciliation for "+relayID,
				fmt.Sprintf("%s accounting sync --relay-id %s --json", options.ConnectorBinary, relayID),
			),
		},
		{
			Name: accountingTimer, Enable: true, Start: true,
			Content: renderTimer("OmniRelay accounting reconciliation timer for "+relayID, accountingService, "45s", fmt.Sprintf("%ds", options.AccountingIntervalSec), "5s"),
		},
		{
			Name: clockService,
			Content: renderOneshotService(
				"OmniRelay clock synchronization for "+relayID,
				fmt.Sprintf("%s clock sync --relay-id %s --json", options.ConnectorBinary, relayID),
			),
		},
		{
			Name: clockTimer, Enable: true, Start: true,
			Content: renderTimer("OmniRelay clock synchronization timer for "+relayID, clockService, "1min", "5min", "15s"),
		},
	}
	if gatewaySpec.Gateway.Protocol == "openvpn_tcp_singbox" {
		openVPNService := "omnirelay-openvpn-" + relayID + ".service"
		limitsService := "omnirelay-openvpn-limits-" + relayID + ".service"
		enforceService := "omnirelay-openvpn-enforce-" + relayID + ".service"
		enforceTimer := "omnirelay-openvpn-enforce-" + relayID + ".timer"
		targetWants = append(targetWants, openVPNService, limitsService, enforceTimer, "dnsmasq.service")
		units = append(units,
			Unit{
				Name: openVPNService, Enable: true, Start: true,
				Content: renderOpenVPNService(relayID, path.Join(gatewayRoot, "openvpn", "server.conf")),
			},
			Unit{
				Name: limitsService, Enable: true, Start: true,
				Content: renderOpenVPNLimitsService(relayID, openVPNService, options),
			},
			Unit{
				Name: enforceService,
				Content: renderOneshotService(
					"OmniRelay OpenVPN ineligible session enforcer for "+relayID,
					fmt.Sprintf("%s openvpn enforce --relay-id %s --json", options.ConnectorBinary, relayID),
				),
			},
			Unit{
				Name: enforceTimer, Enable: true, Start: true,
				Content: renderTimer("OmniRelay OpenVPN enforcement timer for "+relayID, enforceService, "20s", "8s", "1s"),
			},
		)
	}
	if gatewaySpec.Gateway.Protocol == "ipsec_l2tp_singbox" {
		ipsecService := "omnirelay-ipsec-" + relayID + ".service"
		enforceService := "omnirelay-ipsec-enforce-" + relayID + ".service"
		enforceTimer := "omnirelay-ipsec-enforce-" + relayID + ".timer"
		targetWants = append(targetWants, ipsecService, enforceTimer)
		units = append(units,
			Unit{Name: ipsecService, Enable: true, Start: true, Content: renderIPSecService(relayID, options)},
			Unit{Name: enforceService, Content: renderOneshotService(
				"OmniRelay IPsec/L2TP ineligible session enforcer for "+relayID,
				fmt.Sprintf("%s ipsec enforce --relay-id %s --json", options.ConnectorBinary, relayID),
			)},
			Unit{Name: enforceTimer, Enable: true, Start: true, Content: renderTimer(
				"OmniRelay IPsec/L2TP enforcement timer for "+relayID, enforceService, "30s", "20s", "3s",
			)},
		)
	}
	if gatewaySpec.Gateway.Protocol == "openvpn_tcp_singbox" || gatewaySpec.Gateway.Protocol == "ipsec_l2tp_singbox" {
		firewallService := "omnirelay-firewall-" + relayID + ".service"
		targetWants = append(targetWants, firewallService)
		units = append(units, Unit{
			Name: firewallService, Enable: true, Start: true,
			Content: renderFirewallService(relayID, options),
		})
	}
	if gatewaySpec.Panel.Port > 0 {
		panelService := "omnirelay-omnipanel-" + relayID + ".service"
		targetWants = append(targetWants, panelService, "nginx.service")
		units = append(units, Unit{
			Name: panelService, Enable: true, Start: true,
			Content: renderPanelService(relayID, path.Join(gatewayRoot, "panel", "panel.env"), options),
		})
	}
	units = append(units, Unit{
		Name: target, Enable: true, Start: true,
		Content: renderTarget(relayID, targetWants),
	})
	sort.Slice(units, func(i, j int) bool { return units[i].Name < units[j].Name })
	return units
}

func renderOpenVPNLimitsService(relayID string, openVPNService string, options Options) string {
	return fmt.Sprintf(`[Unit]
Description=OmniRelay OpenVPN speed limits for %s
After=%s
Requires=%s
PartOf=omnirelay-gateway-%s.target

[Service]
Type=oneshot
RemainAfterExit=true
ExecStart=%s openvpn limits --relay-id %s --action apply --json
ExecStop=%s openvpn limits --relay-id %s --action cleanup --json

[Install]
WantedBy=omnirelay-gateway-%s.target
`, relayID, openVPNService, openVPNService, relayID, options.ConnectorBinary, relayID, options.ConnectorBinary, relayID, relayID)
}

func renderIPSecService(relayID string, options Options) string {
	return fmt.Sprintf(`[Unit]
Description=OmniRelay IPsec/L2TP activation for %s
After=network-online.target omnirelay-connector-%s.service
Wants=network-online.target omnirelay-connector-%s.service
PartOf=omnirelay-gateway-%s.target

[Service]
Type=oneshot
RemainAfterExit=true
ExecStart=%s ipsec activate --relay-id %s --json
ExecStop=%s ipsec deactivate --relay-id %s --json

[Install]
WantedBy=omnirelay-gateway-%s.target
`, relayID, relayID, relayID, relayID, options.ConnectorBinary, relayID, options.ConnectorBinary, relayID, relayID)
}

func renderFirewallService(relayID string, options Options) string {
	return fmt.Sprintf(`[Unit]
Description=OmniRelay managed firewall rules for %s
After=network-online.target omnirelay-connector-%s.service
Wants=network-online.target omnirelay-connector-%s.service
PartOf=omnirelay-gateway-%s.target

[Service]
Type=oneshot
RemainAfterExit=true
ExecStart=%s firewall apply --relay-id %s --json
ExecStop=%s firewall cleanup --relay-id %s --json

[Install]
WantedBy=omnirelay-gateway-%s.target
`, relayID, relayID, relayID, relayID, options.ConnectorBinary, relayID, options.ConnectorBinary, relayID, relayID)
}

func renderPanelService(relayID string, environmentPath string, options Options) string {
	appRoot := path.Join("/opt/omnirelay/relays", relayID, "omnipanel")
	return fmt.Sprintf(`[Unit]
Description=OmniRelay OmniPanel for %s
After=network-online.target
Wants=network-online.target
PartOf=omnirelay-gateway-%s.target

[Service]
Type=simple
User=omnigateway
Group=omnigateway
EnvironmentFile=%s
ExecStartPre=+%s panel activate --relay-id %s --json
WorkingDirectory=%s/current
ExecStart=/usr/bin/node %s/current/server.js
Restart=always
RestartSec=3
NoNewPrivileges=true

[Install]
WantedBy=omnirelay-gateway-%s.target
`, relayID, relayID, environmentPath, options.ConnectorBinary, relayID, appRoot, appRoot, relayID)
}

func defaults(options Options) Options {
	if options.ConnectorBinary == "" {
		options.ConnectorBinary = "/usr/local/bin/connector-core"
	}
	if options.ConfigRoot == "" {
		options.ConfigRoot = "/etc/omnirelay"
	}
	if options.AccountingIntervalSec <= 0 {
		options.AccountingIntervalSec = 30
	}
	return options
}

func renderTarget(relayID string, wants []string) string {
	sort.Strings(wants)
	return fmt.Sprintf(`[Unit]
Description=OmniRelay gateway target for %s
Wants=%s
After=network-online.target

[Install]
WantedBy=multi-user.target
`, relayID, strings.Join(wants, " "))
}

func renderConnectorService(relayID string, gatewayRoot string, options Options) string {
	connectorRoot := path.Join(gatewayRoot, "connector")
	return fmt.Sprintf(`[Unit]
Description=OmniRelay connector runtime for %s
After=network-online.target
Wants=network-online.target
PartOf=omnirelay-gateway-%s.target

[Service]
Type=simple
UMask=0007
ExecStart=%s run --config %s --metadata %s --accounting-db %s --state-file %s --lock-file %s --interval-sec %d --panel-group omnigateway
Restart=always
RestartSec=3
NoNewPrivileges=true

[Install]
WantedBy=omnirelay-gateway-%s.target
`, relayID, relayID, options.ConnectorBinary,
		path.Join(connectorRoot, "config.json"),
		path.Join(gatewayRoot, "metadata.json"),
		path.Join(connectorRoot, "accounting.db"),
		path.Join(connectorRoot, "connector_core_state.json"),
		path.Join("/run/omnirelay", relayID, "accounting.lock"),
		options.AccountingIntervalSec, relayID)
}

func renderOpenVPNService(relayID string, configPath string) string {
	return fmt.Sprintf(`[Unit]
Description=OmniRelay OpenVPN server for %s
After=network-online.target omnirelay-connector-%s.service
Wants=network-online.target omnirelay-connector-%s.service
PartOf=omnirelay-gateway-%s.target

[Service]
Type=simple
ExecStart=/usr/sbin/openvpn --config %s
Restart=always
RestartSec=3
CapabilityBoundingSet=CAP_NET_ADMIN CAP_NET_BIND_SERVICE CAP_NET_RAW CAP_SETUID CAP_SETGID CAP_SETPCAP
AmbientCapabilities=CAP_NET_ADMIN CAP_NET_BIND_SERVICE CAP_NET_RAW CAP_SETUID CAP_SETGID CAP_SETPCAP

[Install]
WantedBy=omnirelay-gateway-%s.target
`, relayID, relayID, relayID, relayID, configPath, relayID)
}

func renderOneshotService(description string, command string) string {
	return fmt.Sprintf(`[Unit]
Description=%s
After=network-online.target
Wants=network-online.target

[Service]
Type=oneshot
UMask=0007
ExecStart=%s
`, description, command)
}

func renderTimer(description string, service string, onBoot string, onActive string, randomizedDelay string) string {
	return fmt.Sprintf(`[Unit]
Description=%s

[Timer]
OnBootSec=%s
OnUnitActiveSec=%s
RandomizedDelaySec=%s
Persistent=true
Unit=%s

[Install]
WantedBy=timers.target
`, description, onBoot, onActive, randomizedDelay, service)
}
