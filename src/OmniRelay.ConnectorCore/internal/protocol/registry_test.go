package protocol

import "testing"

func TestRegistryContainsExpectedProtocols(t *testing.T) {
	for _, id := range []string{
		"vless_plain_singbox", "vless_tls_singbox", "mixed_singbox", "socks_singbox", "http_singbox",
		"hysteria2_singbox", "trojan_singbox", "naive_singbox", "shadowsocks_singbox",
		"shadowtls_v3_shadowsocks_singbox", "openvpn_tcp_singbox", "ipsec_l2tp_singbox",
	} {
		if _, ok := Lookup(id); !ok {
			t.Fatalf("protocol missing from registry: %s", id)
		}
	}
}
