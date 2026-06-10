package singbox

import (
	"context"
	"encoding/base64"
	"encoding/json"
	"fmt"
	"hash/fnv"
	"net"
	"strconv"
	"strings"

	"github.com/omnirelay/connector-core/internal/spec"
	boxcore "github.com/sagernet/sing-box"
	"github.com/sagernet/sing-box/include"
	"github.com/sagernet/sing-box/option"
	sbjson "github.com/sagernet/sing/common/json"
)

type RenderSpec struct {
	ProtocolID                string
	PublicPort                int
	BackendPort               int
	Users                     []User
	Proxy                     Credential
	TLS                       TLS
	VLESSFlow                 string
	Hysteria2                 Hysteria2
	Naive                     Naive
	ShadowsocksServerPassword string
	ShadowTLS                 ShadowTLS
	DNS                       DNS
}

type User struct {
	ID     string
	Secret string
}

type Credential struct {
	Username string
	Password string
}

type TLS struct {
	Enabled    bool
	ServerName string
	CertFile   string
	KeyFile    string
}

type Hysteria2 struct {
	UpMbps                int
	DownMbps              int
	ObfsPassword          string
	IgnoreClientBandwidth bool
	MasqueradeURL         string
}

type Naive struct {
	Network string
	QUICCC  string
}

type ShadowTLS struct {
	CamouflageServer string
	StrictMode       bool
	WildcardSNI      string
}

type DNS struct {
	DoHEndpoints  []string
	ListenAddress string
	ListenPort    int
}

type configuration struct {
	Log       logOptions        `json:"log"`
	DNS       dnsOptions        `json:"dns"`
	Inbounds  []any             `json:"inbounds"`
	Outbounds []outboundOptions `json:"outbounds"`
	Route     routeOptions      `json:"route"`
}

type logOptions struct {
	Level string `json:"level"`
}

type listenOptions struct {
	Type       string `json:"type"`
	Tag        string `json:"tag"`
	Listen     string `json:"listen"`
	ListenPort int    `json:"listen_port"`
}

type outboundOptions struct {
	Type       string `json:"type"`
	Tag        string `json:"tag"`
	Server     string `json:"server,omitempty"`
	ServerPort int    `json:"server_port,omitempty"`
	Version    string `json:"version,omitempty"`
}

type routeOptions struct {
	Rules []routeRule `json:"rules,omitempty"`
	Final string      `json:"final"`
}

type routeRule struct {
	Inbound  []string `json:"inbound"`
	Protocol string   `json:"protocol,omitempty"`
	Outbound string   `json:"outbound"`
}

type dnsOptions struct {
	Servers          []dnsServer `json:"servers"`
	Final            string      `json:"final"`
	Strategy         string      `json:"strategy"`
	IndependentCache bool        `json:"independent_cache"`
}

type dnsServer struct {
	Tag     string `json:"tag"`
	Address string `json:"address"`
	Detour  string `json:"detour"`
}

type dnsInbound struct {
	listenOptions
	OverrideAddress string `json:"override_address"`
	OverridePort    int    `json:"override_port"`
}

type passwordUser struct {
	Name     string `json:"name,omitempty"`
	Username string `json:"username,omitempty"`
	Password string `json:"password"`
}

type vlessUser struct {
	Name string `json:"name"`
	UUID string `json:"uuid"`
	Flow string `json:"flow,omitempty"`
}

type tlsOptions struct {
	Enabled         bool   `json:"enabled"`
	ServerName      string `json:"server_name,omitempty"`
	CertificatePath string `json:"certificate_path,omitempty"`
	KeyPath         string `json:"key_path,omitempty"`
}

type authenticatedInbound struct {
	listenOptions
	Users []passwordUser `json:"users"`
}

type vlessInbound struct {
	listenOptions
	Users []vlessUser `json:"users"`
	TLS   tlsOptions  `json:"tls"`
}

type hysteria2Inbound struct {
	listenOptions
	Users                 []passwordUser `json:"users"`
	TLS                   tlsOptions     `json:"tls"`
	UpMbps                int            `json:"up_mbps"`
	DownMbps              int            `json:"down_mbps"`
	Obfs                  *obfsOptions   `json:"obfs,omitempty"`
	IgnoreClientBandwidth bool           `json:"ignore_client_bandwidth"`
	Masquerade            string         `json:"masquerade,omitempty"`
}

