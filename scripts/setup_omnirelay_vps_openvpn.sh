#!/usr/bin/env bash
set -Eeuo pipefail
IFS=$'\n\t'
umask 027

SCRIPT_NAME="$(basename "$0")"
COMMAND="install"

PUBLIC_PORT=443
PANEL_PORT=2054
BACKEND_PORT=15000
SSH_PORT=22
BOOTSTRAP_SOCKS_PORT=16080
PROXY_CHECK_URL="https://deb.debian.org/"
VPS_IP=""
DNS_MODE="hybrid"
DOH_ENDPOINTS="https://1.1.1.1/dns-query,https://8.8.8.8/dns-query"
DNS_UDP_ONLY="true"
OPENVPN_NETWORK="10.29.0.0/24"
OPENVPN_CLIENT_DNS="1.1.1.1,8.8.8.8"
PANEL_USER=""
PANEL_PASSWORD=""
PANEL_BASE_PATH=""
TUNNEL_USER=""
TUNNEL_AUTH="host_key"

METADATA_DIR="/etc/omnirelay/gateway"
METADATA_FILE="${METADATA_DIR}/metadata.json"
DNS_PROFILE_FILE="${METADATA_DIR}/dns_profile.json"
OMNIPANEL_ENV_FILE="${METADATA_DIR}/omnipanel.env"
OMNIPANEL_SERVICE="omnirelay-omnipanel"
OMNIPANEL_APP_DIR="/opt/omnirelay/omni-gateway"
PANEL_AUTH_FILE="${OMNIPANEL_APP_DIR}/panel-auth.json"
APT_PROXY_FILE="/etc/apt/apt.conf.d/99-omnirelay-socks"
OPENVPN_SYNC_COMMAND="/usr/bin/sudo -n /usr/local/sbin/omnirelay-gatewayctl sync-clients"

OPENVPN_DIR="${METADATA_DIR}/openvpn"
OPENVPN_EASYRSA_DIR="${OPENVPN_DIR}/easy-rsa"
OPENVPN_PKI_DIR="${OPENVPN_EASYRSA_DIR}/pki"
OPENVPN_TLS_CRYPT_KEY="${OPENVPN_DIR}/ta.key"
OPENVPN_SERVER_CONFIG="${OPENVPN_DIR}/server.conf"
OPENVPN_AUTH_FILE="${OPENVPN_DIR}/users.auth"
OPENVPN_VERIFY_SCRIPT="${OPENVPN_DIR}/auth-verify.sh"
OPENVPN_IPTABLES_UP="${OPENVPN_DIR}/iptables-up.sh"
OPENVPN_IPTABLES_DOWN="${OPENVPN_DIR}/iptables-down.sh"
OPENVPN_CLIENTS_FILE="${OMNIPANEL_APP_DIR}/openvpn_clients.json"
OPENVPN_EXPORT_DIR="${OMNIPANEL_APP_DIR}/openvpn-exports"
OPENVPN_SERVICE="omnirelay-openvpn"
OPENVPN_REDSOCKS_SERVICE="omnirelay-redsocks"
OPENVPN_REDSOCKS_CONFIG="${OPENVPN_DIR}/redsocks.conf"
OPENVPN_REDSOCKS_LOCAL_PORT=12345
OPENVPN_MANAGEMENT_PORT=17505
OPENVPN_STATUS_FILE="/var/log/openvpn/omnirelay-status.log"
OPENVPN_ACCOUNTING_DB="${OPENVPN_DIR}/accounting.db"
OPENVPN_ACCOUNTING_SCRIPT="${OPENVPN_DIR}/accounting-loop.sh"
OPENVPN_ACCOUNTING_HEARTBEAT="${OPENVPN_DIR}/accounting-heartbeat.json"
OPENVPN_ACCOUNTING_SERVICE="omnirelay-openvpn-accounting"
OPENVPN_ACCOUNTING_TIMER="omnirelay-openvpn-accounting.timer"

OMNIPANEL_INTERNAL_PORT=0
STATUS_JSON=0
HEALTH_JSON=0
OPENVPN_NET_ADDR=""
OPENVPN_NETMASK=""

log(){ printf '[%s] %s\n' "$(date -u +'%Y-%m-%dT%H:%M:%SZ')" "$*"; }
die(){ printf '[%s] ERROR: %s\n' "$(date -u +'%Y-%m-%dT%H:%M:%SZ')" "$*" >&2; exit 1; }
progress(){ local pct="$1"; shift; printf 'OMNIRELAY_PROGRESS:%s:%s\n' "$pct" "$*"; }
require_root(){ (( EUID == 0 )) || die "This command requires root."; }
validate_port(){ [[ "$1" =~ ^[0-9]+$ ]] || die "$2 must be integer"; (( $1>=1 && $1<=65535 )) || die "$2 out of range"; }
random_string(){ LC_ALL=C tr -dc 'a-zA-Z0-9' </dev/urandom | head -c "$1" || true; }
random_base64(){ openssl rand -base64 "$1" | tr -d '\r\n'; }
check_listener(){ ss -lnt "( sport = :$1 )" 2>/dev/null | awk 'NR>1{print}' | grep -q . && echo true || echo false; }
choose_port(){ for _ in $(seq 1 200); do p=$((RANDOM%30000+22000)); [[ "$(check_listener "$p")" == "false" ]] && echo "$p" && return 0; done; die "cannot allocate random port"; }
ensure_meta(){ install -d -m 0755 "$METADATA_DIR"; }

usage(){
  cat <<EOF
Usage: sudo ./${SCRIPT_NAME} <install|uninstall|start|stop|get-protocol|status|health|dns-apply|dns-status|dns-repair|sync-clients> [options]
--public-port <port>
--panel-port <port>
--backend-port <port>
--ssh-port <port>
--bootstrap-socks-port <port>
--proxy-check-url <url>
--openvpn-network <cidr>
--openvpn-client-dns <csv>
--dns-mode <hybrid|doh|udp>
--doh-endpoints <csv>
--dns-udp-only <true|false>
--vps-ip <ip-or-host>
--tunnel-user <name>
--tunnel-auth <host_key|password>
--panel-user <name>
--panel-password <password>
--panel-base-path <path>
--json
EOF
}

get_protocol_cmd(){
  echo "openvpn_tcp_relay"
}

configure_proxy(){
  local proxy_url
  proxy_url="socks5h://127.0.0.1:${BOOTSTRAP_SOCKS_PORT}"
  export ALL_PROXY="$proxy_url"
  export HTTPS_PROXY="$proxy_url"
  export HTTP_PROXY="$proxy_url"
  export NO_PROXY="127.0.0.1,localhost"
}

clear_proxy(){ unset ALL_PROXY HTTPS_PROXY HTTP_PROXY NO_PROXY || true; }

configure_apt_proxy(){
  cat > "$APT_PROXY_FILE" <<EOF
Acquire::http::Proxy "socks5h://127.0.0.1:${BOOTSTRAP_SOCKS_PORT}";
Acquire::https::Proxy "socks5h://127.0.0.1:${BOOTSTRAP_SOCKS_PORT}";
EOF
}

resolve_node_bin(){
  if command -v node >/dev/null 2>&1; then command -v node; return 0; fi
  if command -v nodejs >/dev/null 2>&1; then command -v nodejs; return 0; fi
  return 1
}

detect_node_major(){
  local bin
  bin="$(resolve_node_bin 2>/dev/null || true)"
  [[ -n "$bin" ]] || { echo 0; return 0; }
  "$bin" -p "Number(process.versions.node.split('.')[0])" 2>/dev/null || echo 0
}

sync_time_via_bootstrap_socks(){
  local date_header remote_epoch now_epoch delta abs_delta was_ntp
  was_ntp=""
  configure_proxy
  date_header="$(curl --silent --show-error --insecure --max-time 20 --connect-timeout 10 --retry 0 --socks5-hostname "127.0.0.1:${BOOTSTRAP_SOCKS_PORT}" -I "$PROXY_CHECK_URL" 2>/dev/null | tr -d '\r' | awk 'tolower($1)=="date:"{$1="";sub(/^ /,"");print;exit}')"
  [[ -n "$date_header" ]] || return 1
  remote_epoch="$(date -u -d "$date_header" +%s 2>/dev/null || true)"
  [[ -n "$remote_epoch" ]] || return 1
  now_epoch="$(date -u +%s 2>/dev/null || echo 0)"
  delta=$(( remote_epoch - now_epoch ))
  abs_delta=$delta
  (( abs_delta < 0 )) && abs_delta=$(( -abs_delta ))
  if (( abs_delta <= 5 )); then
    log "Clock skew via SOCKS is ${abs_delta}s; no clock adjustment needed."
    return 0
  fi
  if command -v timedatectl >/dev/null 2>&1; then
    was_ntp="$(timedatectl show -p NTP --value 2>/dev/null || true)"
    [[ "$was_ntp" == "yes" ]] && timedatectl set-ntp false >/dev/null 2>&1 || true
  fi
  date -u -s "@${remote_epoch}" >/dev/null 2>&1 || return 1
  command -v hwclock >/dev/null 2>&1 && hwclock --systohc >/dev/null 2>&1 || true
  [[ "$was_ntp" == "yes" ]] && timedatectl set-ntp true >/dev/null 2>&1 || true
  log "Adjusted system clock by ${delta}s via SOCKS-backed HTTPS date (${date_header})."
  return 0
}

