package status

import (
	"context"
	"fmt"
	"net"
	"time"

	dnsapi "github.com/omnirelay/connector-core/internal/dns"
	"github.com/omnirelay/connector-core/internal/host"
	panelapi "github.com/omnirelay/connector-core/internal/panel"
	"github.com/omnirelay/connector-core/internal/probe"
	"github.com/omnirelay/connector-core/internal/spec"
)

type Gateway struct {
	RelayID              string               `json:"relayId"`
	Protocol             string               `json:"protocol"`
	Healthy              bool                 `json:"healthy"`
	ReasonCode           string               `json:"reasonCode"`
	TargetState          string               `json:"targetState"`
	ConnectorState       string               `json:"connectorState"`
	AccountingTimerState string               `json:"accountingTimerState"`
	ClockTimerState      string               `json:"clockTimerState"`
	OpenVPNState         string               `json:"openVpnState,omitempty"`
	IPSecState           string               `json:"ipsecState,omitempty"`
	XL2TPDState          string               `json:"xl2tpdState,omitempty"`
	PanelState           string               `json:"panelState,omitempty"`
	NginxState           string               `json:"nginxState,omitempty"`
	PanelInternalPort    int                  `json:"panelInternalPort,omitempty"`
	PanelListener        bool                 `json:"panelListener,omitempty"`
	DNSMasqState         string               `json:"dnsmasqState,omitempty"`
	FirewallState        string               `json:"firewallState,omitempty"`
	Backend              *probe.BackendResult `json:"backend,omitempty"`
	DNS                  *dnsapi.StatusResult `json:"dns,omitempty"`
	CheckedAtUTC         string               `json:"checkedAtUtc"`
}

type Options struct {
	Systemd              host.Systemd
	IncludeProbe         bool
	PanelListenerChecker func(context.Context, string, int) bool
}

func Collect(ctx context.Context, gatewaySpec spec.GatewaySpec, options Options) Gateway {
	result := Gateway{
		RelayID: gatewaySpec.RelayID, Protocol: gatewaySpec.Gateway.Protocol,
		ReasonCode: "ok", CheckedAtUTC: time.Now().UTC().Format(time.RFC3339),
	}
	if options.Systemd == nil {
		options.Systemd = host.OSSystemd{}
	}
	result.TargetState = activeState(ctx, options.Systemd, host.GatewayTarget(gatewaySpec.RelayID))
	result.ConnectorState = activeState(ctx, options.Systemd, "omnirelay-connector-"+gatewaySpec.RelayID+".service")
	result.AccountingTimerState = activeState(ctx, options.Systemd, "omnirelay-accounting-"+gatewaySpec.RelayID+".timer")
	result.ClockTimerState = activeState(ctx, options.Systemd, "omnirelay-clock-sync-"+gatewaySpec.RelayID+".timer")
	result.Healthy = result.TargetState == "active" && result.ConnectorState == "active"
	if !result.Healthy {
		result.ReasonCode = "gateway_units_not_active"
	}
	switch gatewaySpec.Gateway.Protocol {
	case "openvpn_tcp_singbox":
		result.FirewallState = activeState(ctx, options.Systemd, "omnirelay-firewall-"+gatewaySpec.RelayID+".service")
		result.OpenVPNState = activeState(ctx, options.Systemd, "omnirelay-openvpn-"+gatewaySpec.RelayID+".service")
		if result.OpenVPNState != "active" {
			result.Healthy = false
			result.ReasonCode = "openvpn_not_active"
		}
		result.DNSMasqState = activeState(ctx, options.Systemd, "dnsmasq.service")
		if result.DNSMasqState != "active" {
			result.Healthy = false
			result.ReasonCode = "dnsmasq_not_active"
		}
		if result.FirewallState != "active" {
			result.Healthy = false
			result.ReasonCode = "firewall_not_active"
		}
	case "ipsec_l2tp_singbox":
		result.FirewallState = activeState(ctx, options.Systemd, "omnirelay-firewall-"+gatewaySpec.RelayID+".service")
		result.IPSecState = activeState(ctx, options.Systemd, "strongswan-starter.service")
		if result.IPSecState != "active" {
			result.IPSecState = activeState(ctx, options.Systemd, "ipsec.service")
		}
		result.XL2TPDState = activeState(ctx, options.Systemd, "xl2tpd.service")
		if result.IPSecState != "active" || result.XL2TPDState != "active" {
			result.Healthy = false
			result.ReasonCode = "ipsec_l2tp_not_active"
		}
		result.DNSMasqState = activeState(ctx, options.Systemd, "dnsmasq.service")
		if result.DNSMasqState != "active" {
			result.Healthy = false
			result.ReasonCode = "dnsmasq_not_active"
		}
		if result.FirewallState != "active" {
			result.Healthy = false
			result.ReasonCode = "firewall_not_active"
		}
	}
	if gatewaySpec.Panel.Port > 0 {
		result.PanelState = activeState(ctx, options.Systemd, "omnirelay-omnipanel-"+gatewaySpec.RelayID+".service")
		result.NginxState = activeState(ctx, options.Systemd, "nginx.service")
		result.PanelInternalPort = panelapi.InternalPort(gatewaySpec.RelayID)
		result.PanelListener = panelListener(ctx, options.PanelListenerChecker, "127.0.0.1", result.PanelInternalPort)
		if result.PanelState != "active" || result.NginxState != "active" {
			result.Healthy = false
			result.ReasonCode = "panel_not_active"
		}
		if result.PanelState == "active" && result.NginxState == "active" && !result.PanelListener {
			result.Healthy = false
			result.ReasonCode = "panel_upstream_unreachable"
		}
	}
	if options.IncludeProbe {
		backend := probe.Backend(ctx, probe.BackendOptions{
			Host: gatewaySpec.Tunnel.BackendHost, Port: gatewaySpec.Tunnel.BackendPort,
			Targets: gatewaySpec.Tunnel.ProbeURLs, Timeout: time.Duration(gatewaySpec.Tunnel.TimeoutSecond) * time.Second,
		})
		result.Backend = &backend
		if !backend.Healthy {
			result.Healthy = false
			result.ReasonCode = backend.ReasonCode
		}
		dnsStatus := dnsapi.Status(ctx, gatewaySpec.DNS.ListenAddress, gatewaySpec.DNS.ListenPort, 10*time.Second)
		result.DNS = &dnsStatus
		if !dnsStatus.Healthy {
			result.Healthy = false
			result.ReasonCode = dnsStatus.ReasonCode
		}
	}
	return result
}

func panelListener(ctx context.Context, checker func(context.Context, string, int) bool, host string, port int) bool {
	if checker != nil {
		return checker(ctx, host, port)
	}
	dialer := net.Dialer{Timeout: time.Second}
	conn, err := dialer.DialContext(ctx, "tcp", fmt.Sprintf("%s:%d", host, port))
	if err != nil {
		return false
	}
	_ = conn.Close()
	return true
}

func activeState(ctx context.Context, manager host.Systemd, unit string) string {
	state, err := manager.IsActive(ctx, unit)
	if err != nil && state == "" {
		return "unknown"
	}
	return state
}
