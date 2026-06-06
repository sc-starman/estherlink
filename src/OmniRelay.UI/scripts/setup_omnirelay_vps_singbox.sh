#!/usr/bin/env bash
set -Eeuo pipefail
IFS=$'\n\t'

SCRIPT_NAME="$(basename "$0")"
GATEWAYCTL_SOURCE="${BASH_SOURCE[0]:-$0}"
CONNECTOR_MODE="full_tunnel"

PROTOCOL_ID=""
PROTOCOL_CLIENTS_FILE=""
PROTOCOL_RUNTIME_FILE=""
PROTOCOL_ENV_FILE=""
OUTPUT_JSON="false"
COMMAND=""
SELECTED_PROTOCOL=""

TLS_ENABLED="false"
TLS_SERVER_NAME=""
TLS_CERT_FILE=""
TLS_KEY_FILE=""
VLESS_FLOW=""
PROXY_USERNAME=""
PROXY_PASSWORD=""
HYSTERIA2_UP_MBPS="0"
HYSTERIA2_DOWN_MBPS="0"
HYSTERIA2_OBFS_PASSWORD=""
HYSTERIA2_IGNORE_CLIENT_BANDWIDTH="false"
HYSTERIA2_MASQUERADE_URL=""
NAIVE_NETWORK=""
NAIVE_QUIC_CC=""
CAMOUFLAGE_SERVER=""
SHADOWTLS_STRICT_MODE="false"
SHADOWTLS_WILDCARD_SNI=""

load_connector_common() {
  local candidate
  for candidate in \
    "/tmp/omnirelay-singbox-connector-common.sh" \
    "/usr/local/lib/omnirelay/singbox-connector-common.sh" \
    "$(dirname "$0")/setup_omnirelay_gateway_singbox_connector_common.sh"; do
    if [[ -f "$candidate" ]]; then
      # shellcheck disable=SC1090
      source "$candidate"
      return 0
    fi
  done
  echo "ERROR: shared sing-box connector common script not found." >&2
  exit 1
}

load_connector_common

usage() {
  cat <<EOF
Usage: ${SCRIPT_NAME} <command> --protocol <protocol-id> [options]
Protocols:
  vless_tls_singbox | mixed_singbox | socks_singbox | http_singbox
  hysteria2_singbox | trojan_singbox | naive_singbox
  shadowsocks_singbox | shadowtls_v3_shadowsocks_singbox
EOF
}

require_value() {
  local flag="$1"
  (( $# >= 2 )) || die "Missing value for ${flag}"
}

is_valid_protocol() {
  case "$1" in
    vless_tls_singbox|mixed_singbox|socks_singbox|http_singbox|hysteria2_singbox|trojan_singbox|naive_singbox|shadowsocks_singbox|shadowtls_v3_shadowsocks_singbox) return 0 ;;
    *) return 1 ;;
  esac
}