install_packages(){
  progress 12 "Installing required packages"
  configure_apt_proxy
  configure_proxy
  progress 14 "Syncing VPS clock over SOCKS bootstrap (if needed)"
  sync_time_via_bootstrap_socks || log "Clock sync over SOCKS skipped/failed; continuing."
  apt-get -o Acquire::Retries=3 -o Acquire::http::Timeout=20 -o Acquire::https::Timeout=20 update -y
  DEBIAN_FRONTEND=noninteractive apt-get install -y \
    ca-certificates curl jq tar gzip openssl python3 \
    openvpn easy-rsa iptables redsocks nginx nodejs sqlite3 socat
  clear_proxy
}

ensure_nodejs_runtime(){
  local major
  major="$(detect_node_major)"
  if (( major >= 18 )); then
    return 0
  fi
  progress 18 "Upgrading Node.js runtime to v20"
  configure_proxy
  curl -fSL "https://deb.nodesource.com/setup_20.x" -o /tmp/omnirelay-nodesource-setup.sh
  bash /tmp/omnirelay-nodesource-setup.sh
  DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends nodejs
  clear_proxy
  major="$(detect_node_major)"
  (( major >= 18 )) || die "Node.js 18+ is required for OmniPanel."
}

parse_openvpn_network(){
  local parsed
  parsed="$(python3 - "$OPENVPN_NETWORK" <<'PY'
import ipaddress, sys
raw = (sys.argv[1] or "").strip()
if not raw:
    raise SystemExit(1)
net = ipaddress.ip_network(raw, strict=False)
if net.version != 4:
    raise SystemExit(2)
print(f"{net.network_address} {net.netmask}")
PY
)" || die "Invalid --openvpn-network value: ${OPENVPN_NETWORK}"
  OPENVPN_NET_ADDR="${parsed%% *}"
  OPENVPN_NETMASK="${parsed##* }"
}

ensure_clients_seed_file(){
  install -d -m 0755 "$(dirname "$OPENVPN_CLIENTS_FILE")" "$OPENVPN_EXPORT_DIR"
  if [[ ! -f "$OPENVPN_CLIENTS_FILE" ]]; then
    jq -n --arg id "$(random_string 16)" --arg p "$(random_string 24)" '[{id:$id,email:"omni-client@local",enable:true,username:"ovpn_client",password:$p,totalGB:0,expiryTime:0}]' > "$OPENVPN_CLIENTS_FILE"
  fi
}

init_openvpn_accounting_db(){
  install -d -m 0755 "$OPENVPN_DIR"
  sqlite3 "$OPENVPN_ACCOUNTING_DB" <<'SQL'
PRAGMA journal_mode=WAL;
PRAGMA synchronous=NORMAL;
CREATE TABLE IF NOT EXISTS clients (
  client_id TEXT PRIMARY KEY,
  username TEXT NOT NULL UNIQUE,
  enabled INTEGER NOT NULL DEFAULT 1,
  total_bytes_limit INTEGER NOT NULL DEFAULT 0,
  expiry_unix_ms INTEGER NOT NULL DEFAULT 0,
  created_at INTEGER NOT NULL,
  updated_at INTEGER NOT NULL
);
CREATE TABLE IF NOT EXISTS usage_totals (
  client_id TEXT PRIMARY KEY,
  used_bytes INTEGER NOT NULL DEFAULT 0,
  updated_at INTEGER NOT NULL
);
CREATE TABLE IF NOT EXISTS session_counters (
  session_key TEXT PRIMARY KEY,
  client_id TEXT NOT NULL,
  last_rx INTEGER NOT NULL DEFAULT 0,
  last_tx INTEGER NOT NULL DEFAULT 0,
  last_seen_at INTEGER NOT NULL
);
SQL
  chmod 0640 "$OPENVPN_ACCOUNTING_DB"
}

setup_openvpn_pki(){
  progress 24 "Preparing OpenVPN PKI"
  install -d -m 0755 "$OPENVPN_DIR"
  if [[ ! -d "$OPENVPN_EASYRSA_DIR" ]]; then
    if command -v make-cadir >/dev/null 2>&1; then
      make-cadir "$OPENVPN_EASYRSA_DIR" >/dev/null 2>&1 || true
    fi
  fi
  if [[ ! -f "${OPENVPN_EASYRSA_DIR}/easyrsa" ]]; then
    install -d -m 0755 "$OPENVPN_EASYRSA_DIR"
    cp -a /usr/share/easy-rsa/. "$OPENVPN_EASYRSA_DIR/"
  fi
  local easyrsa
  easyrsa="${OPENVPN_EASYRSA_DIR}/easyrsa"
  chmod +x "$easyrsa"
  if [[ ! -f "${OPENVPN_PKI_DIR}/ca.crt" ]]; then
    ( cd "$OPENVPN_EASYRSA_DIR" && EASYRSA_BATCH=1 EASYRSA_REQ_CN="OmniRelay-CA" "$easyrsa" init-pki && EASYRSA_BATCH=1 EASYRSA_REQ_CN="OmniRelay-CA" "$easyrsa" build-ca nopass )
  fi
  if [[ ! -f "${OPENVPN_PKI_DIR}/issued/server.crt" || ! -f "${OPENVPN_PKI_DIR}/private/server.key" ]]; then
    ( cd "$OPENVPN_EASYRSA_DIR" && EASYRSA_BATCH=1 "$easyrsa" build-server-full server nopass )
  fi
  if [[ ! -f "${OPENVPN_PKI_DIR}/dh.pem" ]]; then
    ( cd "$OPENVPN_EASYRSA_DIR" && EASYRSA_BATCH=1 "$easyrsa" gen-dh )
  fi
  if [[ ! -f "${OPENVPN_PKI_DIR}/crl.pem" ]]; then
    ( cd "$OPENVPN_EASYRSA_DIR" && EASYRSA_BATCH=1 "$easyrsa" gen-crl )
  fi
  if [[ ! -f "$OPENVPN_TLS_CRYPT_KEY" ]]; then
    openvpn --genkey secret "$OPENVPN_TLS_CRYPT_KEY"
  fi
}

write_openvpn_auth_scripts(){
  cat > "$OPENVPN_VERIFY_SCRIPT" <<EOF
#!/usr/bin/env bash
set -Eeuo pipefail
AUTH_DB="${OPENVPN_AUTH_FILE}"
ACCOUNTING_DB="${OPENVPN_ACCOUNTING_DB}"
INPUT_FILE="\${1:-}"
[[ -n "\$INPUT_FILE" && -f "\$INPUT_FILE" && -f "\$AUTH_DB" && -f "\$ACCOUNTING_DB" ]] || exit 1
USERNAME="\$(sed -n '1p' "\$INPUT_FILE" | tr -d '\r\n')"
PASSWORD="\$(sed -n '2p' "\$INPUT_FILE" | tr -d '\r\n')"
[[ -n "\$USERNAME" && -n "\$PASSWORD" ]] || exit 1
HASH="\$(printf '%s' "\$PASSWORD" | sha256sum | awk '{print \$1}')"
awk -F: -v user="\$USERNAME" -v hash="\$HASH" '\$1==user && \$2==hash {ok=1} END{exit(ok?0:1)}' "\$AUTH_DB"
escape_sql_literal(){ printf '%s' "\$1" | sed "s/'/''/g"; }
SQL_USER="\$(escape_sql_literal "\$USERNAME")"
POLICY_ROW="\$(sqlite3 -csv -noheader "\$ACCOUNTING_DB" "SELECT c.enabled, c.total_bytes_limit, c.expiry_unix_ms, COALESCE(u.used_bytes, 0) FROM clients c LEFT JOIN usage_totals u ON u.client_id = c.client_id WHERE c.username = '\${SQL_USER}' LIMIT 1;" 2>/dev/null || true)"
[[ -n "\$POLICY_ROW" ]] || exit 1
IFS=',' read -r ENABLED TOTAL_BYTES_LIMIT EXPIRY_UNIX_MS USED_BYTES <<<"\$POLICY_ROW"
ENABLED="\${ENABLED:-0}"
TOTAL_BYTES_LIMIT="\${TOTAL_BYTES_LIMIT:-0}"
EXPIRY_UNIX_MS="\${EXPIRY_UNIX_MS:-0}"
USED_BYTES="\${USED_BYTES:-0}"
NOW_MS=\$(( \$(date +%s) * 1000 ))
[[ "\$ENABLED" == "1" ]] || exit 1
if [[ "\$EXPIRY_UNIX_MS" =~ ^[0-9]+$ ]] && (( EXPIRY_UNIX_MS > 0 )) && (( NOW_MS >= EXPIRY_UNIX_MS )); then
  exit 1
fi
if [[ "\$TOTAL_BYTES_LIMIT" =~ ^[0-9]+$ ]] && (( TOTAL_BYTES_LIMIT > 0 )); then
  [[ "\$USED_BYTES" =~ ^[0-9]+$ ]] || USED_BYTES=0
  (( USED_BYTES < TOTAL_BYTES_LIMIT )) || exit 1
fi
EOF
  chmod 0755 "$OPENVPN_VERIFY_SCRIPT"
  cat > "$OPENVPN_IPTABLES_UP" <<EOF
#!/usr/bin/env bash
set -Eeuo pipefail
IPT="\$(command -v iptables || true)"
[[ -n "\$IPT" ]] || exit 0
DEV="\${dev:-tun0}"
CHAIN="OMNIRELAY_OVPN"
\$IPT -t nat -N "\$CHAIN" 2>/dev/null || true
\$IPT -t nat -F "\$CHAIN"
\$IPT -t nat -A "\$CHAIN" -d 127.0.0.0/8 -j RETURN
\$IPT -t nat -A "\$CHAIN" -p tcp -j REDIRECT --to-ports "${OPENVPN_REDSOCKS_LOCAL_PORT}"
\$IPT -t nat -C PREROUTING -i "\$DEV" -p tcp -j "\$CHAIN" 2>/dev/null || \$IPT -t nat -A PREROUTING -i "\$DEV" -p tcp -j "\$CHAIN"
EOF
  chmod 0755 "$OPENVPN_IPTABLES_UP"
  cat > "$OPENVPN_IPTABLES_DOWN" <<EOF
#!/usr/bin/env bash
set -Eeuo pipefail
IPT="\$(command -v iptables || true)"
[[ -n "\$IPT" ]] || exit 0
DEV="\${dev:-tun0}"
CHAIN="OMNIRELAY_OVPN"
\$IPT -t nat -D PREROUTING -i "\$DEV" -p tcp -j "\$CHAIN" 2>/dev/null || true
\$IPT -t nat -F "\$CHAIN" 2>/dev/null || true
EOF
  chmod 0755 "$OPENVPN_IPTABLES_DOWN"
}

