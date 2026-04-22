#!/usr/bin/env bash
set -Eeuo pipefail
IFS=$'\n\t'

SCRIPT_NAME="$(basename "$0")"
GATEWAYCTL_SOURCE="${BASH_SOURCE[0]:-$0}"
PROTOCOL_ID="openvpn_tcp_singbox"
CONNECTOR_MODE="internal_tunnel"

PROTOCOL_CLIENTS_FILE="/opt/omnirelay/omni-gateway/openvpn_clients.json"
PROTOCOL_RUNTIME_FILE="${GATEWAY_ROOT_DIR:-/etc/omnirelay/gateway}/openvpn_runtime.json"
PROTOCOL_ENV_FILE="${GATEWAY_ROOT_DIR:-/etc/omnirelay/gateway}/openvpn_panel.env"
OPENVPN_EXPORT_DIR="/opt/omnirelay/omni-gateway/openvpn-exports"
OPENVPN_STATUS_FILE="/var/log/openvpn/omnirelay-status.log"
OPENVPN_INTERFACE="tun0"
OPENVPN_SERVICE="omnirelay-openvpn"
OPENVPN_DIR="${GATEWAY_ROOT_DIR:-/etc/omnirelay/gateway}/openvpn"
OPENVPN_EASYRSA_DIR="${OPENVPN_DIR}/easy-rsa"
OPENVPN_PKI_DIR="${OPENVPN_EASYRSA_DIR}/pki"
OPENVPN_SERVER_CONFIG_FILE="${OPENVPN_DIR}/server.conf"
OPENVPN_AUTH_VERIFY_SCRIPT_FILE="${OPENVPN_DIR}/auth-verify.sh"
OPENVPN_AUTH_FILE="${OPENVPN_DIR}/users.auth"
OPENVPN_TLS_CRYPT_KEY_FILE="${OPENVPN_DIR}/ta.key"
OPENVPN_CA_CERT_FILE="${OPENVPN_PKI_DIR}/ca.crt"
OPENVPN_SERVER_CERT_FILE="${OPENVPN_PKI_DIR}/issued/server.crt"
OPENVPN_SERVER_KEY_FILE="${OPENVPN_PKI_DIR}/private/server.key"
OPENVPN_DH_FILE="${OPENVPN_PKI_DIR}/dh.pem"
OPENVPN_CRL_FILE="${OPENVPN_PKI_DIR}/crl.pem"
OPENVPN_NETWORK="10.29.0.0/24"
OPENVPN_CLIENT_DNS="1.1.1.1,8.8.8.8"
OUTPUT_JSON="false"
COMMAND=""
PANEL_BASE_PATH=""

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
Usage: ${SCRIPT_NAME} <command> [options]
Commands:
  install | uninstall | start | stop | sync-clients
  status | health | dns-apply | dns-status | dns-repair | get-protocol
EOF
}

require_value() {
  local flag="$1"
  (( $# >= 2 )) || die "Missing value for ${flag}"
}

parse_args() {
  while (($# > 0)); do
    case "$1" in
      --json) OUTPUT_JSON="true"; shift ;;
      --public-port) require_value "$1" "${2:-}"; PUBLIC_PORT="$2"; shift 2 ;;
      --panel-port) require_value "$1" "${2:-}"; PANEL_PORT="$2"; shift 2 ;;
      --backend-port) require_value "$1" "${2:-}"; BACKEND_PORT="$2"; shift 2 ;;
      --ssh-port) require_value "$1" "${2:-}"; SSH_PORT="$2"; shift 2 ;;
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
      --dns-mode) require_value "$1" "${2:-}"; DNS_MODE="$2"; shift 2 ;;
      --doh-endpoints) require_value "$1" "${2:-}"; DOH_ENDPOINTS="$2"; shift 2 ;;
      --dns-udp-only) require_value "$1" "${2:-}"; DNS_UDP_ONLY="$2"; shift 2 ;;
      --openvpn-network) require_value "$1" "${2:-}"; OPENVPN_NETWORK="$2"; shift 2 ;;
      --openvpn-client-dns) require_value "$1" "${2:-}"; OPENVPN_CLIENT_DNS="$2"; shift 2 ;;
      --gateway-sni) require_value "$1" "${2:-}"; shift 2 ;;
      --gateway-target) require_value "$1" "${2:-}"; shift 2 ;;
      --camouflage-server) require_value "$1" "${2:-}"; shift 2 ;;
      --) shift; break ;;
      *) die "Unknown argument: $1" ;;
    esac
  done
}

