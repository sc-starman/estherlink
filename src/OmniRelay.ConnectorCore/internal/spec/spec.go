package spec

import (
	"bytes"
	"encoding/json"
	"errors"
	"fmt"
	"hash/fnv"
	"io"
	"net/netip"
	"net/url"
	"os"
	"regexp"
	"sort"
	"strings"

	"github.com/omnirelay/connector-core/internal/protocol"
)

const (
	APIVersion = "omnirelay.io/v1alpha1"
	Kind       = "Gateway"
)

var relayIDPattern = regexp.MustCompile(`^[a-fA-F0-9]{32}$`)

type GatewaySpec struct {
	APIVersion string        `json:"apiVersion"`
	Kind       string        `json:"kind"`
	RelayID    string        `json:"relayId"`
	Release    ReleaseSpec   `json:"release"`
	Gateway    Gateway       `json:"gateway"`
	Tunnel     TunnelSpec    `json:"tunnel,omitempty"`
	DNS        DNSSpec       `json:"dns,omitempty"`
	SingBox    SingBoxSpec   `json:"singBox,omitempty"`
	Panel      PanelSpec     `json:"panel,omitempty"`
	OpenVPN    OpenVPNSpec   `json:"openvpn,omitempty"`
	IPSecL2TP  IPSecL2TPSpec `json:"ipsecL2tp,omitempty"`
}

type ReleaseSpec struct {
	Channel              string `json:"channel"`
	ConnectorCoreVersion string `json:"connectorCoreVersion"`
}

type Gateway struct {
	Type          string `json:"type"`
	Protocol      string `json:"protocol"`
	PublicPort    int    `json:"publicPort"`
	ConnectorMode string `json:"connectorMode"`
}

type PanelSpec struct {
	Port           int    `json:"port,omitempty"`
	Username       string `json:"username,omitempty"`
	Password       string `json:"password,omitempty"`
	PublicHost     string `json:"publicHost,omitempty"`
	Domain         string `json:"domain,omitempty"`
	DomainOnly     bool   `json:"domainOnly,omitempty"`
	TLSEnabled     bool   `json:"tlsEnabled,omitempty"`
	TLSMode        string `json:"tlsMode,omitempty"`
	CertFile       string `json:"certFile,omitempty"`
	KeyFile        string `json:"keyFile,omitempty"`
	ArtifactFile   string `json:"artifactFile,omitempty"`
	ArtifactURL    string `json:"artifactUrl,omitempty"`
	ArtifactSHA256 string `json:"artifactSha256,omitempty"`
}

type NetworkSpec struct {
	Network string `json:"network,omitempty"`
}

type OpenVPNSpec struct {
	Network               string `json:"network,omitempty"`
	PublicHost            string `json:"publicHost,omitempty"`
	SharedCACertFile      string `json:"sharedCaCertFile,omitempty"`
	SharedClientCertFile  string `json:"sharedClientCertFile,omitempty"`
	SharedClientKeyFile   string `json:"sharedClientKeyFile,omitempty"`
	SharedTLSCryptKeyFile string `json:"sharedTlsCryptKeyFile,omitempty"`
}

type IPSecL2TPSpec struct {
	Network      string `json:"network,omitempty"`
	PreSharedKey string `json:"preSharedKey,omitempty"`
}

type TunnelSpec struct {
	BackendHost   string   `json:"backendHost,omitempty"`
	BackendPort   int      `json:"backendPort,omitempty"`
	FRPServerPort int      `json:"frpServerPort,omitempty"`
	FRPAuthToken  string   `json:"frpAuthToken,omitempty"`
	ProbeURLs     []string `json:"probeUrls,omitempty"`
	TimeoutSecond int      `json:"timeoutSeconds,omitempty"`
}

type DNSSpec struct {
	DoHEndpoints  []string `json:"dohEndpoints,omitempty"`
	ListenAddress string   `json:"listenAddress,omitempty"`
	ListenPort    int      `json:"listenPort,omitempty"`
}

type SingBoxSpec struct {
	TLS                       TLSSpec        `json:"tls,omitempty"`
	Proxy                     CredentialSpec `json:"proxy,omitempty"`
	VLESSFlow                 string         `json:"vlessFlow,omitempty"`
	Hysteria2                 Hysteria2Spec  `json:"hysteria2,omitempty"`
	Naive                     NaiveSpec      `json:"naive,omitempty"`
	ShadowsocksServerPassword string         `json:"shadowsocksServerPassword,omitempty"`
	ShadowTLS                 ShadowTLSSpec  `json:"shadowTls,omitempty"`
}

