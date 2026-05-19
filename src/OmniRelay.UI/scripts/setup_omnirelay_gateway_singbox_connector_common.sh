#!/usr/bin/env bash
set -Eeuo pipefail
IFS=$'\n\t'
umask 027

SCRIPT_NAME="${SCRIPT_NAME:-$(basename "${BASH_SOURCE[0]:-$0}")}"

PUBLIC_PORT=443
PANEL_PORT=2054
BACKEND_PORT=15000
SSH_PORT=22
BOOTSTRAP_SOCKS_PORT=16080
BOOTSTRAP_MODE="tunnel"
PROXY_CHECK_URL="https://8.8.8.8/,https://dns.google/,https://9.9.9.9/"
DOH_ENDPOINTS="https://1.1.1.1/dns-query,https://8.8.8.8/dns-query"
CONNECTOR_DNS_LISTEN_ADDRESS="127.0.0.1"
CONNECTOR_DNS_LISTEN_PORT=1053
VPS_IP=""
TUNNEL_USER="omnirelay"
TUNNEL_AUTH="host_key"
PANEL_USER=""
PANEL_PASSWORD=""
PANEL_DOMAIN=""
PANEL_DOMAIN_ONLY="false"
PANEL_SSL_ENABLED="false"
PANEL_SSL_MODE="letsencrypt"
PANEL_CERT_FILE=""
PANEL_KEY_FILE=""
RELAY_ID=""
RELAY_HASH=""
GATEWAYCTL_PATH="/usr/local/sbin/omnirelay-gatewayctl"
PANEL_SUDOERS_FILE="/etc/sudoers.d/omnigateway-singbox"
PANEL_NGINX_SITE_NAME="omnirelay-omnipanel"

if [[ -z "${RELAY_ID:-}" && "${SCRIPT_NAME:-}" == omnirelay-gatewayctl-* ]]; then
  RELAY_ID="${SCRIPT_NAME#omnirelay-gatewayctl-}"
fi

GATEWAY_ROOT_DIR="/etc/omnirelay/gateway"
GATEWAY_METADATA_FILE="${GATEWAY_ROOT_DIR}/metadata.json"
GATEWAY_DNS_PROFILE_FILE="${GATEWAY_ROOT_DIR}/dns_profile.json"
CONNECTOR_DIR="${GATEWAY_ROOT_DIR}/connector"
CONNECTOR_CONFIG_FILE="${CONNECTOR_DIR}/config.json"
CONNECTOR_STATE_FILE="${CONNECTOR_DIR}/state.json"
CONNECTOR_ACCOUNTING_DB="${CONNECTOR_DIR}/accounting.db"
CONNECTOR_CORE_STATE_FILE="${CONNECTOR_DIR}/connector_core_state.json"
CONNECTOR_ACCOUNTING_LOCK_FILE="/run/omnirelay-accounting-sync.lock"
CONNECTOR_BIN="/usr/local/bin/connector-core"
CONNECTOR_CORE_VERSION="${CONNECTOR_CORE_VERSION:-0.1.0}"
CONNECTOR_SERVICE="omnirelay-singbox"

PANEL_APP_DIR="/opt/omnirelay/omni-gateway"
PANEL_RELEASES_DIR="${PANEL_APP_DIR}/releases"
PANEL_CURRENT_DIR="${PANEL_APP_DIR}/current"
PANEL_ENV_FILE="${GATEWAY_ROOT_DIR}/omnipanel.env"
PANEL_SERVICE="omnirelay-omnipanel"
PANEL_USER_ACCOUNT="omnigateway"

BOOTSTRAP_COMMON_SCRIPT="/tmp/omnirelay-bootstrap-common.sh"
PANEL_COMMON_SCRIPT="/tmp/omnirelay-omnipanel-common.sh"
CONNECTOR_COMMON_INSTALLED="/usr/local/lib/omnirelay/singbox-connector-common.sh"
APT_PROXY_FILE="/etc/apt/apt.conf.d/99-omnirelay-socks"
DNSMASQ_GLOBAL_CONFIG_FILE="/etc/dnsmasq.d/00-omnirelay-global.conf"
CONNECTOR_REDIRECT_CHAIN="OMNIRELAY_CONNECTOR_REDIRECT"
CONNECTOR_REJECT_CHAIN="OMNIRELAY_CONNECTOR_REJECT"
CLOCK_SYNC_SCRIPT="/usr/local/sbin/omnirelay-clock-sync"
CLOCK_SYNC_ENV_FILE="${GATEWAY_ROOT_DIR}/clock_sync.env"
CLOCK_SYNC_STATE_FILE="${CONNECTOR_DIR}/clock_sync_state.json"
CLOCK_SYNC_SERVICE="omnirelay-clock-sync.service"
CLOCK_SYNC_TIMER="omnirelay-clock-sync.timer"
CLOCK_SYNC_PROBE_URL="https://8.8.8.8/,https://dns.google/,https://9.9.9.9/"
CLOCK_SYNC_APPLY_THRESHOLD_SEC=5
CLOCK_SYNC_MAX_SKEW_SEC=120
CLOCK_SYNC_STALE_SEC=900

ACCOUNTING_SYNC_SCRIPT="/usr/local/sbin/omnirelay-accounting-sync"
ACCOUNTING_SYNC_ENV_FILE="${GATEWAY_ROOT_DIR}/accounting_sync.env"
ACCOUNTING_SYNC_STATE_FILE="${CONNECTOR_DIR}/accounting_sync_state.json"
ACCOUNTING_SYNC_SERVICE="omnirelay-accounting-sync.service"
ACCOUNTING_SYNC_TIMER="omnirelay-accounting-sync.timer"
ACCOUNTING_SYNC_INTERVAL_SEC=30
ACCOUNTING_SYNC_PPP_SESSIONS_FILE="/run/omnirelay/ppp-sessions.tsv"

log(){ printf '[%s] %s\n' "$(date -u +'%Y-%m-%dT%H:%M:%SZ')" "$*"; }
die(){ printf '[%s] ERROR: %s\n' "$(date -u +'%Y-%m-%dT%H:%M:%SZ')" "$*" >&2; exit 1; }
progress(){ local p="$1"; shift; printf 'OMNIRELAY_PROGRESS:%s:%s\n' "$p" "$*"; }
connector_require_root(){ (( EUID == 0 )) || die "This command requires root."; }
connector_validate_port(){ [[ "$1" =~ ^[0-9]+$ ]] || die "$2 must be integer"; (( $1>=1 && $1<=65535 )) || die "$2 out of range"; }
connector_normalize_bool(){ case "$(printf '%s' "$1" | tr '[:upper:]' '[:lower:]' | xargs)" in true|1|yes|y) echo true;; false|0|no|n) echo false;; *) die "Invalid boolean: $1";; esac; }
connector_random_string(){ LC_ALL=C tr -dc 'a-zA-Z0-9' </dev/urandom | head -c "$1" || true; }
connector_random_uuid(){ [[ -f /proc/sys/kernel/random/uuid ]] && cat /proc/sys/kernel/random/uuid || uuidgen; }
connector_check_listener(){ ss -lnt "( sport = :$1 )" 2>/dev/null | awk 'NR>1 {print}' | grep -q . && echo true || echo false; }
connector_check_udp_listener(){ ss -lun "( sport = :$1 )" 2>/dev/null | awk 'NR>1 {print}' | grep -q . && echo true || echo false; }
connector_check_local_dns_listener(){
  local port="${1:-$CONNECTOR_DNS_LISTEN_PORT}"
  if [[ "$(connector_check_udp_listener "$port")" == true || "$(connector_check_listener "$port")" == true ]]; then
    echo true
  else
    echo false
  fi
}
connector_choose_port(){ local p; for _ in $(seq 1 200); do p=$((RANDOM%30000+22000)); [[ "$(connector_check_listener "$p")" == "false" ]] && echo "$p" && return 0; done; die "cannot allocate free port"; }
connector_sql_escape(){ printf '%s' "$1" | sed "s/'/''/g"; }
connector_json_string_safe(){ printf '%s' "$1" | tr -d '\000-\037' | sed -e 's/\\/\\\\/g' -e 's/"/\\"/g'; }
connector_validate_relay_id(){
  local id="${1:-}"
  [[ "$id" =~ ^[A-Za-z0-9_-]{1,64}$ ]] || die "--relay-id must contain only letters, numbers, underscore, or dash and be at most 64 characters"
}
connector_short_hash(){
  if command -v sha256sum >/dev/null 2>&1; then
    printf '%s' "$1" | sha256sum | awk '{print substr($1,1,10)}'
  else
    printf '%s' "$1" | shasum -a 256 | awk '{print substr($1,1,10)}'
  fi
}