protocol_validate_install_args() {
  [[ -n "$OPENVPN_NETWORK" ]] || die "--openvpn-network is required for ${PROTOCOL_ID}"
}

protocol_seed_clients() {
  install -d -m 0755 "$(dirname "$PROTOCOL_CLIENTS_FILE")"
  if [[ ! -f "$PROTOCOL_CLIENTS_FILE" ]]; then
    jq -n \
      --arg id "$(connector_random_uuid)" \
      --arg password "$(connector_random_string 24)" \
      '[{id:$id,email:"omni-client@local",enable:true,username:"ovpn_client",password:$password,totalGB:0,expiryTime:0}]' > "$PROTOCOL_CLIENTS_FILE"
    chmod 0640 "$PROTOCOL_CLIENTS_FILE" || true
  fi
}

protocol_ensure_runtime() {
  local redirect_port stored_network stored_dns
  install -d -m 0755 "$(dirname "$PROTOCOL_RUNTIME_FILE")"

  if [[ -f "$PROTOCOL_RUNTIME_FILE" ]]; then
    redirect_port="$(jq -r '.connectorRedirectPort // empty' "$PROTOCOL_RUNTIME_FILE" 2>/dev/null || true)"
    stored_network="$(jq -r '.openVpnNetwork // empty' "$PROTOCOL_RUNTIME_FILE" 2>/dev/null || true)"
    stored_dns="$(jq -r '.openVpnClientDns // empty' "$PROTOCOL_RUNTIME_FILE" 2>/dev/null || true)"
  else
    redirect_port=""
    stored_network=""
    stored_dns=""
  fi

  [[ -n "$OPENVPN_NETWORK" ]] || OPENVPN_NETWORK="$stored_network"
  [[ -n "$OPENVPN_NETWORK" ]] || OPENVPN_NETWORK="10.29.0.0/24"
  [[ -n "$OPENVPN_CLIENT_DNS" ]] || OPENVPN_CLIENT_DNS="$stored_dns"
  [[ "$redirect_port" =~ ^[0-9]+$ ]] || redirect_port="$(connector_choose_port)"

  jq -n \
    --argjson connectorRedirectPort "$redirect_port" \
    --arg openVpnNetwork "$OPENVPN_NETWORK" \
    --arg openVpnClientDns "$OPENVPN_CLIENT_DNS" \
    '{connectorRedirectPort:$connectorRedirectPort,openVpnNetwork:$openVpnNetwork,openVpnClientDns:$openVpnClientDns,updatedAtUtc:(now|todate)}' > "$PROTOCOL_RUNTIME_FILE"
  chmod 0600 "$PROTOCOL_RUNTIME_FILE" || true
}

protocol_build_config_json() {
  local runtime redirect_port backend_outbound
  runtime="$(cat "$PROTOCOL_RUNTIME_FILE")"
  redirect_port="$(jq -r '.connectorRedirectPort' <<<"$runtime")"
  backend_outbound="$(connector_backend_outbound_json auto)"

  jq -c -n \
    --argjson redirectPort "$redirect_port" \
    --argjson backendOutbound "$backend_outbound" \
    '{
      log:{level:"warn"},
      inbounds:[
        {type:"redirect",tag:"connector-in",listen:"0.0.0.0",listen_port:$redirectPort}
      ],
      outbounds:[
        $backendOutbound,
        {type:"direct",tag:"direct"}
      ],
      route:{rules:[{inbound:["connector-in"],outbound:"tunnel-backend"}],final:"direct"}
    }'
}

protocol_openvpn_bin() {
  command -v openvpn 2>/dev/null || true
}

