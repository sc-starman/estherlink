package panel

import (
	"crypto/sha256"
	"encoding/hex"
	"fmt"
	"hash/fnv"
	"strconv"
	"strings"

	"github.com/omnirelay/connector-core/internal/spec"
)

type Options struct {
	InternalPort int
}

func InternalPort(relayID string) int {
	hash := fnv.New32a()
	_, _ = hash.Write([]byte("panel:" + relayID))
	return 20000 + int(hash.Sum32()%10000)
}

func RenderEnvironment(gatewaySpec spec.GatewaySpec, options Options) ([]byte, error) {
	if options.InternalPort == 0 {
		options.InternalPort = InternalPort(gatewaySpec.RelayID)
	}
	if options.InternalPort < 1 || options.InternalPort > 65535 {
		return nil, fmt.Errorf("invalid OmniPanel internal port")
	}
	gatewayRoot := "/etc/omnirelay/relays/" + gatewaySpec.RelayID + "/gateway"
	accountingDB := gatewayRoot + "/connector/accounting.db"
	syncCommand := "sudo /usr/local/bin/connector-core clients sync --relay-id " + gatewaySpec.RelayID + " --json"
	sessionHash := sha256.Sum256([]byte("panel-session:" + gatewaySpec.RelayID + ":" + gatewaySpec.Panel.Password))
	values := map[string]string{
		"NODE_ENV":                            "production",
		"HOSTNAME":                            "127.0.0.1",
		"PORT":                                strconv.Itoa(options.InternalPort),
		"SESSION_SECRET":                      hex.EncodeToString(sessionHash[:]),
		"PANEL_SSL_ENABLED":                   strconv.FormatBool(gatewaySpec.Panel.TLSEnabled),
		"OMNIPANEL_SESSION_SECURE":            strconv.FormatBool(gatewaySpec.Panel.TLSEnabled),
		"OMNIPANEL_AUTH_USERNAME":             gatewaySpec.Panel.Username,
		"OMNIPANEL_AUTH_PASSWORD":             gatewaySpec.Panel.Password,
		"OMNIRELAY_ACTIVE_PROTOCOL":           gatewaySpec.Gateway.Protocol,
		"PANEL_PUBLIC_PORT":                   strconv.Itoa(gatewaySpec.Panel.Port),
		"PANEL_PUBLIC_HOST":                   gatewaySpec.Panel.PublicHost,
		"SINGBOX_PUBLIC_PORT":                 strconv.Itoa(gatewaySpec.Gateway.PublicPort),
		"SINGBOX_RELOAD_COMMAND":              syncCommand,
		"OPENVPN_SYNC_COMMAND":                syncCommand,
		"IPSEC_L2TP_SYNC_COMMAND":             syncCommand,
		"SINGBOX_ACCOUNTING_DB":               accountingDB,
		"OPENVPN_ACCOUNTING_DB":               accountingDB,
		"IPSEC_L2TP_ACCOUNTING_DB":            accountingDB,
		"OPENVPN_EXPORT_DIR":                  gatewayRoot + "/openvpn/exports",
		"OPENVPN_STATE_DIR":                   gatewayRoot + "/openvpn",
		"OPENVPN_STATUS_FILE":                 "/var/log/openvpn/omnirelay-status-" + gatewaySpec.RelayID + ".log",
		"OPENVPN_PUBLIC_PORT":                 strconv.Itoa(gatewaySpec.Gateway.PublicPort),
		"IPSEC_L2TP_PSK_FILE":                 gatewayRoot + "/ipsec-l2tp/ipsec.secrets",
		"IPSEC_L2TP_RUNTIME_FILE":             gatewayRoot + "/ipsec-l2tp/runtime.json",
		"OMNIRELAY_PROTOCOL_BACKUP_DIR":       gatewayRoot + "/panel/backups",
		"SINGBOX_SHADOWSOCKS_SERVER_PASSWORD": gatewaySpec.SingBox.ShadowsocksServerPassword,
		"SHADOWTLS_CAMOUFLAGE_SERVER":         gatewaySpec.SingBox.ShadowTLS.CamouflageServer,
		"SHADOWTLS_PUBLIC_PORT":               strconv.Itoa(gatewaySpec.Gateway.PublicPort),
		"SINGBOX_VLESS_TLS_ENABLED":           strconv.FormatBool(gatewaySpec.SingBox.TLS.Enabled),
	}
	order := []string{
		"NODE_ENV", "HOSTNAME", "PORT", "SESSION_SECRET", "PANEL_SSL_ENABLED", "OMNIPANEL_SESSION_SECURE",
		"OMNIPANEL_AUTH_USERNAME", "OMNIPANEL_AUTH_PASSWORD", "OMNIRELAY_ACTIVE_PROTOCOL",
		"PANEL_PUBLIC_PORT", "PANEL_PUBLIC_HOST", "SINGBOX_PUBLIC_PORT", "SINGBOX_RELOAD_COMMAND",
		"OPENVPN_SYNC_COMMAND", "IPSEC_L2TP_SYNC_COMMAND", "SINGBOX_ACCOUNTING_DB", "OPENVPN_ACCOUNTING_DB",
		"IPSEC_L2TP_ACCOUNTING_DB", "OPENVPN_EXPORT_DIR", "OPENVPN_STATE_DIR", "OPENVPN_STATUS_FILE",
		"OPENVPN_PUBLIC_PORT", "IPSEC_L2TP_PSK_FILE", "IPSEC_L2TP_RUNTIME_FILE", "OMNIRELAY_PROTOCOL_BACKUP_DIR",
		"SINGBOX_SHADOWSOCKS_SERVER_PASSWORD", "SHADOWTLS_CAMOUFLAGE_SERVER", "SHADOWTLS_PUBLIC_PORT",
		"SINGBOX_VLESS_TLS_ENABLED",
	}
	var builder strings.Builder
	for _, key := range order {
		fmt.Fprintf(&builder, "%s=%s\n", key, quoteEnvironment(values[key]))
	}
	return []byte(builder.String()), nil
}