parse_args() {
  while (($# > 0)); do
    case "$1" in
      --json) OUTPUT_JSON="true"; shift ;;
      --protocol) require_value "$1" "${2:-}"; SELECTED_PROTOCOL="$2"; shift 2 ;;
      --public-port) require_value "$1" "${2:-}"; PUBLIC_PORT="$2"; shift 2 ;;
      --panel-port) require_value "$1" "${2:-}"; PANEL_PORT="$2"; shift 2 ;;
      --backend-port) require_value "$1" "${2:-}"; BACKEND_PORT="$2"; shift 2 ;;
      --ssh-port) require_value "$1" "${2:-}"; SSH_PORT="$2"; shift 2 ;;
      --frp-server-port) require_value "$1" "${2:-}"; FRP_SERVER_PORT="$2"; shift 2 ;;
      --frp-auth-token) require_value "$1" "${2:-}"; FRP_AUTH_TOKEN="$2"; shift 2 ;;
      --release-channel) require_value "$1" "${2:-}"; RELEASE_CHANNEL="$2"; shift 2 ;;
      --bootstrap-socks-port) require_value "$1" "${2:-}"; BOOTSTRAP_SOCKS_PORT="$2"; shift 2 ;;
      --bootstrap-mode) require_value "$1" "${2:-}"; BOOTSTRAP_MODE="$2"; shift 2 ;;
      --vps-ip) require_value "$1" "${2:-}"; VPS_IP="$2"; shift 2 ;;
      --tunnel-user) require_value "$1" "${2:-}"; TUNNEL_USER="$2"; shift 2 ;;
      --tunnel-auth) require_value "$1" "${2:-}"; TUNNEL_AUTH="$2"; shift 2 ;;
      --panel-user) require_value "$1" "${2:-}"; PANEL_USER="$2"; shift 2 ;;
      --panel-password) require_value "$1" "${2:-}"; PANEL_PASSWORD="$2"; shift 2 ;;
      --panel-base-path) require_value "$1" "${2:-}"; PANEL_BASE_PATH="$2"; shift 2 ;;
      --panel-domain) require_value "$1" "${2:-}"; PANEL_DOMAIN="$2"; shift 2 ;;
      --panel-domain-only) require_value "$1" "${2:-}"; PANEL_DOMAIN_ONLY="$2"; shift 2 ;;
      --panel-ssl) require_value "$1" "${2:-}"; PANEL_SSL_ENABLED="$2"; shift 2 ;;
      --panel-ssl-mode) require_value "$1" "${2:-}"; PANEL_SSL_MODE="$2"; shift 2 ;;
      --panel-cert-file) require_value "$1" "${2:-}"; PANEL_CERT_FILE="$2"; shift 2 ;;
      --panel-key-file) require_value "$1" "${2:-}"; PANEL_KEY_FILE="$2"; shift 2 ;;
      --doh-endpoints) require_value "$1" "${2:-}"; DOH_ENDPOINTS="$2"; shift 2 ;;
      --relay-id) require_value "$1" "${2:-}"; RELAY_ID="$2"; shift 2 ;;
      --tls-enabled) require_value "$1" "${2:-}"; TLS_ENABLED="$2"; shift 2 ;;
      --tls-server-name) require_value "$1" "${2:-}"; TLS_SERVER_NAME="$2"; shift 2 ;;
      --tls-cert-file) require_value "$1" "${2:-}"; TLS_CERT_FILE="$2"; shift 2 ;;
      --tls-key-file) require_value "$1" "${2:-}"; TLS_KEY_FILE="$2"; shift 2 ;;
      --vless-flow) require_value "$1" "${2:-}"; VLESS_FLOW="$2"; shift 2 ;;
      --proxy-username) require_value "$1" "${2:-}"; PROXY_USERNAME="$2"; shift 2 ;;
      --proxy-password) require_value "$1" "${2:-}"; PROXY_PASSWORD="$2"; shift 2 ;;
      --hysteria2-up-mbps) require_value "$1" "${2:-}"; HYSTERIA2_UP_MBPS="$2"; shift 2 ;;
      --hysteria2-down-mbps) require_value "$1" "${2:-}"; HYSTERIA2_DOWN_MBPS="$2"; shift 2 ;;
      --hysteria2-obfs-password) require_value "$1" "${2:-}"; HYSTERIA2_OBFS_PASSWORD="$2"; shift 2 ;;
      --hysteria2-ignore-client-bandwidth) require_value "$1" "${2:-}"; HYSTERIA2_IGNORE_CLIENT_BANDWIDTH="$2"; shift 2 ;;
      --hysteria2-masquerade-url) require_value "$1" "${2:-}"; HYSTERIA2_MASQUERADE_URL="$2"; shift 2 ;;
      --naive-network) require_value "$1" "${2:-}"; NAIVE_NETWORK="$2"; shift 2 ;;
      --naive-quic-cc) require_value "$1" "${2:-}"; NAIVE_QUIC_CC="$2"; shift 2 ;;
      --camouflage-server) require_value "$1" "${2:-}"; CAMOUFLAGE_SERVER="$2"; shift 2 ;;
      --shadowtls-strict-mode) require_value "$1" "${2:-}"; SHADOWTLS_STRICT_MODE="$2"; shift 2 ;;
      --shadowtls-wildcard-sni) require_value "$1" "${2:-}"; SHADOWTLS_WILDCARD_SNI="$2"; shift 2 ;;
      --openvpn-network) require_value "$1" "${2:-}"; shift 2 ;;
      --) shift; break ;;
      *) die "Unknown argument: $1" ;;
    esac
  done
}

require_protocol() {
  [[ -n "$SELECTED_PROTOCOL" ]] || die "--protocol is required"
  is_valid_protocol "$SELECTED_PROTOCOL" || die "Invalid --protocol: $SELECTED_PROTOCOL"
}