protocol_openvpn_network_parts() {
  python3 - "$OPENVPN_NETWORK" <<'PY'
import ipaddress
import sys

cidr = (sys.argv[1] if len(sys.argv) > 1 else "").strip()
try:
    net = ipaddress.ip_network(cidr, strict=False)
except Exception:
    sys.exit(1)
if net.version != 4:
    sys.exit(1)
print(str(net.network_address))
print(str(net.netmask))
PY
}

protocol_ensure_easyrsa_layout() {
  if [[ ! -f "${OPENVPN_EASYRSA_DIR}/easyrsa" ]]; then
    if command -v make-cadir >/dev/null 2>&1; then
      rm -rf "$OPENVPN_EASYRSA_DIR"
      make-cadir "$OPENVPN_EASYRSA_DIR" >/dev/null 2>&1 || true
    fi
  fi
  if [[ ! -f "${OPENVPN_EASYRSA_DIR}/easyrsa" ]]; then
    install -d -m 0755 "$OPENVPN_EASYRSA_DIR"
    cp -a /usr/share/easy-rsa/. "$OPENVPN_EASYRSA_DIR/"
  fi
  chmod +x "${OPENVPN_EASYRSA_DIR}/easyrsa"
}

protocol_ensure_easyrsa_index_attrs() {
  install -d -m 0755 "$OPENVPN_PKI_DIR"
  touch "${OPENVPN_PKI_DIR}/index.txt"
  if [[ ! -f "${OPENVPN_PKI_DIR}/index.txt.attr" ]]; then
    printf 'unique_subject = no\n' > "${OPENVPN_PKI_DIR}/index.txt.attr"
    return 0
  fi
  if ! grep -q '^[[:space:]]*unique_subject[[:space:]]*=' "${OPENVPN_PKI_DIR}/index.txt.attr"; then
    printf 'unique_subject = no\n' >> "${OPENVPN_PKI_DIR}/index.txt.attr"
  fi
}

protocol_setup_openvpn_pki() {
  local openvpn_bin easyrsa
  openvpn_bin="$(protocol_openvpn_bin)"
  [[ -n "$openvpn_bin" ]] || die "openvpn package is required for ${PROTOCOL_ID}"

  install -d -m 0755 "$OPENVPN_DIR" "$OPENVPN_EASYRSA_DIR" "$OPENVPN_PKI_DIR"
  protocol_ensure_easyrsa_layout
  protocol_ensure_easyrsa_index_attrs
  easyrsa="${OPENVPN_EASYRSA_DIR}/easyrsa"

  if [[ ! -f "$OPENVPN_CA_CERT_FILE" ]]; then
    (
      cd "$OPENVPN_EASYRSA_DIR"
      EASYRSA_BATCH=1 EASYRSA_REQ_CN="OmniRelay-CA" "$easyrsa" init-pki
      EASYRSA_BATCH=1 EASYRSA_REQ_CN="OmniRelay-CA" "$easyrsa" build-ca nopass
    )
  fi

  if [[ ! -f "$OPENVPN_SERVER_CERT_FILE" || ! -f "$OPENVPN_SERVER_KEY_FILE" ]]; then
    (
      cd "$OPENVPN_EASYRSA_DIR"
      EASYRSA_BATCH=1 "$easyrsa" build-server-full server nopass
    )
  fi

  if [[ ! -f "$OPENVPN_DH_FILE" ]]; then
    (
      cd "$OPENVPN_EASYRSA_DIR"
      EASYRSA_BATCH=1 "$easyrsa" gen-dh
    )
  fi

  if [[ ! -f "$OPENVPN_CRL_FILE" ]]; then
    (
      cd "$OPENVPN_EASYRSA_DIR"
      EASYRSA_BATCH=1 "$easyrsa" gen-crl
    )
  fi

  if [[ ! -f "$OPENVPN_TLS_CRYPT_KEY_FILE" ]]; then
    "$openvpn_bin" --genkey secret "$OPENVPN_TLS_CRYPT_KEY_FILE" >/dev/null 2>&1 || die "failed to generate OpenVPN tls-crypt key"
  fi

  chmod 0600 "$OPENVPN_TLS_CRYPT_KEY_FILE" || true
  chmod 0644 "$OPENVPN_CA_CERT_FILE" "$OPENVPN_SERVER_CERT_FILE" "$OPENVPN_DH_FILE" "$OPENVPN_CRL_FILE" || true
  chmod 0600 "$OPENVPN_SERVER_KEY_FILE" || true
}