write_openvpn_accounting_script(){
  cat > "$OPENVPN_ACCOUNTING_SCRIPT" <<EOF
#!/usr/bin/env bash
set -Eeuo pipefail
DB="${OPENVPN_ACCOUNTING_DB}"
STATUS_FILE="${OPENVPN_STATUS_FILE}"
HEARTBEAT_FILE="${OPENVPN_ACCOUNTING_HEARTBEAT}"
MGMT_PORT="${OPENVPN_MANAGEMENT_PORT}"
STALE_AFTER_SEC=600

escape_sql_literal(){ printf '%s' "\$1" | sed "s/'/''/g"; }

write_heartbeat(){
  local ok_flag="\$1" error_msg="\$2" now_epoch
  now_epoch="\$(date +%s)"
  jq -n --argjson ok "\$ok_flag" --arg err "\$error_msg" --argjson now "\$now_epoch" '{ok:\$ok,error:\$err,runAtEpoch:\$now,runAtUtc:(\$now|todate)}' > "\${HEARTBEAT_FILE}.tmp"
  mv -f "\${HEARTBEAT_FILE}.tmp" "\$HEARTBEAT_FILE"
  chmod 0640 "\$HEARTBEAT_FILE" || true
}

init_db(){
  sqlite3 "\$DB" <<'SQL'
PRAGMA journal_mode=WAL;
PRAGMA synchronous=NORMAL;
CREATE TABLE IF NOT EXISTS clients (
  client_id TEXT PRIMARY KEY,
  username TEXT NOT NULL UNIQUE,
  enabled INTEGER NOT NULL DEFAULT 1,
  total_bytes_limit INTEGER NOT NULL DEFAULT 0,
  expiry_unix_ms INTEGER NOT NULL DEFAULT 0,
  created_at INTEGER NOT NULL,
  updated_at INTEGER NOT NULL
);
CREATE TABLE IF NOT EXISTS usage_totals (
  client_id TEXT PRIMARY KEY,
  used_bytes INTEGER NOT NULL DEFAULT 0,
  updated_at INTEGER NOT NULL
);
CREATE TABLE IF NOT EXISTS session_counters (
  session_key TEXT PRIMARY KEY,
  client_id TEXT NOT NULL,
  last_rx INTEGER NOT NULL DEFAULT 0,
  last_tx INTEGER NOT NULL DEFAULT 0,
  last_seen_at INTEGER NOT NULL
);
SQL
}

kill_user(){
  local username="\$1"
  [[ -n "\$username" ]] || return 0
  printf 'kill %s\nquit\n' "\$username" | timeout 5 socat - "TCP:127.0.0.1:\${MGMT_PORT},connect-timeout=3" >/dev/null 2>&1 || true
}

main(){
  init_db
  declare -A delta_by_client=()
  declare -A active_users=()
  local now_sec now_ms cutoff_sec
  now_sec="\$(date +%s)"
  now_ms=\$(( now_sec * 1000 ))
  cutoff_sec=\$(( now_sec - STALE_AFTER_SEC ))

  if [[ -f "\$STATUS_FILE" ]]; then
    while IFS= read -r line; do
      [[ "\$line" == CLIENT_LIST,* ]] || continue
      IFS=',' read -r _ common_name real_addr virtual_addr _ bytes_rx bytes_tx _ _ username peer_id _ _ <<< "\$line"
      [[ "\$bytes_rx" =~ ^[0-9]+$ ]] || bytes_rx=0
      [[ "\$bytes_tx" =~ ^[0-9]+$ ]] || bytes_tx=0
      username="\${username:-}"
      if [[ -z "\$username" || "\$username" == "UNDEF" ]]; then
        username="\${common_name:-}"
      fi
      [[ -n "\$username" ]] || continue
      active_users["\$username"]=1

      local esc_username db_client_id session_key esc_session prev_row prev_rx prev_tx delta_rx delta_tx delta_total
      esc_username="\$(escape_sql_literal "\$username")"
      db_client_id="\$(sqlite3 -noheader "\$DB" "SELECT client_id FROM clients WHERE username='\$esc_username' LIMIT 1;" 2>/dev/null || true)"
      [[ -n "\$db_client_id" ]] || continue
      session_key="\${db_client_id}|\${real_addr}|\${virtual_addr}|\${peer_id}"
      esc_session="\$(escape_sql_literal "\$session_key")"
      prev_row="\$(sqlite3 -csv -noheader "\$DB" "SELECT last_rx,last_tx FROM session_counters WHERE session_key='\$esc_session' LIMIT 1;" 2>/dev/null || true)"
      prev_rx=0; prev_tx=0
      if [[ -n "\$prev_row" ]]; then
        IFS=',' read -r prev_rx prev_tx <<< "\$prev_row"
        [[ "\$prev_rx" =~ ^[0-9]+$ ]] || prev_rx=0
        [[ "\$prev_tx" =~ ^[0-9]+$ ]] || prev_tx=0
      fi
      delta_rx=\$(( bytes_rx - prev_rx ))
      delta_tx=\$(( bytes_tx - prev_tx ))
      (( delta_rx < 0 )) && delta_rx=0
      (( delta_tx < 0 )) && delta_tx=0
      delta_total=\$(( delta_rx + delta_tx ))
      delta_by_client["\$db_client_id"]=\$(( \${delta_by_client["\$db_client_id"]:-0} + delta_total ))
      local esc_client_id
      esc_client_id="\$(escape_sql_literal "\$db_client_id")"
      sqlite3 "\$DB" "INSERT INTO session_counters(session_key,client_id,last_rx,last_tx,last_seen_at) VALUES('\$esc_session','\$esc_client_id',\$bytes_rx,\$bytes_tx,\$now_sec) ON CONFLICT(session_key) DO UPDATE SET last_rx=excluded.last_rx,last_tx=excluded.last_tx,last_seen_at=excluded.last_seen_at;" >/dev/null 2>&1 || true
    done < "\$STATUS_FILE"
  fi

  local sql_tmp
  sql_tmp="\$(mktemp)"
  {
    echo "PRAGMA busy_timeout=5000;"
    echo "BEGIN IMMEDIATE;"
    for client_id in "\${!delta_by_client[@]}"; do
      local delta esc_client
      delta="\${delta_by_client["\$client_id"]}"
      (( delta > 0 )) || continue
      esc_client="\$(escape_sql_literal "\$client_id")"
      echo "INSERT INTO usage_totals(client_id,used_bytes,updated_at) VALUES('\$esc_client',0,\$now_sec) ON CONFLICT(client_id) DO NOTHING;"
      echo "UPDATE usage_totals SET used_bytes = used_bytes + \$delta, updated_at = \$now_sec WHERE client_id='\$esc_client';"
    done
    echo "DELETE FROM session_counters WHERE last_seen_at < \$cutoff_sec;"
    echo "COMMIT;"
  } > "\$sql_tmp"
  sqlite3 "\$DB" < "\$sql_tmp"
  rm -f "\$sql_tmp"

  for username in "\${!active_users[@]}"; do
    local esc_username policy enabled total_limit expiry_ms used_bytes
    esc_username="\$(escape_sql_literal "\$username")"
    policy="\$(sqlite3 -csv -noheader "\$DB" "SELECT c.enabled,c.total_bytes_limit,c.expiry_unix_ms,COALESCE(u.used_bytes,0) FROM clients c LEFT JOIN usage_totals u ON u.client_id=c.client_id WHERE c.username='\$esc_username' LIMIT 1;" 2>/dev/null || true)"
    [[ -n "\$policy" ]] || continue
    IFS=',' read -r enabled total_limit expiry_ms used_bytes <<< "\$policy"
    [[ "\$enabled" =~ ^[0-9]+$ ]] || enabled=0
    [[ "\$total_limit" =~ ^[0-9]+$ ]] || total_limit=0
    [[ "\$expiry_ms" =~ ^[0-9]+$ ]] || expiry_ms=0
    [[ "\$used_bytes" =~ ^[0-9]+$ ]] || used_bytes=0
    if (( enabled == 0 )); then
      kill_user "\$username"
      continue
    fi
    if (( expiry_ms > 0 && now_ms >= expiry_ms )); then
      kill_user "\$username"
      continue
    fi
    if (( total_limit > 0 && used_bytes >= total_limit )); then
      kill_user "\$username"
    fi
  done

  write_heartbeat true ""
}

if main 2>/tmp/omnirelay-openvpn-accounting.err; then
  exit 0
fi
err_text="\$(tr '\n' ' ' </tmp/omnirelay-openvpn-accounting.err | sed 's/"/\\"/g' | xargs || true)"
rm -f /tmp/omnirelay-openvpn-accounting.err
write_heartbeat false "\${err_text:-accounting script failed}"
exit 1
EOF
  chmod 0750 "$OPENVPN_ACCOUNTING_SCRIPT"
}