configure_protocol() {
  PROTOCOL_ID="$SELECTED_PROTOCOL"
  case "$PROTOCOL_ID" in
    vless_tls_singbox) PROTOCOL_CLIENTS_FILE=""; PROTOCOL_RUNTIME_FILE="${GATEWAY_ROOT_DIR:-/etc/omnirelay/gateway}/vless_tls_runtime.json"; PROTOCOL_ENV_FILE="${GATEWAY_ROOT_DIR:-/etc/omnirelay/gateway}/vless_tls_panel.env" ;;
    mixed_singbox) PROTOCOL_CLIENTS_FILE=""; PROTOCOL_RUNTIME_FILE="${GATEWAY_ROOT_DIR:-/etc/omnirelay/gateway}/mixed_runtime.json"; PROTOCOL_ENV_FILE="${GATEWAY_ROOT_DIR:-/etc/omnirelay/gateway}/mixed_panel.env" ;;
    socks_singbox) PROTOCOL_CLIENTS_FILE=""; PROTOCOL_RUNTIME_FILE="${GATEWAY_ROOT_DIR:-/etc/omnirelay/gateway}/socks_runtime.json"; PROTOCOL_ENV_FILE="${GATEWAY_ROOT_DIR:-/etc/omnirelay/gateway}/socks_panel.env" ;;
    http_singbox) PROTOCOL_CLIENTS_FILE=""; PROTOCOL_RUNTIME_FILE="${GATEWAY_ROOT_DIR:-/etc/omnirelay/gateway}/http_runtime.json"; PROTOCOL_ENV_FILE="${GATEWAY_ROOT_DIR:-/etc/omnirelay/gateway}/http_panel.env" ;;
    hysteria2_singbox) PROTOCOL_CLIENTS_FILE=""; PROTOCOL_RUNTIME_FILE="${GATEWAY_ROOT_DIR:-/etc/omnirelay/gateway}/hysteria2_runtime.json"; PROTOCOL_ENV_FILE="${GATEWAY_ROOT_DIR:-/etc/omnirelay/gateway}/hysteria2_panel.env" ;;
    trojan_singbox) PROTOCOL_CLIENTS_FILE=""; PROTOCOL_RUNTIME_FILE="${GATEWAY_ROOT_DIR:-/etc/omnirelay/gateway}/trojan_runtime.json"; PROTOCOL_ENV_FILE="${GATEWAY_ROOT_DIR:-/etc/omnirelay/gateway}/trojan_panel.env" ;;
    naive_singbox) PROTOCOL_CLIENTS_FILE=""; PROTOCOL_RUNTIME_FILE="${GATEWAY_ROOT_DIR:-/etc/omnirelay/gateway}/naive_runtime.json"; PROTOCOL_ENV_FILE="${GATEWAY_ROOT_DIR:-/etc/omnirelay/gateway}/naive_panel.env" ;;
    shadowsocks_singbox) PROTOCOL_CLIENTS_FILE=""; PROTOCOL_RUNTIME_FILE="${GATEWAY_ROOT_DIR:-/etc/omnirelay/gateway}/shadowsocks_runtime.json"; PROTOCOL_ENV_FILE="${GATEWAY_ROOT_DIR:-/etc/omnirelay/gateway}/shadowsocks_panel.env" ;;
    shadowtls_v3_shadowsocks_singbox) PROTOCOL_CLIENTS_FILE=""; PROTOCOL_RUNTIME_FILE="${GATEWAY_ROOT_DIR:-/etc/omnirelay/gateway}/shadowtls_runtime.json"; PROTOCOL_ENV_FILE="${GATEWAY_ROOT_DIR:-/etc/omnirelay/gateway}/shadowtls_panel.env" ;;
  esac
}

protocol_apply_relay_scope() {
  PROTOCOL_CLIENTS_FILE=""
  PROTOCOL_RUNTIME_FILE="${GATEWAY_ROOT_DIR}/$(basename "$PROTOCOL_RUNTIME_FILE")"
  PROTOCOL_ENV_FILE="${GATEWAY_ROOT_DIR}/$(basename "$PROTOCOL_ENV_FILE")"
}

protocol_validate_install_args() {
  case "$PROTOCOL_ID" in
    hysteria2_singbox|trojan_singbox|naive_singbox)
      [[ "$TLS_ENABLED" == "true" ]] || die "--tls-enabled true is required for ${PROTOCOL_ID}"
      [[ -n "$TLS_CERT_FILE" ]] || die "--tls-cert-file is required for ${PROTOCOL_ID}"
      [[ -n "$TLS_KEY_FILE" ]] || die "--tls-key-file is required for ${PROTOCOL_ID}"
      ;;
  esac
  if [[ "$PROTOCOL_ID" == "vless_tls_singbox" && "$TLS_ENABLED" == "true" ]]; then
    [[ -n "$TLS_CERT_FILE" ]] || die "--tls-cert-file is required for ${PROTOCOL_ID} when --tls-enabled true"
    [[ -n "$TLS_KEY_FILE" ]] || die "--tls-key-file is required for ${PROTOCOL_ID} when --tls-enabled true"
  fi
  if [[ "$PROTOCOL_ID" == "shadowtls_v3_shadowsocks_singbox" ]]; then
    [[ -n "$CAMOUFLAGE_SERVER" ]] || die "--camouflage-server is required for ${PROTOCOL_ID}"
  fi
}

