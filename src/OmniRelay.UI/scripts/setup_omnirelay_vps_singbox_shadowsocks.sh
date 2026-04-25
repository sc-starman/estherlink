#!/usr/bin/env bash
set -Eeuo pipefail
IFS=$'\n\t'

SCRIPT_NAME="$(basename "$0")"
GATEWAYCTL_SOURCE="${BASH_SOURCE[0]:-$0}"
PROTOCOL_ID="shadowsocks_singbox"
CONNECTOR_MODE="full_tunnel"

PROTOCOL_CLIENTS_FILE="/opt/omnirelay/omni-gateway/shadowsocks_clients.json"
PROTOCOL_RUNTIME_FILE="${GATEWAY_ROOT_DIR:-/etc/omnirelay/gateway}/shadowsocks_runtime.json"
PROTOCOL_ENV_FILE="${GATEWAY_ROOT_DIR:-/etc/omnirelay/gateway}/shadowsocks_panel.env"
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

protocol_generate_2022_key() {
  openssl rand -base64 16 | tr -d '\r\n'
}

protocol_is_valid_2022_key() {
  python3 - "$1" <<'PY'
import base64
import sys

value = (sys.argv[1] if len(sys.argv) > 1 else "").strip()
try:
    decoded = base64.b64decode(value, validate=True)
except Exception:
    sys.exit(1)
sys.exit(0 if len(decoded) == 16 else 1)
PY
}

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
      --gateway-sni) require_value "$1" "${2:-}"; shift 2 ;;
      --gateway-target) require_value "$1" "${2:-}"; shift 2 ;;
      --camouflage-server) require_value "$1" "${2:-}"; shift 2 ;;
      --openvpn-network) require_value "$1" "${2:-}"; shift 2 ;;
      --openvpn-client-dns) require_value "$1" "${2:-}"; shift 2 ;;
      --) shift; break ;;
      *) die "Unknown argument: $1" ;;
    esac
  done
}

protocol_seed_clients() {
  install -d -m 0755 "$(dirname "$PROTOCOL_CLIENTS_FILE")"
  if [[ ! -f "$PROTOCOL_CLIENTS_FILE" ]]; then
    jq -n \
      --arg id "$(connector_random_uuid)" \
      --arg password "$(protocol_generate_2022_key)" \
      '[{id:$id,email:"omni-client@local",enable:true,totalGB:0,expiryTime:0,method:"2022-blake3-aes-128-gcm",password:$password}]' > "$PROTOCOL_CLIENTS_FILE"
    chmod 0640 "$PROTOCOL_CLIENTS_FILE" || true
  fi
}

protocol_normalize_clients() {
  local changed row password normalized rows
  changed=false
  rows='[]'

  while IFS= read -r row; do
    password="$(jq -r '.password // empty' <<<"$row")"
    if ! protocol_is_valid_2022_key "$password"; then
      password="$(protocol_generate_2022_key)"
      changed=true
    fi
    normalized="$(jq -c --arg password "$password" '.method="2022-blake3-aes-128-gcm" | .password=$password' <<<"$row")"
    rows="$(jq -c --argjson item "$normalized" '. + [$item]' <<<"$rows")"
  done < <(jq -c '.[]' "$PROTOCOL_CLIENTS_FILE" 2>/dev/null || true)

  if [[ "$changed" == true ]]; then
    printf '%s\n' "$rows" > "$PROTOCOL_CLIENTS_FILE"
    chmod 0640 "$PROTOCOL_CLIENTS_FILE" || true
  fi
}

protocol_ensure_runtime() {
  local server_password
  install -d -m 0755 "$(dirname "$PROTOCOL_RUNTIME_FILE")"

  if [[ -f "$PROTOCOL_RUNTIME_FILE" ]]; then
    server_password="$(jq -r '.ssServerPassword // empty' "$PROTOCOL_RUNTIME_FILE" 2>/dev/null || true)"
  else
    server_password=""
  fi

  if ! protocol_is_valid_2022_key "$server_password"; then
    server_password="$(protocol_generate_2022_key)"
  fi

  jq -n --arg ssServerPassword "$server_password" '{ssServerPassword:$ssServerPassword,updatedAtUtc:(now|todate)}' > "$PROTOCOL_RUNTIME_FILE"
  chmod 0600 "$PROTOCOL_RUNTIME_FILE" || true
}

