#!/usr/bin/env bash
set -Eeuo pipefail
IFS=$'\n\t'

SCRIPT_NAME="$(basename "$0")"
GATEWAYCTL_SOURCE="${BASH_SOURCE[0]:-$0}"
PROTOCOL_ID="ipsec_l2tp_singbox"
CONNECTOR_MODE="internal_tunnel"

PROTOCOL_CLIENTS_FILE=""
PROTOCOL_RUNTIME_FILE="${GATEWAY_ROOT_DIR:-/etc/omnirelay/gateway}/ipsec_l2tp_runtime.json"
PROTOCOL_ENV_FILE="${GATEWAY_ROOT_DIR:-/etc/omnirelay/gateway}/ipsec_l2tp_panel.env"
IPSEC_PSK_FILE="${GATEWAY_ROOT_DIR:-/etc/omnirelay/gateway}/ipsec/shared_psk"
IPSEC_INTERFACE="ppp+"
IPSEC_STATE_SERVICE="strongswan-starter"
XL2TPD_SERVICE="xl2tpd"
IPSEC_DNSMASQ_CONFIG_FILE="/etc/dnsmasq.d/omnirelay-ipsec-l2tp.conf"
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
      --doh-endpoints) require_value "$1" "${2:-}"; DOH_ENDPOINTS="$2"; shift 2 ;;
      --relay-id) require_value "$1" "${2:-}"; RELAY_ID="$2"; shift 2 ;;
      --gateway-sni) require_value "$1" "${2:-}"; shift 2 ;;
      --gateway-target) require_value "$1" "${2:-}"; shift 2 ;;
      --camouflage-server) require_value "$1" "${2:-}"; shift 2 ;;
      --openvpn-network) require_value "$1" "${2:-}"; shift 2 ;;
      --) shift; break ;;
      *) die "Unknown argument: $1" ;;
    esac
  done
}

protocol_apply_relay_scope() {
  PROTOCOL_CLIENTS_FILE=""
  PROTOCOL_RUNTIME_FILE="${GATEWAY_ROOT_DIR}/ipsec_l2tp_runtime.json"
  PROTOCOL_ENV_FILE="${GATEWAY_ROOT_DIR}/ipsec_l2tp_panel.env"
  IPSEC_PSK_FILE="${GATEWAY_ROOT_DIR}/ipsec/shared_psk"
  IPSEC_DNSMASQ_CONFIG_FILE="/etc/dnsmasq.d/omnirelay-ipsec-l2tp${RELAY_ID:+-${RELAY_ID}}.conf"
}

protocol_apply_port_defaults() {
  PUBLIC_PORT=1701
}

protocol_ipsec_owner_file() {
  printf '/etc/omnirelay/ipsec-l2tp-owner.json'
}

protocol_claim_ipsec_owner() {
  [[ -n "${RELAY_ID:-}" ]] || return 0
  local owner_file owner existing_host
  owner_file="$(protocol_ipsec_owner_file)"
  install -d -m 0755 "$(dirname "$owner_file")"
  if [[ -f "$owner_file" ]]; then
    owner="$(jq -r '.relayId // empty' "$owner_file" 2>/dev/null || true)"
    existing_host="$(jq -r '.vpsIp // empty' "$owner_file" 2>/dev/null || true)"
    if [[ -n "$owner" && "$owner" != "$RELAY_ID" ]]; then
      die "IPSec/L2TP is already owned by relay '${owner}' on this VPS/public IP (${existing_host:-unknown}); only one IPSec/L2TP relay is supported per public IP."
    fi
  fi
  jq -n --arg relayId "$RELAY_ID" --arg vpsIp "$VPS_IP" --arg updatedAtUtc "$(date -u +'%Y-%m-%dT%H:%M:%SZ')" '{relayId:$relayId,vpsIp:$vpsIp,updatedAtUtc:$updatedAtUtc}' > "$owner_file"
  chmod 0600 "$owner_file" || true
}