protocol_remove_legacy_clients_files() {
  rm -f \
    "${PANEL_APP_DIR}/vless_tls_clients.json" \
    "${PANEL_APP_DIR}/mixed_clients.json" \
    "${PANEL_APP_DIR}/socks_clients.json" \
    "${PANEL_APP_DIR}/http_clients.json" \
    "${PANEL_APP_DIR}/hysteria2_clients.json" \
    "${PANEL_APP_DIR}/trojan_clients.json" \
    "${PANEL_APP_DIR}/naive_clients.json" \
    "${PANEL_APP_DIR}/shadowsocks_clients.json" \
    "${PANEL_APP_DIR}/shadowtls_clients.json" >/dev/null 2>&1 || true
}

protocol_seed_clients() {
  protocol_remove_legacy_clients_files
  connector_ensure_accounting_schema
  local existing_count
  existing_count="$(sqlite3 "$CONNECTOR_ACCOUNTING_DB" "SELECT COUNT(1) FROM clients WHERE protocol_id='$(connector_sql_escape "$PROTOCOL_ID")';" 2>/dev/null || echo 0)"
  [[ "$existing_count" =~ ^[0-9]+$ ]] || existing_count=0
  if (( existing_count == 0 )); then
    local id p1 now
    id="$(connector_random_uuid)"
    p1="$(connector_random_string 24)"
    now="$(date +%s)"
    sqlite3 "$CONNECTOR_ACCOUNTING_DB" <<SQL
INSERT INTO clients(client_id,protocol_id,email,username,auth_username,auth_secret,enabled,total_bytes_limit,speed_limit_kbps,expiry_unix_ms,created_at,updated_at)
VALUES('${id}','${PROTOCOL_ID}','omni-client@local','omni-client@local','','${p1}',1,0,0,0,${now},${now});
INSERT OR IGNORE INTO usage_totals(client_id,used_bytes,updated_at) VALUES('${id}',0,${now});
INSERT OR IGNORE INTO connection_counters(client_id,active_connections,last_seen_at) VALUES('${id}',0,0);
INSERT OR IGNORE INTO enforcement_state(client_id,disabled_reason,disabled_at,updated_at) VALUES('${id}','',0,0);
SQL
  fi
}

protocol_db_clients_rows() {
  sqlite3 -separator $'\t' "$CONNECTOR_ACCOUNTING_DB" "
SELECT
  COALESCE(client_id,''),
  COALESCE(enabled,0),
  COALESCE(auth_secret,'')
FROM clients
WHERE protocol_id='${PROTOCOL_ID//\'/\'\'}'
ORDER BY email COLLATE NOCASE, client_id;
" 2>/dev/null || true
}