protocol_client_cn_for_id() {
  local id="$1"
  local cn
  cn="ovpn-$(printf '%s' "$id" | tr -cd 'a-zA-Z0-9' | cut -c1-40)"
  [[ -n "$cn" ]] || cn="ovpn-$(connector_random_string 12)"
  printf '%s\n' "$cn"
}

protocol_ensure_client_cert() {
  local cn="$1"
  local easyrsa cert_path key_path
  easyrsa="${OPENVPN_EASYRSA_DIR}/easyrsa"
  cert_path="${OPENVPN_PKI_DIR}/issued/${cn}.crt"
  key_path="${OPENVPN_PKI_DIR}/private/${cn}.key"
  if [[ ! -f "$cert_path" || ! -f "$key_path" ]]; then
    protocol_ensure_easyrsa_index_attrs
    (
      cd "$OPENVPN_EASYRSA_DIR"
      EASYRSA_BATCH=1 "$easyrsa" build-client-full "$cn" nopass
    )
  fi
}

protocol_write_auth_db_and_client_certs() {
  local auth_tmp row id username password cn hash
  auth_tmp="$(mktemp)"
  : > "$auth_tmp"

  while IFS= read -r row; do
    id="$(jq -r '.id // empty' <<<"$row")"
    username="$(jq -r '.username // empty' <<<"$row")"
    password="$(jq -r '.password // empty' <<<"$row")"
    [[ -n "$id" && -n "$username" && -n "$password" ]] || continue

    cn="$(protocol_client_cn_for_id "$id")"
    protocol_ensure_client_cert "$cn"

    if [[ "$(jq -r '.enable // true' <<<"$row")" == "true" ]]; then
      hash="$(printf '%s' "$password" | sha256sum | awk '{print $1}')"
      printf '%s:%s:%s\n' "$username" "$hash" "$cn" >> "$auth_tmp"
    fi
  done < <(jq -c '.[]' "$PROTOCOL_CLIENTS_FILE" 2>/dev/null || true)

  install -m 0600 "$auth_tmp" "$OPENVPN_AUTH_FILE"
  rm -f "$auth_tmp"
}

protocol_write_auth_verify_script() {
  cat > "$OPENVPN_AUTH_VERIFY_SCRIPT_FILE" <<EOF
#!/usr/bin/env bash
set -Eeuo pipefail
IFS=\$'\\n\\t'
AUTH_DB="${OPENVPN_AUTH_FILE}"
INPUT_FILE="\${1:-}"
[[ -n "\$INPUT_FILE" && -f "\$INPUT_FILE" && -f "\$AUTH_DB" ]] || exit 1
USERNAME="\$(sed -n '1p' "\$INPUT_FILE" | tr -d '\\r\\n')"
PASSWORD="\$(sed -n '2p' "\$INPUT_FILE" | tr -d '\\r\\n')"
[[ -n "\$USERNAME" && -n "\$PASSWORD" ]] || exit 1
HASH="\$(printf '%s' "\$PASSWORD" | sha256sum | awk '{print \$1}')"
awk -F: -v user="\$USERNAME" -v hash="\$HASH" '\$1==user && \$2==hash {ok=1} END{exit(ok?0:1)}' "\$AUTH_DB"
EOF
  chmod 0750 "$OPENVPN_AUTH_VERIFY_SCRIPT_FILE" || true
}

