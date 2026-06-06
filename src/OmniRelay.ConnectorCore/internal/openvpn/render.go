package openvpn

import (
	"crypto/sha256"
	"encoding/hex"
	"fmt"
	"net/netip"
	"sort"
	"strings"
)

type ServerOptions struct {
	RelayID           string
	PublicPort        int
	Network           string
	Interface         string
	CAFile            string
	CertFile          string
	KeyFile           string
	TLSCryptFile      string
	AuthVerifyCommand string
	CCDDir            string
	PoolFile          string
	StatusFile        string
	ClientDNS         string
}

type Client struct {
	ID       string
	Identity string
	Secret   string
	Enabled  bool
}

func RenderServerConfig(options ServerOptions) ([]byte, error) {
	prefix, err := parseNetwork(options.Network)
	if err != nil {
		return nil, err
	}
	if options.PublicPort < 1 || options.PublicPort > 65535 {
		return nil, fmt.Errorf("OpenVPN public port must be between 1 and 65535")
	}
	if strings.TrimSpace(options.Interface) == "" {
		options.Interface = "omniovpn0"
	}
	required := map[string]string{
		"CA file": options.CAFile, "certificate file": options.CertFile, "key file": options.KeyFile,
		"tls-crypt file": options.TLSCryptFile, "auth verify command": options.AuthVerifyCommand,
		"CCD directory": options.CCDDir, "pool file": options.PoolFile, "status file": options.StatusFile,
	}
	for label, value := range required {
		if strings.TrimSpace(value) == "" {
			return nil, fmt.Errorf("OpenVPN %s is required", label)
		}
	}
	if _, err := serverAddress(prefix); err != nil {
		return nil, err
	}
	netmask := netmaskString(prefix.Bits())
	dnsPush := ""
	if strings.TrimSpace(options.ClientDNS) != "" {
		dnsPush = fmt.Sprintf("push \"dhcp-option DNS %s\"\n", strings.TrimSpace(options.ClientDNS))
	}
	content := fmt.Sprintf(`port %d
proto tcp-server
dev-type tun
dev %s
topology subnet
server %s %s
push "redirect-gateway def1 bypass-dhcp"
push "block-ipv6"
%skeepalive 10 60
persist-key
persist-tun
ca %s
cert %s
key %s
dh none
tls-crypt %s
verify-client-cert require
username-as-common-name
auth-user-pass-verify %s via-file
script-security 2
client-config-dir %s
ifconfig-pool-persist %s
cipher AES-256-GCM
auth SHA256
data-ciphers AES-256-GCM:AES-128-GCM:CHACHA20-POLY1305
mssfix 1280
status-version 3
status %s 5
management 127.0.0.1 %d
verb 3
`, options.PublicPort, options.Interface, prefix.Addr(), netmask, dnsPush,
		options.CAFile, options.CertFile, options.KeyFile, options.TLSCryptFile,
		quoteCommand(options.AuthVerifyCommand), options.CCDDir, options.PoolFile, options.StatusFile, ManagementPort(options.RelayID))
	return []byte(content), nil
}

func AssignAddresses(network string, identities []string, existing map[string]string) (map[string]string, error) {
	prefix, err := parseNetwork(network)
	if err != nil {
		return nil, err
	}
	serverIP, err := serverAddress(prefix)
	if err != nil {
		return nil, err
	}
	unique := make(map[string]struct{})
	for _, identity := range identities {
		if value := strings.TrimSpace(identity); value != "" {
			unique[value] = struct{}{}
		}
	}
	ordered := make([]string, 0, len(unique))
	for identity := range unique {
		ordered = append(ordered, identity)
	}
	sort.Strings(ordered)
	result := make(map[string]string, len(ordered))
	used := map[netip.Addr]struct{}{serverIP: {}}
	for _, identity := range ordered {
		candidate, err := netip.ParseAddr(strings.TrimSpace(existing[identity]))
		if err == nil && prefix.Contains(candidate) && candidate != prefix.Addr() {
			if _, taken := used[candidate]; !taken {
				result[identity] = candidate.String()
				used[candidate] = struct{}{}
			}
		}
	}
	next := serverIP.Next()
	for _, identity := range ordered {
		if result[identity] != "" {
			continue
		}
		for prefix.Contains(next) {
			if next == broadcastAddress(prefix) {
				break
			}
			if _, taken := used[next]; !taken {
				result[identity] = next.String()
				used[next] = struct{}{}
				next = next.Next()
				break
			}
			next = next.Next()
		}
		if result[identity] == "" {
			return nil, fmt.Errorf("insufficient OpenVPN addresses in %s for %d clients", network, len(ordered))
		}
	}
	return result, nil
}