protocol_ensure_runtime() {
  install -d -m 0755 "$(dirname "$PROTOCOL_RUNTIME_FILE")"
  local tls_enabled tls_server_name tls_cert_file tls_key_file proxy_username proxy_password
  local ss_server_password
  tls_enabled="$(printf '%s' "$TLS_ENABLED" | tr '[:upper:]' '[:lower:]')"
  tls_server_name="$TLS_SERVER_NAME"
  tls_cert_file="$TLS_CERT_FILE"
  tls_key_file="$TLS_KEY_FILE"
  proxy_username="$PROXY_USERNAME"
  proxy_password="$PROXY_PASSWORD"
  ss_server_password=""
  if [[ -f "$PROTOCOL_RUNTIME_FILE" ]]; then
    [[ -n "$tls_server_name" ]] || tls_server_name="$(jq -r '.tls.serverName // empty' "$PROTOCOL_RUNTIME_FILE" 2>/dev/null || true)"
    [[ -n "$tls_cert_file" ]] || tls_cert_file="$(jq -r '.tls.certFile // empty' "$PROTOCOL_RUNTIME_FILE" 2>/dev/null || true)"
    [[ -n "$tls_key_file" ]] || tls_key_file="$(jq -r '.tls.keyFile // empty' "$PROTOCOL_RUNTIME_FILE" 2>/dev/null || true)"
    [[ -n "$proxy_username" ]] || proxy_username="$(jq -r '.proxy.username // empty' "$PROTOCOL_RUNTIME_FILE" 2>/dev/null || true)"
    [[ -n "$proxy_password" ]] || proxy_password="$(jq -r '.proxy.password // empty' "$PROTOCOL_RUNTIME_FILE" 2>/dev/null || true)"
    ss_server_password="$(jq -r '.ssServerPassword // empty' "$PROTOCOL_RUNTIME_FILE" 2>/dev/null || true)"
  fi
  [[ -n "$proxy_password" ]] || proxy_password="$(connector_random_string 24)"
  [[ -n "$proxy_username" ]] || proxy_username="omni"
  [[ -n "$CAMOUFLAGE_SERVER" ]] || CAMOUFLAGE_SERVER="www.cloudflare.com:443"
  [[ -n "$ss_server_password" ]] || ss_server_password="$(connector_random_string 32)"

  jq -n \
    --arg tlsEnabled "$tls_enabled" \
    --arg serverName "$tls_server_name" \
    --arg certFile "$tls_cert_file" \
    --arg keyFile "$tls_key_file" \
    --arg proxyUsername "$proxy_username" \
    --arg proxyPassword "$proxy_password" \
    --arg vlessFlow "$VLESS_FLOW" \
    --argjson hysteria2UpMbps "$HYSTERIA2_UP_MBPS" \
    --argjson hysteria2DownMbps "$HYSTERIA2_DOWN_MBPS" \
    --arg hysteria2ObfsPassword "$HYSTERIA2_OBFS_PASSWORD" \
    --arg hysteria2IgnoreClientBandwidth "$HYSTERIA2_IGNORE_CLIENT_BANDWIDTH" \
    --arg hysteria2MasqueradeUrl "$HYSTERIA2_MASQUERADE_URL" \
    --arg naiveNetwork "$NAIVE_NETWORK" \
    --arg naiveQuicCc "$NAIVE_QUIC_CC" \
    --arg camouflageServer "$CAMOUFLAGE_SERVER" \
    --arg shadowtlsStrictMode "$SHADOWTLS_STRICT_MODE" \
    --arg shadowtlsWildcardSni "$SHADOWTLS_WILDCARD_SNI" \
    --arg ssServerPassword "$ss_server_password" \
    '{tls:{enabled:($tlsEnabled=="true"),serverName:$serverName,certFile:$certFile,keyFile:$keyFile},proxy:{username:$proxyUsername,password:$proxyPassword},vlessFlow:$vlessFlow,hysteria2:{upMbps:$hysteria2UpMbps,downMbps:$hysteria2DownMbps,obfsPassword:$hysteria2ObfsPassword,ignoreClientBandwidth:($hysteria2IgnoreClientBandwidth=="true"),masqueradeUrl:$hysteria2MasqueradeUrl},naive:{network:$naiveNetwork,quicCc:$naiveQuicCc},shadowtls:{camouflageServer:$camouflageServer,strictMode:($shadowtlsStrictMode=="true"),wildcardSni:$shadowtlsWildcardSni},ssServerPassword:$ssServerPassword,updatedAtUtc:(now|todate)}' > "$PROTOCOL_RUNTIME_FILE"
  chmod 0600 "$PROTOCOL_RUNTIME_FILE" || true
}

protocol_validate_runtime() {
  local runtime
  runtime="$(cat "$PROTOCOL_RUNTIME_FILE")"
  case "$PROTOCOL_ID" in
    shadowsocks_singbox|shadowtls_v3_shadowsocks_singbox)
      [[ -n "$(jq -r '.ssServerPassword // empty' <<<"$runtime")" ]] || die "Shadowsocks runtime server password is missing."
      ;;
    vless_tls_singbox)
      if [[ "$(jq -r '.tls.enabled // false' <<<"$runtime")" == "true" ]]; then
        [[ -n "$(jq -r '.tls.certFile // empty' <<<"$runtime")" ]] || die "VLESS runtime TLS certificate path is missing."
        [[ -n "$(jq -r '.tls.keyFile // empty' <<<"$runtime")" ]] || die "VLESS runtime TLS key path is missing."
      fi
      ;;
  esac
}

