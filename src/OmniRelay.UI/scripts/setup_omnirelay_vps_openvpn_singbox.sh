#!/usr/bin/env bash
set -Eeuo pipefail
IFS=$'\n\t'

SCRIPT_NAME="$(basename "$0")"
GATEWAYCTL_SOURCE="${BASH_SOURCE[0]:-$0}"
PROTOCOL_ID="openvpn_tcp_singbox"
CONNECTOR_MODE="internal_tunnel"

PROTOCOL_CLIENTS_FILE=""
PROTOCOL_RUNTIME_FILE="${GATEWAY_ROOT_DIR:-/etc/omnirelay/gateway}/openvpn_runtime.json"
PROTOCOL_ENV_FILE="${GATEWAY_ROOT_DIR:-/etc/omnirelay/gateway}/openvpn_panel.env"
OPENVPN_EXPORT_DIR="/opt/omnirelay/omni-gateway/openvpn-exports"
OPENVPN_STATUS_FILE="/var/log/openvpn/omnirelay-status.log"
OPENVPN_INTERFACE="tun0"
OPENVPN_SERVICE="omnirelay-openvpn"
OPENVPN_ENFORCE_SERVICE="omnirelay-openvpn-enforce"
OPENVPN_ENFORCE_TIMER="omnirelay-openvpn-enforce.timer"
OPENVPN_MANAGEMENT_PORT=7505
OPENVPN_DNSMASQ_CONFIG_FILE="/etc/dnsmasq.d/omnirelay-openvpn.conf"
OPENVPN_DIR="${GATEWAY_ROOT_DIR:-/etc/omnirelay/gateway}/openvpn"
OPENVPN_EASYRSA_DIR="${OPENVPN_DIR}/easy-rsa"
OPENVPN_PKI_DIR="${OPENVPN_EASYRSA_DIR}/pki"
OPENVPN_SERVER_CONFIG_FILE="${OPENVPN_DIR}/server.conf"
OPENVPN_CCD_DIR="${OPENVPN_DIR}/ccd"
OPENVPN_IP_POOL_FILE="${OPENVPN_DIR}/ipp.txt"
OPENVPN_CLIENT_IP_MAP_FILE="${OPENVPN_DIR}/client-ip-map.json"
OPENVPN_AUTH_VERIFY_SCRIPT_FILE="${OPENVPN_DIR}/auth-verify.sh"
OPENVPN_AUTH_FILE="${OPENVPN_DIR}/users.auth"
OPENVPN_TLS_CRYPT_KEY_FILE="${OPENVPN_DIR}/ta.key"
OPENVPN_CA_CERT_FILE="${OPENVPN_DIR}/ca.crt"
OPENVPN_SERVER_CERT_FILE="${OPENVPN_DIR}/client-shared.crt"
OPENVPN_SERVER_KEY_FILE="${OPENVPN_DIR}/client-shared.key"
OPENVPN_DH_FILE="${OPENVPN_DIR}/dh.pem"
OPENVPN_CRL_FILE="${OPENVPN_DIR}/crl.pem"
OPENVPN_SHARED_CA_CERT_FILE=""
OPENVPN_SHARED_CLIENT_CERT_FILE=""
OPENVPN_SHARED_CLIENT_KEY_FILE=""
OPENVPN_SHARED_TLS_CRYPT_KEY_FILE=""
OPENVPN_SPEED_LIMIT_STATE_FILE="${OPENVPN_DIR}/speed-limit-state.json"
OPENVPN_NETWORK="10.29.0.0/24"
OUTPUT_JSON="false"
COMMAND=""
PANEL_BASE_PATH=""
OPENVPN_PUBLIC_HOST=""

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
  enforce-sessions
  status | health | integrity-check | dns-apply | dns-status | dns-repair | get-protocol
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
      --openvpn-network) require_value "$1" "${2:-}"; OPENVPN_NETWORK="$2"; shift 2 ;;
      --openvpn-shared-ca-cert-file) require_value "$1" "${2:-}"; OPENVPN_SHARED_CA_CERT_FILE="$2"; shift 2 ;;
      --openvpn-shared-client-cert-file) require_value "$1" "${2:-}"; OPENVPN_SHARED_CLIENT_CERT_FILE="$2"; shift 2 ;;
      --openvpn-shared-client-key-file) require_value "$1" "${2:-}"; OPENVPN_SHARED_CLIENT_KEY_FILE="$2"; shift 2 ;;
      --openvpn-shared-tls-crypt-key-file) require_value "$1" "${2:-}"; OPENVPN_SHARED_TLS_CRYPT_KEY_FILE="$2"; shift 2 ;;
      --gateway-sni) require_value "$1" "${2:-}"; shift 2 ;;
      --gateway-target) require_value "$1" "${2:-}"; shift 2 ;;
      --camouflage-server) require_value "$1" "${2:-}"; shift 2 ;;
      --) shift; break ;;
      *) die "Unknown argument: $1" ;;
    esac
  done
}

protocol_openvpn_relay_interface() {
  local h
  h="${RELAY_HASH:-$(connector_short_hash "$RELAY_ID")}"
  printf 'omni%.7s' "$h"
}

protocol_apply_relay_scope() {
  PROTOCOL_CLIENTS_FILE=""
  PROTOCOL_RUNTIME_FILE="${GATEWAY_ROOT_DIR}/openvpn_runtime.json"
  PROTOCOL_ENV_FILE="${GATEWAY_ROOT_DIR}/openvpn_panel.env"
  OPENVPN_EXPORT_DIR="${PANEL_APP_DIR}/openvpn-exports"
  OPENVPN_STATUS_FILE="/var/log/openvpn/omnirelay-status${RELAY_ID:+-${RELAY_ID}}.log"
  if [[ -n "${RELAY_ID:-}" ]]; then
    OPENVPN_INTERFACE="$(protocol_openvpn_relay_interface)"
    OPENVPN_SERVICE="omnirelay-openvpn-${RELAY_ID}"
    OPENVPN_ENFORCE_SERVICE="omnirelay-openvpn-enforce-${RELAY_ID}"
    OPENVPN_ENFORCE_TIMER="omnirelay-openvpn-enforce-${RELAY_ID}.timer"
    OPENVPN_DNSMASQ_CONFIG_FILE="/etc/dnsmasq.d/omnirelay-openvpn-${RELAY_ID}.conf"
  fi
  OPENVPN_DIR="${GATEWAY_ROOT_DIR}/openvpn"
  OPENVPN_EASYRSA_DIR="${OPENVPN_DIR}/easy-rsa"
  OPENVPN_PKI_DIR="${OPENVPN_EASYRSA_DIR}/pki"
  OPENVPN_SERVER_CONFIG_FILE="${OPENVPN_DIR}/server.conf"
  OPENVPN_CCD_DIR="${OPENVPN_DIR}/ccd"
  OPENVPN_IP_POOL_FILE="${OPENVPN_DIR}/ipp.txt"
  OPENVPN_CLIENT_IP_MAP_FILE="${OPENVPN_DIR}/client-ip-map.json"
  OPENVPN_AUTH_VERIFY_SCRIPT_FILE="${OPENVPN_DIR}/auth-verify.sh"
  OPENVPN_AUTH_FILE="${OPENVPN_DIR}/users.auth"
  OPENVPN_TLS_CRYPT_KEY_FILE="${OPENVPN_DIR}/ta.key"
  OPENVPN_CA_CERT_FILE="${OPENVPN_DIR}/ca.crt"
  OPENVPN_SERVER_CERT_FILE="${OPENVPN_DIR}/client-shared.crt"
  OPENVPN_SERVER_KEY_FILE="${OPENVPN_DIR}/client-shared.key"
  OPENVPN_DH_FILE="${OPENVPN_DIR}/dh.pem"
  OPENVPN_CRL_FILE="${OPENVPN_DIR}/crl.pem"
  OPENVPN_SPEED_LIMIT_STATE_FILE="${OPENVPN_DIR}/speed-limit-state.json"
}

protocol_load_panel_env() {
  if [[ -f "$PROTOCOL_ENV_FILE" ]]; then
    # shellcheck disable=SC1090
    source "$PROTOCOL_ENV_FILE" || true
  fi
}