build_openvpn_dns_push_lines(){
  local lines="" value
  IFS=',' read -r -a dns_parts <<< "$OPENVPN_CLIENT_DNS"
  for value in "${dns_parts[@]}"; do
    value="$(echo "$value" | xargs || true)"
    [[ -n "$value" ]] || continue
    lines="${lines}push \"dhcp-option DNS ${value}\"\n"
  done
  printf '%b' "$lines"
}

write_openvpn_server_config(){
  progress 30 "Writing OpenVPN server configuration"
  local dns_push
  install -d -m 0755 /var/log/openvpn
  dns_push="$(build_openvpn_dns_push_lines)"
  cat > "$OPENVPN_SERVER_CONFIG" <<EOF
port ${PUBLIC_PORT}
proto tcp-server
dev tun
topology subnet
server ${OPENVPN_NET_ADDR} ${OPENVPN_NETMASK}
push "redirect-gateway def1 bypass-dhcp"
${dns_push}keepalive 10 60
persist-key
persist-tun
user nobody
group nogroup
ca ${OPENVPN_PKI_DIR}/ca.crt
cert ${OPENVPN_PKI_DIR}/issued/server.crt
key ${OPENVPN_PKI_DIR}/private/server.key
dh ${OPENVPN_PKI_DIR}/dh.pem
crl-verify ${OPENVPN_PKI_DIR}/crl.pem
tls-crypt ${OPENVPN_TLS_CRYPT_KEY}
verify-client-cert require
username-as-common-name
auth-user-pass-verify ${OPENVPN_VERIFY_SCRIPT} via-file
script-security 2
up ${OPENVPN_IPTABLES_UP}
down ${OPENVPN_IPTABLES_DOWN}
cipher AES-256-GCM
auth SHA256
data-ciphers AES-256-GCM:AES-128-GCM:CHACHA20-POLY1305
status-version 3
status ${OPENVPN_STATUS_FILE}
management 127.0.0.1 ${OPENVPN_MANAGEMENT_PORT}
verb 3
EOF
}

write_redsocks_config(){
  cat > "$OPENVPN_REDSOCKS_CONFIG" <<EOF
base {
  log_debug = off;
  log_info = on;
  daemon = off;
  redirector = iptables;
}
redsocks {
  local_ip = 127.0.0.1;
  local_port = ${OPENVPN_REDSOCKS_LOCAL_PORT};
  ip = 127.0.0.1;
  port = ${BACKEND_PORT};
  type = socks5;
}
EOF
}

enable_ip_forward(){
  cat > /etc/sysctl.d/99-omnirelay-openvpn.conf <<EOF
net.ipv4.ip_forward=1
EOF
  sysctl -q -w net.ipv4.ip_forward=1 || true
}

setup_runtime_services(){
  progress 38 "Setting up OpenVPN and redsocks services"
  local redsocks_bin
  redsocks_bin="$(command -v redsocks || true)"
  [[ -n "$redsocks_bin" ]] || die "redsocks binary not found."
  cat > "/etc/systemd/system/${OPENVPN_REDSOCKS_SERVICE}.service" <<EOF
[Unit]
Description=OmniRelay redsocks bridge
After=network-online.target
Wants=network-online.target
[Service]
Type=simple
ExecStart=${redsocks_bin} -c ${OPENVPN_REDSOCKS_CONFIG}
Restart=always
RestartSec=2
[Install]
WantedBy=multi-user.target
EOF
  cat > "/etc/systemd/system/${OPENVPN_SERVICE}.service" <<EOF
[Unit]
Description=OmniRelay OpenVPN (TCP)
After=network-online.target ${OPENVPN_REDSOCKS_SERVICE}.service
Wants=network-online.target ${OPENVPN_REDSOCKS_SERVICE}.service
[Service]
Type=simple
ExecStart=/usr/sbin/openvpn --config ${OPENVPN_SERVER_CONFIG}
Restart=always
RestartSec=3
CapabilityBoundingSet=CAP_NET_ADMIN CAP_NET_BIND_SERVICE CAP_NET_RAW
AmbientCapabilities=CAP_NET_ADMIN CAP_NET_BIND_SERVICE CAP_NET_RAW
[Install]
WantedBy=multi-user.target
EOF
  cat > "/etc/systemd/system/${OPENVPN_ACCOUNTING_SERVICE}.service" <<EOF
[Unit]
Description=OmniRelay OpenVPN accounting sync
After=${OPENVPN_SERVICE}.service
Wants=${OPENVPN_SERVICE}.service
[Service]
Type=oneshot
ExecStart=/usr/bin/env bash ${OPENVPN_ACCOUNTING_SCRIPT}
EOF
  cat > "/etc/systemd/system/${OPENVPN_ACCOUNTING_TIMER}" <<EOF
[Unit]
Description=Run OmniRelay OpenVPN accounting sync every 30 seconds
[Timer]
OnBootSec=45s
OnUnitActiveSec=30s
AccuracySec=5s
Unit=${OPENVPN_ACCOUNTING_SERVICE}.service
Persistent=true
[Install]
WantedBy=timers.target
EOF
  systemctl daemon-reload
  systemctl enable --now "$OPENVPN_REDSOCKS_SERVICE"
  systemctl enable --now "$OPENVPN_SERVICE"
  systemctl enable --now "$OPENVPN_ACCOUNTING_TIMER"
  systemctl start "$OPENVPN_ACCOUNTING_SERVICE" || true
}

gen_cert(){
  local cert_dir cert key server
  cert_dir="${METADATA_DIR}/certs"
  cert="${cert_dir}/omnipanel.crt"
  key="${cert_dir}/omnipanel.key"
  mkdir -p "$cert_dir"
  chmod 0700 "$cert_dir"
  server="${VPS_IP:-$(hostname -I 2>/dev/null | awk '{print $1}')}"
  [[ -n "$server" ]] || server="localhost"
  openssl req -x509 -nodes -newkey rsa:2048 -days 825 -keyout "$key" -out "$cert" -subj "/CN=${server}" >/dev/null 2>&1
  chmod 0600 "$key"
  chmod 0644 "$cert"
}

resolve_omnipanel_internal_port(){
  local port
  port="$(awk -F= '/^PORT=/{print $2; exit}' "$OMNIPANEL_ENV_FILE" 2>/dev/null | tr -d '[:space:]' || true)"
  if [[ "$port" =~ ^[0-9]+$ ]] && (( port >= 1 && port <= 65535 )); then
    printf '%s' "$port"
    return 0
  fi
  if [[ "${OMNIPANEL_INTERNAL_PORT:-0}" =~ ^[0-9]+$ ]] && (( OMNIPANEL_INTERNAL_PORT >= 1 && OMNIPANEL_INTERNAL_PORT <= 65535 )); then
    printf '%s' "$OMNIPANEL_INTERNAL_PORT"
    return 0
  fi
  return 1
}

wait_omnipanel_ready(){
  local port code i restarted
  port="$(resolve_omnipanel_internal_port || true)"
  [[ -n "$port" ]] || die "OmniPanel internal port is not set."
  restarted=0
  for i in $(seq 1 30); do
    code="$(curl --noproxy '*' --silent --output /dev/null --write-out '%{http_code}' --max-time 4 "http://127.0.0.1:${port}/" 2>/dev/null || true)"
    case "$code" in
      200|301|302|307|308|401|403) return 0 ;;
    esac
    if (( i == 12 && restarted == 0 )); then
      systemctl restart "$OMNIPANEL_SERVICE" >/dev/null 2>&1 || true
      restarted=1
    fi
    sleep 1
  done
  log "OmniPanel service did not become ready on 127.0.0.1:${port}."
  log "---- omnipanel systemd status ----"
  systemctl --no-pager --full status "$OMNIPANEL_SERVICE" 2>&1 || true
  log "---- omnipanel journal (last 80 lines) ----"
  journalctl -u "$OMNIPANEL_SERVICE" -n 80 --no-pager 2>&1 || true
  die "OmniPanel service failed to start."
}