protocol_build_config_json() {
  local runtime backend_outbound users_json users_pw_json runtime_tls
  runtime="$(cat "$PROTOCOL_RUNTIME_FILE")"
  backend_outbound="$(connector_backend_outbound_json auto)"
  users_json='[]'
  users_pw_json='[]'

  while IFS=$'\t' read -r cid enabled pw; do
    [[ "$enabled" == "1" ]] || continue
    [[ -n "$cid" ]] && users_json="$(jq -c --arg id "$cid" '. + [{uuid:$id,name:$id}]' <<<"$users_json")"
    [[ -n "$pw" ]] && users_pw_json="$(jq -c --arg name "$cid" --arg password "$pw" '. + [{name:$name,password:$password}]' <<<"$users_pw_json")"
  done < <(protocol_db_clients_rows)

  runtime_tls="$(jq -c '.tls' <<<"$runtime")"

  case "$PROTOCOL_ID" in
    vless_tls_singbox)
      jq -c -n --argjson publicPort "$PUBLIC_PORT" --argjson tls "$runtime_tls" --argjson users "$users_json" --argjson backendOutbound "$backend_outbound" --arg flow "$(jq -r '.vlessFlow // empty' <<<"$runtime")" '{log:{level:"warn"},inbounds:[{type:"vless",tag:"vless-in",listen:"::",listen_port:$publicPort,users:([ $users[] | if (($tls.enabled==true) and ($flow|length)>0) then . + {flow:$flow} else . end ]),tls:{enabled:($tls.enabled==true),server_name:$tls.serverName,certificate_path:$tls.certFile,key_path:$tls.keyFile}}],outbounds:[$backendOutbound,{type:"direct",tag:"direct"}],route:{final:"tunnel-backend"}}'
      ;;
    mixed_singbox)
      jq -c -n --argjson publicPort "$PUBLIC_PORT" --arg username "$(jq -r '.proxy.username' <<<"$runtime")" --arg password "$(jq -r '.proxy.password' <<<"$runtime")" --argjson backendOutbound "$backend_outbound" '{log:{level:"warn"},inbounds:[{type:"mixed",tag:"mixed-in",listen:"::",listen_port:$publicPort,users:[{username:$username,password:$password}]}],outbounds:[$backendOutbound,{type:"direct",tag:"direct"}],route:{final:"tunnel-backend"}}'
      ;;
    socks_singbox)
      jq -c -n --argjson publicPort "$PUBLIC_PORT" --arg username "$(jq -r '.proxy.username' <<<"$runtime")" --arg password "$(jq -r '.proxy.password' <<<"$runtime")" --argjson backendOutbound "$backend_outbound" '{log:{level:"warn"},inbounds:[{type:"socks",tag:"socks-in",listen:"::",listen_port:$publicPort,users:[{username:$username,password:$password}]}],outbounds:[$backendOutbound,{type:"direct",tag:"direct"}],route:{final:"tunnel-backend"}}'
      ;;
    http_singbox)
      jq -c -n --argjson publicPort "$PUBLIC_PORT" --arg username "$(jq -r '.proxy.username' <<<"$runtime")" --arg password "$(jq -r '.proxy.password' <<<"$runtime")" --argjson backendOutbound "$backend_outbound" '{log:{level:"warn"},inbounds:[{type:"http",tag:"http-in",listen:"::",listen_port:$publicPort,users:[{username:$username,password:$password}]}],outbounds:[$backendOutbound,{type:"direct",tag:"direct"}],route:{final:"tunnel-backend"}}'
      ;;
    hysteria2_singbox)
      jq -c -n --argjson publicPort "$PUBLIC_PORT" --argjson tls "$runtime_tls" --arg password "$(jq -r '.proxy.password' <<<"$runtime")" --argjson up "$(jq -r '.hysteria2.upMbps' <<<"$runtime")" --argjson down "$(jq -r '.hysteria2.downMbps' <<<"$runtime")" --arg obfs "$(jq -r '.hysteria2.obfsPassword // empty' <<<"$runtime")" --argjson ignore "$(jq -r '.hysteria2.ignoreClientBandwidth' <<<"$runtime")" --arg masquerade "$(jq -r '.hysteria2.masqueradeUrl // empty' <<<"$runtime")" --argjson backendOutbound "$backend_outbound" '{log:{level:"warn"},inbounds:[{type:"hysteria2",tag:"hy2-in",listen:"::",listen_port:$publicPort,users:[{password:$password}],tls:{enabled:($tls.enabled==true),server_name:$tls.serverName,certificate_path:$tls.certFile,key_path:$tls.keyFile},up_mbps:$up,down_mbps:$down,obfs:(if ($obfs|length)>0 then {type:"salamander",password:$obfs} else null end),ignore_client_bandwidth:$ignore,masquerade:(if ($masquerade|length)>0 then $masquerade else null end)}],outbounds:[$backendOutbound,{type:"direct",tag:"direct"}],route:{final:"tunnel-backend"}}'
      ;;
    trojan_singbox)
      jq -c -n --argjson publicPort "$PUBLIC_PORT" --argjson tls "$runtime_tls" --argjson users "$users_pw_json" --argjson backendOutbound "$backend_outbound" '{log:{level:"warn"},inbounds:[{type:"trojan",tag:"trojan-in",listen:"::",listen_port:$publicPort,users:$users,tls:{enabled:($tls.enabled==true),server_name:$tls.serverName,certificate_path:$tls.certFile,key_path:$tls.keyFile}}],outbounds:[$backendOutbound,{type:"direct",tag:"direct"}],route:{final:"tunnel-backend"}}'
      ;;
    naive_singbox)
      jq -c -n --argjson publicPort "$PUBLIC_PORT" --argjson tls "$runtime_tls" --arg username "$(jq -r '.proxy.username' <<<"$runtime")" --arg password "$(jq -r '.proxy.password' <<<"$runtime")" --arg network "$(jq -r '.naive.network // empty' <<<"$runtime")" --arg quicCc "$(jq -r '.naive.quicCc // empty' <<<"$runtime")" --argjson backendOutbound "$backend_outbound" '{log:{level:"warn"},inbounds:[{type:"naive",tag:"naive-in",listen:"::",listen_port:$publicPort,users:[{username:$username,password:$password}],network:(if ($network|length)>0 then $network else null end),tls:{enabled:($tls.enabled==true),server_name:$tls.serverName,certificate_path:$tls.certFile,key_path:$tls.keyFile},quic:(if ($quicCc|length)>0 then {congestion_control:$quicCc} else null end)}],outbounds:[$backendOutbound,{type:"direct",tag:"direct"}],route:{final:"tunnel-backend"}}'
      ;;
    shadowsocks_singbox)
      jq -c -n --argjson publicPort "$PUBLIC_PORT" --arg ssServerPassword "$(jq -r '.ssServerPassword' <<<"$runtime")" --argjson users "$users_pw_json" --argjson backendOutbound "$backend_outbound" '{log:{level:"warn"},inbounds:[{type:"shadowsocks",tag:"ss-in",listen:"::",listen_port:$publicPort,network:"tcp",method:"2022-blake3-aes-128-gcm",password:$ssServerPassword,users:$users,multiplex:{enabled:true}}],outbounds:[$backendOutbound,{type:"direct",tag:"direct"}],route:{final:"tunnel-backend"}}'
      ;;
    shadowtls_v3_shadowsocks_singbox)
      local ch cp
      ch="$(jq -r '.shadowtls.camouflageServer // "www.cloudflare.com:443"' <<<"$runtime")"
      cp="${ch##*:}"; ch="${ch%:*}"; [[ "$cp" =~ ^[0-9]+$ ]] || cp=443
      jq -c -n --argjson publicPort "$PUBLIC_PORT" --arg ssServerPassword "$(jq -r '.ssServerPassword' <<<"$runtime")" --argjson users "$users_pw_json" --arg ch "$ch" --argjson cp "$cp" --argjson strict "$(jq -r '.shadowtls.strictMode' <<<"$runtime")" --arg wildcard "$(jq -r '.shadowtls.wildcardSni // empty' <<<"$runtime")" --argjson backendOutbound "$backend_outbound" '{log:{level:"warn"},inbounds:[{type:"shadowtls",tag:"shadowtls-in",listen:"::",listen_port:$publicPort,version:3,users:$users,handshake:{server:$ch,server_port:$cp},strict_mode:$strict,wildcard_sni:(if ($wildcard|length)>0 then $wildcard else null end),detour:"ss-inner"},{type:"shadowsocks",tag:"ss-inner",listen:"127.0.0.1",listen_port:32080,network:"tcp",method:"2022-blake3-aes-128-gcm",password:$ssServerPassword,users:$users,multiplex:{enabled:true}}],outbounds:[$backendOutbound,{type:"direct",tag:"direct"}],route:{final:"tunnel-backend"}}'
      ;;
  esac
}