type obfsOptions struct {
	Type     string `json:"type"`
	Password string `json:"password"`
}

type naiveInbound struct {
	listenOptions
	Users   []passwordUser `json:"users"`
	Network string         `json:"network,omitempty"`
	TLS     tlsOptions     `json:"tls"`
	QUIC    *quicOptions   `json:"quic,omitempty"`
}

type quicOptions struct {
	CongestionControl string `json:"congestion_control"`
}

type shadowsocksInbound struct {
	listenOptions
	Network   string         `json:"network"`
	Method    string         `json:"method"`
	Password  string         `json:"password"`
	Users     []passwordUser `json:"users"`
	Multiplex multiplex      `json:"multiplex"`
}

type multiplex struct {
	Enabled bool `json:"enabled"`
}

type shadowTLSInbound struct {
	listenOptions
	Version     int            `json:"version"`
	Users       []passwordUser `json:"users"`
	Handshake   handshake      `json:"handshake"`
	StrictMode  bool           `json:"strict_mode"`
	WildcardSNI string         `json:"wildcard_sni,omitempty"`
	Detour      string         `json:"detour"`
}

type handshake struct {
	Server     string `json:"server"`
	ServerPort int    `json:"server_port"`
}

func Render(spec RenderSpec) ([]byte, error) {
	if err := validateRenderSpec(spec); err != nil {
		return nil, err
	}
	inbounds, err := renderInbounds(spec)
	if err != nil {
		return nil, err
	}
	config := configuration{
		Log:      logOptions{Level: "warn"},
		Inbounds: inbounds,
		Outbounds: []outboundOptions{
			{Type: "socks", Tag: "tunnel-backend", Server: "127.0.0.1", ServerPort: spec.BackendPort, Version: "5"},
			{Type: "direct", Tag: "direct"},
		},
		Route: routeOptions{Final: "tunnel-backend"},
	}
	if err := applyDNS(&config, spec.DNS); err != nil {
		return nil, err
	}
	content, err := json.MarshalIndent(config, "", "  ")
	if err != nil {
		return nil, err
	}
	return append(content, '\n'), nil
}

func RenderInternalTunnel(backendPort int, redirectPort int, dns DNS) ([]byte, error) {
	if backendPort < 1 || backendPort > 65535 {
		return nil, fmt.Errorf("backend port must be between 1 and 65535")
	}
	if redirectPort < 1 || redirectPort > 65535 {
		return nil, fmt.Errorf("redirect port must be between 1 and 65535")
	}
	config := configuration{
		Log: logOptions{Level: "warn"},
		Inbounds: []any{
			listenOptions{Type: "redirect", Tag: "connector-in", Listen: "0.0.0.0", ListenPort: redirectPort},
		},
		Outbounds: []outboundOptions{
			{Type: "socks", Tag: "tunnel-backend", Server: "127.0.0.1", ServerPort: backendPort, Version: "5"},
			{Type: "direct", Tag: "direct"},
		},
		Route: routeOptions{
			Rules: []routeRule{{Inbound: []string{"connector-in"}, Outbound: "tunnel-backend"}},
			Final: "tunnel-backend",
		},
	}
	if err := applyDNS(&config, dns); err != nil {
		return nil, err
	}
	content, err := json.MarshalIndent(config, "", "  ")
	if err != nil {
		return nil, err
	}
	return append(content, '\n'), nil
}