verify_nginx_panel_proxy(){
  local code i
  for i in $(seq 1 20); do
    code="$(curl --noproxy '*' --silent --insecure --output /dev/null --write-out '%{http_code}' --max-time 6 "https://127.0.0.1:${PANEL_PORT}/" 2>/dev/null || true)"
    case "$code" in
      200|301|302|307|308|401|403) return 0 ;;
    esac
    sleep 1
  done
  die "Nginx HTTPS reverse proxy check failed on 127.0.0.1:${PANEL_PORT}."
}

deploy_panel(){
  local node_bin rel dir
  progress 60 "Deploying OmniPanel artifact"
  install -d -m 0755 "$OMNIPANEL_APP_DIR" "$OMNIPANEL_APP_DIR/releases"
  rel="$(date +%Y%m%d%H%M%S)"
  dir="${OMNIPANEL_APP_DIR}/releases/${rel}"
  mkdir -p "$dir"
  configure_proxy
  curl -fSL "https://omnirelay.net/download/omni-gateway" -o /tmp/omni-gateway.tar.gz
  clear_proxy
  tar -xzf /tmp/omni-gateway.tar.gz -C "$dir"
  if [[ ! -f "$dir/server.js" ]]; then
    nd="$(find "$dir" -mindepth 1 -maxdepth 1 -type d | head -n1 || true)"
    [[ -n "$nd" ]] && cp -a "$nd"/. "$dir"/
  fi
  [[ -f "$dir/server.js" ]] || die "omnipanel artifact missing server.js"
  node_bin="$(resolve_node_bin || true)"
  [[ -n "$node_bin" && -x "$node_bin" ]] || die "Node.js executable not found."

  id -u omnigateway >/dev/null 2>&1 || useradd --system --home "$OMNIPANEL_APP_DIR" --shell /usr/sbin/nologin omnigateway
  OMNIPANEL_INTERNAL_PORT="$(choose_port)"
  [[ -n "$PANEL_USER" ]] || PANEL_USER="omniadmin_$(random_string 6)"
  [[ -n "$PANEL_PASSWORD" ]] || PANEL_PASSWORD="$(random_string 24)"
  jq -n --arg u "$PANEL_USER" --arg p "$PANEL_PASSWORD" '{username:$u,password:$p}' > "$PANEL_AUTH_FILE"

  cat > "$OMNIPANEL_ENV_FILE" <<EOF
NODE_ENV=production
HOSTNAME=127.0.0.1
PORT=${OMNIPANEL_INTERNAL_PORT}
SESSION_SECRET=$(random_string 48)
NODE_TLS_REJECT_UNAUTHORIZED=0
OMNIPANEL_AUTH_FILE=${PANEL_AUTH_FILE}
OMNIPANEL_AUTH_USERNAME=${PANEL_USER}
OMNIPANEL_AUTH_PASSWORD=${PANEL_PASSWORD}
OMNIRELAY_ACTIVE_PROTOCOL=openvpn_tcp_relay
PANEL_PUBLIC_PORT=${PANEL_PORT}
PANEL_PUBLIC_HOST=${VPS_IP:-$(hostname -I 2>/dev/null | awk '{print $1}')}
OPENVPN_PUBLIC_PORT=${PUBLIC_PORT}
OPENVPN_CLIENTS_FILE=${OPENVPN_CLIENTS_FILE}
OPENVPN_EXPORT_DIR=${OPENVPN_EXPORT_DIR}
OPENVPN_ACCOUNTING_DB=${OPENVPN_ACCOUNTING_DB}
OPENVPN_SYNC_COMMAND=${OPENVPN_SYNC_COMMAND}
EOF

  cat > "/etc/systemd/system/${OMNIPANEL_SERVICE}.service" <<EOF
[Unit]
Description=OmniRelay OmniPanel
After=network.target ${OPENVPN_SERVICE}.service
Requires=${OPENVPN_SERVICE}.service
[Service]
Type=simple
User=omnigateway
Group=omnigateway
WorkingDirectory=${dir}
EnvironmentFile=${OMNIPANEL_ENV_FILE}
ExecStart=${node_bin} server.js
Restart=always
RestartSec=3
[Install]
WantedBy=multi-user.target
EOF

  echo "omnigateway ALL=(root) NOPASSWD:/usr/local/sbin/omnirelay-gatewayctl sync-clients" > /etc/sudoers.d/omnigateway-openvpn
  chmod 0440 /etc/sudoers.d/omnigateway-openvpn

  ln -sfn "$dir" "${OMNIPANEL_APP_DIR}/current"
  chown -R omnigateway:omnigateway "$OMNIPANEL_APP_DIR"
  chown -R omnigateway:omnigateway "$OPENVPN_EXPORT_DIR"
  chown omnigateway:omnigateway "$OPENVPN_CLIENTS_FILE" "$PANEL_AUTH_FILE" || true
  chmod 0640 "$OPENVPN_CLIENTS_FILE" "$PANEL_AUTH_FILE" || true
  chown root:omnigateway "$OPENVPN_ACCOUNTING_DB" 2>/dev/null || true
  chmod 0640 "$OPENVPN_ACCOUNTING_DB" 2>/dev/null || true
  chown root:omnigateway "$OPENVPN_ACCOUNTING_HEARTBEAT" 2>/dev/null || true
  chmod 0640 "$OPENVPN_ACCOUNTING_HEARTBEAT" 2>/dev/null || true
  systemctl daemon-reload
  systemctl enable --now "$OMNIPANEL_SERVICE"
}

configure_nginx(){
  local panel_internal_port
  progress 76 "Configuring nginx HTTPS reverse-proxy for OmniPanel"
  panel_internal_port="$(resolve_omnipanel_internal_port || true)"
  [[ -n "$panel_internal_port" ]] || die "Cannot determine OmniPanel internal port for nginx config."
  gen_cert
  cat > /etc/nginx/sites-available/omnirelay-omnipanel.conf <<EOF
server {
    listen ${PANEL_PORT} ssl;
    server_name _;
    ssl_certificate ${METADATA_DIR}/certs/omnipanel.crt;
    ssl_certificate_key ${METADATA_DIR}/certs/omnipanel.key;
    ssl_protocols TLSv1.2 TLSv1.3;
    location / {
        proxy_http_version 1.1;
        proxy_set_header Host \$host;
        proxy_set_header X-Forwarded-For \$proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto \$scheme;
        proxy_set_header Upgrade \$http_upgrade;
        proxy_set_header Connection "upgrade";
        proxy_pass http://127.0.0.1:${panel_internal_port};
    }
}
EOF
  ln -sfn /etc/nginx/sites-available/omnirelay-omnipanel.conf /etc/nginx/sites-enabled/omnirelay-omnipanel.conf
  rm -f /etc/nginx/sites-enabled/default || true
  nginx -t
  systemctl enable --now nginx
  systemctl restart nginx
  verify_nginx_panel_proxy
}

configure_host_firewall(){
  if ! command -v ufw >/dev/null 2>&1; then return 0; fi
  if ! ufw status 2>/dev/null | grep -q "^Status: active"; then return 0; fi
  ufw allow "${PANEL_PORT}/tcp" >/dev/null 2>&1 || true
  ufw allow "${PUBLIC_PORT}/tcp" >/dev/null 2>&1 || true
  ufw allow "${SSH_PORT}/tcp" >/dev/null 2>&1 || true
}

write_metadata(){
  progress 88 "Persisting managed gateway metadata"
  jq -n \
    --arg proto "openvpn_tcp_relay" \
    --arg vps "$VPS_IP" \
    --arg port "$PUBLIC_PORT" \
    --arg panel "$PANEL_PORT" \
    --arg user "$PANEL_USER" \
    --arg network "$OPENVPN_NETWORK" \
    --arg dns "$OPENVPN_CLIENT_DNS" \
    --arg clientsFile "$OPENVPN_CLIENTS_FILE" \
    --arg exportsDir "$OPENVPN_EXPORT_DIR" \
    --arg accountingDb "$OPENVPN_ACCOUNTING_DB" \
    --arg intp "$OMNIPANEL_INTERNAL_PORT" \
    '{
      active_protocol:$proto,
      vps_ip:$vps,
      public_port:($port|tonumber),
      omnipanel_public_port:($panel|tonumber),
      omnipanel_internal_port:($intp|tonumber),
      omnipanel_username:$user,
      openvpn:{network:$network,client_dns:$dns,clients_file:$clientsFile,exports_dir:$exportsDir,accounting_db:$accountingDb},
      created_at_utc:(now|todate)
    }' > "$METADATA_FILE"
  chmod 0600 "$METADATA_FILE"
}

dns_apply(){
  progress 94 "Applying DNS-through-tunnel profile"
  jq -n --arg m "$DNS_MODE" --arg d "$DOH_ENDPOINTS" --argjson u "$( [[ "$DNS_UDP_ONLY" == "true" ]] && echo true || echo false )" '{mode:$m,dohEndpoints:$d,dnsUdpOnly:$u,updatedAtUtc:(now|todate)}' > "$DNS_PROFILE_FILE"
  progress 100 "DNS profile applied"
}