type TLSSpec struct {
	Enabled    bool   `json:"enabled,omitempty"`
	ServerName string `json:"serverName,omitempty"`
	CertFile   string `json:"certFile,omitempty"`
	KeyFile    string `json:"keyFile,omitempty"`
}

type CredentialSpec struct {
	Username string `json:"username,omitempty"`
	Password string `json:"password,omitempty"`
}

type Hysteria2Spec struct {
	UpMbps                int    `json:"upMbps,omitempty"`
	DownMbps              int    `json:"downMbps,omitempty"`
	ObfsPassword          string `json:"obfsPassword,omitempty"`
	IgnoreClientBandwidth bool   `json:"ignoreClientBandwidth,omitempty"`
	MasqueradeURL         string `json:"masqueradeUrl,omitempty"`
}

type NaiveSpec struct {
	Network string `json:"network,omitempty"`
	QUICCC  string `json:"quicCongestionControl,omitempty"`
}

type ShadowTLSSpec struct {
	CamouflageServer string `json:"camouflageServer,omitempty"`
	StrictMode       bool   `json:"strictMode,omitempty"`
	WildcardSNI      string `json:"wildcardSni,omitempty"`
}

type ValidationError struct {
	Problems []string
}

func (e *ValidationError) Error() string {
	return strings.Join(e.Problems, "; ")
}

func LoadFile(path string) (GatewaySpec, error) {
	content, err := os.ReadFile(path)
	if err != nil {
		return GatewaySpec{}, err
	}
	return Parse(content)
}

func Parse(content []byte) (GatewaySpec, error) {
	var value GatewaySpec
	decoder := json.NewDecoder(bytes.NewReader(content))
	decoder.DisallowUnknownFields()
	if err := decoder.Decode(&value); err != nil {
		return GatewaySpec{}, fmt.Errorf("invalid gateway spec: %w", err)
	}
	if err := ensureEOF(decoder); err != nil {
		return GatewaySpec{}, err
	}
	value.ApplyDefaults()
	if err := value.Validate(); err != nil {
		return GatewaySpec{}, err
	}
	return value, nil
}

func ensureEOF(decoder *json.Decoder) error {
	var extra any
	err := decoder.Decode(&extra)
	if errors.Is(err, io.EOF) {
		return nil
	}
	if err == nil {
		return errors.New("invalid gateway spec: multiple JSON values are not allowed")
	}
	return fmt.Errorf("invalid gateway spec: %w", err)
}