protocol_write_openvpn_server_config() {
  local network_addr netmask dns_values
  readarray -t _network_parts < <(protocol_openvpn_network_parts) || die "invalid OpenVPN network: $OPENVPN_NETWORK"
  network_addr="${_network_parts[0]:-}"
  netmask="${_network_parts[1]:-}"
  [[ -n "$network_addr" && -n "$netmask" ]] || die "failed to parse OpenVPN network: $OPENVPN_NETWORK"

  mkdir -p "$(dirname "$OPENVPN_STATUS_FILE")"
  cat > "$OPENVPN_SERVER_CONFIG_FILE" <<EOF
port ${PUBLIC_PORT}
proto tcp-server
dev tun
topology subnet
server ${network_addr} ${netmask}
push "redirect-gateway def1 bypass-dhcp"
keepalive 10 60
persist-key
persist-tun
ca ${OPENVPN_CA_CERT_FILE}
cert ${OPENVPN_SERVER_CERT_FILE}
key ${OPENVPN_SERVER_KEY_FILE}
dh ${OPENVPN_DH_FILE}
crl-verify ${OPENVPN_CRL_FILE}
tls-crypt ${OPENVPN_TLS_CRYPT_KEY_FILE}
verify-client-cert require
username-as-common-name
auth-user-pass-verify ${OPENVPN_AUTH_VERIFY_SCRIPT_FILE} via-file
script-security 2
cipher AES-256-GCM
auth SHA256
data-ciphers AES-256-GCM:AES-128-GCM:CHACHA20-POLY1305
status-version 3
status ${OPENVPN_STATUS_FILE} 5
verb 3
EOF

  dns_values="$(printf '%s' "$OPENVPN_CLIENT_DNS" | tr ',' '\n' | awk 'NF{print $1}')"
  while IFS= read -r dns_server; do
    [[ -n "$dns_server" ]] || continue
    printf 'push "dhcp-option DNS %s"\n' "$dns_server" >> "$OPENVPN_SERVER_CONFIG_FILE"
  done <<< "$dns_values"

  chmod 0600 "$OPENVPN_SERVER_CONFIG_FILE" || true
}

protocol_write_openvpn_service_unit() {
  local openvpn_bin
  openvpn_bin="$(protocol_openvpn_bin)"
  [[ -n "$openvpn_bin" ]] || die "openvpn binary not found"
  cat > "/etc/systemd/system/${OPENVPN_SERVICE}.service" <<EOF
[Unit]
Description=OmniRelay OpenVPN server
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
ExecStart=${openvpn_bin} --config ${OPENVPN_SERVER_CONFIG_FILE}
Restart=always
RestartSec=3
CapabilityBoundingSet=CAP_NET_ADMIN CAP_NET_BIND_SERVICE CAP_NET_RAW CAP_SETUID CAP_SETGID CAP_SETPCAP
AmbientCapabilities=CAP_NET_ADMIN CAP_NET_BIND_SERVICE CAP_NET_RAW CAP_SETUID CAP_SETGID CAP_SETPCAP

[Install]
WantedBy=multi-user.target
EOF
}

protocol_bootstrap_openvpn_runtime() {
  protocol_setup_openvpn_pki
  protocol_write_auth_verify_script
  protocol_write_openvpn_server_config
  protocol_write_openvpn_service_unit
  systemctl daemon-reload
  systemctl enable --now "$OPENVPN_SERVICE" >/dev/null 2>&1 || die "failed to start ${OPENVPN_SERVICE}"
}