dns_status_json(){
  local cfg rule mode doh udpOnly udp53 path
  if [[ -f "$DNS_PROFILE_FILE" ]]; then
    cfg=true
    rule=true
    mode="$(jq -r '.mode' "$DNS_PROFILE_FILE")"
    doh="$(jq -r '.dohEndpoints' "$DNS_PROFILE_FILE")"
    udpOnly="$(jq -r '.dnsUdpOnly' "$DNS_PROFILE_FILE")"
  else
    cfg=false
    rule=false
    mode=unknown
    doh=""
    udpOnly=false
  fi
  udp53="$(check_listener 53)"
  path=false
  [[ "$cfg" == true && "$rule" == true ]] && path=true
  printf '{"dnsConfigPresent":%s,"dnsRuleActive":%s,"dohReachableViaTunnel":%s,"udp53PathReady":%s,"dnsPathHealthy":%s,"dnsMode":"%s","dnsUdpOnly":%s,"dohEndpoints":"%s"}\n' "$cfg" "$rule" "$cfg" "$udp53" "$path" "$mode" "$udpOnly" "$(printf '%s' "$doh" | sed 's/"/\\"/g')"
}

dns_status(){ dns_status_json; }
dns_repair(){ progress 94 "Repairing DNS profile"; dns_apply; }

write_client_profile(){
  local id="$1" username="$2" password="$3" cert_path="$4" key_path="$5"
  local remote_host profile
  remote_host="$(jq -r '.vps_ip // empty' "$METADATA_FILE" 2>/dev/null || true)"
  if [[ -z "$remote_host" ]]; then
    remote_host="${VPS_IP:-$(hostname -I 2>/dev/null | awk '{print $1}')}"
  fi
  [[ -n "$remote_host" ]] || remote_host="127.0.0.1"
  profile="${OPENVPN_EXPORT_DIR}/${id}.ovpn"
  {
    printf 'client\n'
    printf 'dev tun\n'
    printf 'proto tcp\n'
    printf 'remote %s %s\n' "$remote_host" "$PUBLIC_PORT"
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
    cat "${OPENVPN_PKI_DIR}/ca.crt"
    printf '</ca>\n'
    printf '<cert>\n'
    cat "$cert_path"
    printf '</cert>\n'
    printf '<key>\n'
    cat "$key_path"
    printf '</key>\n'
    printf '<tls-crypt>\n'
    cat "$OPENVPN_TLS_CRYPT_KEY"
    printf '</tls-crypt>\n'
  } > "$profile"
  chmod 0640 "$profile"
}

sync_clients_cmd(){
  require_root
  ensure_clients_seed_file
  init_openvpn_accounting_db
  setup_openvpn_pki
  write_openvpn_auth_scripts
  write_openvpn_accounting_script
  write_openvpn_server_config
  local easyrsa auth_tmp keep_tmp row id email enable username password cn cert_path key_path hash
  local total_gb_raw total_gb_bytes expiry_raw expiry_unix_ms now_sec sql_tmp enable_int
  easyrsa="${OPENVPN_EASYRSA_DIR}/easyrsa"
  auth_tmp="$(mktemp)"
  keep_tmp="$(mktemp)"
  sql_tmp="$(mktemp)"
  now_sec="$(date +%s)"
  : > "$auth_tmp"
  : > "$keep_tmp"
  cat > "$sql_tmp" <<SQL
PRAGMA busy_timeout=5000;
BEGIN IMMEDIATE;
CREATE TEMP TABLE IF NOT EXISTS sync_keep(client_id TEXT PRIMARY KEY);
DELETE FROM sync_keep;
SQL
  while IFS= read -r row; do
    id="$(jq -r '.id // empty' <<<"$row")"
    email="$(jq -r '.email // empty' <<<"$row")"
    enable="$(jq -r '.enable // true' <<<"$row")"
    username="$(jq -r '.username // empty' <<<"$row")"
    password="$(jq -r '.password // empty' <<<"$row")"
    total_gb_raw="$(jq -r '.totalGB // 0' <<<"$row")"
    expiry_raw="$(jq -r '.expiryTime // 0' <<<"$row")"
    [[ -n "$id" && -n "$email" && -n "$username" && -n "$password" ]] || continue
    total_gb_bytes="$(python3 - "$total_gb_raw" <<'PY'
import math, sys
try:
    value = float(sys.argv[1])
except Exception:
    value = 0.0
if not math.isfinite(value) or value <= 0:
    print(0)
else:
    print(int(value * (1024 ** 3)))
PY
)"
    expiry_unix_ms="$(python3 - "$expiry_raw" <<'PY'
import math, sys
try:
    value = float(sys.argv[1])
except Exception:
    value = 0.0
if not math.isfinite(value) or value <= 0:
    print(0)
else:
    print(int(value))
PY
)"
    [[ "$total_gb_bytes" =~ ^[0-9]+$ ]] || total_gb_bytes=0
    [[ "$expiry_unix_ms" =~ ^[0-9]+$ ]] || expiry_unix_ms=0
    enable_int=0
    [[ "$enable" == "true" ]] && enable_int=1
    cn="ovpn-$(printf '%s' "$id" | tr -cd 'a-zA-Z0-9' | cut -c1-40)"
    [[ -n "$cn" ]] || cn="ovpn-$(random_string 12)"
    cert_path="${OPENVPN_PKI_DIR}/issued/${cn}.crt"
    key_path="${OPENVPN_PKI_DIR}/private/${cn}.key"
    if [[ ! -f "$cert_path" || ! -f "$key_path" ]]; then
      ( cd "$OPENVPN_EASYRSA_DIR" && EASYRSA_BATCH=1 "$easyrsa" build-client-full "$cn" nopass )
      cert_path="${OPENVPN_PKI_DIR}/issued/${cn}.crt"
      key_path="${OPENVPN_PKI_DIR}/private/${cn}.key"
    fi
    if [[ "$enable" == "true" ]]; then
      hash="$(printf '%s' "$password" | sha256sum | awk '{print $1}')"
      printf '%s:%s:%s\n' "$username" "$hash" "$cn" >> "$auth_tmp"
    fi
    printf '%s\n' "$id" >> "$keep_tmp"
    printf "INSERT OR IGNORE INTO sync_keep(client_id) VALUES('%s');\n" "$(printf '%s' "$id" | sed "s/'/''/g")" >> "$sql_tmp"
    printf "INSERT INTO clients(client_id,username,enabled,total_bytes_limit,expiry_unix_ms,created_at,updated_at) VALUES('%s','%s',%s,%s,%s,%s,%s) ON CONFLICT(client_id) DO UPDATE SET username=excluded.username,enabled=excluded.enabled,total_bytes_limit=excluded.total_bytes_limit,expiry_unix_ms=excluded.expiry_unix_ms,updated_at=excluded.updated_at;\n" \
      "$(printf '%s' "$id" | sed "s/'/''/g")" \
      "$(printf '%s' "$username" | sed "s/'/''/g")" \
      "$enable_int" "$total_gb_bytes" "$expiry_unix_ms" "$now_sec" "$now_sec" >> "$sql_tmp"
    printf "INSERT OR IGNORE INTO usage_totals(client_id,used_bytes,updated_at) VALUES('%s',0,%s);\n" \
      "$(printf '%s' "$id" | sed "s/'/''/g")" "$now_sec" >> "$sql_tmp"
    printf "UPDATE usage_totals SET updated_at=%s WHERE client_id='%s';\n" \
      "$now_sec" "$(printf '%s' "$id" | sed "s/'/''/g")" >> "$sql_tmp"
    write_client_profile "$id" "$username" "$password" "$cert_path" "$key_path"
  done < <(jq -c '.[]' "$OPENVPN_CLIENTS_FILE" 2>/dev/null || true)
  cat >> "$sql_tmp" <<'SQL'
