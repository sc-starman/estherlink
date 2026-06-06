package ipsec

import (
	"fmt"
	"net/netip"
	"sort"
	"strings"
)

type Client struct {
	Username string
	Secret   string
	Enabled  bool
}

type Paths struct {
	PPPOptions string
}

func RenderIPSecConfig(connectionName string) ([]byte, error) {
	connectionName = strings.TrimSpace(connectionName)
	if connectionName == "" || strings.ContainsAny(connectionName, " \t\r\n") {
		return nil, fmt.Errorf("invalid IPsec connection name")
	}
	return []byte(fmt.Sprintf(`config setup
  uniqueids=no

conn %s
  keyexchange=ikev1
  authby=psk
  type=transport
  left=%%defaultroute
  leftprotoport=17/1701
  right=%%any
  rightprotoport=17/%%any
  ike=aes256-sha1-modp2048,aes128-sha1-modp2048!
  esp=aes256-sha1,aes128-sha1!
  auto=add
`, connectionName)), nil
}

func RenderSecrets(preSharedKey string) ([]byte, error) {
	preSharedKey = strings.TrimSpace(preSharedKey)
	if len(preSharedKey) < 16 {
		return nil, fmt.Errorf("IPsec pre-shared key must contain at least 16 characters")
	}
	return []byte(fmt.Sprintf("%%any %%any : PSK %s\n", quote(preSharedKey))), nil
}

func RenderXL2TPD(network string, paths Paths) ([]byte, error) {
	localIP, poolStart, poolEnd, err := NetworkAddresses(network)
	if err != nil {
		return nil, err
	}
	if strings.TrimSpace(paths.PPPOptions) == "" {
		return nil, fmt.Errorf("PPP options path is required")
	}
	return []byte(fmt.Sprintf(`[global]
ipsec saref = no
listen-addr = 0.0.0.0

[lns default]
ip range = %s-%s
local ip = %s
require chap = yes
refuse pap = yes
require authentication = no
name = l2tpd
ppp debug = no
pppoptfile = %s
length bit = yes
`, poolStart, poolEnd, localIP, paths.PPPOptions)), nil
}

func RenderPPPOptions(dnsAddress string) []byte {
	dns := ""
	if strings.TrimSpace(dnsAddress) != "" {
		dns = "ms-dns " + strings.TrimSpace(dnsAddress) + "\n"
	}
	return []byte(`ipcp-accept-local
ipcp-accept-remote
noccp
auth
mtu 1400
mru 1400
nodefaultroute
lock
proxyarp
connect-delay 5000
refuse-pap
refuse-chap
refuse-mschap
require-mschap-v2
require-mppe-128
` + dns)
}

func RenderChapSecrets(clients []Client) []byte {
	ordered := append([]Client(nil), clients...)
	sort.Slice(ordered, func(i, j int) bool { return ordered[i].Username < ordered[j].Username })
	var builder strings.Builder
	for _, client := range ordered {
		if !client.Enabled || strings.TrimSpace(client.Username) == "" || client.Secret == "" {
			continue
		}
		fmt.Fprintf(&builder, "%s l2tpd %s *\n", quote(client.Username), quote(client.Secret))
	}
	return []byte(builder.String())
}

func NetworkAddresses(network string) (netip.Addr, netip.Addr, netip.Addr, error) {
	prefix, err := netip.ParsePrefix(strings.TrimSpace(network))
	if err != nil || !prefix.Addr().Is4() || prefix.Bits() < 16 || prefix.Bits() > 29 {
		return netip.Addr{}, netip.Addr{}, netip.Addr{}, fmt.Errorf("invalid IPsec/L2TP IPv4 network %q", network)
	}
	prefix = prefix.Masked()
	localIP := prefix.Addr().Next()
	poolStart := localIP.Next()
	poolEnd := broadcastAddress(prefix).Prev()
	if !prefix.Contains(poolEnd) || poolStart.Compare(poolEnd) > 0 {
		return netip.Addr{}, netip.Addr{}, netip.Addr{}, fmt.Errorf("IPsec/L2TP network %s has insufficient client addresses", prefix)
	}
	return localIP, poolStart, poolEnd, nil
}

func ConnectionName(relayID string) string {
	if len(relayID) > 8 {
		relayID = relayID[:8]
	}
	return "L2TP-PSK-" + strings.ToLower(relayID)
}

func quote(value string) string {
	value = strings.ReplaceAll(value, `\`, `\\`)
	value = strings.ReplaceAll(value, `"`, `\"`)
	return `"` + value + `"`
}

func broadcastAddress(prefix netip.Prefix) netip.Addr {
	raw := prefix.Addr().As4()
	value := uint32(raw[0])<<24 | uint32(raw[1])<<16 | uint32(raw[2])<<8 | uint32(raw[3])
	hostBits := 32 - prefix.Bits()
	if hostBits > 0 {
		value |= (uint32(1) << hostBits) - 1
	}
	return netip.AddrFrom4([4]byte{byte(value >> 24), byte(value >> 16), byte(value >> 8), byte(value)})
}
