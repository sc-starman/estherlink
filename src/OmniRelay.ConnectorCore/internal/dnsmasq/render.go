package dnsmasq

import (
	"fmt"
	"net/netip"
	"strings"
)

func Render(interfaceName string, listenAddress string, upstreamAddress string, upstreamPort int) ([]byte, error) {
	if strings.TrimSpace(interfaceName) == "" {
		return nil, fmt.Errorf("dnsmasq interface is required")
	}
	listen, err := netip.ParseAddr(strings.TrimSpace(listenAddress))
	if err != nil || !listen.Is4() {
		return nil, fmt.Errorf("dnsmasq listen address must be IPv4")
	}
	upstream, err := netip.ParseAddr(strings.TrimSpace(upstreamAddress))
	if err != nil || !upstream.IsLoopback() {
		return nil, fmt.Errorf("dnsmasq upstream address must be loopback")
	}
	if upstreamPort < 1 || upstreamPort > 65535 {
		return nil, fmt.Errorf("dnsmasq upstream port must be between 1 and 65535")
	}
	return []byte(fmt.Sprintf("interface=%s\nlisten-address=%s\nserver=%s#%d\n", interfaceName, listen, upstream, upstreamPort)), nil
}