func applyDNS(config *configuration, dns DNS) error {
	if len(dns.DoHEndpoints) == 0 {
		dns.DoHEndpoints = []string{"https://1.1.1.1/dns-query", "https://8.8.8.8/dns-query"}
	}
	if strings.TrimSpace(dns.ListenAddress) == "" {
		dns.ListenAddress = "127.0.0.1"
	}
	if dns.ListenPort == 0 {
		dns.ListenPort = 5353
	}
	if dns.ListenPort < 1 || dns.ListenPort > 65535 {
		return fmt.Errorf("DNS listen port must be between 1 and 65535")
	}
	servers := make([]dnsServer, 0, len(dns.DoHEndpoints))
	for index, endpoint := range dns.DoHEndpoints {
		if strings.TrimSpace(endpoint) == "" {
			return fmt.Errorf("DNS DoH endpoint must not be empty")
		}
		servers = append(servers, dnsServer{Tag: fmt.Sprintf("dns-doh-%d", index), Address: endpoint, Detour: "tunnel-backend"})
	}
	config.DNS = dnsOptions{
		Servers: servers, Final: "dns-doh-0", Strategy: "prefer_ipv4", IndependentCache: true,
	}
	config.Inbounds = append(config.Inbounds, dnsInbound{
		listenOptions:   listenOptions{Type: "direct", Tag: "dns-in", Listen: dns.ListenAddress, ListenPort: dns.ListenPort},
		OverrideAddress: "1.1.1.1", OverridePort: 53,
	})
	config.Outbounds = append(config.Outbounds, outboundOptions{Type: "dns", Tag: "dns-out"})
	config.Route.Rules = append([]routeRule{
		{Inbound: []string{"dns-in"}, Outbound: "dns-out"},
		{Protocol: "dns", Outbound: "dns-out"},
	}, config.Route.Rules...)
	return nil
}

func InternalRedirectPort(relayID string) int {
	hash := fnv.New32a()
	_, _ = hash.Write([]byte(relayID))
	return 30000 + int(hash.Sum32()%20000)
}

func FromGatewaySpec(gatewaySpec spec.GatewaySpec, users []User) RenderSpec {
	return RenderSpec{
		ProtocolID: gatewaySpec.Gateway.Protocol, PublicPort: gatewaySpec.Gateway.PublicPort,
		BackendPort: gatewaySpec.Tunnel.BackendPort, Users: users,
		Proxy: Credential{
			Username: gatewaySpec.SingBox.Proxy.Username,
			Password: gatewaySpec.SingBox.Proxy.Password,
		},
		TLS: TLS{
			Enabled: gatewaySpec.SingBox.TLS.Enabled, ServerName: gatewaySpec.SingBox.TLS.ServerName,
			CertFile: gatewaySpec.SingBox.TLS.CertFile, KeyFile: gatewaySpec.SingBox.TLS.KeyFile,
		},
		VLESSFlow: gatewaySpec.SingBox.VLESSFlow,
		Hysteria2: Hysteria2{
			UpMbps: gatewaySpec.SingBox.Hysteria2.UpMbps, DownMbps: gatewaySpec.SingBox.Hysteria2.DownMbps,
			ObfsPassword:          gatewaySpec.SingBox.Hysteria2.ObfsPassword,
			IgnoreClientBandwidth: gatewaySpec.SingBox.Hysteria2.IgnoreClientBandwidth,
			MasqueradeURL:         gatewaySpec.SingBox.Hysteria2.MasqueradeURL,
		},
		Naive: Naive{
			Network: gatewaySpec.SingBox.Naive.Network, QUICCC: gatewaySpec.SingBox.Naive.QUICCC,
		},
		ShadowsocksServerPassword: gatewaySpec.SingBox.ShadowsocksServerPassword,
		ShadowTLS: ShadowTLS{
			CamouflageServer: gatewaySpec.SingBox.ShadowTLS.CamouflageServer,
			StrictMode:       gatewaySpec.SingBox.ShadowTLS.StrictMode,
			WildcardSNI:      gatewaySpec.SingBox.ShadowTLS.WildcardSNI,
		},
		DNS: DNS{
			DoHEndpoints: gatewaySpec.DNS.DoHEndpoints, ListenAddress: gatewaySpec.DNS.ListenAddress, ListenPort: gatewaySpec.DNS.ListenPort,
		},
	}
}

func Validate(content []byte) error {
	ctx := boxcore.Context(
		context.Background(),
		include.InboundRegistry(),
		include.OutboundRegistry(),
		include.EndpointRegistry(),
	)
	options, err := sbjson.UnmarshalExtendedContext[option.Options](ctx, content)
	if err != nil {
		return err
	}
	instance, err := boxcore.New(boxcore.Options{Context: ctx, Options: options})
	if err != nil {
		return err
	}
	return instance.Close()
}