protocol_build_config_json() {
  local users_json runtime server_password backend_outbound
  users_json='[]'

  while IFS= read -r row; do
    local password client_id
    password="$(jq -r '.password // empty' <<<"$row")"
    client_id="$(jq -r '.id // empty' <<<"$row")"
    [[ -n "$password" && -n "$client_id" ]] || continue
    users_json="$(jq -c --arg password "$password" --arg clientId "$client_id" '. + [{password:$password,name:$clientId}]' <<<"$users_json")"
  done < <(jq -c '.[] | select((.enable // true) == true)' "$PROTOCOL_CLIENTS_FILE" 2>/dev/null || true)

  runtime="$(cat "$PROTOCOL_RUNTIME_FILE")"
  server_password="$(jq -r '.ssServerPassword' <<<"$runtime")"
  backend_outbound="$(connector_backend_outbound_json auto)"

  jq -c -n \
    --argjson publicPort "$PUBLIC_PORT" \
    --argjson backendOutbound "$backend_outbound" \
    --arg serverPassword "$server_password" \
    --argjson users "$users_json" \
    '{
      log:{level:"warn"},
      inbounds:[
        {
          type:"shadowsocks",
          tag:"ss-in",
          listen:"::",
          listen_port:$publicPort,
          method:"2022-blake3-aes-128-gcm",
          password:$serverPassword,
          users:$users
        }
      ],
      outbounds:[
        $backendOutbound,
        {type:"direct",tag:"direct"}
      ],
      route:{final:"tunnel-backend"}
    }'
}

protocol_write_panel_env() {
  local runtime
  runtime="$(cat "$PROTOCOL_RUNTIME_FILE")"
  cat > "$PROTOCOL_ENV_FILE" <<EOF
SINGBOX_SHADOWSOCKS_CLIENTS_FILE=${PROTOCOL_CLIENTS_FILE}
SINGBOX_SHADOWSOCKS_SERVER_PASSWORD=$(jq -r '.ssServerPassword' <<<"$runtime")
EOF
  chmod 0600 "$PROTOCOL_ENV_FILE" || true
}

protocol_sync_clients() {
  local config_json
  protocol_seed_clients
  protocol_normalize_clients
  protocol_ensure_runtime
  connector_sync_accounting_db "$PROTOCOL_CLIENTS_FILE" "$PROTOCOL_ID"
  config_json="$(protocol_build_config_json)"
  connector_render_apply "$CONNECTOR_MODE" "$config_json"
  connector_clear_internal_redirect "tun0"
  connector_clear_internal_redirect "ppp+"
}

protocol_status_json() {
  local base_json inbound_id
  base_json="$(connector_status_base_json)"
  inbound_id="$(jq -r '.inbounds[0].tag // ""' "$CONNECTOR_CONFIG_FILE" 2>/dev/null || true)"
  jq -c \
    --arg inboundId "$inbound_id" \
    --arg openVpnState "n/a" \
    --arg ipsecState "n/a" \
    --arg xl2tpdState "n/a" \
    '. + {inboundId:$inboundId,openVpnState:$openVpnState,ipsecState:$ipsecState,xl2tpdState:$xl2tpdState}' <<<"$base_json"
}

protocol_health_json() {
  local status_json
  status_json="$(protocol_status_json)"
  connector_health_base_json "$status_json"
}

command_install() {
  connector_require_root
  connector_validate_common_args

  progress 2 "Initializing connector runtime directories"
  connector_init
  connector_install_gatewayctl "$GATEWAYCTL_SOURCE"
  connector_verify_bootstrap
  connector_install_runtime
  connector_clean_legacy

  progress 40 "Preparing Shadowsocks runtime"
  protocol_seed_clients
  protocol_ensure_runtime
  protocol_sync_clients

  progress 72 "Applying DNS profile"
  connector_dns_apply "$DNS_MODE" "$DOH_ENDPOINTS" "$DNS_UDP_ONLY"

  progress 82 "Deploying OmniPanel"
  protocol_write_panel_env
  connector_deploy_omnipanel "$PROTOCOL_ID" "$PROTOCOL_ENV_FILE"

  connector_write_metadata_base "$PROTOCOL_ID" "$CONNECTOR_MODE"
  connector_merge_metadata_json "$(jq -c -n --arg clientsFile "$PROTOCOL_CLIENTS_FILE" --arg runtimeFile "$PROTOCOL_RUNTIME_FILE" '{shadowsocks:{clientsFile:$clientsFile,runtimeFile:$runtimeFile},accounting:{source:"connector_tracker",clientsFile:$clientsFile}}')"
  progress 100 "${PROTOCOL_ID} install completed"
}

command_uninstall() {
  connector_require_root
  connector_clear_internal_redirect "tun0"
  connector_clear_internal_redirect "ppp+"
  connector_uninstall_runtime
}

command_start() {
  connector_require_root
  connector_start_services
}

command_stop() {
  connector_require_root
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