protocol_render_exports() {
  local host row id username password cn cert_path key_path profile export_group
  host="${PANEL_DOMAIN:-${VPS_IP:-$(hostname -I 2>/dev/null | awk '{print $1}')}}"
  host="${host%% *}"
  [[ -n "$host" ]] || host="127.0.0.1"
  export_group="${PANEL_USER_ACCOUNT:-omnigateway}"

  [[ -f "$OPENVPN_CA_CERT_FILE" ]] || die "OpenVPN CA certificate missing: ${OPENVPN_CA_CERT_FILE}"
  [[ -f "$OPENVPN_TLS_CRYPT_KEY_FILE" ]] || die "OpenVPN tls-crypt key missing: ${OPENVPN_TLS_CRYPT_KEY_FILE}"

  if id -u "$export_group" >/dev/null 2>&1; then
    install -d -m 2750 -o root -g "$export_group" "$OPENVPN_EXPORT_DIR"
  else
    install -d -m 0755 "$OPENVPN_EXPORT_DIR"
  fi
  find "$OPENVPN_EXPORT_DIR" -type f -name '*.ovpn' -delete 2>/dev/null || true

  while IFS= read -r row; do
    id="$(jq -r '.id // empty' <<<"$row")"
    username="$(jq -r '.username // empty' <<<"$row")"
    password="$(jq -r '.password // empty' <<<"$row")"
    [[ -n "$id" && -n "$username" && -n "$password" ]] || continue

    cn="$(protocol_client_cn_for_id "$id")"
    cert_path="${OPENVPN_PKI_DIR}/issued/${cn}.crt"
    key_path="${OPENVPN_PKI_DIR}/private/${cn}.key"
    [[ -f "$cert_path" && -f "$key_path" ]] || die "OpenVPN client certificate missing for ${id} (${cn})"

    profile="${OPENVPN_EXPORT_DIR}/${id}.ovpn"
    {
      printf 'client\n'
      printf 'dev tun\n'
      printf 'proto tcp\n'
      printf 'remote %s %s\n' "$host" "$PUBLIC_PORT"
      printf 'resolv-retry infinite\n'
      printf 'nobind\n'
      printf 'persist-key\n'
      printf 'persist-tun\n'
      printf 'auth-user-pass\n'
      printf 'auth-nocache\n'
      printf 'remote-cert-tls server\n'
      printf 'cipher AES-256-GCM\n'
      printf 'auth SHA256\n'
      printf 'verb 3\n'
      printf '# OmniRelay Username: %s\n' "$username"
      printf '# OmniRelay Password: %s\n' "$password"
      printf '<ca>\n'
      cat "$OPENVPN_CA_CERT_FILE"
      printf '</ca>\n'
      printf '<cert>\n'
      cat "$cert_path"
      printf '</cert>\n'
      printf '<key>\n'
      cat "$key_path"
      printf '</key>\n'
      printf '<tls-crypt>\n'
      cat "$OPENVPN_TLS_CRYPT_KEY_FILE"
      printf '</tls-crypt>\n'
    } > "$profile"
    if id -u "$export_group" >/dev/null 2>&1; then
      chown "root:${export_group}" "$profile" || true
      chmod 0640 "$profile" || true
    else
      chmod 0644 "$profile" || true
    fi
  done < <(jq -c '.[]' "$PROTOCOL_CLIENTS_FILE" 2>/dev/null || true)
}

protocol_write_panel_env() {
  cat > "$PROTOCOL_ENV_FILE" <<EOF
OPENVPN_CLIENTS_FILE=${PROTOCOL_CLIENTS_FILE}
OPENVPN_EXPORT_DIR=${OPENVPN_EXPORT_DIR}
OPENVPN_PUBLIC_PORT=${PUBLIC_PORT}
OPENVPN_ACCOUNTING_DB=${CONNECTOR_ACCOUNTING_DB}
OPENVPN_STATUS_FILE=${OPENVPN_STATUS_FILE}
EOF
  chmod 0600 "$PROTOCOL_ENV_FILE" || true
}

protocol_sync_clients() {
  local config_json
  protocol_seed_clients
  protocol_ensure_runtime
  protocol_setup_openvpn_pki
  protocol_write_auth_db_and_client_certs
  protocol_bootstrap_openvpn_runtime
  connector_sync_accounting_db "$PROTOCOL_CLIENTS_FILE" "$PROTOCOL_ID"

  config_json="$(protocol_build_config_json)"
  connector_render_apply "$CONNECTOR_MODE" "$config_json"
  connector_apply_internal_redirect "$OPENVPN_INTERFACE"

  protocol_render_exports

  if ! systemctl restart "$OPENVPN_SERVICE" >/dev/null 2>&1; then
    systemctl --no-pager -l status "$OPENVPN_SERVICE" >&2 || true
    journalctl -u "$OPENVPN_SERVICE" -n 80 --no-pager >&2 || true
    die "OpenVPN service is not active after syncing clients."
  fi
}

protocol_openvpn_state() {
  connector_service_state "$OPENVPN_SERVICE"
}