protocol_release_ipsec_owner() {
  [[ -n "${RELAY_ID:-}" ]] || return 0
  local owner_file owner
  owner_file="$(protocol_ipsec_owner_file)"
  [[ -f "$owner_file" ]] || return 0
  owner="$(jq -r '.relayId // empty' "$owner_file" 2>/dev/null || true)"
  [[ "$owner" == "$RELAY_ID" ]] && rm -f "$owner_file"
}

protocol_validate_install_args() {
  [[ "$PANEL_PORT" != "1701" ]] || die "--panel-port must differ from 1701 for ${PROTOCOL_ID}"
}

protocol_seed_clients() {
  rm -f "${PANEL_APP_DIR}/ipsec_l2tp_clients.json" >/dev/null 2>&1 || true
  install -d -m 0755 "$(dirname "$IPSEC_PSK_FILE")"
  connector_ensure_accounting_schema
  local existing_count
  existing_count="$(sqlite3 "$CONNECTOR_ACCOUNTING_DB" "SELECT COUNT(1) FROM clients WHERE protocol_id='$(connector_sql_escape "$PROTOCOL_ID")';" 2>/dev/null || echo 0)"
  [[ "$existing_count" =~ ^[0-9]+$ ]] || existing_count=0
  if (( existing_count == 0 )); then
    local id password now
    id="$(connector_random_uuid)"
    password="$(connector_random_string 24)"
    now="$(date +%s)"
    sqlite3 "$CONNECTOR_ACCOUNTING_DB" <<SQL
INSERT INTO clients(client_id,protocol_id,email,username,auth_username,auth_secret,enabled,total_bytes_limit,speed_limit_kbps,expiry_unix_ms,created_at,updated_at)
VALUES('${id}','${PROTOCOL_ID}','omni-client@local','l2tp_client','l2tp_client','${password}',1,0,0,0,${now},${now});
INSERT OR IGNORE INTO usage_totals(client_id,used_bytes,updated_at) VALUES('${id}',0,${now});
INSERT OR IGNORE INTO connection_counters(client_id,active_connections,last_seen_at) VALUES('${id}',0,0);
INSERT OR IGNORE INTO enforcement_state(client_id,disabled_reason,disabled_at,updated_at) VALUES('${id}','',0,0);
SQL
  fi
  if [[ ! -f "$IPSEC_PSK_FILE" ]]; then
    printf '%s\n' "$(connector_random_string 32)" > "$IPSEC_PSK_FILE"
    chmod 0600 "$IPSEC_PSK_FILE" || true
  fi
}

protocol_ensure_runtime() {
  local redirect_port
  install -d -m 0755 "$(dirname "$PROTOCOL_RUNTIME_FILE")"

  if [[ -f "$PROTOCOL_RUNTIME_FILE" ]]; then
    redirect_port="$(jq -r '.connectorRedirectPort // empty' "$PROTOCOL_RUNTIME_FILE" 2>/dev/null || true)"
  else
    redirect_port=""
  fi

  [[ "$redirect_port" =~ ^[0-9]+$ ]] || redirect_port="$(connector_choose_port)"

  jq -n \
    --argjson connectorRedirectPort "$redirect_port" \
    '{connectorRedirectPort:$connectorRedirectPort,updatedAtUtc:(now|todate)}' > "$PROTOCOL_RUNTIME_FILE"
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
      route:{rules:[{inbound:["connector-in"],outbound:"tunnel-backend"}],final:"tunnel-backend"}
    }'
}

protocol_write_panel_env() {
  cat > "$PROTOCOL_ENV_FILE" <<EOF
IPSEC_L2TP_PSK_FILE=${IPSEC_PSK_FILE}
IPSEC_L2TP_ACCOUNTING_DB=${CONNECTOR_ACCOUNTING_DB}
EOF
  chmod 0600 "$PROTOCOL_ENV_FILE" || true
}