protocol_write_panel_env() {
  local runtime
  runtime="$(cat "$PROTOCOL_RUNTIME_FILE")"
  cat > "$PROTOCOL_ENV_FILE" <<EOF
SINGBOX_PROTOCOL_ID=${PROTOCOL_ID}
SINGBOX_SHADOWSOCKS_SERVER_PASSWORD=$(jq -r '.ssServerPassword // empty' <<<"$runtime")
SINGBOX_VLESS_TLS_ENABLED=$(jq -r '.tls.enabled // false' <<<"$runtime")
SINGBOX_VLESS_TLS_SERVER_NAME=$(jq -r '.tls.serverName // empty' <<<"$runtime")
SINGBOX_VLESS_FLOW=$(jq -r '.vlessFlow // empty' <<<"$runtime")
SHADOWTLS_CAMOUFLAGE_SERVER=$(jq -r '.shadowtls.camouflageServer // empty' <<<"$runtime")
SHADOWTLS_PUBLIC_PORT=${PUBLIC_PORT}
EOF
  chmod 0600 "$PROTOCOL_ENV_FILE" || true
}

protocol_sync_clients_locked() {
  local config_json
  protocol_remove_legacy_clients_files
  protocol_seed_clients
  protocol_ensure_runtime
  protocol_validate_runtime
  config_json="$(protocol_build_config_json)"
  connector_render_apply "$CONNECTOR_MODE" "$config_json"
  connector_clear_internal_redirect "tun0"
  connector_clear_internal_redirect "ppp+"
}