protocol_status_json() {
  local base_json inbound_id openvpn_state accounting_state accounting_timer accounting_error accounting_healthy
  base_json="$(connector_status_base_json)"
  inbound_id="$(jq -r '.inbounds[0].tag // ""' "$CONNECTOR_CONFIG_FILE" 2>/dev/null || true)"
  openvpn_state="$(protocol_openvpn_state)"
  accounting_state="$(jq -r '.accountingSyncState // "unknown"' <<<"$base_json")"
  accounting_timer="$(jq -r '.accountingTimerState // "unknown"' <<<"$base_json")"
  accounting_error="$(jq -r '.accountingLastError // ""' <<<"$base_json")"
  accounting_healthy=false
  if [[ "$accounting_state" == "ok" ]]; then
    accounting_healthy=true
  elif [[ "$accounting_state" == "failed" && "$accounting_error" == "state_missing" && "$accounting_timer" == "active" ]]; then
    accounting_healthy=true
  fi
  jq -c \
    --arg inboundId "$inbound_id" \
    --arg openVpnState "$openvpn_state" \
    --arg ipsecState "n/a" \
    --arg xl2tpdState "n/a" \
    --argjson openVpnAccountingHealthy "$accounting_healthy" \
    '. + {inboundId:$inboundId,openVpnState:$openVpnState,ipsecState:$ipsecState,xl2tpdState:$xl2tpdState,openVpnAccountingHealthy:$openVpnAccountingHealthy}' <<<"$base_json"
}

protocol_health_json() {
  local status_json
  status_json="$(protocol_status_json)"
  connector_health_base_json "$status_json"
}

command_install() {
  connector_require_root
  connector_validate_common_args
  protocol_validate_install_args

  progress 2 "Initializing connector runtime directories"
  systemctl disable --now openvpn-server@server openvpn@server openvpn >/dev/null 2>&1 || true
  connector_init
  connector_install_gatewayctl "$GATEWAYCTL_SOURCE"
  connector_verify_bootstrap
  connector_install_runtime openvpn easy-rsa
  connector_clean_legacy

  progress 40 "Preparing OpenVPN connector runtime"
  protocol_seed_clients
  protocol_ensure_runtime
  protocol_sync_clients

  progress 72 "Applying DNS profile"
  connector_dns_apply "$DNS_MODE" "$DOH_ENDPOINTS" "$DNS_UDP_ONLY"

  progress 82 "Deploying OmniPanel"
  protocol_write_panel_env
  connector_deploy_omnipanel "$PROTOCOL_ID" "$PROTOCOL_ENV_FILE"

  connector_write_metadata_base "$PROTOCOL_ID" "$CONNECTOR_MODE"
  connector_merge_metadata_json "$(jq -c -n --arg clientsFile "$PROTOCOL_CLIENTS_FILE" --arg runtimeFile "$PROTOCOL_RUNTIME_FILE" --arg exportDir "$OPENVPN_EXPORT_DIR" --arg openVpnStatusFile "$OPENVPN_STATUS_FILE" '{openVpn:{clientsFile:$clientsFile,runtimeFile:$runtimeFile,exportDir:$exportDir},accounting:{source:"openvpn_status",clientsFile:$clientsFile,openVpnStatusFile:$openVpnStatusFile}}')"
  progress 100 "${PROTOCOL_ID} install completed"
}

command_uninstall() {
  connector_require_root
  connector_clear_internal_redirect "$OPENVPN_INTERFACE"
  systemctl disable --now "$OPENVPN_SERVICE" >/dev/null 2>&1 || true
  connector_uninstall_runtime
}

command_start() {
  connector_require_root
  connector_start_services
  systemctl enable --now "$OPENVPN_SERVICE" >/dev/null 2>&1 || true
}

command_stop() {
  connector_require_root
  systemctl stop "$OPENVPN_SERVICE" >/dev/null 2>&1 || true
  connector_stop_services
}

command_dns_apply() {
  connector_require_root
  connector_validate_common_args
  connector_dns_apply "$DNS_MODE" "$DOH_ENDPOINTS" "$DNS_UDP_ONLY"
}

command_dns_status() {
  connector_dns_status_json
}

command_dns_repair() {
  connector_require_root
  connector_validate_common_args
  connector_dns_apply "$DNS_MODE" "$DOH_ENDPOINTS" "$DNS_UDP_ONLY"
  protocol_sync_clients
}

main() {
  COMMAND="${1:-}"
  if [[ -z "$COMMAND" ]]; then
    usage
    exit 1
  fi
  shift || true
  connector_load_metadata_defaults
  parse_args "$@"

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