protocol_validate_install_args() {
  [[ -n "$OPENVPN_NETWORK" ]] || die "--openvpn-network is required for ${PROTOCOL_ID}"
  [[ -n "$OPENVPN_SHARED_CA_CERT_FILE" ]] || die "--openvpn-shared-ca-cert-file is required for ${PROTOCOL_ID}"
  [[ -n "$OPENVPN_SHARED_CLIENT_CERT_FILE" ]] || die "--openvpn-shared-client-cert-file is required for ${PROTOCOL_ID}"
  [[ -n "$OPENVPN_SHARED_CLIENT_KEY_FILE" ]] || die "--openvpn-shared-client-key-file is required for ${PROTOCOL_ID}"
  [[ -n "$OPENVPN_SHARED_TLS_CRYPT_KEY_FILE" ]] || die "--openvpn-shared-tls-crypt-key-file is required for ${PROTOCOL_ID}"
}

protocol_seed_clients() {
  rm -f "${PANEL_APP_DIR}/openvpn_clients.json" >/dev/null 2>&1 || true
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
VALUES('${id}','${PROTOCOL_ID}','omni-client@local','ovpn_client','ovpn_client','${password}',1,0,0,0,${now},${now});
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
  COALESCE(email,''),
  COALESCE(auth_username, username, ''),
  COALESCE(auth_secret,''),
  CAST(COALESCE(enabled,0) AS TEXT),
  CAST(COALESCE(speed_limit_kbps,0) AS TEXT)
FROM clients
WHERE protocol_id='${PROTOCOL_ID//\'/\'\'}'
ORDER BY email COLLATE NOCASE, client_id;
" 2>/dev/null || true
}