protocol_ipsec_dns_address() {
  if [[ -f /etc/xl2tpd/xl2tpd.conf ]]; then
    awk -F= '/^[[:space:]]*local ip[[:space:]]*=/{gsub(/[[:space:]]/,"",$2); print $2; exit}' /etc/xl2tpd/xl2tpd.conf
  fi
}

protocol_ensure_dnsmasq_runtime() {
  if command -v dnsmasq >/dev/null 2>&1; then
    return 0
  fi
  connector_configure_proxy
  apt-get -o Acquire::Retries=4 -o Acquire::http::Timeout=30 -o Acquire::https::Timeout=30 update -y
  DEBIAN_FRONTEND=noninteractive apt-get -o Acquire::Retries=4 -o Acquire::http::Timeout=30 -o Acquire::https::Timeout=30 install -y --no-install-recommends dnsmasq
  connector_clear_proxy
}

protocol_write_ipsec_dns_config() {
  local dns_ip
  dns_ip="$(protocol_ipsec_dns_address || true)"
  [[ -n "$dns_ip" ]] || return 0
  install -d -m 0755 "$(dirname "$IPSEC_DNSMASQ_CONFIG_FILE")"
  {
    printf 'interface=%s\n' "$IPSEC_INTERFACE"
    printf 'listen-address=%s\n' "$dns_ip"
    printf 'bind-dynamic\n'
    printf 'no-resolv\n'
    printf 'cache-size=10000\n'
    printf 'server=%s#%s\n' "$CONNECTOR_DNS_LISTEN_ADDRESS" "$CONNECTOR_DNS_LISTEN_PORT"
  } > "$IPSEC_DNSMASQ_CONFIG_FILE"
  chmod 0644 "$IPSEC_DNSMASQ_CONFIG_FILE" || true

  if [[ -f /etc/ppp/options.xl2tpd ]]; then
    sed -i '/^[[:space:]]*ms-dns[[:space:]]/d' /etc/ppp/options.xl2tpd
    printf 'ms-dns %s\n' "$dns_ip" >> /etc/ppp/options.xl2tpd
  fi
}

protocol_restart_ipsec_dnsmasq() {
  if command -v systemctl >/dev/null 2>&1 && systemctl list-unit-files dnsmasq.service >/dev/null 2>&1; then
    systemctl enable dnsmasq >/dev/null 2>&1 || true
    systemctl restart dnsmasq >/dev/null 2>&1 || true
  fi
}

protocol_sync_clients() {
  local config_json
  rm -f "${PANEL_APP_DIR}/ipsec_l2tp_clients.json" >/dev/null 2>&1 || true
  protocol_seed_clients
  protocol_ensure_runtime
  config_json="$(protocol_build_config_json)"
  connector_render_apply "$CONNECTOR_MODE" "$config_json"
  connector_apply_internal_redirect "$IPSEC_INTERFACE"
  protocol_ensure_dnsmasq_runtime
  protocol_write_ipsec_dns_config
  protocol_restart_ipsec_dnsmasq
}

protocol_ipsec_state() {
  local state
  state="$(connector_service_state "$IPSEC_STATE_SERVICE")"
  if [[ "$state" == "inactive" || "$state" == "unknown" || "$state" == "not-found" ]]; then
    state="$(connector_service_state "ipsec")"
  fi
  [[ -n "$state" ]] || state="inactive"
  printf '%s' "$state"
}

protocol_xl2tpd_state() {
  local state
  state="$(connector_service_state "$XL2TPD_SERVICE")"
  [[ -n "$state" ]] || state="inactive"
  printf '%s' "$state"
}