connector_default_dns_port_for_relay(){
  local relay_hash="${1:-}"
  # Keep legacy default for non-relay/global mode.
  if [[ -z "$relay_hash" ]]; then
    echo 1053
    return 0
  fi

  # Deterministic per-relay DNS listener port to avoid 127.0.0.1:1053 collisions
  # when multiple relays run on the same VPS.
  local first4="${relay_hash:0:4}"
  if [[ ! "$first4" =~ ^[0-9a-fA-F]{4}$ ]]; then
    first4="1f1f"
  fi
  local seed=$((16#$first4))
  echo $((20000 + (seed % 20000)))
}

connector_apply_relay_scope(){
  if [[ -z "${RELAY_ID:-}" ]]; then
    die "--relay-id is required; global gateway runtime is not supported."
  fi

  connector_validate_relay_id "$RELAY_ID"
  RELAY_HASH="$(connector_short_hash "$RELAY_ID")"
  CONNECTOR_DNS_LISTEN_PORT="$(connector_default_dns_port_for_relay "$RELAY_HASH")"
  GATEWAY_ROOT_DIR="/etc/omnirelay/relays/${RELAY_ID}/gateway"
  GATEWAY_METADATA_FILE="${GATEWAY_ROOT_DIR}/metadata.json"
  GATEWAY_DNS_PROFILE_FILE="${GATEWAY_ROOT_DIR}/dns_profile.json"
  CONNECTOR_DIR="${GATEWAY_ROOT_DIR}/connector"
  CONNECTOR_CONFIG_FILE="${CONNECTOR_DIR}/config.json"
  CONNECTOR_STATE_FILE="${CONNECTOR_DIR}/state.json"
  CONNECTOR_ACCOUNTING_DB="${CONNECTOR_DIR}/accounting.db"
  CONNECTOR_CORE_STATE_FILE="${CONNECTOR_DIR}/connector_core_state.json"
  CONNECTOR_ACCOUNTING_LOCK_FILE="/run/omnirelay/${RELAY_ID}/accounting-sync.lock"
  CONNECTOR_SERVICE="omnirelay-singbox-${RELAY_ID}"
  PANEL_APP_DIR="/opt/omnirelay/relays/${RELAY_ID}/omni-gateway"
  PANEL_RELEASES_DIR="${PANEL_APP_DIR}/releases"
  PANEL_CURRENT_DIR="${PANEL_APP_DIR}/current"
  PANEL_ENV_FILE="${GATEWAY_ROOT_DIR}/omnipanel.env"
  PANEL_SERVICE="omnirelay-omnipanel-${RELAY_ID}"
  CONNECTOR_REDIRECT_CHAIN="OMNIREDIR${RELAY_HASH}"
  CONNECTOR_REJECT_CHAIN="OMNIREJ${RELAY_HASH}"
  CLOCK_SYNC_SCRIPT="/usr/local/sbin/omnirelay-clock-sync-${RELAY_ID}"
  CLOCK_SYNC_ENV_FILE="${GATEWAY_ROOT_DIR}/clock_sync.env"
  CLOCK_SYNC_STATE_FILE="${CONNECTOR_DIR}/clock_sync_state.json"
  CLOCK_SYNC_SERVICE="omnirelay-clock-sync-${RELAY_ID}.service"
  CLOCK_SYNC_TIMER="omnirelay-clock-sync-${RELAY_ID}.timer"
  ACCOUNTING_SYNC_SCRIPT="/usr/local/sbin/omnirelay-accounting-sync-${RELAY_ID}"
  ACCOUNTING_SYNC_ENV_FILE="${GATEWAY_ROOT_DIR}/accounting_sync.env"
  ACCOUNTING_SYNC_STATE_FILE="${CONNECTOR_DIR}/accounting_sync_state.json"
  ACCOUNTING_SYNC_SERVICE="omnirelay-accounting-sync-${RELAY_ID}.service"
  ACCOUNTING_SYNC_TIMER="omnirelay-accounting-sync-${RELAY_ID}.timer"
  ACCOUNTING_SYNC_PPP_SESSIONS_FILE="/run/omnirelay/${RELAY_ID}/ppp-sessions.tsv"
  GATEWAYCTL_PATH="/usr/local/sbin/omnirelay-gatewayctl-${RELAY_ID}"
  PANEL_SUDOERS_FILE="/etc/sudoers.d/omnigateway-singbox-${RELAY_ID}"
  PANEL_NGINX_SITE_NAME="omnirelay-omnipanel-${RELAY_ID}"
}
connector_with_accounting_lock(){
  local timeout_sec="${1:-30}"
  shift || true
  install -d -m 0755 "$(dirname "$CONNECTOR_ACCOUNTING_LOCK_FILE")"
  (
    flock -w "$timeout_sec" 9 || exit 99
    "$@"
  ) 9>"$CONNECTOR_ACCOUNTING_LOCK_FILE"
}
connector_ensure_accounting_schema(){
  sqlite3 "$CONNECTOR_ACCOUNTING_DB" <<'SQL' >/dev/null
PRAGMA journal_mode=WAL;
PRAGMA synchronous=NORMAL;
CREATE TABLE IF NOT EXISTS clients (
  client_id TEXT PRIMARY KEY,
  protocol_id TEXT NOT NULL,
  email TEXT NOT NULL DEFAULT '',
  username TEXT NOT NULL,
  auth_username TEXT NOT NULL DEFAULT '',
  auth_secret TEXT NOT NULL DEFAULT '',
  enabled INTEGER NOT NULL DEFAULT 1,
  total_bytes_limit INTEGER NOT NULL DEFAULT 0,
  speed_limit_kbps INTEGER NOT NULL DEFAULT 0,
  expiry_unix_ms INTEGER NOT NULL DEFAULT 0,
  created_at INTEGER NOT NULL,
  updated_at INTEGER NOT NULL
);
CREATE TABLE IF NOT EXISTS usage_totals (
  client_id TEXT PRIMARY KEY,
  used_bytes INTEGER NOT NULL DEFAULT 0,
  updated_at INTEGER NOT NULL
);
CREATE TABLE IF NOT EXISTS connection_counters (
  client_id TEXT PRIMARY KEY,
  active_connections INTEGER NOT NULL DEFAULT 0,
  last_seen_at INTEGER NOT NULL DEFAULT 0
);
CREATE TABLE IF NOT EXISTS enforcement_state (
  client_id TEXT PRIMARY KEY,
  disabled_reason TEXT NOT NULL DEFAULT '',
  disabled_at INTEGER NOT NULL DEFAULT 0,
  updated_at INTEGER NOT NULL DEFAULT 0
);
CREATE TABLE IF NOT EXISTS sampler_state (
  source TEXT NOT NULL,
  state_key TEXT NOT NULL,
  last_value INTEGER NOT NULL DEFAULT 0,
  updated_at INTEGER NOT NULL DEFAULT 0,
  PRIMARY KEY (source, state_key)
);
CREATE TABLE IF NOT EXISTS sampler_sessions (
  source TEXT NOT NULL,
  session_key TEXT NOT NULL,
  client_id TEXT NOT NULL DEFAULT '',
  upload_bytes INTEGER NOT NULL DEFAULT 0,
  download_bytes INTEGER NOT NULL DEFAULT 0,
  last_seen_at INTEGER NOT NULL DEFAULT 0,
  closed_at INTEGER NOT NULL DEFAULT 0,
  PRIMARY KEY (source, session_key)
);
SQL
  if ! sqlite3 "$CONNECTOR_ACCOUNTING_DB" "PRAGMA table_info(clients);" | grep -q '|speed_limit_kbps|'; then
    sqlite3 "$CONNECTOR_ACCOUNTING_DB" "ALTER TABLE clients ADD COLUMN speed_limit_kbps INTEGER NOT NULL DEFAULT 0;" >/dev/null
  fi
  if ! sqlite3 "$CONNECTOR_ACCOUNTING_DB" "PRAGMA table_info(clients);" | grep -q '|email|'; then
    sqlite3 "$CONNECTOR_ACCOUNTING_DB" "ALTER TABLE clients ADD COLUMN email TEXT NOT NULL DEFAULT '';" >/dev/null
  fi
  if ! sqlite3 "$CONNECTOR_ACCOUNTING_DB" "PRAGMA table_info(clients);" | grep -q '|auth_username|'; then
    sqlite3 "$CONNECTOR_ACCOUNTING_DB" "ALTER TABLE clients ADD COLUMN auth_username TEXT NOT NULL DEFAULT '';" >/dev/null
  fi
  if ! sqlite3 "$CONNECTOR_ACCOUNTING_DB" "PRAGMA table_info(clients);" | grep -q '|auth_secret|'; then
    sqlite3 "$CONNECTOR_ACCOUNTING_DB" "ALTER TABLE clients ADD COLUMN auth_secret TEXT NOT NULL DEFAULT '';" >/dev/null
  fi
  sqlite3 "$CONNECTOR_ACCOUNTING_DB" "CREATE INDEX IF NOT EXISTS idx_clients_protocol_email ON clients(protocol_id,email);" >/dev/null
  sqlite3 "$CONNECTOR_ACCOUNTING_DB" "CREATE INDEX IF NOT EXISTS idx_clients_protocol_auth_username ON clients(protocol_id,auth_username);" >/dev/null
}

connector_fix_accounting_permissions(){
  # OmniPanel runs as $PANEL_USER_ACCOUNT and must open SQLite in WAL mode.
  # Even read-only queries need access to WAL/SHM sidecars for locking metadata.
  if id -u "$PANEL_USER_ACCOUNT" >/dev/null 2>&1; then
    install -d -m 2770 -o root -g "$PANEL_USER_ACCOUNT" "$CONNECTOR_DIR"
    chown "root:${PANEL_USER_ACCOUNT}" "$CONNECTOR_ACCOUNTING_DB" 2>/dev/null || true
    chmod 0660 "$CONNECTOR_ACCOUNTING_DB" 2>/dev/null || true
    for sidecar in "${CONNECTOR_ACCOUNTING_DB}-wal" "${CONNECTOR_ACCOUNTING_DB}-shm"; do
      [[ -e "$sidecar" ]] || continue
      chown "root:${PANEL_USER_ACCOUNT}" "$sidecar" 2>/dev/null || true
      chmod 0660 "$sidecar" 2>/dev/null || true
    done
  fi
}
connector_service_state(){
  local svc="$1" out
  out="$(systemctl is-active "$svc" 2>/dev/null || true)"
  out="${out%%$'\n'*}"
  out="$(printf '%s' "$out" | tr -d '\r\n\t')"
  [[ -n "$out" ]] || out="inactive"
  printf '%s' "$out"
}

connector_wait_service_active(){
  local svc="$1"
  local timeout_sec="${2:-20}"
  local i state
  for i in $(seq 1 "$timeout_sec"); do
    state="$(connector_service_state "$svc")"
    if [[ "$state" == "active" ]]; then
      return 0
    fi
    sleep 1
  done
  return 1
}
connector_load_metadata_defaults(){
  local meta_public meta_panel meta_backend config_public
  meta_public=""
  meta_panel=""
  meta_backend=""
  config_public=""

  if [[ -f "$GATEWAY_METADATA_FILE" ]]; then
    meta_public="$(jq -r '.public_port // empty' "$GATEWAY_METADATA_FILE" 2>/dev/null || true)"
    meta_panel="$(jq -r '.omnipanel_public_port // empty' "$GATEWAY_METADATA_FILE" 2>/dev/null || true)"
    meta_backend="$(jq -r '.backend_port // empty' "$GATEWAY_METADATA_FILE" 2>/dev/null || true)"
  fi

  if [[ -f "$CONNECTOR_CONFIG_FILE" ]]; then
    config_public="$(jq -r '.inbounds[0].listen_port // empty' "$CONNECTOR_CONFIG_FILE" 2>/dev/null || true)"
  fi

  if [[ "$meta_public" =~ ^[0-9]+$ ]]; then
    PUBLIC_PORT="$meta_public"
  elif [[ "$config_public" =~ ^[0-9]+$ ]]; then
    PUBLIC_PORT="$config_public"
  fi

  if [[ "$meta_panel" =~ ^[0-9]+$ ]]; then
    PANEL_PORT="$meta_panel"
  fi

  if [[ "$meta_backend" =~ ^[0-9]+$ ]]; then
    BACKEND_PORT="$meta_backend"
  fi
}

connector_is_full_tunnel_singbox_protocol(){
  case "${1:-}" in
    shadowsocks_singbox|shadowtls_v3_shadowsocks_singbox|vless_tls_singbox|mixed_singbox|socks_singbox|http_singbox|hysteria2_singbox|trojan_singbox|naive_singbox) return 0 ;;
    *) return 1 ;;
  esac
}

connector_load_bootstrap_common(){
  local c
  for c in "$BOOTSTRAP_COMMON_SCRIPT" "/usr/local/lib/omnirelay/bootstrap-common.sh" "$(dirname "${BASH_SOURCE[0]:-$0}")/setup_omnirelay_gateway_bootstrap_common.sh"; do
    [[ -f "$c" ]] && source "$c" && return 0
  done
  die "Bootstrap helper script not found."
}

connector_load_panel_common(){
  local c
  for c in "$PANEL_COMMON_SCRIPT" "/usr/local/lib/omnirelay/omnipanel-common.sh" "$(dirname "${BASH_SOURCE[0]:-$0}")/setup_omnirelay_omnipanel_common.sh"; do
    [[ -f "$c" ]] && source "$c" && return 0
  done
  die "OmniPanel helper script not found."
}

connector_ensure_bootstrap_common(){ declare -F omnirelay_bootstrap_normalize_mode >/dev/null 2>&1 || connector_load_bootstrap_common; }

connector_configure_proxy(){
  connector_ensure_bootstrap_common
  omnirelay_bootstrap_configure_proxy_env "$BOOTSTRAP_MODE" "$BOOTSTRAP_SOCKS_PORT"
}

connector_clear_proxy(){
  connector_ensure_bootstrap_common
  omnirelay_bootstrap_disable_proxy_env
}

connector_write_dnsmasq_global_config(){
  install -d -m 0755 "$(dirname "$DNSMASQ_GLOBAL_CONFIG_FILE")"
  cat > "$DNSMASQ_GLOBAL_CONFIG_FILE" <<'EOF'
bind-dynamic
no-resolv
cache-size=10000
EOF
  chmod 0644 "$DNSMASQ_GLOBAL_CONFIG_FILE" || true
}

connector_remove_dnsmasq_global_config(){
  rm -f "$DNSMASQ_GLOBAL_CONFIG_FILE"
}

connector_sanitize_dnsmasq_omnirelay_configs(){
  local cfg
  shopt -s nullglob
  for cfg in /etc/dnsmasq.d/omnirelay-*.conf; do
    [[ "$cfg" == "$DNSMASQ_GLOBAL_CONFIG_FILE" ]] && continue
    [[ -f "$cfg" ]] || continue
    sed -i \
      -e '/^[[:space:]]*bind-dynamic[[:space:]]*$/d' \
      -e '/^[[:space:]]*no-resolv[[:space:]]*$/d' \
      -e '/^[[:space:]]*cache-size[[:space:]]*=[[:space:]]*[0-9][0-9]*[[:space:]]*$/d' \
      "$cfg" || true
  done
  shopt -u nullglob
}

connector_verify_bootstrap(){
  connector_ensure_bootstrap_common
  omnirelay_bootstrap_verify_egress "$BOOTSTRAP_MODE" "$BOOTSTRAP_SOCKS_PORT" "$PROXY_CHECK_URL" 24 5 || die "Bootstrap path is not healthy."
  omnirelay_bootstrap_sync_clock "$BOOTSTRAP_MODE" "$BOOTSTRAP_SOCKS_PORT" "$PROXY_CHECK_URL" || log "WARNING: Unable to synchronize VPS clock during gateway bootstrap; continuing."
}

connector_write_clock_sync_env(){
  cat > "$CLOCK_SYNC_ENV_FILE" <<EOF
OMNIRELAY_CLOCK_SYNC_URL=${CLOCK_SYNC_PROBE_URL}
OMNIRELAY_CLOCK_SYNC_BACKEND_PORT=${BACKEND_PORT}
OMNIRELAY_CLOCK_SYNC_BOOTSTRAP_PORT=${BOOTSTRAP_SOCKS_PORT}
OMNIRELAY_CLOCK_SYNC_BOOTSTRAP_MODE=${BOOTSTRAP_MODE}
OMNIRELAY_CLOCK_SYNC_APPLY_THRESHOLD_SEC=${CLOCK_SYNC_APPLY_THRESHOLD_SEC}
OMNIRELAY_CLOCK_SYNC_MAX_SKEW_SEC=${CLOCK_SYNC_MAX_SKEW_SEC}
OMNIRELAY_CLOCK_SYNC_STATE_FILE=${CLOCK_SYNC_STATE_FILE}
EOF
  chmod 0600 "$CLOCK_SYNC_ENV_FILE" || true
}