protocol_ensure_runtime() {
  local redirect_port stored_network
  install -d -m 0755 "$(dirname "$PROTOCOL_RUNTIME_FILE")"

  if [[ -f "$PROTOCOL_RUNTIME_FILE" ]]; then
    redirect_port="$(jq -r '.connectorRedirectPort // empty' "$PROTOCOL_RUNTIME_FILE" 2>/dev/null || true)"
    stored_network="$(jq -r '.openVpnNetwork // empty' "$PROTOCOL_RUNTIME_FILE" 2>/dev/null || true)"
  else
    redirect_port=""
    stored_network=""
  fi

  [[ -n "$OPENVPN_NETWORK" ]] || OPENVPN_NETWORK="$stored_network"
  [[ -n "$OPENVPN_NETWORK" ]] || OPENVPN_NETWORK="10.29.0.0/24"
  [[ "$redirect_port" =~ ^[0-9]+$ ]] || redirect_port="$(connector_choose_port)"

  jq -n \
    --argjson connectorRedirectPort "$redirect_port" \
    --arg openVpnNetwork "$OPENVPN_NETWORK" \
    '{connectorRedirectPort:$connectorRedirectPort,openVpnNetwork:$openVpnNetwork,updatedAtUtc:(now|todate)}' > "$PROTOCOL_RUNTIME_FILE"
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

protocol_openvpn_server_address() {
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
hosts = net.hosts()
try:
    print(str(next(hosts)))
except StopIteration:
    sys.exit(1)
PY
}

protocol_default_client_dns() {
  protocol_openvpn_server_address
}

protocol_openvpn_ifb_interface() {
  local h
  h="${RELAY_HASH:-$(connector_short_hash "$RELAY_ID")}"
  printf 'ifb%.8s' "$h"
}

protocol_openvpn_management_port() {
  if [[ -z "${RELAY_ID:-}" ]]; then
    printf '%s\n' "$OPENVPN_MANAGEMENT_PORT"
    return 0
  fi

  local hash_seed dec
  hash_seed="${RELAY_HASH:-$(connector_short_hash "$RELAY_ID")}"
  hash_seed="${hash_seed:0:4}"
  if [[ ! "$hash_seed" =~ ^[0-9a-fA-F]{4}$ ]]; then
    hash_seed="1f1f"
  fi
  dec=$((16#$hash_seed))
  printf '%s\n' "$((7505 + (dec % 1000)))"
}

protocol_write_speed_limit_state() {
  local state="${1:-unknown}" applied="${2:-0}" err="${3:-}" details_json="${4:-[]}"
  install -d -m 0755 "$(dirname "$OPENVPN_SPEED_LIMIT_STATE_FILE")"
  if ! jq -e . >/dev/null 2>&1 <<<"$details_json"; then
    details_json='[]'
  fi
  jq -n \
    --arg state "$state" \
    --argjson appliedClients "$applied" \
    --arg ifbInterface "$(protocol_openvpn_ifb_interface)" \
    --arg openVpnInterface "$OPENVPN_INTERFACE" \
    --arg lastError "$err" \
    --argjson details "$details_json" \
    --arg updatedAtUtc "$(date -u +'%Y-%m-%dT%H:%M:%SZ')" \
    '{state:$state,appliedClients:$appliedClients,ifbInterface:$ifbInterface,openVpnInterface:$openVpnInterface,lastError:$lastError,details:$details,updatedAtUtc:$updatedAtUtc}' > "$OPENVPN_SPEED_LIMIT_STATE_FILE"
  chmod 0640 "$OPENVPN_SPEED_LIMIT_STATE_FILE" || true
}

protocol_rebuild_client_ip_map() {
  local tmp_map server_ip
  tmp_map="$(mktemp)"
  server_ip="$(protocol_openvpn_server_address)" || die "failed to compute OpenVPN server address"

  python3 - "$OPENVPN_NETWORK" "$CONNECTOR_ACCOUNTING_DB" "$PROTOCOL_ID" "$OPENVPN_CLIENT_IP_MAP_FILE" "$server_ip" "$tmp_map" <<'PY'
import ipaddress
import json
import os
import sys

network_cidr, db_path, protocol_id, map_path, server_ip, out_path = sys.argv[1:7]
network = ipaddress.ip_network(network_cidr, strict=False)
if network.version != 4:
    raise SystemExit("OpenVPN speed limit supports IPv4 networks only.")

import sqlite3
conn = sqlite3.connect(db_path)
rows = conn.execute(
    "SELECT COALESCE(client_id,''), COALESCE(auth_username, username, '') FROM clients WHERE protocol_id=? ORDER BY email COLLATE NOCASE, client_id",
    (protocol_id,),
).fetchall()
clients_payload = [{"id": str(r[0] or ""), "identity": str(r[1] or "").strip()} for r in rows]

existing = {}
if os.path.exists(map_path):
    try:
        loaded = json.load(open(map_path, "r", encoding="utf-8"))
        if isinstance(loaded, dict):
            existing = {str(k): str(v) for k, v in loaded.items()}
    except Exception:
        existing = {}

server_addr = ipaddress.ip_address(server_ip)
hosts = [str(h) for h in network.hosts()]
reserved = {str(server_addr)}

valid_clients = []
for item in clients_payload:
    if not isinstance(item, dict):
        continue
    identity = str(item.get("identity", "")).strip()
    if identity:
        valid_clients.append(identity)

valid_clients = sorted(set(valid_clients))
used = set()
new_map = {}

for identity in valid_clients:
    candidate = existing.get(identity, "").strip()
    if candidate:
        try:
            parsed = ipaddress.ip_address(candidate)
            in_range = parsed in network
        except Exception:
            in_range = False
        if in_range and candidate not in reserved and candidate not in used:
            new_map[identity] = candidate
            used.add(candidate)
            continue

    assigned = None
    for host in hosts:
        if host in reserved or host in used:
            continue
        assigned = host
        break
    if not assigned:
        raise SystemExit(f"insufficient VPN addresses in {network_cidr} for {len(valid_clients)} clients")
    new_map[identity] = assigned
    used.add(assigned)

with open(out_path, "w", encoding="utf-8") as handle:
    json.dump(new_map, handle, indent=2, sort_keys=True)
    handle.write("\n")
PY

  install -d -m 0755 "$(dirname "$OPENVPN_CLIENT_IP_MAP_FILE")"
  install -m 0640 "$tmp_map" "$OPENVPN_CLIENT_IP_MAP_FILE"
  rm -f "$tmp_map"
}

protocol_render_client_ccd() {
  local netmask row id username cn ip ccd_file tmp_ccd
  readarray -t _network_parts < <(protocol_openvpn_network_parts) || die "invalid OpenVPN network: $OPENVPN_NETWORK"
  netmask="${_network_parts[1]:-}"
  [[ -n "$netmask" ]] || die "failed parsing OpenVPN netmask"
  install -d -m 0755 "$OPENVPN_CCD_DIR"
  find "$OPENVPN_CCD_DIR" -type f -delete 2>/dev/null || true

  while IFS=$'\t' read -r id _email username _secret _enabled _speed; do
    [[ -n "$id" && -n "$username" ]] || continue
    cn="$username"
    ip="$(jq -r --arg identity "$username" '.[$identity] // empty' "$OPENVPN_CLIENT_IP_MAP_FILE" 2>/dev/null || true)"
    [[ -n "$ip" ]] || die "missing client IP mapping for identity ${username}"
    ccd_file="${OPENVPN_CCD_DIR}/${cn}"
    tmp_ccd="$(mktemp)"
    printf 'ifconfig-push %s %s\n' "$ip" "$netmask" > "$tmp_ccd"
    install -m 0600 "$tmp_ccd" "$ccd_file"
    rm -f "$tmp_ccd"
  done < <(protocol_db_clients_rows)
}

protocol_cleanup_openvpn_speed_limits() {
  local ifb
  ifb="$(protocol_openvpn_ifb_interface)"
  tc qdisc del dev "$OPENVPN_INTERFACE" ingress 2>/dev/null || true
  tc qdisc del dev "$OPENVPN_INTERFACE" root 2>/dev/null || true
  tc qdisc del dev "$ifb" root 2>/dev/null || true
  ip link set "$ifb" down 2>/dev/null || true
  ip link delete "$ifb" type ifb 2>/dev/null || true
}

protocol_wait_openvpn_interface() {
  local i
  for i in $(seq 1 20); do
    if ip link show dev "$OPENVPN_INTERFACE" >/dev/null 2>&1; then
      return 0
    fi
    sleep 0.5
  done
  return 1
}

protocol_apply_openvpn_speed_limits() {
  local ifb rate_kbit classid applied=0 row id username speed ip details_json
  details_json='[]'
  command -v tc >/dev/null 2>&1 || { protocol_write_speed_limit_state "error" 0 "tc binary not found" "$details_json"; die "OpenVPN speed limit enforcement requires tc (iproute2)." ; }
  command -v ip >/dev/null 2>&1 || { protocol_write_speed_limit_state "error" 0 "ip binary not found" "$details_json"; die "OpenVPN speed limit enforcement requires ip command (iproute2)." ; }

  if ! protocol_wait_openvpn_interface; then
    protocol_write_speed_limit_state "error" 0 "openvpn interface ${OPENVPN_INTERFACE} not present" "$details_json"
    die "OpenVPN interface ${OPENVPN_INTERFACE} is not available for speed limit enforcement."
  fi

  ifb="$(protocol_openvpn_ifb_interface)"
  protocol_cleanup_openvpn_speed_limits
  ip link add "$ifb" type ifb 2>/dev/null || true
  ip link show dev "$ifb" >/dev/null 2>&1 || {
    protocol_write_speed_limit_state "error" 0 "ifb_create_failed:${ifb}" "$details_json"
    die "OpenVPN speed limit enforcement requires IFB support (failed to create ${ifb})."
  }
  ip link set dev "$ifb" up >/dev/null 2>&1 || {
    protocol_write_speed_limit_state "error" 0 "ifb_up_failed:${ifb}" "$details_json"
    die "Failed to bring IFB interface ${ifb} up for OpenVPN speed limiting."
  }

  tc qdisc add dev "$OPENVPN_INTERFACE" root handle 1: htb default 9999 >/dev/null 2>&1 || {
    protocol_write_speed_limit_state "error" 0 "tun_root_qdisc_failed:${OPENVPN_INTERFACE}" "$details_json"
    die "Failed to configure OpenVPN egress shaper on ${OPENVPN_INTERFACE}."
  }
  tc class add dev "$OPENVPN_INTERFACE" parent 1: classid 1:1 htb rate 10000mbit ceil 10000mbit >/dev/null 2>&1 || {
    protocol_write_speed_limit_state "error" 0 "tun_parent_class_failed:${OPENVPN_INTERFACE}" "$details_json"
    die "Failed to configure OpenVPN parent egress class."
  }
  tc class add dev "$OPENVPN_INTERFACE" parent 1:1 classid 1:9999 htb rate 10000mbit ceil 10000mbit >/dev/null 2>&1 || {
    protocol_write_speed_limit_state "error" 0 "tun_default_class_failed:${OPENVPN_INTERFACE}" "$details_json"
    die "Failed to configure OpenVPN default egress class."
  }

  tc qdisc add dev "$ifb" root handle 1: htb default 9999 >/dev/null 2>&1 || {
    protocol_write_speed_limit_state "error" 0 "ifb_root_qdisc_failed:${ifb}" "$details_json"
    die "Failed to configure OpenVPN ingress shaper on ${ifb}."
  }
  tc class add dev "$ifb" parent 1: classid 1:1 htb rate 10000mbit ceil 10000mbit >/dev/null 2>&1 || {
    protocol_write_speed_limit_state "error" 0 "ifb_parent_class_failed:${ifb}" "$details_json"
    die "Failed to configure OpenVPN parent ingress class."
  }
  tc class add dev "$ifb" parent 1:1 classid 1:9999 htb rate 10000mbit ceil 10000mbit >/dev/null 2>&1 || {
    protocol_write_speed_limit_state "error" 0 "ifb_default_class_failed:${ifb}" "$details_json"
    die "Failed to configure OpenVPN default ingress class."
  }

  tc qdisc add dev "$OPENVPN_INTERFACE" handle ffff: ingress >/dev/null 2>&1 || {
    protocol_write_speed_limit_state "error" 0 "tun_ingress_qdisc_failed:${OPENVPN_INTERFACE}" "$details_json"
    die "Failed to configure OpenVPN ingress redirect qdisc."
  }
  tc filter add dev "$OPENVPN_INTERFACE" parent ffff: protocol all prio 1 matchall action mirred egress redirect dev "$ifb" >/dev/null 2>&1 || {
    protocol_write_speed_limit_state "error" 0 "tun_ingress_redirect_failed:${OPENVPN_INTERFACE}" "$details_json"
    die "Failed to attach OpenVPN ingress redirect to ${ifb}."
  }

  classid=10
  while IFS=$'\t' read -r id _email username _secret enabled speed; do
    [[ -n "$id" && -n "$username" ]] || continue
    [[ "$enabled" == "1" ]] || continue
    [[ "$speed" =~ ^[0-9]+$ ]] || speed=0
    (( speed > 0 )) || continue
    ip="$(jq -r --arg identity "$username" '.[$identity] // empty' "$OPENVPN_CLIENT_IP_MAP_FILE" 2>/dev/null || true)"
    if [[ -z "$ip" ]]; then
      log "openvpn speed-limit: skipped client_id=${id} identity=${username} reason=missing_ip_mapping"
      details_json="$(jq -c --arg clientId "$id" --arg identity "$username" --arg reason "missing_ip_mapping" '. + [{"status":"skipped","clientId":$clientId,"identity":$identity,"reason":$reason}]' <<<"$details_json")"
      continue
    fi
    rate_kbit="${speed}kbit"

    tc class add dev "$OPENVPN_INTERFACE" parent 1:1 classid "1:${classid}" htb rate "$rate_kbit" ceil "$rate_kbit" burst 32k cburst 32k >/dev/null 2>&1 || {
      protocol_write_speed_limit_state "error" "$applied" "tun_class_failed:${id}" "$details_json"
      die "Failed adding OpenVPN egress speed class for client ${id}."
    }
    tc filter add dev "$OPENVPN_INTERFACE" parent 1: protocol ip prio 10 u32 match ip dst "${ip}/32" flowid "1:${classid}" >/dev/null 2>&1 || {
      protocol_write_speed_limit_state "error" "$applied" "tun_filter_failed:${id}" "$details_json"
      die "Failed adding OpenVPN egress speed filter for client ${id}."
    }
    tc class add dev "$ifb" parent 1:1 classid "1:${classid}" htb rate "$rate_kbit" ceil "$rate_kbit" burst 32k cburst 32k >/dev/null 2>&1 || {
      protocol_write_speed_limit_state "error" "$applied" "ifb_class_failed:${id}" "$details_json"
      die "Failed adding OpenVPN ingress speed class for client ${id}."
    }
    tc filter add dev "$ifb" parent 1: protocol ip prio 10 u32 match ip src "${ip}/32" flowid "1:${classid}" >/dev/null 2>&1 || {
      protocol_write_speed_limit_state "error" "$applied" "ifb_filter_failed:${id}" "$details_json"
      die "Failed adding OpenVPN ingress speed filter for client ${id}."
    }

    log "openvpn speed-limit: applied client_id=${id} identity=${username} ip=${ip} kbps=${speed} classid=1:${classid}"
    details_json="$(jq -c --arg clientId "$id" --arg identity "$username" --arg ip "$ip" --arg kbps "$speed" --arg classId "1:${classid}" '. + [{"status":"applied","clientId":$clientId,"identity":$identity,"ip":$ip,"kbps":($kbps|tonumber),"classId":$classId}]' <<<"$details_json")"
    applied=$((applied + 1))
    classid=$((classid + 1))
  done < <(protocol_db_clients_rows)

  protocol_write_speed_limit_state "ok" "$applied" "" "$details_json"
}

protocol_generate_tls_crypt_key() {
  local openvpn_bin="$1"
  local key_file="$2"
  local err_log
  err_log="$(mktemp)"

  if "$openvpn_bin" --genkey secret "$key_file" > /dev/null 2>>"$err_log"; then
    rm -f "$err_log"
    return 0
  fi
  if "$openvpn_bin" --genkey tls-crypt "$key_file" > /dev/null 2>>"$err_log"; then
    rm -f "$err_log"
    return 0
  fi
  if "$openvpn_bin" --genkey tls-auth "$key_file" > /dev/null 2>>"$err_log"; then
    rm -f "$err_log"
    return 0
  fi
  if "$openvpn_bin" --genkey --secret "$key_file" > /dev/null 2>>"$err_log"; then
    rm -f "$err_log"
    return 0
  fi

  log "OpenVPN tls-crypt key generation error: $(tr '\n' ';' < "$err_log" | sed 's/;*$//' | cut -c1-600)"
  rm -f "$err_log"
  return 1
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
  install -d -m 0755 "$OPENVPN_DIR"
  if [[ -n "$OPENVPN_SHARED_CA_CERT_FILE" || -n "$OPENVPN_SHARED_CLIENT_CERT_FILE" || -n "$OPENVPN_SHARED_CLIENT_KEY_FILE" || -n "$OPENVPN_SHARED_TLS_CRYPT_KEY_FILE" ]]; then
    [[ -f "$OPENVPN_SHARED_CA_CERT_FILE" ]] || die "OpenVPN shared CA cert missing: $OPENVPN_SHARED_CA_CERT_FILE"
    [[ -f "$OPENVPN_SHARED_CLIENT_CERT_FILE" ]] || die "OpenVPN shared client cert missing: $OPENVPN_SHARED_CLIENT_CERT_FILE"
    [[ -f "$OPENVPN_SHARED_CLIENT_KEY_FILE" ]] || die "OpenVPN shared client key missing: $OPENVPN_SHARED_CLIENT_KEY_FILE"
    [[ -f "$OPENVPN_SHARED_TLS_CRYPT_KEY_FILE" ]] || die "OpenVPN shared tls-crypt key missing: $OPENVPN_SHARED_TLS_CRYPT_KEY_FILE"

    install -m 0644 "$OPENVPN_SHARED_CA_CERT_FILE" "$OPENVPN_CA_CERT_FILE"
    install -m 0644 "$OPENVPN_SHARED_CLIENT_CERT_FILE" "$OPENVPN_SERVER_CERT_FILE"
    install -m 0600 "$OPENVPN_SHARED_CLIENT_KEY_FILE" "$OPENVPN_SERVER_KEY_FILE"
    install -m 0600 "$OPENVPN_SHARED_TLS_CRYPT_KEY_FILE" "$OPENVPN_TLS_CRYPT_KEY_FILE"
  fi

  [[ -f "$OPENVPN_CA_CERT_FILE" ]] || die "OpenVPN shared CA cert missing: $OPENVPN_CA_CERT_FILE"
  [[ -f "$OPENVPN_SERVER_CERT_FILE" ]] || die "OpenVPN shared client cert missing: $OPENVPN_SERVER_CERT_FILE"
  [[ -f "$OPENVPN_SERVER_KEY_FILE" ]] || die "OpenVPN shared client key missing: $OPENVPN_SERVER_KEY_FILE"
  [[ -f "$OPENVPN_TLS_CRYPT_KEY_FILE" ]] || die "OpenVPN shared tls-crypt key missing: $OPENVPN_TLS_CRYPT_KEY_FILE"
  if [[ ! -f "$OPENVPN_DH_FILE" ]]; then
    openssl dhparam -out "$OPENVPN_DH_FILE" 2048 >/dev/null 2>&1 || die "failed to generate OpenVPN DH params"
  fi
  chmod 0644 "$OPENVPN_CA_CERT_FILE" "$OPENVPN_SERVER_CERT_FILE" "$OPENVPN_DH_FILE" || true
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

  while IFS=$'\t' read -r id _email username password enabled _speed; do
    [[ -n "$id" && -n "$username" && -n "$password" ]] || continue

    cn="shared-client"

    if [[ "$enabled" == "1" ]]; then
      hash="$(printf '%s' "$password" | sha256sum | awk '{print $1}')"
      printf '%s:%s:%s\n' "$username" "$hash" "$cn" >> "$auth_tmp"
    fi
  done < <(protocol_db_clients_rows)

  install -m 0600 "$auth_tmp" "$OPENVPN_AUTH_FILE"
  rm -f "$auth_tmp"
}

protocol_sync_client_addressing() {
  protocol_rebuild_client_ip_map
  protocol_render_client_ccd
}

protocol_ensure_clients_file_runtime_access() {
  local panel_group runtime_dir relay_root_dir
  panel_group="${PANEL_USER_ACCOUNT:-omnigateway}"
  runtime_dir="$(dirname "$PROTOCOL_RUNTIME_FILE")"
  relay_root_dir="$(dirname "$runtime_dir")"

  if ! id -u "$panel_group" >/dev/null 2>&1; then
    return 0
  fi

  install -d -m 2770 -o root -g "$panel_group" "$relay_root_dir"
  install -d -m 2770 -o root -g "$panel_group" "$runtime_dir"
  install -d -m 2770 -o root -g "$panel_group" "$OPENVPN_DIR"
  install -d -m 2770 -o root -g "$panel_group" "$OPENVPN_EASYRSA_DIR" "$OPENVPN_PKI_DIR"
  install -d -m 2770 -o root -g "$panel_group" "$OPENVPN_CCD_DIR"

  chown "root:${panel_group}" "$relay_root_dir" "$runtime_dir" 2>/dev/null || true
  chmod 2770 "$relay_root_dir" "$runtime_dir" 2>/dev/null || true

  [[ -f "$PROTOCOL_RUNTIME_FILE" ]] && chown "root:${panel_group}" "$PROTOCOL_RUNTIME_FILE" 2>/dev/null || true
  [[ -f "$PROTOCOL_RUNTIME_FILE" ]] && chmod 0660 "$PROTOCOL_RUNTIME_FILE" 2>/dev/null || true

  chown -R "root:${panel_group}" "$OPENVPN_DIR" 2>/dev/null || true
  # Keep execute bits on scripts/binaries while granting panel group write access.
  chmod -R g+rwX "$OPENVPN_DIR" 2>/dev/null || true
  find "$OPENVPN_DIR" -type d -exec chmod g+s {} + 2>/dev/null || true
  [[ -f "$OPENVPN_AUTH_VERIFY_SCRIPT_FILE" ]] && chmod 0750 "$OPENVPN_AUTH_VERIFY_SCRIPT_FILE" 2>/dev/null || true
}

protocol_write_auth_verify_script() {
  cat > "$OPENVPN_AUTH_VERIFY_SCRIPT_FILE" <<'EOF'
#!/usr/bin/env bash
set -Eeuo pipefail
IFS=$'\n\t'
ACCOUNTING_DB="__ACCOUNTING_DB__"
PROTOCOL_ID="__PROTOCOL_ID__"
INPUT_FILE="${1:-}"
[[ -n "$INPUT_FILE" && -f "$INPUT_FILE" ]] || exit 1
USERNAME="$(sed -n '1p' "$INPUT_FILE" | tr -d '\r\n')"
PASSWORD="$(sed -n '2p' "$INPUT_FILE" | tr -d '\r\n')"
[[ -n "$USERNAME" && -n "$PASSWORD" ]] || exit 1
HASH="$(printf '%s' "$PASSWORD" | sha256sum | awk '{print $1}')"
# Defensive normalization for legacy callers that may wrap username in quotes.
USERNAME="${USERNAME#\"}"
USERNAME="${USERNAME%\"}"

command -v sqlite3 >/dev/null 2>&1 || exit 1
[[ -f "$ACCOUNTING_DB" ]] || exit 1

row="$(
  sqlite3 -separator '|' "$ACCOUNTING_DB" "
    SELECT
      CAST(COALESCE(c.enabled,0) AS TEXT),
      COALESCE(c.auth_secret,''),
      COALESCE(e.disabled_reason,''),
      COALESCE(c.client_id,'')
    FROM clients c
    LEFT JOIN enforcement_state e ON e.client_id=c.client_id
    WHERE c.protocol_id='${PROTOCOL_ID//\'/\'\'}'
      AND c.auth_username='${USERNAME//\'/\'\'}'
    LIMIT 1;
  " 2>/dev/null || true
)"
[[ -n "$row" ]] || exit 1

enabled_flag="${row%%|*}"
rest="${row#*|}"
stored_password="${rest%%|*}"
rest="${rest#*|}"
disabled_reason="${rest%%|*}"
client_id="${rest#*|}"

[[ "$enabled_flag" == "1" ]] || exit 1
case "$disabled_reason" in
  quota_exceeded|expired|manual_disabled) exit 1 ;;
esac

stored_hash="$(printf '%s' "$stored_password" | sha256sum | awk '{print $1}')"
if [[ "$stored_hash" == "$HASH" ]]; then
  logger -t omnirelay-openvpn-auth "auth_allow protocol=${PROTOCOL_ID} username=${USERNAME} client_id=${client_id}"
  exit 0
fi
logger -t omnirelay-openvpn-auth "auth_deny_bad_secret protocol=${PROTOCOL_ID} username=${USERNAME} client_id=${client_id}"
exit 1
EOF
  sed -i \
    -e "s|__ACCOUNTING_DB__|${CONNECTOR_ACCOUNTING_DB}|g" \
    -e "s|__PROTOCOL_ID__|${PROTOCOL_ID}|g" \
    "$OPENVPN_AUTH_VERIFY_SCRIPT_FILE"
  chmod 0750 "$OPENVPN_AUTH_VERIFY_SCRIPT_FILE" || true
  if ! bash -n "$OPENVPN_AUTH_VERIFY_SCRIPT_FILE" >/dev/null 2>&1; then
    die "generated auth-verify script is invalid: ${OPENVPN_AUTH_VERIFY_SCRIPT_FILE}"
  fi
}

protocol_write_openvpn_server_config() {
  local network_addr netmask dns_values management_port
  readarray -t _network_parts < <(protocol_openvpn_network_parts) || die "invalid OpenVPN network: $OPENVPN_NETWORK"
  network_addr="${_network_parts[0]:-}"
  netmask="${_network_parts[1]:-}"
  management_port="$(protocol_openvpn_management_port)"
  [[ -n "$network_addr" && -n "$netmask" ]] || die "failed to parse OpenVPN network: $OPENVPN_NETWORK"

  install -d -m 0755 "$OPENVPN_CCD_DIR"
  install -d -m 0755 "$(dirname "$OPENVPN_IP_POOL_FILE")"
  mkdir -p "$(dirname "$OPENVPN_STATUS_FILE")"
cat > "$OPENVPN_SERVER_CONFIG_FILE" <<EOF
port ${PUBLIC_PORT}
proto tcp-server
dev-type tun
dev ${OPENVPN_INTERFACE}
topology subnet
server ${network_addr} ${netmask}
push "redirect-gateway def1 bypass-dhcp"
push "block-ipv6"
keepalive 10 60
persist-key
persist-tun
ca ${OPENVPN_CA_CERT_FILE}
cert ${OPENVPN_SERVER_CERT_FILE}
key ${OPENVPN_SERVER_KEY_FILE}
dh ${OPENVPN_DH_FILE}
tls-crypt ${OPENVPN_TLS_CRYPT_KEY_FILE}
verify-client-cert require
username-as-common-name
auth-user-pass-verify ${OPENVPN_AUTH_VERIFY_SCRIPT_FILE} via-file
script-security 2
client-config-dir ${OPENVPN_CCD_DIR}
ifconfig-pool-persist ${OPENVPN_IP_POOL_FILE}
cipher AES-256-GCM
auth SHA256
data-ciphers AES-256-GCM:AES-128-GCM:CHACHA20-POLY1305
mssfix 1280
status-version 3
status ${OPENVPN_STATUS_FILE} 5
management 127.0.0.1 ${management_port}
verb 3
EOF

  printf 'push "dhcp-option DNS %s"\n' "$(protocol_default_client_dns)" >> "$OPENVPN_SERVER_CONFIG_FILE"

  chmod 0600 "$OPENVPN_SERVER_CONFIG_FILE" || true
}

protocol_disconnect_ineligible_clients() {
  local management_port status_snapshot enabled_tmp connected_tmp mgmt_dump_tmp id username cn identity
  management_port="$(protocol_openvpn_management_port)"
  enabled_tmp="$(mktemp)"
  connected_tmp="$(mktemp)"
  mgmt_dump_tmp="$(mktemp)"

  protocol_db_clients_rows | awk -F'\t' '$5=="1"{print $1 "\t" $3}' \
    | while IFS=$'\t' read -r id username; do
        [[ -n "$id" ]] || continue
        cn="$(protocol_client_cn_for_id "$id")"
        [[ -n "$cn" ]] && printf '%s\n' "$cn"
        [[ -n "$username" ]] && printf '%s\n' "$username"
      done \
    | sed '/^[[:space:]]*$/d' \
    | sort -u > "$enabled_tmp"

  # Prefer live management status (status 3), fallback to status file.
  if command -v nc >/dev/null 2>&1; then
    { printf 'status 3\n'; sleep 1; printf 'quit\n'; } | nc 127.0.0.1 "$management_port" > "$mgmt_dump_tmp" 2>/dev/null || true
  elif command -v netcat >/dev/null 2>&1; then
    { printf 'status 3\n'; sleep 1; printf 'quit\n'; } | netcat 127.0.0.1 "$management_port" > "$mgmt_dump_tmp" 2>/dev/null || true
  fi

  if [[ -s "$mgmt_dump_tmp" ]]; then
    awk -F, '
      BEGIN { cn_i=2; user_i=0; cid_i=0 }
      $1=="HEADER" && $2=="CLIENT_LIST" {
        for (i=3; i<=NF; i++) {
          if ($i=="Common Name") cn_i=i
          else if ($i=="Username") user_i=i
          else if ($i=="Client ID") cid_i=i
        }
        next
      }
      $1=="CLIENT_LIST" {
        cn=(cn_i>0 && cn_i<=NF ? $cn_i : "")
        user=(user_i>0 && user_i<=NF ? $user_i : "")
        cid=(cid_i>0 && cid_i<=NF ? $cid_i : "")
        gsub(/^[[:space:]]+|[[:space:]]+$/, "", cn)
        gsub(/^[[:space:]]+|[[:space:]]+$/, "", user)
        gsub(/^[[:space:]]+|[[:space:]]+$/, "", cid)
        printf "%s\t%s\t%s\n", cn, user, cid
      }
    ' "$mgmt_dump_tmp" 2>/dev/null > "$connected_tmp"
    if [[ ! -s "$connected_tmp" ]]; then
      # Fallback parser for whitespace-formatted "status 3" output.
      awk '
        /^CLIENT_LIST[[:space:]]+/ {
          cn=$2
          user=$10
          cid=$11
          if (user=="UNDEF") user=""
          if (cn!="" || user!="" || cid!="") {
            printf "%s\t%s\t%s\n", cn, user, cid
          }
        }
      ' "$mgmt_dump_tmp" 2>/dev/null > "$connected_tmp"
    fi
  elif [[ -s "$OPENVPN_STATUS_FILE" ]]; then
    # Resolve indices dynamically from HEADER,CLIENT_LIST to avoid format drift.
    awk -F, '
      BEGIN { cn_i=2; user_i=0; cid_i=0 }
      $1=="HEADER" && $2=="CLIENT_LIST" {
        for (i=3; i<=NF; i++) {
          if ($i=="Common Name") cn_i=i
          else if ($i=="Username") user_i=i
          else if ($i=="Client ID") cid_i=i
        }
        next
      }
      $1=="CLIENT_LIST" {
        cn=(cn_i>0 && cn_i<=NF ? $cn_i : "")
        user=(user_i>0 && user_i<=NF ? $user_i : "")
        cid=(cid_i>0 && cid_i<=NF ? $cid_i : "")
        gsub(/^[[:space:]]+|[[:space:]]+$/, "", cn)
        gsub(/^[[:space:]]+|[[:space:]]+$/, "", user)
        gsub(/^[[:space:]]+|[[:space:]]+$/, "", cid)
        printf "%s\t%s\t%s\n", cn, user, cid
      }
    ' "$OPENVPN_STATUS_FILE" 2>/dev/null > "$connected_tmp"
    if [[ ! -s "$connected_tmp" ]]; then
      # Fallback parser for whitespace-formatted legacy status file output.
      awk '
        /^CLIENT_LIST[[:space:]]+/ {
          cn=$2
          user=$10
          cid=$11
          if (user=="UNDEF") user=""
          if (cn!="" || user!="" || cid!="") {
            printf "%s\t%s\t%s\n", cn, user, cid
          }
        }
      ' "$OPENVPN_STATUS_FILE" 2>/dev/null > "$connected_tmp"
    fi
  else
    : > "$connected_tmp"
  fi

  while IFS=$'\t' read -r cn username cid; do
    [[ -n "$cn$username$cid" ]] || continue
    identity="$username"
    if [[ -z "$identity" ]]; then
      identity="$cn"
    fi
    [[ -n "$identity" ]] || continue

    if ! grep -Fxq "$identity" "$enabled_tmp"; then
      log "openvpn enforcement: disconnecting ineligible session username='${username:-}' cn='${cn:-}' client_id='${cid:-}'"
      for kick_attempt in 1 2 3; do
        if command -v nc >/dev/null 2>&1; then
          [[ -n "$cid" ]] && printf 'client-kill %s\n' "$cid" | nc -w 2 127.0.0.1 "$management_port" >/dev/null 2>&1 || true
          [[ -n "$cn" ]] && printf 'kill %s\n' "$cn" | nc -w 2 127.0.0.1 "$management_port" >/dev/null 2>&1 || true
          [[ -n "$username" ]] && printf 'kill %s\n' "$username" | nc -w 2 127.0.0.1 "$management_port" >/dev/null 2>&1 || true
          log "openvpn enforcement: kill commands sent via nc attempt=${kick_attempt} username='${username:-}' cn='${cn:-}' client_id='${cid:-}'"
        elif command -v netcat >/dev/null 2>&1; then
          [[ -n "$cid" ]] && printf 'client-kill %s\n' "$cid" | netcat -w 2 127.0.0.1 "$management_port" >/dev/null 2>&1 || true
          [[ -n "$cn" ]] && printf 'kill %s\n' "$cn" | netcat -w 2 127.0.0.1 "$management_port" >/dev/null 2>&1 || true
          [[ -n "$username" ]] && printf 'kill %s\n' "$username" | netcat -w 2 127.0.0.1 "$management_port" >/dev/null 2>&1 || true
          log "openvpn enforcement: kill commands sent via netcat attempt=${kick_attempt} username='${username:-}' cn='${cn:-}' client_id='${cid:-}'"
        else
          {
            exec 3<>"/dev/tcp/127.0.0.1/${management_port}" || exit 0
            [[ -n "$cid" ]] && printf 'client-kill %s\n' "$cid" >&3
            [[ -n "$cn" ]] && printf 'kill %s\n' "$cn" >&3
            [[ -n "$username" ]] && printf 'kill %s\n' "$username" >&3
            printf 'quit\n' >&3
            exec 3<&-
            exec 3>&-
          } >/dev/null 2>&1 || true
          log "openvpn enforcement: kill commands sent via /dev/tcp attempt=${kick_attempt} username='${username:-}' cn='${cn:-}' client_id='${cid:-}'"
        fi
        sleep 0.2
      done
    fi
  done < "$connected_tmp"

  rm -f "$enabled_tmp" "$connected_tmp" "$mgmt_dump_tmp"
}

protocol_openvpn_integrity_json() {
  local auth_script_valid clients_readable clients_json_valid management_reachable sync_ready details
  auth_script_valid=false
  clients_readable=false
  clients_json_valid=false
  management_reachable=false
  sync_ready=true
  details=""

  if [[ -f "$OPENVPN_AUTH_VERIFY_SCRIPT_FILE" ]] && bash -n "$OPENVPN_AUTH_VERIFY_SCRIPT_FILE" >/dev/null 2>&1; then
    auth_script_valid=true
  else
    details="${details}auth_script_invalid;"
  fi

  if [[ -r "$CONNECTOR_ACCOUNTING_DB" ]]; then
    clients_readable=true
  else
    details="${details}clients_unreadable;"
  fi

  if command -v sqlite3 >/dev/null 2>&1; then
    if sqlite3 "$CONNECTOR_ACCOUNTING_DB" "SELECT 1 FROM clients LIMIT 1;" >/dev/null 2>&1; then
      clients_json_valid=true
    else
      details="${details}clients_json_invalid;"
    fi
  else
    details="${details}jq_missing;"
  fi

  if command -v nc >/dev/null 2>&1; then
    if printf 'state\nquit\n' | nc -w 2 127.0.0.1 "$(protocol_openvpn_management_port)" >/dev/null 2>&1; then
      management_reachable=true
    fi
  elif command -v netcat >/dev/null 2>&1; then
    if printf 'state\nquit\n' | netcat -w 2 127.0.0.1 "$(protocol_openvpn_management_port)" >/dev/null 2>&1; then
      management_reachable=true
    fi
  fi
  if [[ "$management_reachable" != true ]]; then
    details="${details}management_unreachable;"
  fi

  if [[ ! -x "$GATEWAYCTL_PATH" ]]; then
    sync_ready=false
    details="${details}gatewayctl_not_executable;"
  fi

  if command -v jq >/dev/null 2>&1; then
    jq -c -n \
      --argjson authScriptValid "$auth_script_valid" \
      --argjson clientsReadable "$clients_readable" \
      --argjson clientsJsonValid "$clients_json_valid" \
      --argjson managementReachable "$management_reachable" \
      --argjson syncReady "$sync_ready" \
      --arg details "$details" \
      '{
        authScriptValid:$authScriptValid,
        clientsReadable:$clientsReadable,
        clientsJsonValid:$clientsJsonValid,
        managementReachable:$managementReachable,
        syncReady:$syncReady,
        details:$details
      }'
  else
    printf '{"authScriptValid":%s,"clientsReadable":%s,"clientsJsonValid":%s,"managementReachable":%s,"syncReady":%s,"details":"%s"}\n' \
      "$auth_script_valid" "$clients_readable" "$clients_json_valid" "$management_reachable" "$sync_ready" "$details"
  fi
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

protocol_gatewayctl_path() {
  if [[ -n "${RELAY_ID:-}" ]]; then
    printf '/usr/local/sbin/omnirelay-gatewayctl-%s\n' "$RELAY_ID"
    return
  fi
  printf '/usr/local/sbin/omnirelay-gatewayctl\n'
}

protocol_write_openvpn_enforcement_units() {
  local gatewayctl_path
  gatewayctl_path="$(protocol_gatewayctl_path)"
  cat > "/etc/systemd/system/${OPENVPN_ENFORCE_SERVICE}.service" <<EOF
[Unit]
Description=OmniRelay OpenVPN ineligible session enforcer
After=${OPENVPN_SERVICE}.service
Wants=${OPENVPN_SERVICE}.service

[Service]
Type=oneshot
ExecStart=${gatewayctl_path} enforce-sessions
EOF

  cat > "/etc/systemd/system/${OPENVPN_ENFORCE_TIMER}" <<EOF
[Unit]
Description=Run OmniRelay OpenVPN ineligible session enforcer periodically

[Timer]
OnBootSec=20s
OnUnitActiveSec=8s
AccuracySec=1s
Unit=${OPENVPN_ENFORCE_SERVICE}.service

[Install]
WantedBy=timers.target
EOF
}

protocol_enable_openvpn_enforcement_timer() {
  systemctl daemon-reload
  systemctl enable --now "${OPENVPN_ENFORCE_TIMER}" >/dev/null 2>&1 || true
}

protocol_disable_openvpn_enforcement_timer() {
  systemctl disable --now "${OPENVPN_ENFORCE_TIMER}" >/dev/null 2>&1 || true
  rm -f "/etc/systemd/system/${OPENVPN_ENFORCE_SERVICE}.service" "/etc/systemd/system/${OPENVPN_ENFORCE_TIMER}"
  systemctl daemon-reload >/dev/null 2>&1 || true
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

protocol_write_openvpn_dnsmasq_config() {
  local listen_ip
  listen_ip="$(protocol_default_client_dns)"

  connector_write_dnsmasq_global_config
  install -d -m 0755 "$(dirname "$OPENVPN_DNSMASQ_CONFIG_FILE")"
  {
    printf 'interface=%s\n' "$OPENVPN_INTERFACE"
    printf 'listen-address=%s\n' "$listen_ip"
    printf 'server=%s#%s\n' "$CONNECTOR_DNS_LISTEN_ADDRESS" "$CONNECTOR_DNS_LISTEN_PORT"
  } > "$OPENVPN_DNSMASQ_CONFIG_FILE"
  chmod 0644 "$OPENVPN_DNSMASQ_CONFIG_FILE" || true
}

protocol_restart_openvpn_dnsmasq() {
  connector_sanitize_dnsmasq_omnirelay_configs
  if command -v systemctl >/dev/null 2>&1 && systemctl list-unit-files dnsmasq.service >/dev/null 2>&1; then
    systemctl enable dnsmasq >/dev/null 2>&1 || true
    systemctl restart dnsmasq >/dev/null 2>&1 || {
      systemctl --no-pager -l status dnsmasq >&2 || true
      journalctl -u dnsmasq -n 80 --no-pager >&2 || true
      die "dnsmasq is not active after OpenVPN DNS config apply"
    }
  fi
}

protocol_apply_openvpn_network_controls() {
  local dns_server
  dns_server="$(protocol_default_client_dns)"
  sysctl -w net.ipv4.ip_forward=1 >/dev/null 2>&1 || true
  printf 'net.ipv4.ip_forward=1\n' > "/etc/sysctl.d/99-omnirelay-openvpn${RELAY_ID:+-${RELAY_ID}}.conf"

  iptables -C INPUT -i "$OPENVPN_INTERFACE" -d "$dns_server" -p udp --dport 53 -j ACCEPT 2>/dev/null || \
    iptables -I INPUT 1 -i "$OPENVPN_INTERFACE" -d "$dns_server" -p udp --dport 53 -j ACCEPT
  iptables -C INPUT -i "$OPENVPN_INTERFACE" -d "$dns_server" -p tcp --dport 53 -j ACCEPT 2>/dev/null || \
    iptables -I INPUT 1 -i "$OPENVPN_INTERFACE" -d "$dns_server" -p tcp --dport 53 -j ACCEPT
}

protocol_bootstrap_openvpn_runtime() {
  protocol_setup_openvpn_pki
  protocol_write_auth_verify_script
  protocol_write_openvpn_server_config
  protocol_write_openvpn_service_unit
  protocol_write_openvpn_enforcement_units
  protocol_ensure_dnsmasq_runtime
  protocol_write_openvpn_dnsmasq_config
  systemctl daemon-reload
  systemctl enable --now "$OPENVPN_SERVICE" >/dev/null 2>&1 || die "failed to start ${OPENVPN_SERVICE}"
  protocol_enable_openvpn_enforcement_timer
  protocol_restart_openvpn_dnsmasq
}

protocol_render_exports() {
  local host id username password profile export_group
  host="${PANEL_DOMAIN:-${OPENVPN_PUBLIC_HOST:-${VPS_IP:-$(hostname -I 2>/dev/null | awk '{print $1}')}}}"
  host="${host%% *}"
  [[ -n "$host" ]] || host="127.0.0.1"
  export_group="${PANEL_USER_ACCOUNT:-omnigateway}"

  [[ -f "$OPENVPN_CA_CERT_FILE" ]] || die "OpenVPN CA certificate missing: ${OPENVPN_CA_CERT_FILE}"
  [[ -f "$OPENVPN_TLS_CRYPT_KEY_FILE" ]] || die "OpenVPN tls-crypt key missing: ${OPENVPN_TLS_CRYPT_KEY_FILE}"

  if id -u "$export_group" >/dev/null 2>&1; then
    install -d -m 2770 -o root -g "$export_group" "$OPENVPN_EXPORT_DIR"
  else
    install -d -m 0755 "$OPENVPN_EXPORT_DIR"
  fi
  find "$OPENVPN_EXPORT_DIR" -type f -name '*.ovpn' -delete 2>/dev/null || true

  while IFS=$'\t' read -r id _email username password _enabled _speed; do
    [[ -n "$id" && -n "$username" && -n "$password" ]] || continue

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
      cat "$OPENVPN_SERVER_CERT_FILE"
      printf '</cert>\n'
      printf '<key>\n'
      cat "$OPENVPN_SERVER_KEY_FILE"
      printf '</key>\n'
      printf '<tls-crypt>\n'
      cat "$OPENVPN_TLS_CRYPT_KEY_FILE"
      printf '</tls-crypt>\n'
    } > "$profile"
    if id -u "$export_group" >/dev/null 2>&1; then
      chown "root:${export_group}" "$profile" || true
      chmod 0660 "$profile" || true
    else
      chmod 0644 "$profile" || true
    fi
  done < <(protocol_db_clients_rows)
}

protocol_write_panel_env() {
  local panel_public_host
  panel_public_host="${PANEL_DOMAIN:-${VPS_IP:-${OPENVPN_PUBLIC_HOST:-$(hostname -I 2>/dev/null | awk '{print $1}')}}}"
  panel_public_host="${panel_public_host%% *}"
  [[ -n "$panel_public_host" ]] || panel_public_host="127.0.0.1"
  cat > "$PROTOCOL_ENV_FILE" <<EOF
OPENVPN_EXPORT_DIR=${OPENVPN_EXPORT_DIR}
OPENVPN_STATE_DIR=${OPENVPN_DIR}
OPENVPN_RUNTIME_FILE=${PROTOCOL_RUNTIME_FILE}
OPENVPN_PUBLIC_PORT=${PUBLIC_PORT}
OPENVPN_PUBLIC_HOST=${panel_public_host}
OPENVPN_ACCOUNTING_DB=${CONNECTOR_ACCOUNTING_DB}
OPENVPN_STATUS_FILE=${OPENVPN_STATUS_FILE}
EOF
  chmod 0600 "$PROTOCOL_ENV_FILE" || true
}

protocol_sync_clients() {
  local config_json
  rm -f "${PANEL_APP_DIR}/openvpn_clients.json" >/dev/null 2>&1 || true
  protocol_seed_clients
  protocol_ensure_runtime
  protocol_ensure_clients_file_runtime_access
  protocol_setup_openvpn_pki
  protocol_sync_client_addressing
  protocol_write_auth_db_and_client_certs
  protocol_bootstrap_openvpn_runtime
  protocol_ensure_clients_file_runtime_access

  config_json="$(protocol_build_config_json)"
  connector_render_apply "$CONNECTOR_MODE" "$config_json"
  connector_apply_internal_redirect "$OPENVPN_INTERFACE"
  protocol_apply_openvpn_network_controls

  protocol_render_exports

  if ! systemctl is-active --quiet "$OPENVPN_SERVICE"; then
    systemctl start "$OPENVPN_SERVICE" >/dev/null 2>&1 || true
  fi
  if ! systemctl is-active --quiet "$OPENVPN_SERVICE"; then
    systemctl --no-pager -l status "$OPENVPN_SERVICE" >&2 || true
    journalctl -u "$OPENVPN_SERVICE" -n 80 --no-pager >&2 || true
    die "OpenVPN service is not active after syncing clients."
  fi
  # Final safety guard: auth-user-pass-verify script must stay executable.
  [[ -f "$OPENVPN_AUTH_VERIFY_SCRIPT_FILE" ]] && chmod 0750 "$OPENVPN_AUTH_VERIFY_SCRIPT_FILE" || true
  protocol_disconnect_ineligible_clients
  protocol_apply_openvpn_speed_limits
}

protocol_openvpn_state() {
  connector_service_state "$OPENVPN_SERVICE"
}

protocol_status_json() {
  local base_json inbound_id openvpn_state accounting_state accounting_timer accounting_error accounting_healthy speed_state speed_error speed_clients integrity_json
  base_json="$(connector_status_base_json)"
  inbound_id="$(jq -r '.inbounds[0].tag // ""' "$CONNECTOR_CONFIG_FILE" 2>/dev/null || true)"
  openvpn_state="$(protocol_openvpn_state)"
  accounting_state="$(jq -r '.accountingSyncState // "unknown"' <<<"$base_json")"
  accounting_timer="$(jq -r '.accountingTimerState // "unknown"' <<<"$base_json")"
  accounting_error="$(jq -r '.accountingLastError // ""' <<<"$base_json")"
  speed_state="unknown"
  speed_error=""
  speed_clients=0
  if [[ -f "$OPENVPN_SPEED_LIMIT_STATE_FILE" ]]; then
    speed_state="$(jq -r '.state // "unknown"' "$OPENVPN_SPEED_LIMIT_STATE_FILE" 2>/dev/null || echo "unknown")"
    speed_error="$(jq -r '.lastError // ""' "$OPENVPN_SPEED_LIMIT_STATE_FILE" 2>/dev/null || true)"
    speed_clients="$(jq -r '.appliedClients // 0' "$OPENVPN_SPEED_LIMIT_STATE_FILE" 2>/dev/null || echo 0)"
  fi
  [[ "$speed_clients" =~ ^[0-9]+$ ]] || speed_clients=0
  accounting_healthy=false
  if [[ "$accounting_state" == "ok" ]]; then
    accounting_healthy=true
  elif [[ "$accounting_state" == "failed" && "$accounting_error" == "state_missing" && "$accounting_timer" == "active" ]]; then
    accounting_healthy=true
  fi
  integrity_json="$(protocol_openvpn_integrity_json)"
  jq -c \
    --arg inboundId "$inbound_id" \
    --arg openVpnState "$openvpn_state" \
    --arg ipsecState "n/a" \
    --arg xl2tpdState "n/a" \
    --arg openVpnSpeedLimitState "$speed_state" \
    --arg openVpnSpeedLimitLastError "$speed_error" \
    --argjson openVpnSpeedLimitAppliedClients "$speed_clients" \
    --argjson openVpnAccountingHealthy "$accounting_healthy" \
    --argjson openVpnIntegrity "$integrity_json" \
    '. + {inboundId:$inboundId,openVpnState:$openVpnState,ipsecState:$ipsecState,xl2tpdState:$xl2tpdState,openVpnAccountingHealthy:$openVpnAccountingHealthy,openVpnSpeedLimitState:$openVpnSpeedLimitState,openVpnSpeedLimitLastError:$openVpnSpeedLimitLastError,openVpnSpeedLimitAppliedClients:$openVpnSpeedLimitAppliedClients,openVpnIntegrity:$openVpnIntegrity}' <<<"$base_json"
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
  connector_install_runtime openvpn easy-rsa dnsmasq iproute2
  connector_clean_legacy

  progress 40 "Preparing OpenVPN connector runtime"
  protocol_seed_clients
  protocol_ensure_runtime
  protocol_sync_clients

  progress 72 "Applying DNS profile"
  connector_dns_apply "$DOH_ENDPOINTS"

  progress 82 "Deploying OmniPanel"
  protocol_write_panel_env
  connector_deploy_omnipanel "$PROTOCOL_ID" "$PROTOCOL_ENV_FILE"

  connector_write_metadata_base "$PROTOCOL_ID" "$CONNECTOR_MODE"
  connector_merge_metadata_json "$(jq -c -n --arg runtimeFile "$PROTOCOL_RUNTIME_FILE" --arg exportDir "$OPENVPN_EXPORT_DIR" --arg openVpnStatusFile "$OPENVPN_STATUS_FILE" '{openVpn:{runtimeFile:$runtimeFile,exportDir:$exportDir},accounting:{source:"openvpn_status",openVpnStatusFile:$openVpnStatusFile}}')"
  progress 100 "${PROTOCOL_ID} install completed"
}

command_uninstall() {
  connector_require_root
  protocol_cleanup_openvpn_speed_limits
  connector_clear_internal_redirect "$OPENVPN_INTERFACE"
  protocol_disable_openvpn_enforcement_timer
  systemctl disable --now "$OPENVPN_SERVICE" >/dev/null 2>&1 || true
  rm -f "$OPENVPN_DNSMASQ_CONFIG_FILE" "/etc/sysctl.d/99-omnirelay-openvpn${RELAY_ID:+-${RELAY_ID}}.conf"
  rm -f "$OPENVPN_SPEED_LIMIT_STATE_FILE"
  systemctl restart dnsmasq >/dev/null 2>&1 || true
  connector_uninstall_runtime
}

command_start() {
  connector_require_root
  protocol_seed_clients
  protocol_ensure_runtime
  protocol_sync_client_addressing
  protocol_ensure_dnsmasq_runtime
  protocol_write_openvpn_dnsmasq_config
  connector_start_services
  systemctl enable --now "$OPENVPN_SERVICE" >/dev/null 2>&1 || true
  protocol_write_openvpn_enforcement_units
  protocol_enable_openvpn_enforcement_timer
  connector_apply_internal_redirect "$OPENVPN_INTERFACE"
  protocol_apply_openvpn_network_controls
  protocol_restart_openvpn_dnsmasq
  protocol_apply_openvpn_speed_limits
}

command_stop() {
  connector_require_root
  protocol_cleanup_openvpn_speed_limits
  systemctl stop "${OPENVPN_ENFORCE_TIMER}" >/dev/null 2>&1 || true
  systemctl stop "$OPENVPN_SERVICE" >/dev/null 2>&1 || true
  connector_stop_services
}

command_enforce_sessions() {
  connector_require_root
  protocol_disconnect_ineligible_clients
}

command_dns_apply() {
  connector_require_root
  connector_validate_common_args
  connector_dns_apply "$DOH_ENDPOINTS"
  protocol_write_openvpn_dnsmasq_config
  protocol_restart_openvpn_dnsmasq
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

command_integrity_check() {
  protocol_openvpn_integrity_json
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
  protocol_load_panel_env
  connector_load_metadata_defaults

  case "$COMMAND" in
    install) command_install ;;
    uninstall) command_uninstall ;;
    start) command_start ;;
    stop) command_stop ;;
    sync-clients) connector_require_root; protocol_sync_clients ;;
    enforce-sessions) command_enforce_sessions ;;
    status) protocol_status_json ;;
    health) protocol_health_json ;;
    integrity-check) command_integrity_check ;;
    dns-apply) command_dns_apply ;;
    dns-status) command_dns_status ;;
    dns-repair) command_dns_repair ;;
    get-protocol) echo "$PROTOCOL_ID" ;;
    *) usage; die "Unsupported command: $COMMAND" ;;
  esac
}

main "$@"