func RenderSudoers(gatewaySpec spec.GatewaySpec) []byte {
	return []byte(fmt.Sprintf(
		"omnigateway ALL=(root) NOPASSWD: /usr/local/bin/connector-core clients sync --relay-id %s --json\n",
		gatewaySpec.RelayID,
	))
}

func RenderNginx(gatewaySpec spec.GatewaySpec, options Options) ([]byte, error) {
	if gatewaySpec.Panel.Port < 1 || gatewaySpec.Panel.Port > 65535 {
		return nil, fmt.Errorf("panel port must be between 1 and 65535")
	}
	if options.InternalPort == 0 {
		options.InternalPort = InternalPort(gatewaySpec.RelayID)
	}
	domain := strings.TrimSpace(gatewaySpec.Panel.Domain)
	if gatewaySpec.Panel.DomainOnly && domain == "" {
		return nil, fmt.Errorf("domain-only panel access requires a domain")
	}
	if gatewaySpec.Panel.TLSEnabled && (strings.TrimSpace(gatewaySpec.Panel.CertFile) == "" || strings.TrimSpace(gatewaySpec.Panel.KeyFile) == "") {
		return nil, fmt.Errorf("panel TLS requires certificate and key paths")
	}
	serverName := "_"
	if domain != "" {
		serverName = domain
	}
	listen := strconv.Itoa(gatewaySpec.Panel.Port)
	tls := ""
	if gatewaySpec.Panel.TLSEnabled {
		listen += " ssl"
		tls = fmt.Sprintf(`    ssl_certificate %s;
    ssl_certificate_key %s;
    ssl_session_cache shared:SSL:10m;
    ssl_session_timeout 1d;
    ssl_protocols TLSv1.2 TLSv1.3;
`, gatewaySpec.Panel.CertFile, gatewaySpec.Panel.KeyFile)
	}
	hostGuard := ""
	if gatewaySpec.Panel.DomainOnly {
		hostGuard = fmt.Sprintf(`    if ($host != "%s") {
        return 444;
    }
`, domain)
	}
	return []byte(fmt.Sprintf(`server {
    listen %s;
    server_name %s;
%s%s
    location / {
        proxy_http_version 1.1;
        proxy_set_header Host $host;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection "upgrade";
        proxy_pass http://127.0.0.1:%d;
    }
}
`, listen, serverName, tls, hostGuard, options.InternalPort)), nil
}

func quoteEnvironment(value string) string {
	value = strings.ReplaceAll(value, `\`, `\\`)
	value = strings.ReplaceAll(value, `"`, `\"`)
	value = strings.ReplaceAll(value, "\n", `\n`)
	return `"` + value + `"`
}