connector_write_clock_sync_script(){
  cat > "$CLOCK_SYNC_SCRIPT" <<'EOF'
#!/usr/bin/env bash
set -Eeuo pipefail
IFS=$'\n\t'

ENV_FILE="/etc/omnirelay/gateway/clock_sync.env"
[[ -f "$ENV_FILE" ]] && source "$ENV_FILE"

CLOCK_SYNC_URL="${OMNIRELAY_CLOCK_SYNC_URL:-https://8.8.8.8/,https://dns.google/,https://9.9.9.9/}"
BACKEND_PORT="${OMNIRELAY_CLOCK_SYNC_BACKEND_PORT:-15000}"
BOOTSTRAP_PORT="${OMNIRELAY_CLOCK_SYNC_BOOTSTRAP_PORT:-16080}"
BOOTSTRAP_MODE="${OMNIRELAY_CLOCK_SYNC_BOOTSTRAP_MODE:-tunnel}"
APPLY_THRESHOLD_SEC="${OMNIRELAY_CLOCK_SYNC_APPLY_THRESHOLD_SEC:-5}"
MAX_SKEW_SEC="${OMNIRELAY_CLOCK_SYNC_MAX_SKEW_SEC:-120}"
STATE_FILE="${OMNIRELAY_CLOCK_SYNC_STATE_FILE:-/etc/omnirelay/gateway/connector/clock_sync_state.json}"

sync_checked_at_utc(){ date -u +'%Y-%m-%dT%H:%M:%SZ'; }
sync_json_escape(){ printf '%s' "$1" | tr -d '\000-\010\013\014\016-\037' | sed 's/"/\\"/g'; }
sync_abs(){
  local v="${1:-0}"
  [[ "$v" =~ ^-?[0-9]+$ ]] || { echo 0; return; }
  (( v < 0 )) && v=$(( -v ))
  echo "$v"
}
sync_record(){
  local ok="$1" reason="$2" source="$3" remote_epoch="$4" before="$5" after="$6" skew_before="$7" skew_after="$8" applied="$9"
  install -d -m 0755 "$(dirname "$STATE_FILE")"
  jq -n \
    --argjson ok "$ok" \
    --arg reason "$reason" \
    --arg source "$source" \
    --arg probeUrl "$CLOCK_SYNC_URL" \
    --argjson remoteEpoch "$remote_epoch" \
    --argjson localEpochBefore "$before" \
    --argjson localEpochAfter "$after" \
    --argjson skewSecBefore "$skew_before" \
    --argjson skewSecAfter "$skew_after" \
    --argjson applied "$applied" \
    --arg checkedAtUtc "$(sync_checked_at_utc)" \
    '{ok:$ok,reason:$reason,source:$source,probeUrl:$probeUrl,remoteEpoch:$remoteEpoch,localEpochBefore:$localEpochBefore,localEpochAfter:$localEpochAfter,skewSecBefore:$skewSecBefore,skewSecAfter:$skewSecAfter,applied:$applied,checkedAtUtc:$checkedAtUtc}' > "$STATE_FILE"
  chmod 0644 "$STATE_FILE" || true
}

sync_fetch_date_via_socks(){
  local port="$1"
  local header target
  local normalized_targets
  [[ "$port" =~ ^[0-9]+$ ]] || return 1
  normalized_targets="$(printf '%s' "$CLOCK_SYNC_URL" | tr ', ' '\n\n' | awk 'NF')"
  while IFS= read -r target; do
    [[ -n "$target" ]] || continue
    header="$(curl --silent --show-error --insecure --max-time 20 --connect-timeout 10 --retry 0 --socks5-hostname "127.0.0.1:${port}" -I "$target" 2>/dev/null | tr -d '\r' | awk 'tolower($1)=="date:"{$1="";sub(/^ /,"");print;exit}')"
    [[ -n "$header" ]] || continue
    printf '%s|%s\n' "socks5:127.0.0.1:${port}:${target}" "$header"
    return 0
  done <<<"$normalized_targets"
  return 1
}

sync_fetch_date_direct(){
  local header target
  local normalized_targets
  normalized_targets="$(printf '%s' "$CLOCK_SYNC_URL" | tr ', ' '\n\n' | awk 'NF')"
  while IFS= read -r target; do
    [[ -n "$target" ]] || continue
    header="$(curl --silent --show-error --insecure --max-time 20 --connect-timeout 10 --retry 0 -I "$target" 2>/dev/null | tr -d '\r' | awk 'tolower($1)=="date:"{$1="";sub(/^ /,"");print;exit}')"
    [[ -n "$header" ]] || continue
    printf '%s|%s\n' "direct:${target}" "$header"
    return 0
  done <<<"$normalized_targets"
  return 1
}

main(){
  local fetched source date_header remote_epoch local_before local_after skew_before skew_after abs_before abs_after applied
  fetched=""
  if fetched="$(sync_fetch_date_via_socks "$BACKEND_PORT" 2>/dev/null)"; then
    :
  elif fetched="$(sync_fetch_date_via_socks "$BOOTSTRAP_PORT" 2>/dev/null)"; then
    :
  elif [[ "$BOOTSTRAP_MODE" == "direct" ]] && fetched="$(sync_fetch_date_direct 2>/dev/null)"; then
    :
  else
    sync_record false "date_probe_failed" "none" 0 0 0 0 0 false
    exit 1
  fi

  source="${fetched%%|*}"
  date_header="${fetched#*|}"
  remote_epoch="$(date -u -d "$date_header" +%s 2>/dev/null || true)"
  if [[ -z "$remote_epoch" || ! "$remote_epoch" =~ ^[0-9]+$ ]]; then
    sync_record false "invalid_date_header" "$source" 0 0 0 0 0 false
    exit 1
  fi

  local_before="$(date -u +%s)"
  skew_before=$(( local_before - remote_epoch ))
  abs_before="$(sync_abs "$skew_before")"
  applied=false

  if (( abs_before > APPLY_THRESHOLD_SEC )); then
    timedatectl set-ntp false >/dev/null 2>&1 || true
    if ! date -u -s "@${remote_epoch}" >/dev/null 2>&1; then
      sync_record false "clock_set_failed" "$source" "$remote_epoch" "$local_before" "$local_before" "$skew_before" "$skew_before" false
      exit 1
    fi
    command -v hwclock >/dev/null 2>&1 && hwclock --systohc >/dev/null 2>&1 || true
    applied=true
  fi

  local_after="$(date -u +%s)"
  skew_after=$(( local_after - remote_epoch ))
  abs_after="$(sync_abs "$skew_after")"

  if (( abs_after > MAX_SKEW_SEC )); then
    sync_record false "skew_exceeded" "$source" "$remote_epoch" "$local_before" "$local_after" "$skew_before" "$skew_after" "$applied"
    exit 1
  fi

  sync_record true "ok" "$source" "$remote_epoch" "$local_before" "$local_after" "$skew_before" "$skew_after" "$applied"
}

main "$@"
EOF
  chmod 0755 "$CLOCK_SYNC_SCRIPT"
}

connector_write_clock_sync_units(){
  cat > "/etc/systemd/system/${CLOCK_SYNC_SERVICE}" <<EOF
[Unit]
Description=OmniRelay gateway clock sync
After=network-online.target
Wants=network-online.target

[Service]
Type=oneshot
ExecStart=${CLOCK_SYNC_SCRIPT}
EOF

  cat > "/etc/systemd/system/${CLOCK_SYNC_TIMER}" <<EOF
[Unit]
Description=Run OmniRelay clock sync periodically

[Timer]
OnBootSec=1min
OnUnitActiveSec=5min
RandomizedDelaySec=15
Persistent=true
Unit=${CLOCK_SYNC_SERVICE}

[Install]
WantedBy=timers.target
EOF

  systemctl daemon-reload
}

connector_install_clock_sync_runtime(){
  connector_write_clock_sync_env
  connector_write_clock_sync_script
  connector_write_clock_sync_units
  if [[ "$BOOTSTRAP_MODE" == "tunnel" ]]; then
    timedatectl set-ntp false >/dev/null 2>&1 || true
  fi
  "$CLOCK_SYNC_SCRIPT" >/dev/null 2>&1 || die "clock sync failed during installation"
  systemctl enable --now "$CLOCK_SYNC_TIMER" >/dev/null 2>&1 || die "failed to enable clock sync timer"
}

connector_uninstall_clock_sync_runtime(){
  systemctl disable --now "$CLOCK_SYNC_TIMER" "$CLOCK_SYNC_SERVICE" >/dev/null 2>&1 || true
  rm -f "/etc/systemd/system/${CLOCK_SYNC_SERVICE}" "/etc/systemd/system/${CLOCK_SYNC_TIMER}" "$CLOCK_SYNC_SCRIPT" "$CLOCK_SYNC_ENV_FILE" "$CLOCK_SYNC_STATE_FILE"
  systemctl daemon-reload || true
}

connector_write_ppp_accounting_hooks(){
  install -d -m 0755 /etc/ppp/ip-up.d /etc/ppp/ip-down.d /run/omnirelay
  local sessions_file
  sessions_file="${ACCOUNTING_SYNC_PPP_SESSIONS_FILE:-/run/omnirelay/ppp-sessions.tsv}"
  cat > /etc/ppp/ip-up.d/99-omnirelay-accounting <<'EOF'
#!/usr/bin/env bash
set -Eeuo pipefail
IFS=$'\n\t'
state_file="__OMNIRELAY_PPP_SESSIONS_FILE__"
ifname="${IFNAME:-}"
username="${PEERNAME:-${PPPLOGNAME:-}}"
[[ -n "$ifname" ]] || exit 0
install -d -m 0755 "$(dirname "$state_file")"
tmp="$(mktemp)"
if [[ -f "$state_file" ]]; then
  awk -F'\t' -v ifn="$ifname" '$1!=ifn' "$state_file" > "$tmp" || true
fi
printf '%s\t%s\n' "$ifname" "${username:-__unknown__}" >> "$tmp"
mv -f "$tmp" "$state_file"
chmod 0644 "$state_file" || true
EOF
  sed -i "s|__OMNIRELAY_PPP_SESSIONS_FILE__|${sessions_file}|g" /etc/ppp/ip-up.d/99-omnirelay-accounting
  chmod 0755 /etc/ppp/ip-up.d/99-omnirelay-accounting

  cat > /etc/ppp/ip-down.d/99-omnirelay-accounting <<'EOF'
#!/usr/bin/env bash
set -Eeuo pipefail
IFS=$'\n\t'
state_file="__OMNIRELAY_PPP_SESSIONS_FILE__"
ifname="${IFNAME:-}"
[[ -n "$ifname" && -f "$state_file" ]] || exit 0
tmp="$(mktemp)"
awk -F'\t' -v ifn="$ifname" '$1!=ifn' "$state_file" > "$tmp" || true
mv -f "$tmp" "$state_file"
chmod 0644 "$state_file" || true
EOF
  sed -i "s|__OMNIRELAY_PPP_SESSIONS_FILE__|${sessions_file}|g" /etc/ppp/ip-down.d/99-omnirelay-accounting
  chmod 0755 /etc/ppp/ip-down.d/99-omnirelay-accounting
}

connector_write_accounting_sync_env(){
  {
    printf 'OMNIRELAY_ACCOUNTING_DB=%q\n' "$CONNECTOR_ACCOUNTING_DB"
    printf 'OMNIRELAY_ACCOUNTING_METADATA_FILE=%q\n' "$GATEWAY_METADATA_FILE"
    printf 'OMNIRELAY_ACCOUNTING_STATE_FILE=%q\n' "$ACCOUNTING_SYNC_STATE_FILE"
    printf 'OMNIRELAY_ACCOUNTING_LOCK_FILE=%q\n' "$CONNECTOR_ACCOUNTING_LOCK_FILE"
    printf 'OMNIRELAY_ACCOUNTING_PPP_SESSIONS_FILE=%q\n' "$ACCOUNTING_SYNC_PPP_SESSIONS_FILE"
    printf 'OMNIRELAY_ACCOUNTING_PANEL_GROUP=%q\n' "$PANEL_USER_ACCOUNT"
    printf 'OMNIRELAY_ACCOUNTING_RELAY_ID=%q\n' "${RELAY_ID:-}"
    # Keep the command shell-quoted so the value survives env-file parsing and preserves argv separation.
    if [[ -n "${RELAY_ID:-}" ]]; then
      printf "OMNIRELAY_ACCOUNTING_SYNC_COMMAND='%s'\n" "${GATEWAYCTL_PATH} sync-clients --relay-id ${RELAY_ID}"
    else
      printf "OMNIRELAY_ACCOUNTING_SYNC_COMMAND='%s'\n" "${GATEWAYCTL_PATH} sync-clients"
    fi
  } > "$ACCOUNTING_SYNC_ENV_FILE"
  chmod 0600 "$ACCOUNTING_SYNC_ENV_FILE" || true
}