func renderInbounds(spec RenderSpec) ([]any, error) {
	listen := func(protocolType string, tag string) listenOptions {
		return listenOptions{Type: protocolType, Tag: tag, Listen: "::", ListenPort: spec.PublicPort}
	}
	switch spec.ProtocolID {
	case "vless_plain_singbox", "vless_tls_singbox":
		tls := spec.TLS
		if spec.ProtocolID == "vless_plain_singbox" {
			tls = TLS{}
		}
		users := make([]vlessUser, 0, len(spec.Users))
		for _, item := range spec.Users {
			users = append(users, vlessUser{Name: item.ID, UUID: item.ID, Flow: flowWhenTLS(tls, spec.VLESSFlow)})
		}
		return []any{vlessInbound{listen("vless", "vless-in"), users, renderTLS(tls)}}, nil
	case "mixed_singbox":
		return []any{authenticatedInbound{listen("mixed", "mixed-in"), []passwordUser{proxyUser(spec.Proxy)}}}, nil
	case "socks_singbox":
		return []any{authenticatedInbound{listen("socks", "socks-in"), []passwordUser{proxyUser(spec.Proxy)}}}, nil
	case "http_singbox":
		return []any{authenticatedInbound{listen("http", "http-in"), []passwordUser{proxyUser(spec.Proxy)}}}, nil
	case "hysteria2_singbox":
		inbound := hysteria2Inbound{
			listenOptions:         listen("hysteria2", "hy2-in"),
			Users:                 []passwordUser{{Password: spec.Proxy.Password}},
			TLS:                   renderTLS(spec.TLS),
			UpMbps:                spec.Hysteria2.UpMbps,
			DownMbps:              spec.Hysteria2.DownMbps,
			IgnoreClientBandwidth: spec.Hysteria2.IgnoreClientBandwidth,
			Masquerade:            spec.Hysteria2.MasqueradeURL,
		}
		if spec.Hysteria2.ObfsPassword != "" {
			inbound.Obfs = &obfsOptions{Type: "salamander", Password: spec.Hysteria2.ObfsPassword}
		}
		return []any{inbound}, nil
	case "trojan_singbox":
		return []any{struct {
			listenOptions
			Users []passwordUser `json:"users"`
			TLS   tlsOptions     `json:"tls"`
		}{listen("trojan", "trojan-in"), passwordUsers(spec.Users), renderTLS(spec.TLS)}}, nil
	case "naive_singbox":
		inbound := naiveInbound{
			listenOptions: listen("naive", "naive-in"),
			Users:         []passwordUser{proxyUser(spec.Proxy)},
			Network:       spec.Naive.Network,
			TLS:           renderTLS(spec.TLS),
		}
		if spec.Naive.QUICCC != "" {
			inbound.QUIC = &quicOptions{CongestionControl: spec.Naive.QUICCC}
		}
		return []any{inbound}, nil
	case "shadowsocks_singbox":
		return []any{renderShadowsocks(listen("shadowsocks", "ss-in"), spec)}, nil
	case "shadowtls_v3_shadowsocks_singbox":
		host, port, err := parseCamouflageServer(spec.ShadowTLS.CamouflageServer)
		if err != nil {
			return nil, err
		}
		inner := renderShadowsocks(listenOptions{
			Type: "shadowsocks", Tag: "ss-inner", Listen: "127.0.0.1", ListenPort: 32080,
		}, spec)
		outer := shadowTLSInbound{
			listenOptions: listen("shadowtls", "shadowtls-in"),
			Version:       3,
			Users:         passwordUsers(spec.Users),
			Handshake:     handshake{Server: host, ServerPort: port},
			StrictMode:    spec.ShadowTLS.StrictMode,
			WildcardSNI:   spec.ShadowTLS.WildcardSNI,
			Detour:        "ss-inner",
		}
		return []any{outer, inner}, nil
	default:
		return nil, fmt.Errorf("unsupported sing-box protocol %q", spec.ProtocolID)
	}
}

func renderShadowsocks(listen listenOptions, spec RenderSpec) shadowsocksInbound {
	return shadowsocksInbound{
		listenOptions: listen,
		Network:       "tcp",
		Method:        "2022-blake3-aes-128-gcm",
		Password:      spec.ShadowsocksServerPassword,
		Users:         shadowsocks2022Users(spec.Users),
		Multiplex:     multiplex{Enabled: true},
	}
}