protocol_sync_clients() {
  connector_with_accounting_lock 30 protocol_sync_clients_locked
}

protocol_status_json() {
  local base_json inbound_id
  base_json="$(connector_status_base_json)"
  inbound_id="$(jq -r '.inbounds[0].tag // ""' "$CONNECTOR_CONFIG_FILE" 2>/dev/null || true)"
  jq -c --arg inboundId "$inbound_id" --arg openVpnState "n/a" --arg ipsecState "n/a" --arg xl2tpdState "n/a" '. + {inboundId:$inboundId,openVpnState:$openVpnState,ipsecState:$ipsecState,xl2tpdState:$xl2tpdState}' <<<"$base_json"
}

protocol_health_json() { local status_json; status_json="$(protocol_status_json)"; connector_health_base_json "$status_json"; }

protocol_metadata_json() { jq -c -n --arg p "$PROTOCOL_ID" --arg runtimeFile "$PROTOCOL_RUNTIME_FILE" '{protocol:$p,runtimeFile:$runtimeFile,accounting:{source:"connector_tracker"}}'; }

command_install() {
  connector_require_root
  connector_validate_common_args
  protocol_validate_install_args
  progress 2 "Initializing connector runtime directories"
  connector_init
  connector_install_gatewayctl "$GATEWAYCTL_SOURCE"
  connector_verify_bootstrap
  connector_install_runtime
  connector_clean_legacy
  progress 40 "Preparing ${PROTOCOL_ID} runtime"
  protocol_sync_clients
  progress 72 "Applying DNS profile"
  connector_dns_apply "$DOH_ENDPOINTS"
  progress 82 "Deploying OmniPanel"
  protocol_write_panel_env
  connector_deploy_omnipanel "$PROTOCOL_ID" "$PROTOCOL_ENV_FILE"
  connector_write_metadata_base "$PROTOCOL_ID" "$CONNECTOR_MODE"
  connector_merge_metadata_json "$(protocol_metadata_json)"
  progress 100 "${PROTOCOL_ID} install completed"
}

command_uninstall() {
  connector_require_root
  connector_clear_internal_redirect "tun0"
  connector_clear_internal_redirect "ppp+"
  connector_uninstall_runtime
}

command_start(){ connector_require_root; connector_start_services; }
command_stop(){ connector_require_root; connector_stop_services; }
command_dns_apply(){ connector_require_root; connector_validate_common_args; connector_dns_apply "$DOH_ENDPOINTS"; }
command_dns_status(){ connector_dns_status_json; }
command_dns_repair(){ connector_require_root; connector_validate_common_args; connector_dns_apply "$DOH_ENDPOINTS"; protocol_sync_clients; }

main() {
  COMMAND="${1:-}"; [[ -n "$COMMAND" ]] || { usage; exit 1; }
  [[ "$COMMAND" == "help" ]] && { usage; exit 0; }
  shift || true
  parse_args "$@"
  require_protocol
  configure_protocol
  connector_apply_relay_scope
  protocol_apply_relay_scope
  connector_load_metadata_defaults
  case "$COMMAND" in
    install) command_install ;;
    uninstall) command_uninstall ;;
    start) command_start ;;
    stop) command_stop ;;
    sync-clients) connector_require_root; protocol_sync_clients ;;
    status) protocol_status_json ;;
    health) protocol_health_json ;;
    dns-apply) command_dns_apply ;;
    dns-status) command_dns_status ;;
    dns-repair) command_dns_repair ;;
    get-protocol) echo "$PROTOCOL_ID" ;;
    *) usage; die "Unsupported command: $COMMAND" ;;
  esac
}

main "$@"