func (s *GatewaySpec) ApplyDefaults() {
	s.APIVersion = strings.TrimSpace(s.APIVersion)
	s.Kind = strings.TrimSpace(s.Kind)
	s.RelayID = strings.ToLower(strings.TrimSpace(s.RelayID))
	s.Release.Channel = strings.ToLower(strings.TrimSpace(s.Release.Channel))
	s.Release.ConnectorCoreVersion = strings.TrimSpace(s.Release.ConnectorCoreVersion)
	s.Gateway.Type = strings.ToLower(strings.TrimSpace(s.Gateway.Type))
	s.Gateway.Protocol = strings.ToLower(strings.TrimSpace(s.Gateway.Protocol))
	s.Gateway.ConnectorMode = strings.ToLower(strings.TrimSpace(s.Gateway.ConnectorMode))
	if s.Release.Channel == "" {
		s.Release.Channel = "stable"
	}
	if s.Gateway.Type == "" {
		s.Gateway.Type = "remote"
	}
	if s.Gateway.ConnectorMode == "" {
		s.Gateway.ConnectorMode = "full_tunnel"
	}
	s.Tunnel.BackendHost = strings.TrimSpace(s.Tunnel.BackendHost)
	if s.Tunnel.BackendHost == "" {
		s.Tunnel.BackendHost = "127.0.0.1"
	}
	if s.Tunnel.BackendPort == 0 {
		s.Tunnel.BackendPort = 15000
	}
	s.Tunnel.FRPAuthToken = strings.TrimSpace(s.Tunnel.FRPAuthToken)
	if len(s.Tunnel.ProbeURLs) == 0 {
		s.Tunnel.ProbeURLs = []string{"https://8.8.8.8/", "https://dns.google/", "https://9.9.9.9/"}
	}
	for index := range s.Tunnel.ProbeURLs {
		s.Tunnel.ProbeURLs[index] = strings.TrimSpace(s.Tunnel.ProbeURLs[index])
	}
	if s.Tunnel.TimeoutSecond == 0 {
		s.Tunnel.TimeoutSecond = 20
	}
	if len(s.DNS.DoHEndpoints) == 0 {
		s.DNS.DoHEndpoints = []string{"https://1.1.1.1/dns-query", "https://8.8.8.8/dns-query"}
	}
	if strings.TrimSpace(s.DNS.ListenAddress) == "" {
		s.DNS.ListenAddress = "127.0.0.1"
	}
	if s.DNS.ListenPort == 0 && s.RelayID != "" {
		s.DNS.ListenPort = deterministicPort("dns:"+s.RelayID, 35000, 10000)
	}
	if s.SingBox.Hysteria2.UpMbps == 0 {
		s.SingBox.Hysteria2.UpMbps = 100
	}
	if s.SingBox.Hysteria2.DownMbps == 0 {
		s.SingBox.Hysteria2.DownMbps = 100
	}
	if s.SingBox.ShadowTLS.CamouflageServer == "" {
		s.SingBox.ShadowTLS.CamouflageServer = "www.cloudflare.com:443"
	}
	if s.Gateway.Protocol == "openvpn_tcp_singbox" && strings.TrimSpace(s.OpenVPN.Network) == "" {
		s.OpenVPN.Network = "10.29.0.0/24"
	}
	s.OpenVPN.PublicHost = strings.TrimSpace(s.OpenVPN.PublicHost)
	s.Panel.TLSMode = strings.ToLower(strings.TrimSpace(s.Panel.TLSMode))
	if s.Panel.TLSEnabled && s.Panel.TLSMode == "" {
		s.Panel.TLSMode = "uploaded"
	}
	if s.Gateway.Protocol == "ipsec_l2tp_singbox" && strings.TrimSpace(s.IPSecL2TP.Network) == "" {
		s.IPSecL2TP.Network = "10.39.0.0/24"
	}
}