// shadowsocks2022UserKeySize is the required decoded PSK length for
// 2022-blake3-aes-128-gcm user passwords (must match the cipher's key size).
const shadowsocks2022UserKeySize = 16

// shadowsocks2022Users filters spec users to those whose Secret is a valid
// standard base64 string that decodes to exactly shadowsocks2022UserKeySize
// bytes. Shadowsocks 2022 (2022-blake3-aes-128-gcm) decodes each user PSK
// with base64.StdEncoding and requires it to match the cipher's key size;
// secrets in other formats or lengths (e.g. UUIDs from a previous VLESS
// configuration, or mis-sized PSKs) cause sing-box to refuse the user's
// connections with "invalid request" errors. Invalid users are silently
// dropped — they can be re-provisioned with a proper 16-byte base64 PSK via
// the panel.
func shadowsocks2022Users(users []User) []passwordUser {
	result := make([]passwordUser, 0, len(users))
	for _, item := range users {
		decoded, err := base64.StdEncoding.DecodeString(item.Secret)
		if err != nil || len(decoded) != shadowsocks2022UserKeySize {
			continue
		}
		result = append(result, passwordUser{Name: item.ID, Password: item.Secret})
	}
	return result
}

func renderTLS(value TLS) tlsOptions {
	return tlsOptions{
		Enabled: value.Enabled, ServerName: value.ServerName,
		CertificatePath: value.CertFile, KeyPath: value.KeyFile,
	}
}

func proxyUser(value Credential) passwordUser {
	return passwordUser{Username: value.Username, Password: value.Password}
}

func passwordUsers(users []User) []passwordUser {
	result := make([]passwordUser, 0, len(users))
	for _, item := range users {
		result = append(result, passwordUser{Name: item.ID, Password: item.Secret})
	}
	return result
}

func flowWhenTLS(tls TLS, flow string) string {
	if tls.Enabled {
		return flow
	}
	return ""
}

func validateRenderSpec(spec RenderSpec) error {
	if spec.PublicPort < 1 || spec.PublicPort > 65535 {
		return fmt.Errorf("public port must be between 1 and 65535")
	}
	if spec.BackendPort < 1 || spec.BackendPort > 65535 {
		return fmt.Errorf("backend port must be between 1 and 65535")
	}
	if spec.TLS.Enabled && (spec.TLS.CertFile == "" || spec.TLS.KeyFile == "") {
		return fmt.Errorf("enabled TLS requires certificate and key paths")
	}
	switch spec.ProtocolID {
	case "mixed_singbox", "socks_singbox", "http_singbox", "naive_singbox":
		if spec.Proxy.Username == "" || spec.Proxy.Password == "" {
			return fmt.Errorf("%s requires proxy username and password", spec.ProtocolID)
		}
	case "hysteria2_singbox":
		if spec.Proxy.Password == "" {
			return fmt.Errorf("hysteria2_singbox requires a password")
		}
	case "trojan_singbox", "shadowsocks_singbox", "shadowtls_v3_shadowsocks_singbox":
		for _, user := range spec.Users {
			if user.ID == "" || user.Secret == "" {
				return fmt.Errorf("%s users require IDs and secrets", spec.ProtocolID)
			}
		}
	}
	if (spec.ProtocolID == "shadowsocks_singbox" || spec.ProtocolID == "shadowtls_v3_shadowsocks_singbox") &&
		spec.ShadowsocksServerPassword == "" {
		return fmt.Errorf("%s requires a shadowsocks server password", spec.ProtocolID)
	}
	return nil
}

func parseCamouflageServer(value string) (string, int, error) {
	value = strings.TrimSpace(value)
	if value == "" {
		value = "www.cloudflare.com:443"
	}
	host, portText, err := net.SplitHostPort(value)
	if err != nil {
		return "", 0, fmt.Errorf("invalid shadowtls camouflage server %q: %w", value, err)
	}
	port, err := strconv.Atoi(portText)
	if err != nil || port < 1 || port > 65535 {
		return "", 0, fmt.Errorf("invalid shadowtls camouflage port %q", portText)
	}
	return host, port, nil
}