connector_write_accounting_sync_script(){
  cat > "$ACCOUNTING_SYNC_SCRIPT" <<'EOF'
#!/usr/bin/env bash
set -Eeuo pipefail
IFS=$'\n\t'

ENV_FILE="/etc/omnirelay/gateway/accounting_sync.env"
load_accounting_env_file(){
  local file="$1" line key value
  [[ -f "$file" ]] || return 0
  while IFS= read -r line || [[ -n "$line" ]]; do
    line="${line%$'\r'}"
    [[ -z "$line" || "$line" =~ ^[[:space:]]*# ]] && continue
    [[ "$line" == *"="* ]] || continue
    key="${line%%=*}"
    value="${line#*=}"
    key="$(printf '%s' "$key" | tr -d '[:space:]')"
    [[ -n "$key" ]] || continue
    if [[ "$value" == \"*\" && "$value" == *\" ]]; then
      value="${value:1:${#value}-2}"
    elif [[ "$value" == \'*\' && "$value" == *\' ]]; then
      value="${value:1:${#value}-2}"
    else
      # Backward compatibility for previously generated %q values (for example '\ ').
      value="${value//\\ / }"
      value="${value//\\\\/\\}"
    fi
    printf -v "$key" '%s' "$value"
    export "$key"
  done < "$file"
}
load_accounting_env_file "$ENV_FILE"

DB_PATH="${OMNIRELAY_ACCOUNTING_DB:-/etc/omnirelay/gateway/connector/accounting.db}"
METADATA_FILE="${OMNIRELAY_ACCOUNTING_METADATA_FILE:-/etc/omnirelay/gateway/metadata.json}"
STATE_FILE="${OMNIRELAY_ACCOUNTING_STATE_FILE:-/etc/omnirelay/gateway/connector/accounting_sync_state.json}"
LOCK_FILE="${OMNIRELAY_ACCOUNTING_LOCK_FILE:-/run/omnirelay-accounting-sync.lock}"
PPP_SESSIONS_FILE="${OMNIRELAY_ACCOUNTING_PPP_SESSIONS_FILE:-/run/omnirelay/ppp-sessions.tsv}"
PANEL_GROUP="${OMNIRELAY_ACCOUNTING_PANEL_GROUP:-omnigateway}"
RELAY_ID="${OMNIRELAY_ACCOUNTING_RELAY_ID:-}"
SYNC_COMMAND="${OMNIRELAY_ACCOUNTING_SYNC_COMMAND:-}"

normalize_sync_command(){
  local cmd="$1" relay_id="${RELAY_ID:-}"
  [[ -n "$cmd" ]] || { echo ""; return 0; }
  if [[ "$cmd" != *"--relay-id"* && -n "$relay_id" ]]; then
    cmd="${cmd} --relay-id ${relay_id}"
  fi
  echo "$cmd"
}
SYNC_COMMAND="$(normalize_sync_command "$SYNC_COMMAND")"

# Do not force chmod/chown on STATE_FILE parent; it contains accounting.db and
# must keep group-writable/setgid permissions for omnigateway SQLite access.
install -d -m 0755 "$(dirname "$LOCK_FILE")"
mkdir -p "$(dirname "$STATE_FILE")"

fix_accounting_db_permissions(){
  local db_dir sidecar
  db_dir="$(dirname "$DB_PATH")"
  if getent group "$PANEL_GROUP" >/dev/null 2>&1; then
    install -d -m 2770 -o root -g "$PANEL_GROUP" "$db_dir" >/dev/null 2>&1 || true
    chown "root:${PANEL_GROUP}" "$DB_PATH" >/dev/null 2>&1 || true
    chmod 0660 "$DB_PATH" >/dev/null 2>&1 || true
    for sidecar in "${DB_PATH}-wal" "${DB_PATH}-shm"; do
      [[ -e "$sidecar" ]] || continue
      chown "root:${PANEL_GROUP}" "$sidecar" >/dev/null 2>&1 || true
      chmod 0660 "$sidecar" >/dev/null 2>&1 || true
    done
  fi
}
fix_accounting_db_permissions

run_result=""
sync_error=""
accounting_rc=0

if ! run_result="$(
  (
    flock -w 25 9 || exit 99
    PANEL_GROUP="$PANEL_GROUP" python3 - "$DB_PATH" "$METADATA_FILE" "$PPP_SESSIONS_FILE" <<'PY'
import json
import os
import re
import sqlite3
import sys
import time
from pathlib import Path

db_path = Path(sys.argv[1])
metadata_path = Path(sys.argv[2])
ppp_sessions_file = Path(sys.argv[3])

FULL_TUNNEL_SINGBOX_PROTOCOLS = {
    "shadowsocks_singbox",
    "shadowtls_v3_shadowsocks_singbox",
    "vless_tls_singbox",
    "mixed_singbox",
    "socks_singbox",
    "http_singbox",
    "hysteria2_singbox",
    "trojan_singbox",
    "naive_singbox",
}

PER_CLIENT_SINGBOX_PROTOCOLS = {
    "vless_tls_singbox",
    "shadowsocks_singbox",
    "shadowtls_v3_shadowsocks_singbox",
    "trojan_singbox",
}

now_sec = int(time.time())
now_ms = int(time.time() * 1000)
result = {
    "ok": True,
    "needsSync": False,
    "source": "",
    "sampledClients": 0,
    "updatedClients": 0,
    "error": "",
    "migrationApplied": False,
    "migrationError": "",
    "attributionMode": "strict",
    "attributionConfidence": 1.0,
    "observedSessions": 0,
    "attributedSessions": 0,
    "stateChanged": False,
    "hasDisabledClients": False,
    "hasIneligibleActive": False,
    "degraded": False,
    "degradedReason": "",
    "checkedAtUtc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime(now_sec)),
}

def fail(message: str):
    result["ok"] = False
    result["error"] = message
    print(json.dumps(result, separators=(",", ":")))
    sys.exit(0)

if not db_path.exists():
    fail("accounting_db_missing")

if not metadata_path.exists():
    fail("metadata_missing")

try:
    metadata = json.loads(metadata_path.read_text(encoding="utf-8"))
except Exception as exc:
    fail(f"metadata_parse_failed:{exc}")

protocol_id = str(metadata.get("active_protocol") or "").strip()
accounting = metadata.get("accounting") if isinstance(metadata.get("accounting"), dict) else {}
source = str(accounting.get("source") or "").strip()
panel_group = str(os.environ.get("PANEL_GROUP") or "").strip()
if source == "singbox_v2ray_api":
    fail("unsupported_source:singbox_v2ray_api")
if protocol_id in FULL_TUNNEL_SINGBOX_PROTOCOLS and source == "singbox_log":
    fail("unsupported_source:singbox_log")
result["source"] = source or "none"

conn = sqlite3.connect(str(db_path))
conn.row_factory = sqlite3.Row
cur = conn.cursor()

cur.executescript(
    """
    PRAGMA journal_mode=WAL;
    PRAGMA synchronous=NORMAL;
    CREATE TABLE IF NOT EXISTS clients (
      client_id TEXT PRIMARY KEY,
      protocol_id TEXT NOT NULL,
      email TEXT NOT NULL DEFAULT '',
      username TEXT NOT NULL,
      auth_username TEXT NOT NULL DEFAULT '',
      auth_secret TEXT NOT NULL DEFAULT '',
      enabled INTEGER NOT NULL DEFAULT 1,
      total_bytes_limit INTEGER NOT NULL DEFAULT 0,
      speed_limit_kbps INTEGER NOT NULL DEFAULT 0,
      expiry_unix_ms INTEGER NOT NULL DEFAULT 0,
      created_at INTEGER NOT NULL,
      updated_at INTEGER NOT NULL
    );
    CREATE TABLE IF NOT EXISTS usage_totals (
      client_id TEXT PRIMARY KEY,
      used_bytes INTEGER NOT NULL DEFAULT 0,
      updated_at INTEGER NOT NULL
    );
    CREATE TABLE IF NOT EXISTS connection_counters (
      client_id TEXT PRIMARY KEY,
      active_connections INTEGER NOT NULL DEFAULT 0,
      last_seen_at INTEGER NOT NULL DEFAULT 0
    );
    CREATE TABLE IF NOT EXISTS enforcement_state (
      client_id TEXT PRIMARY KEY,
      disabled_reason TEXT NOT NULL DEFAULT '',
      disabled_at INTEGER NOT NULL DEFAULT 0,
      updated_at INTEGER NOT NULL DEFAULT 0
    );
    CREATE TABLE IF NOT EXISTS sampler_state (
      source TEXT NOT NULL,
      state_key TEXT NOT NULL,
      last_value INTEGER NOT NULL DEFAULT 0,
      updated_at INTEGER NOT NULL DEFAULT 0,
      PRIMARY KEY (source, state_key)
    );
    CREATE TABLE IF NOT EXISTS sampler_sessions (
      source TEXT NOT NULL,
      session_key TEXT NOT NULL,
      client_id TEXT NOT NULL DEFAULT '',
      upload_bytes INTEGER NOT NULL DEFAULT 0,
      download_bytes INTEGER NOT NULL DEFAULT 0,
      last_seen_at INTEGER NOT NULL DEFAULT 0,
      closed_at INTEGER NOT NULL DEFAULT 0,
      PRIMARY KEY (source, session_key)
    );
    """
)

def load_or_init_counter(source_key: str, state_key: str, value: int) -> int:
    row = cur.execute(
        "SELECT last_value FROM sampler_state WHERE source=? AND state_key=?",
        (source_key, state_key),
    ).fetchone()
    if row is None:
        cur.execute(
            "INSERT OR REPLACE INTO sampler_state(source,state_key,last_value,updated_at) VALUES(?,?,?,?)",
            (source_key, state_key, max(0, int(value)), now_sec),
        )
        return 0
    previous = int(row["last_value"] or 0)
    current = max(0, int(value))
    delta = current - previous if current >= previous else current
    cur.execute(
        "UPDATE sampler_state SET last_value=?, updated_at=? WHERE source=? AND state_key=?",
        (current, now_sec, source_key, state_key),
    )
    return max(0, delta)

def add_usage(client_id: str, delta: int):
    if not client_id or delta <= 0:
        return
    cur.execute(
        """
        INSERT INTO usage_totals(client_id,used_bytes,updated_at)
        VALUES(?,?,?)
        ON CONFLICT(client_id) DO UPDATE SET
          used_bytes=usage_totals.used_bytes + excluded.used_bytes,
          updated_at=excluded.updated_at
        """,
        (client_id, int(delta), now_sec),
    )

def load_state_int(source_key: str, state_key: str, default_value: int = 0) -> int:
    row = cur.execute(
        "SELECT last_value FROM sampler_state WHERE source=? AND state_key=?",
        (source_key, state_key),
    ).fetchone()
    if row is None:
        return int(default_value)
    try:
        return int(row["last_value"] or 0)
    except Exception:
        return int(default_value)

def save_state_int(source_key: str, state_key: str, value: int):
    cur.execute(
        "INSERT OR REPLACE INTO sampler_state(source,state_key,last_value,updated_at) VALUES(?,?,?,?)",
        (source_key, state_key, int(value), now_sec),
    )

client_rows = cur.execute(
    "SELECT client_id, username, auth_username, enabled, total_bytes_limit, expiry_unix_ms FROM clients WHERE protocol_id=?",
    (protocol_id,),
).fetchall()
client_ids = {str(row["client_id"]): str(row["client_id"]) for row in client_rows}
username_to_client = {}
openvpn_cn_to_client = {}

def openvpn_cn_from_client_id(client_id: str) -> str:
    sanitized = re.sub(r"[^A-Za-z0-9]", "", str(client_id or ""))
    sanitized = sanitized[:40]
    if not sanitized:
        return ""
    return f"ovpn-{sanitized}"

for row in client_rows:
    client_id = str(row["client_id"])
    username = str(row["username"] or "").strip()
    auth_username = str(row["auth_username"] or "").strip()
    for candidate in (username, auth_username):
        if candidate and candidate not in username_to_client:
            username_to_client[candidate] = client_id
    if protocol_id == "openvpn_tcp_singbox":
        cn = openvpn_cn_from_client_id(client_id)
        if cn and cn not in openvpn_cn_to_client:
            openvpn_cn_to_client[cn] = client_id

active_counts = {}
observed_session_keys = set()
attributed_session_keys = set()

def parse_int(value) -> int:
    try:
        return int(str(value).strip())
    except Exception:
        return 0

def is_uint(value) -> bool:
    return bool(re.fullmatch(r"\d+", str(value or "").strip()))

def split_openvpn_fields(line: str):
    if "," in line:
        return [part.strip() for part in line.split(",")]
    if "\t" in line:
        # Preserve empty columns (for example Virtual IPv6 Address).
        return [part.strip() for part in line.split("\t")]
    return [part.strip() for part in re.split(r"\s{2,}", line.strip())]

def resolve_openvpn_client_id(username_value: str, common_name_value: str) -> str:
    username = str(username_value or "").strip()
    common_name = str(common_name_value or "").strip()
    for candidate in (username, common_name):
        if not candidate or candidate == "UNDEF":
            continue
        client_id = (
            username_to_client.get(candidate)
            or client_ids.get(candidate)
            or openvpn_cn_to_client.get(candidate)
        )
        if client_id:
            return client_id
    return ""

if source == "connector_tracker":
    # Full-tunnel sing-box accounting/enforcement is handled by connector-core.
    result["attributionMode"] = "strict"
    result["attributionConfidence"] = 1.0
    result["observedSessions"] = 0
    result["attributedSessions"] = 0
elif source == "openvpn_status":
    status_file = Path(str(accounting.get("openVpnStatusFile") or "/var/log/openvpn/omnirelay-status.log"))
    if status_file.exists():
        usage_by_user = {}
        in_client_table = False
        table_headers = []

        def add_openvpn_sample(common_name_value: str, username_value: str, rx_raw, tx_raw):
            common_name = str(common_name_value or "").strip()
            username = str(username_value or "").strip()
            key = username if username and username != "UNDEF" else common_name
            if not key:
                return
            client_id = resolve_openvpn_client_id(username, common_name)
            if client_id:
                active_counts[client_id] = active_counts.get(client_id, 0) + 1
            rx = max(0, parse_int(rx_raw))
            tx = max(0, parse_int(tx_raw))
            total = rx + tx
            if total <= 0:
                return
            usage_by_user[key] = usage_by_user.get(key, 0) + total

        for raw_line in status_file.read_text(encoding="utf-8", errors="ignore").splitlines():
            line = raw_line.strip()
            if not line:
                continue
            upper_line = line.upper()

            if upper_line.startswith("CLIENT_LIST"):
                parts = split_openvpn_fields(line)
                if not parts or parts[0].upper() != "CLIENT_LIST":
                    continue
                payload = parts[1:]
                if not payload:
                    continue
                common_name = payload[0] if len(payload) > 0 else ""
                username = ""
                for idx in (8, 9, 7):
                    if len(payload) > idx and payload[idx].strip() and payload[idx].strip() != "UNDEF":
                        username = payload[idx].strip()
                        break
                rx_raw = "0"
                tx_raw = "0"
                for rx_idx, tx_idx in ((4, 5), (2, 3), (5, 6), (3, 4)):
                    if len(payload) > max(rx_idx, tx_idx):
                        rx_candidate = payload[rx_idx]
                        tx_candidate = payload[tx_idx]
                        if is_uint(rx_candidate) and is_uint(tx_candidate):
                            rx_raw = rx_candidate
                            tx_raw = tx_candidate
                            break
                add_openvpn_sample(common_name, username, rx_raw, tx_raw)
                continue

            if upper_line.startswith("OPENVPN CLIENT LIST"):
                in_client_table = True
                table_headers = []
                continue

            if (
                upper_line.startswith("ROUTING TABLE")
                or upper_line.startswith("ROUTING_TABLE")
                or upper_line.startswith("GLOBAL STATS")
                or upper_line.startswith("GLOBAL_STATS")
                or upper_line == "END"
            ):
                in_client_table = False
                table_headers = []
                continue

            parts = split_openvpn_fields(line)
            if len(parts) < 2:
                continue

            # OpenVPN status-version 3 tab format includes a HEADER row.
            if parts[0].upper() == "HEADER":
                header_kind = parts[1].upper()
                if header_kind == "CLIENT_LIST":
                    in_client_table = True
                    table_headers = [field.lower().strip() for field in parts[2:]]
                    continue
                if header_kind in ("ROUTING_TABLE", "GLOBAL_STATS"):
                    in_client_table = False
                    table_headers = []
                    continue

            normalized = [field.lower().strip() for field in parts]
            if (
                "common name" in normalized
                and "bytes received" in normalized
                and "bytes sent" in normalized
            ):
                in_client_table = True
                table_headers = normalized
                continue

            if not in_client_table:
                continue

            row_map = {}
            if table_headers:
                for index, key in enumerate(table_headers):
                    if index < len(parts):
                        row_map[key] = parts[index]

            common_name = row_map.get("common name", parts[0] if parts else "")
            username = row_map.get("username", "")
            rx_raw = row_map.get("bytes received", "")
            tx_raw = row_map.get("bytes sent", "")

            if not rx_raw and not tx_raw:
                if len(parts) > 5:
                    rx_raw = parts[4]
                    tx_raw = parts[5]
                elif len(parts) > 3:
                    rx_raw = parts[2]
                    tx_raw = parts[3]

            add_openvpn_sample(common_name, username, rx_raw, tx_raw)

        for key, total in usage_by_user.items():
            client_id = (
                username_to_client.get(key)
                or client_ids.get(key)
                or openvpn_cn_to_client.get(key)
            )
            if not client_id:
                continue
            delta = load_or_init_counter("openvpn_user_total", key, int(total))
            add_usage(client_id, delta)
            result["sampledClients"] += 1
    result["attributionMode"] = "strict"
    result["attributionConfidence"] = 1.0
    result["observedSessions"] = result["sampledClients"]
    result["attributedSessions"] = result["sampledClients"]
elif source == "ipsec_ppp":
    sessions = {}
    enabled_clients = []
    for row in client_rows:
        try:
            if int(row["enabled"] or 0) != 0:
                enabled_clients.append(str(row["client_id"]))
        except Exception:
            continue

    if ppp_sessions_file.exists():
        for line in ppp_sessions_file.read_text(encoding="utf-8", errors="ignore").splitlines():
            parts = line.split("\t", 1)
            if len(parts) != 2:
                continue
            iface = parts[0].strip()
            username = parts[1].strip()
            if iface and username:
                sessions[iface] = username
    for iface, username in sessions.items():
        stat_base = Path(f"/sys/class/net/{iface}/statistics")
        if not stat_base.exists():
            continue
        try:
            rx = int((stat_base / "rx_bytes").read_text(encoding="utf-8").strip() or "0")
            tx = int((stat_base / "tx_bytes").read_text(encoding="utf-8").strip() or "0")
        except Exception:
            continue
        total = max(0, rx) + max(0, tx)
        client_id = username_to_client.get(username) or client_ids.get(username)
        if (not client_id) and username in ("", "__unknown__") and len(enabled_clients) == 1:
            # Fallback attribution when PPP hook does not provide PEERNAME and there is exactly one enabled IPSec client.
            client_id = enabled_clients[0]
        if not client_id:
            continue
        delta = load_or_init_counter("ipsec_iface_total", iface, int(total))
        add_usage(client_id, delta)
        active_counts[client_id] = active_counts.get(client_id, 0) + 1
        result["sampledClients"] += 1
    result["attributionMode"] = "strict"
    result["attributionConfidence"] = 1.0
    result["observedSessions"] = result["sampledClients"]
    result["attributedSessions"] = result["sampledClients"]
else:
    if source:
        result["error"] = f"unknown_source:{source}"

for row in client_rows:
    client_id = str(row["client_id"])
    active = int(active_counts.get(client_id, 0))
    cur.execute(
        """
        INSERT INTO connection_counters(client_id,active_connections,last_seen_at)
        VALUES(?,?,?)
        ON CONFLICT(client_id) DO UPDATE SET
          active_connections=excluded.active_connections,
          last_seen_at=CASE
            WHEN excluded.active_connections > 0 THEN excluded.last_seen_at
            ELSE connection_counters.last_seen_at
          END
        """,
        (client_id, active, now_sec if active > 0 else 0),
    )

usage_rows = cur.execute(
    "SELECT c.client_id, c.enabled, c.total_bytes_limit, c.expiry_unix_ms, COALESCE(u.used_bytes,0) AS used_bytes, COALESCE(e.disabled_reason,'') AS disabled_reason "
    "FROM clients c "
    "LEFT JOIN usage_totals u ON u.client_id=c.client_id "
    "LEFT JOIN enforcement_state e ON e.client_id=c.client_id "
    "WHERE c.protocol_id=?",
    (protocol_id,),
).fetchall()

quota_enforcement_allowed = True
enforcement_enabled = (
    source in ("openvpn_status", "ipsec_ppp")
    or (source == "connector_tracker" and protocol_id in PER_CLIENT_SINGBOX_PROTOCOLS)
)
if not enforcement_enabled:
    quota_enforcement_allowed = False

ineligible_active_found = False
state_changed = False
has_disabled_clients = False
for row in usage_rows:
    if not enforcement_enabled:
        continue
    client_id = str(row["client_id"])
    enabled = int(row["enabled"] or 0)
    limit_bytes = int(row["total_bytes_limit"] or 0)
    expiry_ms = int(row["expiry_unix_ms"] or 0)
    used = int(row["used_bytes"] or 0)
    disabled_reason = str(row["disabled_reason"] or "")

    reason = ""
    if expiry_ms > 0 and now_ms >= expiry_ms:
        reason = "expired"
    elif limit_bytes > 0 and used >= limit_bytes and quota_enforcement_allowed:
        reason = "quota_exceeded"

    if reason:
        has_disabled_clients = True
        active_now = int(active_counts.get(client_id, 0))
        if active_now > 0:
            ineligible_active_found = True
        if disabled_reason != reason:
            state_changed = True
        if enabled != 0:
            cur.execute(
                "UPDATE clients SET enabled=0, updated_at=? WHERE client_id=?",
                (now_sec, client_id),
            )
            result["updatedClients"] += 1
            state_changed = True
        cur.execute(
            "INSERT OR REPLACE INTO enforcement_state(client_id,disabled_reason,disabled_at,updated_at) VALUES(?,?,?,?)",
            (client_id, reason, now_sec, now_sec),
        )
    else:
        if enabled == 0 and disabled_reason in ("expired", "quota_exceeded"):
            cur.execute(
                "UPDATE clients SET enabled=1, updated_at=? WHERE client_id=?",
                (now_sec, client_id),
            )
            result["updatedClients"] += 1
            state_changed = True
        if disabled_reason:
            state_changed = True
        cur.execute(
            "INSERT OR REPLACE INTO enforcement_state(client_id,disabled_reason,disabled_at,updated_at) VALUES(?,?,?,?)",
            (client_id, "", 0, now_sec),
        )

result["stateChanged"] = state_changed
result["hasDisabledClients"] = has_disabled_clients
result["hasIneligibleActive"] = ineligible_active_found
result["needsSync"] = bool(state_changed or ineligible_active_found)

conn.commit()
conn.close()
print(json.dumps(result, separators=(",", ":")))
PY
  ) 9>"$LOCK_FILE"
)"; then
  accounting_rc=$?
  if [[ "$accounting_rc" -eq 99 ]]; then
    exit 0
  fi
  run_result='{"ok":false,"needsSync":false,"error":"accounting_runtime_failed","checkedAtUtc":"'"$(date -u +'%Y-%m-%dT%H:%M:%SZ')"'" }'
fi

if [[ "$(jq -r '.needsSync // false' <<<"$run_result" 2>/dev/null || echo false)" == "true" ]]; then
  printf '[%s] accounting-sync: needsSync=true source=%s cmd=%s\n' \
    "$(date -u +'%Y-%m-%dT%H:%M:%SZ')" \
    "$(jq -r '.source // \"\"' <<<"$run_result" 2>/dev/null || echo "")" \
    "$SYNC_COMMAND" >> /tmp/omnirelay-accounting-sync.log 2>/dev/null || true
  if ! bash -lc "$SYNC_COMMAND" >>/tmp/omnirelay-accounting-sync.log 2>&1; then
    sync_error="$(tail -n 60 /tmp/omnirelay-accounting-sync.log 2>/dev/null | tr '\n' ';' | sed 's/"/\\"/g' || true)"
    [[ -n "$sync_error" ]] || sync_error="sync_command_failed"
    run_result="$(jq -c --arg err "$sync_error" '.ok=false | .syncError=$err' <<<"$run_result" 2>/dev/null || printf '{"ok":false,"needsSync":true,"error":"%s"}' "$sync_error")"
  fi
fi

printf '%s\n' "$run_result" > "$STATE_FILE"
chmod 0644 "$STATE_FILE" || true
fix_accounting_db_permissions
EOF
  chmod 0755 "$ACCOUNTING_SYNC_SCRIPT"
}

connector_write_accounting_sync_units(){
  cat > "/etc/systemd/system/${ACCOUNTING_SYNC_SERVICE}" <<EOF
[Unit]
Description=OmniRelay accounting sampler and enforcement
After=network-online.target ${CONNECTOR_SERVICE}.service
Wants=network-online.target ${CONNECTOR_SERVICE}.service

[Service]
Type=oneshot
UMask=0007
EnvironmentFile=-${ACCOUNTING_SYNC_ENV_FILE}
ExecStart=${ACCOUNTING_SYNC_SCRIPT}
EOF

  cat > "/etc/systemd/system/${ACCOUNTING_SYNC_TIMER}" <<EOF
[Unit]
Description=Run OmniRelay accounting sampler periodically

[Timer]
OnBootSec=45s
OnUnitActiveSec=${ACCOUNTING_SYNC_INTERVAL_SEC}s
RandomizedDelaySec=5
Persistent=true
Unit=${ACCOUNTING_SYNC_SERVICE}

[Install]
WantedBy=timers.target
EOF

  systemctl daemon-reload
}

connector_install_accounting_runtime(){
  connector_ensure_accounting_schema
  connector_write_ppp_accounting_hooks
  connector_write_accounting_sync_env
  connector_write_accounting_sync_script
  connector_write_accounting_sync_units
  systemctl enable --now "$ACCOUNTING_SYNC_TIMER" >/dev/null 2>&1 || die "failed to enable accounting sync timer"
  systemctl start "$ACCOUNTING_SYNC_SERVICE" >/dev/null 2>&1 || true
}

connector_uninstall_accounting_runtime(){
  systemctl disable --now "$ACCOUNTING_SYNC_TIMER" "$ACCOUNTING_SYNC_SERVICE" >/dev/null 2>&1 || true
  rm -f "/etc/systemd/system/${ACCOUNTING_SYNC_SERVICE}" "/etc/systemd/system/${ACCOUNTING_SYNC_TIMER}" "$ACCOUNTING_SYNC_SCRIPT" "$ACCOUNTING_SYNC_ENV_FILE" "$ACCOUNTING_SYNC_STATE_FILE" /etc/ppp/ip-up.d/99-omnirelay-accounting /etc/ppp/ip-down.d/99-omnirelay-accounting
  systemctl daemon-reload || true
}

connector_resolve_node_bin(){
  command -v node >/dev/null 2>&1 && command -v node && return 0
  command -v nodejs >/dev/null 2>&1 && command -v nodejs && return 0
  return 1
}

connector_detect_node_major(){
  local b
  b="$(connector_resolve_node_bin 2>/dev/null || true)"
  [[ -z "$b" ]] && { echo 0; return; }
  "$b" -p "Number(process.versions.node.split('.')[0])" 2>/dev/null || echo 0
}

connector_ensure_node_runtime(){
  local m
  m="$(connector_detect_node_major)"
  (( m >= 18 )) && return 0
  connector_configure_proxy
  curl -fSL "https://deb.nodesource.com/setup_20.x" -o /tmp/omnirelay-nodesource.sh
  bash /tmp/omnirelay-nodesource.sh
  DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends nodejs
  connector_clear_proxy
  m="$(connector_detect_node_major)"
  (( m >= 18 )) || die "Node.js 18+ is required."
}

connector_write_service_unit(){
  local sync_cmd
  sync_cmd="${GATEWAYCTL_PATH} sync-clients"
  if [[ -n "${RELAY_ID:-}" ]]; then
    sync_cmd="${sync_cmd} --relay-id ${RELAY_ID}"
  fi
  cat > "/etc/systemd/system/${CONNECTOR_SERVICE}.service" <<EOF
[Unit]
Description=OmniRelay sing-box connector
After=network-online.target
Wants=network-online.target
[Service]
Type=simple
ExecStart=${CONNECTOR_BIN} run --config ${CONNECTOR_CONFIG_FILE} --metadata ${GATEWAY_METADATA_FILE} --accounting-db ${CONNECTOR_ACCOUNTING_DB} --state-file ${CONNECTOR_CORE_STATE_FILE} --lock-file ${CONNECTOR_ACCOUNTING_LOCK_FILE} --interval-sec ${ACCOUNTING_SYNC_INTERVAL_SEC} "--sync-command=${sync_cmd}" --panel-group ${PANEL_USER_ACCOUNT}
Restart=always
RestartSec=3
[Install]
WantedBy=multi-user.target
EOF
  systemctl daemon-reload
}

connector_install_connector_core_binary(){
  progress 24 "Installing connector-core runtime"
  local arch="amd64"
  [[ "$(uname -m)" =~ ^(aarch64|arm64)$ ]] && arch="arm64"
  local artifact_url="https://omnirelay.net/download/connector-core/linux/${arch}"
  local fallback_base_url="https://github.com/omnirelay/OmniRelay/releases/download/connector-core-v${CONNECTOR_CORE_VERSION}"
  local fallback_url="${fallback_base_url}/connector-core-linux-${arch}.tar.gz"
  local url="${CONNECTOR_CORE_DOWNLOAD_URL:-$artifact_url}"
  local tmp="/tmp/connector-core-${CONNECTOR_CORE_VERSION}-${arch}.tar.gz"
  local dir="/tmp/connector-core-${CONNECTOR_CORE_VERSION}-${arch}"
  local bin

  connector_configure_proxy
  if ! curl -fSL "$url" -o "$tmp"; then
    if [[ -z "${CONNECTOR_CORE_DOWNLOAD_URL:-}" && "$url" != "$fallback_url" ]]; then
      log "Primary connector-core artifact URL failed (${url}); retrying GitHub release fallback."
      curl -fSL "$fallback_url" -o "$tmp" || {
        connector_clear_proxy
        die "failed to download connector-core release artifact"
      }
    else
      connector_clear_proxy
      die "failed to download connector-core release artifact"
    fi
  fi
  connector_clear_proxy
  if [[ ! -s "$tmp" ]]; then
    connector_clear_proxy
    die "failed to download connector-core release artifact"
  fi

  rm -rf "$dir"
  mkdir -p "$dir"
  tar -xzf "$tmp" -C "$dir"
  bin="$(find "$dir" -type f -name connector-core | head -n1 || true)"
  [[ -f "${bin:-}" ]] || die "connector-core binary install failed"
  install -m 0755 "$bin" "$CONNECTOR_BIN"
}

connector_init(){
  install -d -m 0755 /usr/local/lib/omnirelay "$GATEWAY_ROOT_DIR" "$CONNECTOR_DIR" "$PANEL_APP_DIR" "$PANEL_RELEASES_DIR"
  [[ -f "$BOOTSTRAP_COMMON_SCRIPT" ]] && install -m 0755 "$BOOTSTRAP_COMMON_SCRIPT" /usr/local/lib/omnirelay/bootstrap-common.sh || true
  [[ -f "$PANEL_COMMON_SCRIPT" ]] && install -m 0755 "$PANEL_COMMON_SCRIPT" /usr/local/lib/omnirelay/omnipanel-common.sh || true
  [[ -f "${BASH_SOURCE[0]:-$0}" ]] && install -m 0755 "${BASH_SOURCE[0]:-$0}" "$CONNECTOR_COMMON_INSTALLED" || true
}

connector_install_runtime(){
  progress 14 "Installing required packages"
  connector_ensure_bootstrap_common
  omnirelay_bootstrap_configure_apt_proxy "$BOOTSTRAP_MODE" "$BOOTSTRAP_SOCKS_PORT" "$APT_PROXY_FILE"
  connector_configure_proxy
  apt-get -o Acquire::Retries=4 -o Acquire::http::Timeout=30 -o Acquire::https::Timeout=30 update -y
  local packages=(curl jq tar gzip ca-certificates openssl python3 sqlite3 nginx nodejs iptables)
  while (( $# > 0 )); do
    packages+=("$1")
    shift
  done
  DEBIAN_FRONTEND=noninteractive apt-get -o Acquire::Retries=4 -o Acquire::http::Timeout=30 -o Acquire::https::Timeout=30 install -y --no-install-recommends "${packages[@]}"
  connector_clear_proxy
  connector_ensure_node_runtime
  connector_install_connector_core_binary
  connector_write_service_unit
  connector_install_clock_sync_runtime
  connector_install_accounting_runtime
  systemctl enable --now "$CONNECTOR_SERVICE"
}

connector_validate_dns_profile(){
  local endpoints="${1:-}"
  [[ -n "$(printf '%s' "$endpoints" | tr ',' '\n' | awk 'NF{print $1; exit}')" ]] || die "--doh-endpoints is required"
  while IFS= read -r endpoint; do
    [[ -n "$endpoint" ]] || continue
    [[ "$endpoint" =~ ^https://[^[:space:]]+$ ]] || die "DoH endpoint must be an https URL: ${endpoint}"
  done < <(printf '%s' "$endpoints" | tr ',' '\n' | xargs -n1 2>/dev/null || true)
}

connector_maybe_load_dns_profile_defaults(){
  case "${COMMAND:-}" in
    install|dns-apply) return 0 ;;
  esac
  [[ -f "$GATEWAY_DNS_PROFILE_FILE" ]] || return 0
  DOH_ENDPOINTS="$(jq -r '.dohEndpoints // "https://1.1.1.1/dns-query,https://8.8.8.8/dns-query"' "$GATEWAY_DNS_PROFILE_FILE" 2>/dev/null || echo "https://1.1.1.1/dns-query,https://8.8.8.8/dns-query")"
}

connector_dns_profile_json(){
  local endpoints="${DOH_ENDPOINTS:-}"
  connector_validate_dns_profile "$endpoints"
  jq -c -n \
    --arg doh "$endpoints" \
    --arg listen "$CONNECTOR_DNS_LISTEN_ADDRESS" \
    --argjson port "$CONNECTOR_DNS_LISTEN_PORT" \
    '{
      dohEndpoints:$doh,
      listenAddress:$listen,
      listenPort:$port,
      updatedAtUtc:(now|todate)
    }'
}

connector_dns_singbox_json(){
  connector_dns_profile_json | jq -c '
    def csv: split(",") | map(gsub("^\\s+|\\s+$";"")) | map(select(length > 0));
    . as $profile
    | ($profile.dohEndpoints | csv) as $doh
    | ([$doh[] as $address | {tag:("dns-doh-" + ($doh | index($address) | tostring)), address:$address, detour:"tunnel-backend"}]) as $servers
    | {
        dns:{
          servers:$servers,
          final:"dns-doh-0",
          strategy:"prefer_ipv4",
          independent_cache:true
        },
        dnsInbound:{
          type:"direct",
          tag:"dns-in",
          listen:$profile.listenAddress,
          listen_port:$profile.listenPort,
          override_address:"1.1.1.1",
          override_port:53
        },
        dnsOutbound:{type:"dns",tag:"dns-out"},
        dnsRouteRuleInbound:{inbound:["dns-in"],outbound:"dns-out"},
        dnsRouteRuleProtocol:{protocol:"dns",outbound:"dns-out"}
      }'
}

connector_apply_dns_to_config_json(){
  local config_json="$1"
  local dns_json
  dns_json="$(connector_dns_singbox_json)"
  jq -c --argjson dnsPatch "$dns_json" '
    def without_tag($tag): map(select((.tag // "") != $tag));
    .dns = $dnsPatch.dns
    | .inbounds = ((.inbounds // []) | without_tag("dns-in") + [$dnsPatch.dnsInbound])
    | .outbounds = ((.outbounds // []) | without_tag("dns-out") + [$dnsPatch.dnsOutbound])
    | .route = (.route // {})
    | .route.rules = (
        [$dnsPatch.dnsRouteRuleInbound, $dnsPatch.dnsRouteRuleProtocol]
        + (
            (.route.rules // [])
            | map(
                select(
                  (.protocol != "dns")
                  and ((.inbound // []) | index("dns-in") | not)
                )
              )
          )
      )
  ' <<<"$config_json"
}

connector_render_apply(){
  local mode="$1"
  local config_json="$2"
  [[ "$mode" == "full_tunnel" || "$mode" == "internal_tunnel" ]] || die "connector mode must be full_tunnel|internal_tunnel"
  jq -e 'type=="object"' >/dev/null 2>&1 <<<"$config_json" || die "Invalid connector JSON payload"
  connector_maybe_load_dns_profile_defaults
  connector_validate_dns_profile "$DOH_ENDPOINTS"
  config_json="$(connector_apply_dns_to_config_json "$config_json")"
  jq -e 'type=="object"' >/dev/null 2>&1 <<<"$config_json" || die "Invalid connector JSON payload after DNS injection"
  printf '%s\n' "$config_json" > "$CONNECTOR_CONFIG_FILE"
  "$CONNECTOR_BIN" check --config "$CONNECTOR_CONFIG_FILE" >/tmp/omnirelay-singbox-check.log 2>&1 || { sed -n '1,120p' /tmp/omnirelay-singbox-check.log >&2 || true; die "connector-core config validation failed"; }
  jq -n --arg mode "$mode" '{mode:$mode,updatedAtUtc:(now|todate)}' > "$CONNECTOR_STATE_FILE"
  if ! systemctl kill -s HUP "$CONNECTOR_SERVICE" >/dev/null 2>&1; then
    systemctl restart "$CONNECTOR_SERVICE"
  fi
  if ! connector_wait_service_active "$CONNECTOR_SERVICE" 25; then
    log "---- ${CONNECTOR_SERVICE} systemd status ----"
    systemctl --no-pager --full status "$CONNECTOR_SERVICE" 2>&1 || true
    log "---- ${CONNECTOR_SERVICE} journal (last 80 lines) ----"
    journalctl -u "$CONNECTOR_SERVICE" -n 80 --no-pager 2>&1 || true
    die "sing-box is not active after apply"
  fi
}

connector_apply_internal_redirect(){
  local iface="$1"
  [[ -n "$iface" ]] || return 0
  local redirect_port
  redirect_port="$(jq -r '.inbounds[]? | select(.tag=="connector-in") | .listen_port' "$CONNECTOR_CONFIG_FILE" 2>/dev/null | head -n1 || true)"
  [[ "$redirect_port" =~ ^[0-9]+$ ]] || die "connector-in listen_port not found in config"
  iptables -t nat -N "$CONNECTOR_REDIRECT_CHAIN" 2>/dev/null || true
  iptables -t nat -F "$CONNECTOR_REDIRECT_CHAIN"
  iptables -t nat -A "$CONNECTOR_REDIRECT_CHAIN" -d 127.0.0.0/8 -j RETURN
  iptables -t nat -A "$CONNECTOR_REDIRECT_CHAIN" -p tcp -j REDIRECT --to-ports "$redirect_port"
  iptables -t nat -C PREROUTING -i "$iface" -p tcp -j "$CONNECTOR_REDIRECT_CHAIN" 2>/dev/null || iptables -t nat -A PREROUTING -i "$iface" -p tcp -j "$CONNECTOR_REDIRECT_CHAIN"

  iptables -N "$CONNECTOR_REJECT_CHAIN" 2>/dev/null || true
  iptables -F "$CONNECTOR_REJECT_CHAIN"
  iptables -A "$CONNECTOR_REJECT_CHAIN" -p udp --dport 443 -j REJECT --reject-with icmp-port-unreachable
  iptables -C FORWARD -i "$iface" -p udp --dport 443 -j "$CONNECTOR_REJECT_CHAIN" 2>/dev/null || iptables -I FORWARD 1 -i "$iface" -p udp --dport 443 -j "$CONNECTOR_REJECT_CHAIN"
  if command -v ip6tables >/dev/null 2>&1; then
    ip6tables -C FORWARD -i "$iface" -j REJECT 2>/dev/null || ip6tables -I FORWARD 1 -i "$iface" -j REJECT
  fi
}

connector_clear_internal_redirect(){
  local iface="$1"
  [[ -n "$iface" ]] || return 0
  iptables -t nat -D PREROUTING -i "$iface" -p tcp -j "$CONNECTOR_REDIRECT_CHAIN" 2>/dev/null || true
  iptables -t nat -F "$CONNECTOR_REDIRECT_CHAIN" 2>/dev/null || true
  iptables -t nat -X "$CONNECTOR_REDIRECT_CHAIN" 2>/dev/null || true
  iptables -D FORWARD -i "$iface" -p udp --dport 443 -j "$CONNECTOR_REJECT_CHAIN" 2>/dev/null || true
  iptables -F "$CONNECTOR_REJECT_CHAIN" 2>/dev/null || true
  iptables -X "$CONNECTOR_REJECT_CHAIN" 2>/dev/null || true
  if command -v ip6tables >/dev/null 2>&1; then
    ip6tables -D FORWARD -i "$iface" -j REJECT 2>/dev/null || true
  fi
}

connector_dns_apply(){
  local endpoints="$1"
  DOH_ENDPOINTS="$endpoints"
  connector_validate_dns_profile "$DOH_ENDPOINTS"
  install -d -m 0755 "$(dirname "$GATEWAY_DNS_PROFILE_FILE")"
  connector_dns_profile_json > "$GATEWAY_DNS_PROFILE_FILE"
  chmod 0600 "$GATEWAY_DNS_PROFILE_FILE" || true
  if [[ -f "$CONNECTOR_CONFIG_FILE" && -x "$CONNECTOR_BIN" ]]; then
    local updated_config
    updated_config="$(connector_apply_dns_to_config_json "$(cat "$CONNECTOR_CONFIG_FILE")")"
    printf '%s\n' "$updated_config" > "$CONNECTOR_CONFIG_FILE"
    "$CONNECTOR_BIN" check --config "$CONNECTOR_CONFIG_FILE" >/tmp/omnirelay-singbox-check.log 2>&1 || { sed -n '1,120p' /tmp/omnirelay-singbox-check.log >&2 || true; die "connector-core config validation failed after DNS apply"; }
    if ! systemctl kill -s HUP "$CONNECTOR_SERVICE" >/dev/null 2>&1; then
      systemctl restart "$CONNECTOR_SERVICE" >/dev/null 2>&1 || true
    fi
  fi
}

connector_dns_status_json(){
  local d cfg local_listener h config_dns config_route doh_ok
  if [[ -f "$GATEWAY_DNS_PROFILE_FILE" ]]; then
    d="$(jq -r '.dohEndpoints // ""' "$GATEWAY_DNS_PROFILE_FILE")"
    cfg=true
  else
    d=""
    cfg=false
  fi
  local_listener="$(connector_check_local_dns_listener "$CONNECTOR_DNS_LISTEN_PORT")"
  config_dns=false
  config_route=false
  if [[ -f "$CONNECTOR_CONFIG_FILE" ]]; then
    jq -e '.dns.servers | type=="array" and length > 0' "$CONNECTOR_CONFIG_FILE" >/dev/null 2>&1 && config_dns=true
    jq -e '.route.rules[]? | select(.protocol=="dns" and .outbound=="dns-out")' "$CONNECTOR_CONFIG_FILE" >/dev/null 2>&1 && config_route=true
  fi
  doh_ok=false
  if [[ "$cfg" == true ]]; then
    local first_doh
    first_doh="$(printf '%s' "$d" | tr ',' '\n' | awk 'NF{print $1; exit}')"
    if [[ -n "$first_doh" ]] && command -v curl >/dev/null 2>&1; then
      curl -fsS --max-time 5 -H 'accept: application/dns-message' "$first_doh?dns=AAABAAABAAAAAAAAA3d3dwdleGFtcGxlA2NvbQAAAQAB" >/dev/null 2>&1 && doh_ok=true
    fi
  fi
  h=false
  [[ "$cfg" == true && "$config_dns" == true && "$config_route" == true && "$local_listener" == true && "$doh_ok" == true ]] && h=true
  printf '{"dnsConfigPresent":%s,"dnsRuleActive":%s,"dohReachableViaTunnel":%s,"dnsPathHealthy":%s,"dohEndpoints":"%s","localDnsListener":%s,"singBoxDnsConfigPresent":%s,"singBoxDnsRuleActive":%s}\n' "$cfg" "$config_route" "$doh_ok" "$h" "$(printf '%s' "$d" | sed 's/"/\\"/g')" "$local_listener" "$config_dns" "$config_route"
}

connector_tunnel_probe_json(){
  local out
  if [[ -x /usr/local/sbin/omnirelay-tunnelctl ]]; then
    out="$(/usr/local/sbin/omnirelay-tunnelctl probe --backend-host 127.0.0.1 --backend-port "$BACKEND_PORT" --json 2>/dev/null || true)"
    jq -e 'type=="object"' >/dev/null 2>&1 <<<"$out" && { echo "$out"; return; }
  fi
  echo '{"healthy":false,"reasonCode":"tunnelctl_unavailable","backendProtocol":"unknown","egressReachable":false}'
}

connector_detect_backend_protocol(){
  local probe
  probe="$(connector_tunnel_probe_json)"
  jq -r '.backendProtocol // "unknown"' <<<"$probe" 2>/dev/null || echo "unknown"
}

connector_backend_outbound_json(){
  jq -c -n --argjson port "$BACKEND_PORT" '{type:"socks",tag:"tunnel-backend",server:"127.0.0.1",server_port:$port,version:"5"}'
}

connector_active_protocol(){
  jq -r '.active_protocol // "unknown"' "$GATEWAY_METADATA_FILE" 2>/dev/null || echo "unknown"
}

connector_panel_internal_port(){
  awk -F= '/^PORT=/{print $2; exit}' "$PANEL_ENV_FILE" 2>/dev/null | tr -d '[:space:]'
}

connector_configured_backend_outbound_type(){
  jq -r '.outbounds[]? | select(.tag=="tunnel-backend" or .tag=="tunnel-socks") | .type' "$CONNECTOR_CONFIG_FILE" 2>/dev/null | head -n1
}

connector_clock_status_json(){
  local checked_at checked_epoch now skew abs_skew source reason state timer_enabled
  now="$(date +%s)"
  timer_enabled=false
  if systemctl is-enabled --quiet "$CLOCK_SYNC_TIMER" >/dev/null 2>&1; then
    timer_enabled=true
  fi
  if [[ ! -f "$CLOCK_SYNC_STATE_FILE" ]]; then
    printf '{"clockSyncEnabled":%s,"clockSyncState":"missing","clockSkewSec":999999,"clockLastCheckedUtc":"","clockSource":"","clockReason":"state_missing"}\n' "$timer_enabled"
    return 0
  fi

  checked_at="$(jq -r '.checkedAtUtc // ""' "$CLOCK_SYNC_STATE_FILE" 2>/dev/null || true)"
  checked_epoch="$(date -u -d "$checked_at" +%s 2>/dev/null || echo 0)"
  skew="$(jq -r '.skewSecAfter // .skewSecBefore // 999999' "$CLOCK_SYNC_STATE_FILE" 2>/dev/null || echo 999999)"
  source="$(connector_json_string_safe "$(jq -r '.source // ""' "$CLOCK_SYNC_STATE_FILE" 2>/dev/null || true)")"
  reason="$(connector_json_string_safe "$(jq -r '.reason // "unknown"' "$CLOCK_SYNC_STATE_FILE" 2>/dev/null || true)")"
  if [[ ! "$skew" =~ ^-?[0-9]+$ ]]; then
    skew=999999
  fi
  abs_skew="$skew"
  (( abs_skew < 0 )) && abs_skew=$(( -abs_skew ))
  state="healthy"
  if [[ "$(jq -r '.ok // false' "$CLOCK_SYNC_STATE_FILE" 2>/dev/null || echo false)" != true ]]; then
    state="failed"
  elif (( checked_epoch <= 0 || now - checked_epoch > CLOCK_SYNC_STALE_SEC )); then
    state="stale"
  elif (( abs_skew > CLOCK_SYNC_MAX_SKEW_SEC )); then
    state="skew_exceeded"
  fi

  printf '{"clockSyncEnabled":%s,"clockSyncState":"%s","clockSkewSec":%s,"clockLastCheckedUtc":"%s","clockSource":"%s","clockReason":"%s"}\n' \
    "$timer_enabled" "$state" "$skew" "$(connector_json_string_safe "$checked_at")" "$source" "$reason"
}

connector_accounting_status_json(){
  local protocol_id timer_state last_checked state error
  protocol_id="$(connector_active_protocol)"
  if connector_is_full_tunnel_singbox_protocol "$protocol_id"; then
    timer_state="n/a"
    if [[ ! -f "$CONNECTOR_CORE_STATE_FILE" ]]; then
      printf '{"accountingTimerState":"%s","lastAccountingSyncUtc":"","accountingSyncState":"failed","accountingLastError":"state_missing"}\n' "$(connector_json_string_safe "$timer_state")"
      return 0
    fi
    last_checked="$(jq -r '.lastSyncUtc // ""' "$CONNECTOR_CORE_STATE_FILE" 2>/dev/null || true)"
    if [[ "$(jq -r '.ok // false' "$CONNECTOR_CORE_STATE_FILE" 2>/dev/null || echo false)" == "true" ]]; then
      state="ok"
      error=""
    else
      state="failed"
      error="$(jq -r '.lastError // "unknown"' "$CONNECTOR_CORE_STATE_FILE" 2>/dev/null || echo unknown)"
    fi
  else
    timer_state="$(connector_service_state "$ACCOUNTING_SYNC_TIMER")"
    if [[ ! -f "$ACCOUNTING_SYNC_STATE_FILE" ]]; then
      printf '{"accountingTimerState":"%s","lastAccountingSyncUtc":"","accountingSyncState":"failed","accountingLastError":"state_missing"}\n' "$(connector_json_string_safe "$timer_state")"
      return 0
    fi
    last_checked="$(jq -r '.checkedAtUtc // ""' "$ACCOUNTING_SYNC_STATE_FILE" 2>/dev/null || true)"
    if [[ "$(jq -r '.ok // false' "$ACCOUNTING_SYNC_STATE_FILE" 2>/dev/null || echo false)" == "true" ]]; then
      state="ok"
      error=""
    else
      state="failed"
      error="$(jq -r '.syncError // .error // "unknown"' "$ACCOUNTING_SYNC_STATE_FILE" 2>/dev/null || echo unknown)"
    fi
  fi
  printf '{"accountingTimerState":"%s","lastAccountingSyncUtc":"%s","accountingSyncState":"%s","accountingLastError":"%s"}\n' \
    "$(connector_json_string_safe "$timer_state")" "$(connector_json_string_safe "$last_checked")" "$(connector_json_string_safe "$state")" "$(connector_json_string_safe "$error")"
}

connector_status_base_json(){
  local active ssh_state connector_state panel_state nginx_state fail2_state intp back pub pan inl dns tunnel dns_endpoints tunnel_reason tunnel_backend configured_backend_outbound_type clock clock_state clock_source clock_reason accounting
  active="$(connector_json_string_safe "$(connector_active_protocol)")"
  ssh_state="$(connector_service_state ssh)"
  if [[ "$ssh_state" != "active" ]]; then
    local sshd_state
    sshd_state="$(connector_service_state sshd)"
    [[ "$sshd_state" == "active" ]] && ssh_state="$sshd_state"
  fi
  ssh_state="$(connector_json_string_safe "$ssh_state")"
  connector_state="$(connector_json_string_safe "$(connector_service_state "$CONNECTOR_SERVICE")")"
  panel_state="$(connector_json_string_safe "$(connector_service_state "$PANEL_SERVICE")")"
  nginx_state="$(connector_json_string_safe "$(connector_service_state nginx)")"
  fail2_state="$(connector_service_state fail2ban)"
  [[ "$fail2_state" == "unknown" ]] && fail2_state="disabled"
  fail2_state="$(connector_json_string_safe "$fail2_state")"
  intp="$(connector_panel_internal_port || echo 0)"
  back="$(connector_check_listener "$BACKEND_PORT")"
  pub="$(connector_check_listener "$PUBLIC_PORT")"
  pan="$(connector_check_listener "$PANEL_PORT")"
  inl=false
  [[ "$intp" =~ ^[0-9]+$ && "$intp" != "0" ]] && inl="$(connector_check_listener "$intp")"
  dns="$(connector_dns_status_json)"
  tunnel="$(connector_tunnel_probe_json)"
  dns_endpoints="$(connector_json_string_safe "$(jq -r '.dohEndpoints' <<<"$dns")")"
  tunnel_reason="$(connector_json_string_safe "$(jq -r '.reasonCode // .reason // "unknown"' <<<"$tunnel")")"
  tunnel_backend="$(connector_json_string_safe "$(jq -r '.backendProtocol // "unknown"' <<<"$tunnel")")"
  configured_backend_outbound_type="$(connector_json_string_safe "$(connector_configured_backend_outbound_type)")"
  clock="$(connector_clock_status_json)"
  clock_state="$(connector_json_string_safe "$(jq -r '.clockSyncState // "unknown"' <<<"$clock")")"
  clock_source="$(connector_json_string_safe "$(jq -r '.clockSource // ""' <<<"$clock")")"
  clock_reason="$(connector_json_string_safe "$(jq -r '.clockReason // "unknown"' <<<"$clock")")"
  accounting="$(connector_accounting_status_json)"
  printf '{"activeProtocol":"%s","sshState":"%s","singBoxState":"%s","omniPanelState":"%s","nginxState":"%s","fail2banState":"%s","backendPort":%s,"publicPort":%s,"panelPort":%s,"omniPanelInternalPort":%s,"backendListener":%s,"publicListener":%s,"panelListener":%s,"omniPanelInternalListener":%s,"inboundId":"","dnsConfigPresent":%s,"dnsRuleActive":%s,"dohReachableViaTunnel":%s,"dnsPathHealthy":%s,"dohEndpoints":"%s","tunnelHealthy":%s,"tunnelReason":"%s","tunnelBackendProtocol":"%s","tunnelConfiguredOutboundType":"%s","tunnelEgressReachable":%s,"clockSyncEnabled":%s,"clockSyncState":"%s","clockSkewSec":%s,"clockLastCheckedUtc":"%s","clockSource":"%s","clockReason":"%s","accountingTimerState":"%s","lastAccountingSyncUtc":"%s","accountingSyncState":"%s","accountingLastError":"%s"}\n' \
    "$active" "$ssh_state" "$connector_state" "$panel_state" "$nginx_state" "$fail2_state" "$BACKEND_PORT" "$PUBLIC_PORT" "$PANEL_PORT" "${intp:-0}" "$back" "$pub" "$pan" "$inl" \
    "$(jq -r '.dnsConfigPresent' <<<"$dns")" "$(jq -r '.dnsRuleActive' <<<"$dns")" "$(jq -r '.dohReachableViaTunnel' <<<"$dns")" "$(jq -r '.dnsPathHealthy' <<<"$dns")" "$dns_endpoints" \
    "$(jq -r '.healthy // false' <<<"$tunnel")" "$tunnel_reason" "$tunnel_backend" "$configured_backend_outbound_type" "$(jq -r '.egressReachable // false' <<<"$tunnel")" \
    "$(jq -r '.clockSyncEnabled // false' <<<"$clock")" "$clock_state" "$(jq -r '.clockSkewSec // 999999' <<<"$clock")" "$(connector_json_string_safe "$(jq -r '.clockLastCheckedUtc // ""' <<<"$clock")")" "$clock_source" "$clock_reason" \
    "$(connector_json_string_safe "$(jq -r '.accountingTimerState // "unknown"' <<<"$accounting")")" "$(connector_json_string_safe "$(jq -r '.lastAccountingSyncUtc // ""' <<<"$accounting")")" "$(connector_json_string_safe "$(jq -r '.accountingSyncState // "unknown"' <<<"$accounting")")" "$(connector_json_string_safe "$(jq -r '.accountingLastError // ""' <<<"$accounting")")"
}

connector_health_base_json(){
  local status h err backend_protocol outbound_type clock_state clock_skew clock_abs accounting_state accounting_timer_state active_protocol
  status="${1:-$(connector_status_base_json)}"
  h=true
  err=""
  [[ "$(jq -r '.sshState' <<<"$status")" == active ]] || h=false
  [[ "$(jq -r '.singBoxState' <<<"$status")" == active ]] || h=false
  [[ "$(jq -r '.omniPanelState' <<<"$status")" == active ]] || h=false
  [[ "$(jq -r '.nginxState' <<<"$status")" == active ]] || h=false
  [[ "$(jq -r '.backendListener' <<<"$status")" == true ]] || h=false
  [[ "$(jq -r '.panelListener' <<<"$status")" == true ]] || h=false
  [[ "$(jq -r '.omniPanelInternalListener' <<<"$status")" == true ]] || h=false
  [[ "$(jq -r '.dnsPathHealthy' <<<"$status")" == true ]] || h=false
  [[ "$(jq -r '.tunnelHealthy' <<<"$status")" == true ]] || h=false
  backend_protocol="$(jq -r '.tunnelBackendProtocol // "unknown"' <<<"$status")"
  outbound_type="$(jq -r '.tunnelConfiguredOutboundType // "unknown"' <<<"$status")"
  if [[ "$backend_protocol" != "socks5" ]]; then
    h=false
    err="backendProtocolNotSocks5"
  elif [[ "$outbound_type" != "socks" ]]; then
    h=false
    err="backendOutboundNotSocks5"
  fi
  clock_state="$(jq -r '.clockSyncState // "unknown"' <<<"$status")"
  clock_skew="$(jq -r '.clockSkewSec // 999999' <<<"$status")"
  [[ "$clock_skew" =~ ^-?[0-9]+$ ]] || clock_skew=999999
  clock_abs="$clock_skew"
  (( clock_abs < 0 )) && clock_abs=$(( -clock_abs ))
  if [[ "$clock_state" != "healthy" || "$clock_abs" -gt "$CLOCK_SYNC_MAX_SKEW_SEC" ]]; then
    h=false
    [[ -n "$err" ]] || err="clockSyncUnhealthy"
  fi
  active_protocol="$(jq -r '.activeProtocol // "unknown"' <<<"$status")"
  accounting_state="$(jq -r '.accountingSyncState // "unknown"' <<<"$status")"
  accounting_timer_state="$(jq -r '.accountingTimerState // "unknown"' <<<"$status")"
  if connector_is_full_tunnel_singbox_protocol "$active_protocol"; then
    if [[ "$accounting_state" != "ok" ]]; then
      h=false
      [[ -n "$err" ]] || err="accountingSyncUnhealthy"
    fi
  else
    if [[ "$accounting_timer_state" != "active" || "$accounting_state" == "failed" ]]; then
      h=false
      [[ -n "$err" ]] || err="accountingSyncUnhealthy"
    fi
  fi
  [[ -n "$err" ]] || [[ "$(jq -r '.dnsConfigPresent' <<<"$status")" == true ]] || err="dnsConfigMissing"
  [[ -n "$err" ]] || [[ "$(jq -r '.dnsRuleActive' <<<"$status")" == true ]] || err="dnsRuleInactive"
  [[ -n "$err" ]] || [[ "$(jq -r '.tunnelHealthy' <<<"$status")" == true ]] || err="$(jq -r '.tunnelReason' <<<"$status")"
  jq -c --argjson healthy "$( [[ "$h" == true ]] && echo true || echo false )" --arg dnsLastError "$err" '. + {healthy:$healthy,dnsLastError:$dnsLastError}' <<<"$status"
}

connector_clean_legacy(){
  systemctl disable --now x-ui omnirelay-redsocks >/dev/null 2>&1 || true
  rm -f /etc/systemd/system/x-ui.service /etc/systemd/system/omnirelay-redsocks.service
  rm -rf /usr/local/x-ui /etc/x-ui /var/log/x-ui
  systemctl daemon-reload || true
}

connector_write_metadata_base(){
  local protocol_id="$1"
  local mode="$2"
  local panel_internal
  panel_internal="$(connector_panel_internal_port || echo 0)"
  jq -n --arg protocol "$protocol_id" --arg mode "$mode" --argjson publicPort "$PUBLIC_PORT" --argjson panelPort "$PANEL_PORT" --argjson backendPort "$BACKEND_PORT" --argjson panelInternalPort "${panel_internal:-0}" '{
    active_protocol:$protocol,
    connector_mode:$mode,
    public_port:$publicPort,
    omnipanel_public_port:$panelPort,
    backend_port:$backendPort,
    omnipanel_internal_port:$panelInternalPort,
    created_at_utc:(now|todate)
  }' > "$GATEWAY_METADATA_FILE"
  chmod 0600 "$GATEWAY_METADATA_FILE" || true
}

connector_merge_metadata_json(){
  local patch_json="$1"
  jq -c --argjson patch "$patch_json" '. + $patch' "$GATEWAY_METADATA_FILE" > "${GATEWAY_METADATA_FILE}.tmp"
  mv -f "${GATEWAY_METADATA_FILE}.tmp" "$GATEWAY_METADATA_FILE"
  chmod 0600 "$GATEWAY_METADATA_FILE" || true
}

connector_deploy_omnipanel(){
  local protocol_id="$1"
  local extra_env_file="${2:-}"
  progress 60 "Deploying OmniPanel artifact"
  local node_bin rel dir intp host panel_backup_dir singbox_sync_cmd openvpn_sync_cmd ipsec_sync_cmd
  node_bin="$(connector_resolve_node_bin || true)"
  [[ -n "$node_bin" ]] || die "Node.js executable not found"
  install -d -m 0755 "$PANEL_APP_DIR" "$PANEL_RELEASES_DIR"
  rel="$(date +%Y%m%d%H%M%S)"
  dir="${PANEL_RELEASES_DIR}/${rel}"
  mkdir -p "$dir"
  connector_configure_proxy
  curl -fSL "https://omnirelay.net/download/omni-gateway" -o /tmp/omni-gateway.tar.gz
  connector_clear_proxy
  tar -xzf /tmp/omni-gateway.tar.gz -C "$dir"
  if [[ ! -f "$dir/server.js" ]]; then
    local nested
    nested="$(find "$dir" -mindepth 1 -maxdepth 1 -type d | head -n1 || true)"
    [[ -n "$nested" ]] && cp -a "$nested"/. "$dir"/
  fi
  [[ -f "$dir/server.js" ]] || die "omnipanel artifact missing server.js"
  id -u "$PANEL_USER_ACCOUNT" >/dev/null 2>&1 || useradd --system --home "$PANEL_APP_DIR" --shell /usr/sbin/nologin "$PANEL_USER_ACCOUNT"
  [[ -n "$PANEL_USER" ]] || PANEL_USER="omniadmin_$(connector_random_string 6)"
  [[ -n "$PANEL_PASSWORD" ]] || PANEL_PASSWORD="$(connector_random_string 24)"
  intp="$(connector_choose_port)"
  host="${PANEL_DOMAIN:-${VPS_IP:-$(hostname -I 2>/dev/null | awk '{print $1}') }}"
  host="${host%% *}"
  [[ -n "$host" ]] || host="127.0.0.1"
  panel_backup_dir="${PANEL_APP_DIR}/backups"
  install -d -m 0750 "$panel_backup_dir"
  chown "${PANEL_USER_ACCOUNT}:${PANEL_USER_ACCOUNT}" "$panel_backup_dir" || true
  singbox_sync_cmd="/usr/bin/sudo -n ${GATEWAYCTL_PATH} sync-clients"
  openvpn_sync_cmd="/usr/bin/sudo -n ${GATEWAYCTL_PATH} sync-clients"
  ipsec_sync_cmd="/usr/bin/sudo -n ${GATEWAYCTL_PATH} sync-clients"
  if [[ -n "${RELAY_ID:-}" ]]; then
    singbox_sync_cmd="${singbox_sync_cmd} --relay-id ${RELAY_ID}"
    openvpn_sync_cmd="${openvpn_sync_cmd} --relay-id ${RELAY_ID}"
    ipsec_sync_cmd="${ipsec_sync_cmd} --relay-id ${RELAY_ID}"
  fi
  if connector_is_full_tunnel_singbox_protocol "$protocol_id"; then
    singbox_sync_cmd="${singbox_sync_cmd} --protocol ${protocol_id}"
  fi

  cat > "$PANEL_ENV_FILE" <<EOF
NODE_ENV=production
HOSTNAME=127.0.0.1
PORT=${intp}
SESSION_SECRET=$(connector_random_string 48)
NODE_TLS_REJECT_UNAUTHORIZED=0
PANEL_SSL_ENABLED=${PANEL_SSL_ENABLED}
OMNIPANEL_SESSION_SECURE=${PANEL_SSL_ENABLED}
OMNIPANEL_AUTH_USERNAME=${PANEL_USER}
OMNIPANEL_AUTH_PASSWORD=${PANEL_PASSWORD}
OMNIRELAY_ACTIVE_PROTOCOL=${protocol_id}
PANEL_PUBLIC_PORT=${PANEL_PORT}
PANEL_PUBLIC_HOST=${host}
SINGBOX_PUBLIC_PORT=${PUBLIC_PORT}
SINGBOX_RELOAD_COMMAND=${singbox_sync_cmd}
OPENVPN_SYNC_COMMAND=${openvpn_sync_cmd}
IPSEC_L2TP_SYNC_COMMAND=${ipsec_sync_cmd}
SINGBOX_ACCOUNTING_DB=${CONNECTOR_ACCOUNTING_DB}
OMNIRELAY_PROTOCOL_BACKUP_DIR=${panel_backup_dir}
EOF

  if [[ -n "$extra_env_file" && -f "$extra_env_file" ]]; then
    cat "$extra_env_file" >> "$PANEL_ENV_FILE"
  fi

  cat > "/etc/systemd/system/${PANEL_SERVICE}.service" <<EOF
[Unit]
Description=OmniRelay OmniPanel
After=network.target ${CONNECTOR_SERVICE}.service
Wants=${CONNECTOR_SERVICE}.service
[Service]
Type=simple
User=${PANEL_USER_ACCOUNT}
Group=${PANEL_USER_ACCOUNT}
WorkingDirectory=${dir}
EnvironmentFile=${PANEL_ENV_FILE}
ExecStart=${node_bin} server.js
Restart=always
RestartSec=3
[Install]
WantedBy=multi-user.target
EOF

  cat > "$PANEL_SUDOERS_FILE" <<EOF
${PANEL_USER_ACCOUNT} ALL=(root) NOPASSWD:${GATEWAYCTL_PATH} sync-clients, ${GATEWAYCTL_PATH} sync-clients *
EOF
  chmod 0440 "$PANEL_SUDOERS_FILE"
  ln -sfn "$dir" "$PANEL_CURRENT_DIR"
  chown -R "${PANEL_USER_ACCOUNT}:${PANEL_USER_ACCOUNT}" "$PANEL_APP_DIR"
  connector_fix_accounting_permissions
  systemctl daemon-reload
  systemctl enable "$PANEL_SERVICE" >/dev/null 2>&1 || true
  systemctl restart "$PANEL_SERVICE"
  connector_wait_omnipanel_ready
  connector_load_panel_common
  omnipanel_configure_nginx_proxy "$PANEL_PORT" "$intp" "$GATEWAY_ROOT_DIR" "$PANEL_DOMAIN" "$PANEL_DOMAIN_ONLY" "$PANEL_SSL_ENABLED" "$PANEL_SSL_MODE" "$PANEL_CERT_FILE" "$PANEL_KEY_FILE" "$(hostname -I 2>/dev/null | awk '{print $1}')" "$VPS_IP" "$PANEL_NGINX_SITE_NAME"
}

connector_wait_omnipanel_ready(){
  local p i code service_state tail_logs
  p="$(connector_panel_internal_port)"
  [[ "$p" =~ ^[0-9]+$ ]] || die "OmniPanel internal port missing"
  for i in $(seq 1 90); do
    code="$(curl --noproxy '*' --silent --output /dev/null --write-out '%{http_code}' --max-time 4 "http://127.0.0.1:${p}/" 2>/dev/null || true)"
    case "$code" in
      200|301|302|307|308|401|403|404) return 0 ;;
    esac
    if (( i % 10 == 0 )); then
      service_state="$(connector_service_state "$PANEL_SERVICE")"
      log "Waiting OmniPanel on 127.0.0.1:${p} (attempt ${i}/90, http=${code:-none}, service=${service_state})"
    fi
    sleep 1
  done
  service_state="$(connector_service_state "$PANEL_SERVICE")"
  tail_logs="$(journalctl -u "$PANEL_SERVICE" -n 20 --no-pager 2>/dev/null | tail -n 10 | tr '\n' '|' | sed 's/|$//')"
  die "OmniPanel not ready on 127.0.0.1:${p} (service=${service_state}, lastLogs=${tail_logs:-none})"
}

connector_start_services(){
  systemctl enable --now "$CONNECTOR_SERVICE" "$PANEL_SERVICE" nginx >/dev/null 2>&1 || true
}

connector_stop_services(){
  systemctl stop "$PANEL_SERVICE" "$CONNECTOR_SERVICE" >/dev/null 2>&1 || true
  [[ -z "${RELAY_ID:-}" ]] && systemctl stop nginx >/dev/null 2>&1 || true
}

connector_uninstall_runtime(){
  connector_uninstall_accounting_runtime
  connector_uninstall_clock_sync_runtime
  systemctl disable --now "$CONNECTOR_SERVICE" "$PANEL_SERVICE" >/dev/null 2>&1 || true
  rm -f "/etc/systemd/system/${CONNECTOR_SERVICE}.service" "/etc/systemd/system/${PANEL_SERVICE}.service" "$PANEL_SUDOERS_FILE" "$GATEWAYCTL_PATH"
  if [[ -z "${RELAY_ID:-}" && ! -d /etc/omnirelay/relays ]]; then
    rm -f "$CONNECTOR_COMMON_INSTALLED" /usr/local/lib/omnirelay/bootstrap-common.sh /usr/local/lib/omnirelay/omnipanel-common.sh
    connector_remove_dnsmasq_global_config
  fi
  rm -f "/etc/nginx/sites-enabled/${PANEL_NGINX_SITE_NAME}.conf" "/etc/nginx/sites-available/${PANEL_NGINX_SITE_NAME}.conf"
  rm -rf "$GATEWAY_ROOT_DIR" "$PANEL_APP_DIR"
  systemctl daemon-reload || true
}

connector_install_gatewayctl(){
  local script_source="$1"
  local dst="$GATEWAYCTL_PATH"
  local src_real dst_real
  [[ -f "$script_source" ]] || die "gatewayctl source script not found: $script_source"
  src_real="$(readlink -f "$script_source" 2>/dev/null || printf '%s' "$script_source")"
  dst_real="$(readlink -f "$dst" 2>/dev/null || printf '%s' "$dst")"
  if [[ "$src_real" == "$dst_real" ]]; then
    chmod 0755 "$dst" >/dev/null 2>&1 || true
    return 0
  fi
  install -m 0755 "$script_source" "$dst"
}

connector_validate_common_args(){
  PANEL_DOMAIN_ONLY="$(connector_normalize_bool "$PANEL_DOMAIN_ONLY")"
  PANEL_SSL_ENABLED="$(connector_normalize_bool "$PANEL_SSL_ENABLED")"
  connector_validate_port "$PUBLIC_PORT" "--public-port"
  connector_validate_port "$PANEL_PORT" "--panel-port"
  connector_validate_port "$BACKEND_PORT" "--backend-port"
  connector_validate_port "$SSH_PORT" "--ssh-port"
  connector_validate_port "$BOOTSTRAP_SOCKS_PORT" "--bootstrap-socks-port"
  connector_ensure_bootstrap_common
  BOOTSTRAP_MODE="$(omnirelay_bootstrap_normalize_mode "$BOOTSTRAP_MODE" || true)"
  [[ -n "$BOOTSTRAP_MODE" ]] || die "Invalid --bootstrap-mode"
  if [[ -n "${DNS_MODE:-}" ]]; then
    die "DNS mode selection is removed; DoH-only is enforced"
  fi
  if [[ -n "${DNS_UDP_ONLY:-}" ]]; then
    die "UDP DNS toggle is removed; DoH-only is enforced"
  fi
  [[ "$PUBLIC_PORT" != "$PANEL_PORT" ]] || die "--public-port and --panel-port must differ"
}