func (s GatewaySpec) Validate() error {
	problems := make([]string, 0)
	if s.APIVersion != APIVersion {
		problems = append(problems, "apiVersion must be "+APIVersion)
	}
	if s.Kind != Kind {
		problems = append(problems, "kind must be "+Kind)
	}
	if !relayIDPattern.MatchString(s.RelayID) {
		problems = append(problems, "relayId must contain exactly 32 hexadecimal characters")
	}
	if s.Release.Channel != "stable" && s.Release.Channel != "beta" {
		problems = append(problems, "release.channel must be stable or beta")
	}
	if s.Gateway.Type != "remote" && s.Gateway.Type != "local" {
		problems = append(problems, "gateway.type must be remote or local")
	}
	if _, ok := protocol.Lookup(s.Gateway.Protocol); !ok {
		problems = append(problems, "gateway.protocol is unsupported")
	}
	if s.Gateway.PublicPort < 1 || s.Gateway.PublicPort > 65535 {
		problems = append(problems, "gateway.publicPort must be between 1 and 65535")
	}
	if s.Gateway.ConnectorMode != "full_tunnel" && s.Gateway.ConnectorMode != "internal_tunnel" {
		problems = append(problems, "gateway.connectorMode must be full_tunnel or internal_tunnel")
	}
	if s.Panel.Port < 0 || s.Panel.Port > 65535 {
		problems = append(problems, "panel.port must be between 1 and 65535 when set")
	}
	if s.Panel.Port > 0 && s.Panel.Port == s.Gateway.PublicPort {
		problems = append(problems, "panel.port conflicts with gateway.publicPort")
	}
	if s.Panel.Port > 0 && (strings.TrimSpace(s.Panel.PublicHost) == "" || strings.ContainsAny(s.Panel.PublicHost, " \t\r\n")) {
		problems = append(problems, "panel.publicHost is required and must not contain whitespace when panel is enabled")
	}
	if s.Panel.DomainOnly && strings.TrimSpace(s.Panel.Domain) == "" {
		problems = append(problems, "panel.domain is required when panel.domainOnly is enabled")
	}
	if s.Panel.TLSEnabled {
		if s.Panel.TLSMode != "uploaded" && s.Panel.TLSMode != "self_signed" && s.Panel.TLSMode != "certbot" {
			problems = append(problems, "panel.tlsMode must be uploaded, self_signed, or certbot when TLS is enabled")
		}
		if s.Panel.TLSMode == "uploaded" && (strings.TrimSpace(s.Panel.CertFile) == "" || strings.TrimSpace(s.Panel.KeyFile) == "") {
			problems = append(problems, "uploaded panel TLS requires certFile and keyFile")
		}
		if s.Panel.TLSMode == "certbot" && strings.TrimSpace(s.Panel.Domain) == "" {
			problems = append(problems, "certbot panel TLS requires panel.domain")
		}
	}
	artifactFileSet := strings.TrimSpace(s.Panel.ArtifactFile) != ""
	artifactURLSet := strings.TrimSpace(s.Panel.ArtifactURL) != ""
	artifactHashSet := strings.TrimSpace(s.Panel.ArtifactSHA256) != ""
	if artifactFileSet && artifactURLSet {
		problems = append(problems, "panel artifactFile and artifactUrl are mutually exclusive")
	}
	if (artifactFileSet || artifactURLSet) != artifactHashSet {
		problems = append(problems, "panel artifact source and artifactSha256 must either both be set or both be omitted")
	}
	if artifactURLSet {
		parsed, err := url.Parse(strings.TrimSpace(s.Panel.ArtifactURL))
		if err != nil || parsed.Host == "" || parsed.Scheme != "https" {
			problems = append(problems, "panel.artifactUrl must be a valid HTTPS URL")
		}
	}
	if s.Panel.ArtifactSHA256 != "" && !regexp.MustCompile(`^[a-fA-F0-9]{64}$`).MatchString(s.Panel.ArtifactSHA256) {
		problems = append(problems, "panel.artifactSha256 must contain exactly 64 hexadecimal characters")
	}
	if s.Tunnel.BackendPort < 1 || s.Tunnel.BackendPort > 65535 {
		problems = append(problems, "tunnel.backendPort must be between 1 and 65535")
	}
	if s.Gateway.Type == "remote" {
		if s.Tunnel.FRPServerPort < 1 || s.Tunnel.FRPServerPort > 65535 {
			problems = append(problems, "tunnel.frpServerPort must be between 1 and 65535 for remote gateways")
		}
		if len(s.Tunnel.FRPAuthToken) < 16 || strings.ContainsAny(s.Tunnel.FRPAuthToken, "\"\r\n") {
			problems = append(problems, "tunnel.frpAuthToken must contain at least 16 safe characters for remote gateways")
		}
	}
	if s.Tunnel.TimeoutSecond < 3 || s.Tunnel.TimeoutSecond > 120 {
		problems = append(problems, "tunnel.timeoutSeconds must be between 3 and 120")
	}
	if len(s.Tunnel.ProbeURLs) > 10 {
		problems = append(problems, "tunnel.probeUrls supports at most 10 targets")
	}
	for _, target := range s.Tunnel.ProbeURLs {
		parsed, err := url.Parse(target)
		if err != nil || parsed.Host == "" || (parsed.Scheme != "http" && parsed.Scheme != "https") {
			problems = append(problems, "tunnel.probeUrls contains an invalid HTTP(S) URL")
			break
		}
	}
	for _, target := range s.DNS.DoHEndpoints {
		parsed, err := url.Parse(strings.TrimSpace(target))
		if err != nil || parsed.Host == "" || parsed.Scheme != "https" {
			problems = append(problems, "dns.dohEndpoints contains an invalid HTTPS URL")
			break
		}
	}
	dnsAddress, dnsAddressErr := netip.ParseAddr(strings.TrimSpace(s.DNS.ListenAddress))
	if dnsAddressErr != nil || !dnsAddress.IsLoopback() {
		problems = append(problems, "dns.listenAddress must be a loopback IP address")
	}
	if s.DNS.ListenPort < 1 || s.DNS.ListenPort > 65535 {
		problems = append(problems, "dns.listenPort must be between 1 and 65535")
	}
	for label, port := range map[string]int{"gateway.publicPort": s.Gateway.PublicPort, "panel.port": s.Panel.Port, "tunnel.backendPort": s.Tunnel.BackendPort, "tunnel.frpServerPort": s.Tunnel.FRPServerPort} {
		if port > 0 && s.DNS.ListenPort == port {
			problems = append(problems, "dns.listenPort conflicts with "+label)
		}
	}
	ports := map[int]string{}
	for label, port := range map[string]int{"gateway.publicPort": s.Gateway.PublicPort, "panel.port": s.Panel.Port, "tunnel.backendPort": s.Tunnel.BackendPort, "tunnel.frpServerPort": s.Tunnel.FRPServerPort} {
		if port == 0 {
			continue
		}
		if previous, exists := ports[port]; exists {
			problems = append(problems, label+" conflicts with "+previous)
			continue
		}
		ports[port] = label
	}
	if s.SingBox.TLS.Enabled && (strings.TrimSpace(s.SingBox.TLS.CertFile) == "" || strings.TrimSpace(s.SingBox.TLS.KeyFile) == "") {
		problems = append(problems, "singBox.tls requires certFile and keyFile when enabled")
	}
	if s.Gateway.Protocol == "vless_plain_singbox" && s.SingBox.TLS.Enabled {
		problems = append(problems, "singBox.tls must be disabled for vless_plain_singbox")
	}
	switch s.Gateway.Protocol {
	case "mixed_singbox", "socks_singbox", "http_singbox", "naive_singbox":
		if s.SingBox.Proxy.Username == "" || s.SingBox.Proxy.Password == "" {
			problems = append(problems, "singBox.proxy username and password are required for selected protocol")
		}
	case "hysteria2_singbox":
		if s.SingBox.Proxy.Password == "" {
			problems = append(problems, "singBox.proxy password is required for selected protocol")
		}
	case "shadowsocks_singbox", "shadowtls_v3_shadowsocks_singbox":
		if s.SingBox.ShadowsocksServerPassword == "" {
			problems = append(problems, "singBox.shadowsocksServerPassword is required for selected protocol")
		}
	}
	if s.Gateway.Protocol == "openvpn_tcp_singbox" {
		if err := validateIPv4ClientNetwork(s.OpenVPN.Network); err != nil {
			problems = append(problems, "openvpn.network "+err.Error())
		}
		if strings.TrimSpace(s.OpenVPN.PublicHost) == "" || strings.ContainsAny(s.OpenVPN.PublicHost, " \t\r\n") {
			problems = append(problems, "openvpn.publicHost is required and must not contain whitespace")
		}
		assetPaths := []string{
			s.OpenVPN.SharedCACertFile,
			s.OpenVPN.SharedClientCertFile,
			s.OpenVPN.SharedClientKeyFile,
			s.OpenVPN.SharedTLSCryptKeyFile,
		}
		supplied := 0
		for _, path := range assetPaths {
			if strings.TrimSpace(path) != "" {
				supplied++
			}
		}
		if supplied != 0 && supplied != len(assetPaths) {
			problems = append(problems, "openvpn shared asset paths must either all be set or all be omitted")
		}
	}
	if s.Gateway.Protocol == "ipsec_l2tp_singbox" {
		if err := validateIPv4ClientNetwork(s.IPSecL2TP.Network); err != nil {
			problems = append(problems, "ipsecL2tp.network "+err.Error())
		}
		if len(strings.TrimSpace(s.IPSecL2TP.PreSharedKey)) < 16 {
			problems = append(problems, "ipsecL2tp.preSharedKey must contain at least 16 characters")
		}
	}
	sort.Strings(problems)
	if len(problems) > 0 {
		return &ValidationError{Problems: problems}
	}
	return nil
}

func deterministicPort(seed string, base int, span int) int {
	hash := fnv.New32a()
	_, _ = hash.Write([]byte(seed))
	return base + int(hash.Sum32()%uint32(span))
}

func validateIPv4ClientNetwork(value string) error {
	prefix, err := netip.ParsePrefix(strings.TrimSpace(value))
	if err != nil || !prefix.Addr().Is4() {
		return errors.New("must be a valid IPv4 prefix")
	}
	if prefix.Bits() < 16 || prefix.Bits() > 29 {
		return errors.New("prefix length must be between /16 and /29")
	}
	return nil
}

func (s GatewaySpec) CanonicalJSON() ([]byte, error) {
	return json.MarshalIndent(s, "", "  ")
}