func RenderCCD(network string, assignments map[string]string) (map[string][]byte, error) {
	prefix, err := parseNetwork(network)
	if err != nil {
		return nil, err
	}
	netmask := netmaskString(prefix.Bits())
	result := make(map[string][]byte, len(assignments))
	for identity, address := range assignments {
		parsed, err := netip.ParseAddr(address)
		if err != nil || !prefix.Contains(parsed) {
			return nil, fmt.Errorf("invalid OpenVPN client address %q for %q", address, identity)
		}
		result[identity] = []byte(fmt.Sprintf("ifconfig-push %s %s\n", parsed, netmask))
	}
	return result, nil
}

func RenderAuthEntries(clients []Client) []byte {
	ordered := append([]Client(nil), clients...)
	sort.Slice(ordered, func(i, j int) bool { return ordered[i].Identity < ordered[j].Identity })
	var builder strings.Builder
	for _, client := range ordered {
		if !client.Enabled || client.Identity == "" || client.Secret == "" {
			continue
		}
		hash := sha256.Sum256([]byte(client.Secret))
		fmt.Fprintf(&builder, "%s:%s:shared-client\n", client.Identity, hex.EncodeToString(hash[:]))
	}
	return []byte(builder.String())
}

func ManagementPort(relayID string) int {
	if len(relayID) < 4 {
		return 7505
	}
	var value int
	if _, err := fmt.Sscanf(relayID[:4], "%x", &value); err != nil {
		return 7505
	}
	return 7505 + value%1000
}

func InterfaceName(relayID string) string {
	if len(relayID) < 7 {
		return "omniovpn"
	}
	return "omni" + strings.ToLower(relayID[:7])
}

func ServerAddress(network string) (netip.Addr, error) {
	prefix, err := parseNetwork(network)
	if err != nil {
		return netip.Addr{}, err
	}
	return serverAddress(prefix)
}

func quoteCommand(value string) string {
	value = strings.TrimSpace(value)
	if strings.ContainsAny(value, " \t") {
		return `"` + strings.ReplaceAll(value, `"`, `\"`) + `"`
	}
	return value
}

func parseNetwork(value string) (netip.Prefix, error) {
	prefix, err := netip.ParsePrefix(strings.TrimSpace(value))
	if err != nil || !prefix.Addr().Is4() {
		return netip.Prefix{}, fmt.Errorf("invalid IPv4 OpenVPN network %q", value)
	}
	return prefix.Masked(), nil
}

func serverAddress(prefix netip.Prefix) (netip.Addr, error) {
	server := prefix.Addr().Next()
	if !prefix.Contains(server) || server == broadcastAddress(prefix) {
		return netip.Addr{}, fmt.Errorf("OpenVPN network %s has no usable server address", prefix)
	}
	return server, nil
}

func broadcastAddress(prefix netip.Prefix) netip.Addr {
	raw := prefix.Addr().As4()
	bits := prefix.Bits()
	value := uint32(raw[0])<<24 | uint32(raw[1])<<16 | uint32(raw[2])<<8 | uint32(raw[3])
	hostBits := 32 - bits
	if hostBits > 0 {
		value |= (uint32(1) << hostBits) - 1
	}
	return netip.AddrFrom4([4]byte{byte(value >> 24), byte(value >> 16), byte(value >> 8), byte(value)})
}

func netmaskString(bits int) string {
	var value uint32
	if bits > 0 {
		value = ^uint32(0) << (32 - bits)
	}
	return fmt.Sprintf("%d.%d.%d.%d", byte(value>>24), byte(value>>16), byte(value>>8), byte(value))
}