DELETE FROM clients WHERE client_id NOT IN (SELECT client_id FROM sync_keep);
DELETE FROM usage_totals WHERE client_id NOT IN (SELECT client_id FROM sync_keep);
DELETE FROM session_counters WHERE client_id NOT IN (SELECT client_id FROM sync_keep);
DROP TABLE sync_keep;
COMMIT;
SQL
  sqlite3 "$OPENVPN_ACCOUNTING_DB" < "$sql_tmp"
  rm -f "$sql_tmp"
  install -m 0600 "$auth_tmp" "$OPENVPN_AUTH_FILE"
  rm -f "$auth_tmp"
  shopt -s nullglob
  for profile in "$OPENVPN_EXPORT_DIR"/*.ovpn; do
    local profile_id
    profile_id="$(basename "$profile" .ovpn)"
    if ! grep -Fxq "$profile_id" "$keep_tmp"; then
      rm -f "$profile"
    fi
  done
  shopt -u nullglob
  rm -f "$keep_tmp"
  chown -R omnigateway:omnigateway "$OPENVPN_EXPORT_DIR" "$OPENVPN_CLIENTS_FILE" || true
  chmod 0750 "$OPENVPN_EXPORT_DIR" || true
  chmod 0640 "$OPENVPN_CLIENTS_FILE" || true
  chown root:omnigateway "$OPENVPN_ACCOUNTING_DB" 2>/dev/null || true
  chmod 0640 "$OPENVPN_ACCOUNTING_DB" 2>/dev/null || true
  systemctl daemon-reload
  systemctl restart "$OPENVPN_REDSOCKS_SERVICE"
  if ! systemctl restart "$OPENVPN_SERVICE"; then
    log "---- openvpn systemd status ----"
    systemctl --no-pager --full status "$OPENVPN_SERVICE" 2>&1 || true
    log "---- openvpn journal (last 80 lines) ----"
    journalctl -u "$OPENVPN_SERVICE" -n 80 --no-pager 2>&1 || true
    die "OpenVPN service is not active after syncing clients."
  fi
  systemctl is-active --quiet "$OPENVPN_SERVICE" || die "OpenVPN service is not active after syncing clients."
  systemctl start "$OPENVPN_ACCOUNTING_SERVICE" >/dev/null 2>&1 || true
  chown root:omnigateway "$OPENVPN_ACCOUNTING_HEARTBEAT" 2>/dev/null || true
  chmod 0640 "$OPENVPN_ACCOUNTING_HEARTBEAT" 2>/dev/null || true
  echo "ok"
}

status_cmd(){
  local sshState openvpnState redsocksState panelState nginxState fail2 backendListener publicListener panelListener internalListener dns iport
  local accountingTimerState accountingServiceState accountingDbReady accountingHealthy hbOk hbEpoch nowEpoch
  sshState="$(systemctl is-active ssh 2>/dev/null || systemctl is-active sshd 2>/dev/null || echo inactive)"
  openvpnState="$(systemctl is-active "$OPENVPN_SERVICE" 2>/dev/null || echo inactive)"
  redsocksState="$(systemctl is-active "$OPENVPN_REDSOCKS_SERVICE" 2>/dev/null || echo inactive)"
  accountingTimerState="$(systemctl is-active "$OPENVPN_ACCOUNTING_TIMER" 2>/dev/null || echo inactive)"
  accountingServiceState="$(systemctl is-active "$OPENVPN_ACCOUNTING_SERVICE" 2>/dev/null || echo inactive)"
  panelState="$(systemctl is-active "$OMNIPANEL_SERVICE" 2>/dev/null || echo inactive)"
  nginxState="$(systemctl is-active nginx 2>/dev/null || echo inactive)"
  fail2="disabled"
  iport="$(jq -r '.omnipanel_internal_port // 0' "$METADATA_FILE" 2>/dev/null || echo 0)"
  backendListener="$(check_listener "$BACKEND_PORT")"
  publicListener="$(check_listener "$PUBLIC_PORT")"
  panelListener="$(check_listener "$PANEL_PORT")"
  if [[ "$iport" =~ ^[0-9]+$ ]] && (( iport > 0 )); then
    internalListener="$(check_listener "$iport")"
  else
    internalListener="false"
  fi
  dns="$(dns_status_json)"
  accountingDbReady=false
  sqlite3 "$OPENVPN_ACCOUNTING_DB" "SELECT 1;" >/dev/null 2>&1 && accountingDbReady=true || true
  hbOk=false
  hbEpoch=0
  if [[ -f "$OPENVPN_ACCOUNTING_HEARTBEAT" ]]; then
    hbOk="$(jq -r '.ok // false' "$OPENVPN_ACCOUNTING_HEARTBEAT" 2>/dev/null || echo false)"
    hbEpoch="$(jq -r '.runAtEpoch // 0' "$OPENVPN_ACCOUNTING_HEARTBEAT" 2>/dev/null || echo 0)"
  fi
  nowEpoch="$(date +%s)"
  accountingHealthy=false
  if [[ "$accountingTimerState" == "active" && "$accountingDbReady" == "true" && "$hbOk" == "true" && "$hbEpoch" =~ ^[0-9]+$ ]]; then
    if (( hbEpoch > 0 && (nowEpoch - hbEpoch) <= 120 )); then
      accountingHealthy=true
    fi
  fi

  printf '{"activeProtocol":"openvpn_tcp_relay","sshState":"%s","xuiState":"inactive","singBoxState":"inactive","openVpnState":"%s","omniPanelState":"%s","nginxState":"%s","fail2banState":"%s","backendPort":%s,"publicPort":%s,"panelPort":%s,"omniPanelInternalPort":%s,"xuiPanelPort":0,"backendListener":%s,"publicListener":%s,"panelListener":%s,"omniPanelInternalListener":%s,"inboundId":"","dnsConfigPresent":%s,"dnsRuleActive":%s,"dohReachableViaTunnel":%s,"udp53PathReady":%s,"dnsPathHealthy":%s,"dnsMode":"%s","dnsUdpOnly":%s,"dohEndpoints":"%s","redsocksState":"%s","openVpnAccountingTimerState":"%s","openVpnAccountingServiceState":"%s","openVpnAccountingDbReady":%s,"openVpnAccountingHealthy":%s}\n' \
    "$sshState" "$openvpnState" "$panelState" "$nginxState" "$fail2" "$BACKEND_PORT" "$PUBLIC_PORT" "$PANEL_PORT" "$iport" \
    "$backendListener" "$publicListener" "$panelListener" "$internalListener" \
    "$(jq -r '.dnsConfigPresent' <<<"$dns")" "$(jq -r '.dnsRuleActive' <<<"$dns")" "$(jq -r '.dohReachableViaTunnel' <<<"$dns")" "$(jq -r '.udp53PathReady' <<<"$dns")" "$(jq -r '.dnsPathHealthy' <<<"$dns")" "$(jq -r '.dnsMode' <<<"$dns")" "$(jq -r '.dnsUdpOnly' <<<"$dns")" "$(jq -r '.dohEndpoints' <<<"$dns" | sed 's/"/\\"/g')" \
    "$redsocksState" "$accountingTimerState" "$accountingServiceState" "$accountingDbReady" "$accountingHealthy"
}

health_cmd(){
  local status healthy dnsLastError redsocksState
  status="$(status_cmd)"
  healthy=true
  [[ "$(jq -r '.sshState' <<<"$status")" == "active" ]] || healthy=false
  [[ "$(jq -r '.openVpnState' <<<"$status")" == "active" ]] || healthy=false
  [[ "$(jq -r '.omniPanelState' <<<"$status")" == "active" ]] || healthy=false
  [[ "$(jq -r '.nginxState' <<<"$status")" == "active" ]] || healthy=false
  [[ "$(jq -r '.backendListener' <<<"$status")" == "true" ]] || healthy=false
  [[ "$(jq -r '.publicListener' <<<"$status")" == "true" ]] || healthy=false
  [[ "$(jq -r '.panelListener' <<<"$status")" == "true" ]] || healthy=false
  [[ "$(jq -r '.omniPanelInternalListener' <<<"$status")" == "true" ]] || healthy=false
  [[ "$(jq -r '.dnsPathHealthy' <<<"$status")" == "true" ]] || healthy=false
  redsocksState="$(jq -r '.redsocksState // "inactive"' <<<"$status")"
  [[ "$redsocksState" == "active" ]] || healthy=false
  [[ "$(jq -r '.openVpnAccountingHealthy // false' <<<"$status")" == "true" ]] || healthy=false

  dnsLastError=""
  [[ "$(jq -r '.dnsConfigPresent' <<<"$status")" == "true" ]] || dnsLastError="dnsConfigMissing"
  [[ "$(jq -r '.dnsRuleActive' <<<"$status")" == "true" ]] || dnsLastError="dnsRuleInactive"
  if [[ "$(jq -r '.dnsMode' <<<"$status")" == "hybrid" ]]; then
    [[ "$(jq -r '.dohReachableViaTunnel' <<<"$status")" == "true" ]] || dnsLastError="dohUnreachableViaTunnel"
    [[ "$(jq -r '.udp53PathReady' <<<"$status")" == "true" ]] || dnsLastError="udp53PathNotReady"
  fi
  if [[ "$(jq -r '.dnsMode' <<<"$status")" == "doh" ]]; then
    [[ "$(jq -r '.dohReachableViaTunnel' <<<"$status")" == "true" ]] || dnsLastError="dohUnreachableViaTunnel"
  fi
  if [[ "$(jq -r '.dnsMode' <<<"$status")" == "udp" ]]; then
    [[ "$(jq -r '.udp53PathReady' <<<"$status")" == "true" ]] || dnsLastError="udp53PathNotReady"
  fi

  jq -c --argjson healthy "$( [[ "$healthy" == true ]] && echo true || echo false )" --arg dnsLastError "$dnsLastError" 'del(.redsocksState) + {healthy:$healthy,dnsLastError:$dnsLastError}' <<<"$status"
}

install_cmd(){
  require_root
  progress 3 "Validating platform"
  ensure_meta
  install -m 0755 "$0" /usr/local/sbin/omnirelay-gatewayctl
  if [[ -x "${IPSEC_RULES_CLEAR_SCRIPT:-}" ]]; then
    "${IPSEC_RULES_CLEAR_SCRIPT}" >/dev/null 2>&1 || true
  fi
  systemctl disable --now "$OPENVPN_ACCOUNTING_TIMER" "$OPENVPN_ACCOUNTING_SERVICE" "$OPENVPN_SERVICE" "$OPENVPN_REDSOCKS_SERVICE" 2>/dev/null || true
  systemctl disable --now x-ui 2>/dev/null || true
  systemctl disable --now omnirelay-singbox 2>/dev/null || true
  systemctl disable --now omnirelay-ipsec-accounting.timer omnirelay-ipsec-accounting.service omnirelay-ipsec-rules xl2tpd ipsec strongswan-starter 2>/dev/null || true
  rm -f \
    /etc/systemd/system/x-ui.service \
    /etc/systemd/system/omnirelay-singbox.service \
    /etc/systemd/system/omnirelay-ipsec-rules.service \
    /etc/systemd/system/omnirelay-redsocks.service \
    /etc/systemd/system/omnirelay-ipsec-accounting.service \
    /etc/systemd/system/omnirelay-ipsec-accounting.timer \
    /etc/sudoers.d/omnigateway-singbox \
    /etc/sudoers.d/omnigateway-ipsec
  rm -f /etc/sysctl.d/99-omnirelay-ipsec.conf "$OPENVPN_STATUS_FILE" /etc/ppp/ip-up.d/99-omnirelay-accounting /etc/ppp/ip-down.d/99-omnirelay-accounting
  systemctl daemon-reload || true

  install_packages
  ensure_nodejs_runtime
  parse_openvpn_network
  ensure_clients_seed_file
  init_openvpn_accounting_db
  setup_openvpn_pki
  write_openvpn_auth_scripts
  write_openvpn_accounting_script
  write_redsocks_config
  enable_ip_forward
  write_openvpn_server_config
  setup_runtime_services
  deploy_panel
  sync_clients_cmd
  wait_omnipanel_ready
  configure_nginx
  configure_host_firewall
  write_metadata
  dns_apply

  progress 100 "Gateway install completed"
  local endpoint
  endpoint="${VPS_IP:-$(hostname -I 2>/dev/null | awk '{print $1}')}"
  [[ -n "$endpoint" ]] || endpoint="<VPS_IP_OR_HOSTNAME>"
  log "OmniPanel URL: https://${endpoint}:${PANEL_PORT}/ | Username: ${PANEL_USER} | Password: ${PANEL_PASSWORD}"
}

start_cmd(){
  require_root
  progress 96 "Starting gateway services"
  systemctl enable --now "$OPENVPN_REDSOCKS_SERVICE" "$OPENVPN_SERVICE" "$OPENVPN_ACCOUNTING_TIMER" "$OMNIPANEL_SERVICE" nginx >/dev/null 2>&1 || true
  systemctl start "$OPENVPN_ACCOUNTING_SERVICE" >/dev/null 2>&1 || true
  progress 100 "Gateway start completed"
}

stop_cmd(){
  require_root
  progress 96 "Stopping gateway services"
  systemctl stop "$OPENVPN_ACCOUNTING_TIMER" "$OPENVPN_ACCOUNTING_SERVICE" "$OMNIPANEL_SERVICE" "$OPENVPN_SERVICE" "$OPENVPN_REDSOCKS_SERVICE" nginx >/dev/null 2>&1 || true
  progress 100 "Gateway stop completed"
}

uninstall_cmd(){
  require_root
  progress 96 "Uninstalling gateway"
  systemctl disable --now "$OPENVPN_ACCOUNTING_TIMER" "$OPENVPN_ACCOUNTING_SERVICE" "$OMNIPANEL_SERVICE" "$OPENVPN_SERVICE" "$OPENVPN_REDSOCKS_SERVICE" nginx >/dev/null 2>&1 || true
  systemctl disable --now x-ui omnirelay-singbox omnirelay-ipsec-rules xl2tpd ipsec strongswan-starter omnirelay-ipsec-accounting.timer omnirelay-ipsec-accounting.service >/dev/null 2>&1 || true
  if [[ -x "${IPSEC_RULES_CLEAR_SCRIPT:-}" ]]; then
    "${IPSEC_RULES_CLEAR_SCRIPT}" >/dev/null 2>&1 || true
  fi
  rm -f \
    "/etc/systemd/system/${OMNIPANEL_SERVICE}.service" \
    "/etc/systemd/system/${OPENVPN_SERVICE}.service" \
    "/etc/systemd/system/${OPENVPN_REDSOCKS_SERVICE}.service" \
    "/etc/systemd/system/${OPENVPN_ACCOUNTING_SERVICE}.service" \
    "/etc/systemd/system/${OPENVPN_ACCOUNTING_TIMER}" \
    /etc/systemd/system/x-ui.service \
    /etc/systemd/system/omnirelay-singbox.service \
    /etc/systemd/system/omnirelay-ipsec-rules.service \
    /etc/systemd/system/omnirelay-ipsec-accounting.service \
    /etc/systemd/system/omnirelay-ipsec-accounting.timer \
    /etc/systemd/system/omnirelay-redsocks.service
  rm -f /etc/nginx/sites-enabled/omnirelay-omnipanel.conf /etc/nginx/sites-available/omnirelay-omnipanel.conf
  rm -f /etc/sudoers.d/omnigateway-openvpn /etc/sudoers.d/omnigateway-ipsec /etc/sudoers.d/omnigateway-singbox /usr/local/sbin/omnirelay-gatewayctl
  rm -f /etc/sysctl.d/99-omnirelay-openvpn.conf /etc/sysctl.d/99-omnirelay-ipsec.conf "$OPENVPN_STATUS_FILE" /etc/ppp/ip-up.d/99-omnirelay-accounting /etc/ppp/ip-down.d/99-omnirelay-accounting
  rm -rf "$METADATA_DIR" "$OMNIPANEL_APP_DIR"
  systemctl daemon-reload
  progress 100 "Gateway uninstall completed"
}

parse_args(){
  if [[ $# -gt 0 && ! "$1" =~ ^- ]]; then
    COMMAND="$1"
    shift
  fi
  while [[ $# -gt 0 ]]; do
    case "$1" in
      --public-port) PUBLIC_PORT="${2:-}"; shift 2 ;;
      --panel-port) PANEL_PORT="${2:-}"; shift 2 ;;
      --backend-port) BACKEND_PORT="${2:-}"; shift 2 ;;
      --ssh-port) SSH_PORT="${2:-}"; shift 2 ;;
      --bootstrap-socks-port) BOOTSTRAP_SOCKS_PORT="${2:-}"; shift 2 ;;
      --proxy-check-url) PROXY_CHECK_URL="${2:-}"; shift 2 ;;
      --openvpn-network) OPENVPN_NETWORK="${2:-}"; shift 2 ;;
      --openvpn-client-dns) OPENVPN_CLIENT_DNS="${2:-}"; shift 2 ;;
      --dns-mode) DNS_MODE="${2:-}"; shift 2 ;;
      --doh-endpoints) DOH_ENDPOINTS="${2:-}"; shift 2 ;;
      --dns-udp-only) DNS_UDP_ONLY="${2:-}"; shift 2 ;;
      --vps-ip) VPS_IP="${2:-}"; shift 2 ;;
      --tunnel-user) TUNNEL_USER="${2:-}"; shift 2 ;;
      --tunnel-auth) TUNNEL_AUTH="${2:-}"; shift 2 ;;
      --panel-user) PANEL_USER="${2:-}"; shift 2 ;;
      --panel-password) PANEL_PASSWORD="${2:-}"; shift 2 ;;
      --panel-base-path) PANEL_BASE_PATH="${2:-}"; shift 2 ;;
      --json) STATUS_JSON=1; HEALTH_JSON=1; shift ;;
      -h|--help) usage; exit 0 ;;
      *) die "Unknown option: $1" ;;
    esac
  done
}

main(){
  parse_args "$@"
  validate_port "$PUBLIC_PORT" "--public-port"
  validate_port "$PANEL_PORT" "--panel-port"
  validate_port "$BACKEND_PORT" "--backend-port"
  validate_port "$SSH_PORT" "--ssh-port"
  validate_port "$BOOTSTRAP_SOCKS_PORT" "--bootstrap-socks-port"
  [[ "$DNS_MODE" == "hybrid" || "$DNS_MODE" == "doh" || "$DNS_MODE" == "udp" ]] || die "--dns-mode must be hybrid|doh|udp"
  [[ "$DNS_UDP_ONLY" == "true" || "$DNS_UDP_ONLY" == "false" ]] || die "--dns-udp-only must be true or false"
  [[ "$PUBLIC_PORT" != "$PANEL_PORT" ]] || die "--public-port and --panel-port must differ"

  case "$COMMAND" in
    install) install_cmd ;;
    uninstall) uninstall_cmd ;;
    start) start_cmd ;;
    stop) stop_cmd ;;
    get-protocol) get_protocol_cmd ;;
    status) STATUS_JSON=1; status_cmd ;;
    health) HEALTH_JSON=1; health_cmd ;;
    dns-apply) dns_apply ;;
    dns-status) STATUS_JSON=1; dns_status ;;
    dns-repair) dns_repair ;;
    sync-clients) sync_clients_cmd ;;
    *) die "Unknown command: ${COMMAND}. Expected install|uninstall|start|stop|get-protocol|status|health|dns-apply|dns-status|dns-repair|sync-clients" ;;
  esac
}

main "$@"
