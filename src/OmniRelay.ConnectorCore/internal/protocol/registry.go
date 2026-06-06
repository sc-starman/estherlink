package protocol

import "sort"

type Definition struct {
	ID               string   `json:"id"`
	Runtime          string   `json:"runtime"`
	PerClient        bool     `json:"perClient"`
	RequiredPackages []string `json:"requiredPackages"`
	AccountingSource string   `json:"accountingSource"`
}

var definitions = map[string]Definition{
	"vless_plain_singbox": {
		ID: "vless_plain_singbox", Runtime: "singbox", PerClient: true, AccountingSource: "connector_tracker",
	},
	"vless_tls_singbox": {
		ID: "vless_tls_singbox", Runtime: "singbox", PerClient: true, AccountingSource: "connector_tracker",
	},
	"mixed_singbox": {
		ID: "mixed_singbox", Runtime: "singbox", AccountingSource: "connector_tracker",
	},
	"socks_singbox": {
		ID: "socks_singbox", Runtime: "singbox", AccountingSource: "connector_tracker",
	},
	"http_singbox": {
		ID: "http_singbox", Runtime: "singbox", AccountingSource: "connector_tracker",
	},
	"hysteria2_singbox": {
		ID: "hysteria2_singbox", Runtime: "singbox", AccountingSource: "connector_tracker",
	},
	"trojan_singbox": {
		ID: "trojan_singbox", Runtime: "singbox", PerClient: true, AccountingSource: "connector_tracker",
	},
	"naive_singbox": {
		ID: "naive_singbox", Runtime: "singbox", AccountingSource: "connector_tracker",
	},
	"shadowsocks_singbox": {
		ID: "shadowsocks_singbox", Runtime: "singbox", PerClient: true, AccountingSource: "connector_tracker",
	},
	"shadowtls_v3_shadowsocks_singbox": {
		ID: "shadowtls_v3_shadowsocks_singbox", Runtime: "singbox", PerClient: true, AccountingSource: "connector_tracker",
	},
	"openvpn_tcp_singbox": {
		ID: "openvpn_tcp_singbox", Runtime: "openvpn", RequiredPackages: []string{"openvpn", "easy-rsa", "dnsmasq", "iproute2"}, AccountingSource: "openvpn_status",
	},
	"ipsec_l2tp_singbox": {
		ID: "ipsec_l2tp_singbox", Runtime: "ipsec_l2tp", RequiredPackages: []string{"ppp", "xl2tpd", "strongswan", "dnsmasq"}, AccountingSource: "ipsec_ppp",
	},
}

func Lookup(id string) (Definition, bool) {
	definition, ok := definitions[id]
	return definition, ok
}

func All() []Definition {
	result := make([]Definition, 0, len(definitions))
	for _, definition := range definitions {
		result = append(result, definition)
	}
	sort.Slice(result, func(i, j int) bool { return result[i].ID < result[j].ID })
	return result
}