protocol_status_json() {
  local base_json inbound_id ipsec_state xl2tpd_state accounting_state accounting_timer accounting_error accounting_healthy
  base_json="$(connector_status_base_json)"
  inbound_id="$(jq -r '.inbounds[0].tag // ""' "$CONNECTOR_CONFIG_FILE" 2>/dev/null || true)"
  ipsec_state="$(protocol_ipsec_state)"
  xl2tpd_state="$(protocol_xl2tpd_state)"
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
    --arg openVpnState "n/a" \
    --arg ipsecState "$ipsec_state" \
    --arg xl2tpdState "$xl2tpd_state" \
    --argjson ipsecAccountingHealthy "$accounting_healthy" \
    '. + {inboundId:$inboundId,openVpnState:$openVpnState,ipsecState:$ipsecState,xl2tpdState:$xl2tpdState,ipsecAccountingHealthy:$ipsecAccountingHealthy}' <<<"$base_json"
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
  protocol_claim_ipsec_owner

  progress 2 "Initializing connector runtime directories"
  connector_init
  connector_install_gatewayctl "$GATEWAYCTL_SOURCE"
  connector_verify_bootstrap
  connector_install_runtime ppp xl2tpd strongswan dnsmasq
  connector_clean_legacy

  progress 40 "Preparing IPSec/L2TP connector runtime"
  protocol_seed_clients
  protocol_ensure_runtime
  protocol_sync_clients

  progress 72 "Applying DNS profile"
  connector_dns_apply "$DOH_ENDPOINTS"

  progress 82 "Deploying OmniPanel"
  protocol_write_panel_env
  connector_deploy_omnipanel "$PROTOCOL_ID" "$PROTOCOL_ENV_FILE"

  connector_write_metadata_base "$PROTOCOL_ID" "$CONNECTOR_MODE"
  connector_merge_metadata_json "$(jq -c -n --arg runtimeFile "$PROTOCOL_RUNTIME_FILE" --arg pskFile "$IPSEC_PSK_FILE" --arg pppSessionsFile "$ACCOUNTING_SYNC_PPP_SESSIONS_FILE" '{ipsecL2tp:{runtimeFile:$runtimeFile,pskFile:$pskFile},accounting:{source:"ipsec_ppp",pppSessionsFile:$pppSessionsFile}}')"
  progress 100 "${PROTOCOL_ID} install completed"
}

command_uninstall() {
  connector_require_root
  connector_clear_internal_redirect "$IPSEC_INTERFACE"
  systemctl disable --now "$XL2TPD_SERVICE" "$IPSEC_STATE_SERVICE" ipsec >/dev/null 2>&1 || true
  rm -f "$IPSEC_DNSMASQ_CONFIG_FILE"
  systemctl restart dnsmasq >/dev/null 2>&1 || true
  protocol_release_ipsec_owner
  connector_uninstall_runtime
}

command_start() {
  connector_require_root
  protocol_ensure_dnsmasq_runtime
  protocol_write_ipsec_dns_config
  connector_start_services
  systemctl enable --now "$IPSEC_STATE_SERVICE" "$XL2TPD_SERVICE" >/dev/null 2>&1 || systemctl enable --now ipsec "$XL2TPD_SERVICE" >/dev/null 2>&1 || true
  connector_apply_internal_redirect "$IPSEC_INTERFACE"
  protocol_restart_ipsec_dnsmasq
}

command_stop() {
  connector_require_root
  systemctl stop "$XL2TPD_SERVICE" "$IPSEC_STATE_SERVICE" ipsec >/dev/null 2>&1 || true
  connector_stop_services
}

command_dns_apply() {
  connector_require_root
  connector_validate_common_args
  connector_dns_apply "$DOH_ENDPOINTS"
  protocol_write_ipsec_dns_config
  protocol_restart_ipsec_dnsmasq
}

command_dns_status() {
  connector_dns_status_json
}

command_dns_repair() {
  connector_require_root
  connector_validate_common_args
  connector_dns_apply "$DOH_ENDPOINTS"
  protocol_sync_clients
}

main() {
  COMMAND="${1:-}"
  if [[ -z "$COMMAND" ]]; then
    usage
    exit 1
  fi
  shift || true
  parse_args "$@"
  connector_apply_relay_scope
  protocol_apply_relay_scope
  connector_load_metadata_defaults
  protocol_apply_port_defaults

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
