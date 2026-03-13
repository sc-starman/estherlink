#!/usr/bin/env bash
set -Eeuo pipefail
IFS=$'\n\t'
umask 027

SCRIPT_NAME="$(basename "$0")"
COMMAND="install"

PUBLIC_PORT=1701
PANEL_PORT=2054
BACKEND_PORT=15000
SSH_PORT=22
BOOTSTRAP_SOCKS_PORT=16080
PROXY_CHECK_URL="https://deb.debian.org/"
VPS_IP=""
DNS_MODE="hybrid"
DOH_ENDPOINTS="https://1.1.1.1/dns-query,https://8.8.8.8/dns-query"
DNS_UDP_ONLY="true"
IPSEC_CLIENT_DNS="1.1.1.1,8.8.8.8"
PANEL_USER=""
PANEL_PASSWORD=""
PANEL_BASE_PATH=""
PANEL_DOMAIN=""
PANEL_DOMAIN_ONLY="false"
PANEL_SSL_ENABLED="false"
PANEL_SSL_MODE="letsencrypt"
PANEL_CERT_FILE=""
PANEL_KEY_FILE=""
TUNNEL_USER=""
TUNNEL_AUTH="host_key"

IPSEC_IKE_PORT=500
IPSEC_NATT_PORT=4500
IPSEC_L2TP_PORT=1701
HWD_SL2_COMMIT="dfa3d7e7939297b397a1188e1962d27d97c112f6"
HWD_SL2_SETUP_URL="https://raw.githubusercontent.com/hwdsl2/setup-ipsec-vpn/${HWD_SL2_COMMIT}/vpnsetup.sh"

METADATA_DIR="/etc/omnirelay/gateway"
METADATA_FILE="${METADATA_DIR}/metadata.json"
DNS_PROFILE_FILE="${METADATA_DIR}/dns_profile.json"
OMNIPANEL_ENV_FILE="${METADATA_DIR}/omnipanel.env"
OMNIPANEL_SERVICE="omnirelay-omnipanel"
OMNIPANEL_APP_DIR="/opt/omnirelay/omni-gateway"
PANEL_AUTH_FILE="${OMNIPANEL_APP_DIR}/panel-auth.json"
APT_PROXY_FILE="/etc/apt/apt.conf.d/99-omnirelay-socks"
IPSEC_SYNC_COMMAND="/usr/bin/sudo -n /usr/local/sbin/omnirelay-gatewayctl sync-clients"
OMNIPANEL_COMMON_SCRIPT="/tmp/omnirelay-omnipanel-common.sh"

IPSEC_DIR="${METADATA_DIR}/ipsec"
IPSEC_CLIENTS_FILE="${OMNIPANEL_APP_DIR}/ipsec_l2tp_clients.json"
IPSEC_PSK_FILE="${IPSEC_DIR}/shared_psk"
IPSEC_REDSOCKS_SERVICE="omnirelay-redsocks"
IPSEC_REDSOCKS_CONFIG="${IPSEC_DIR}/redsocks.conf"
IPSEC_REDSOCKS_LOCAL_PORT=12345
IPSEC_RULES_SERVICE="omnirelay-ipsec-rules"
IPSEC_RULES_APPLY_SCRIPT="${IPSEC_DIR}/apply-rules.sh"
IPSEC_RULES_CLEAR_SCRIPT="${IPSEC_DIR}/clear-rules.sh"
IPSEC_ACCOUNTING_DB="${IPSEC_DIR}/accounting.db"
IPSEC_ACCOUNTING_SCRIPT="${IPSEC_DIR}/accounting-loop.sh"
IPSEC_ACCOUNTING_HEARTBEAT="${IPSEC_DIR}/accounting-heartbeat.json"
IPSEC_ACCOUNTING_SERVICE="omnirelay-ipsec-accounting"
IPSEC_ACCOUNTING_TIMER="omnirelay-ipsec-accounting.timer"
IPSEC_PPP_HOOK_UP="/etc/ppp/ip-up.d/99-omnirelay-accounting"
IPSEC_PPP_HOOK_DOWN="/etc/ppp/ip-down.d/99-omnirelay-accounting"
IPSEC_CHAP_SECRETS="/etc/ppp/chap-secrets"

OMNIPANEL_INTERNAL_PORT=0
IPSEC_SHARED_PSK=""

log(){ printf '[%s] %s\n' "$(date -u +'%Y-%m-%dT%H:%M:%SZ')" "$*"; }
die(){ printf '[%s] ERROR: %s\n' "$(date -u +'%Y-%m-%dT%H:%M:%SZ')" "$*" >&2; exit 1; }
progress(){ local pct="$1"; shift; printf 'OMNIRELAY_PROGRESS:%s:%s\n' "$pct" "$*"; }
require_root(){ (( EUID == 0 )) || die "This command requires root."; }
validate_port(){ [[ "$1" =~ ^[0-9]+$ ]] || die "$2 must be integer"; (( $1>=1 && $1<=65535 )) || die "$2 out of range"; }
random_string(){ LC_ALL=C tr -dc 'a-zA-Z0-9' </dev/urandom | head -c "$1" || true; }
check_listener(){ ss -lnt "( sport = :$1 )" 2>/dev/null | awk 'NR>1{print}' | grep -q . && echo true || echo false; }
check_udp_listener(){ ss -lun "( sport = :$1 )" 2>/dev/null | awk 'NR>1{print}' | grep -q . && echo true || echo false; }
choose_port(){ for _ in $(seq 1 200); do p=$((RANDOM%30000+22000)); [[ "$(check_listener "$p")" == "false" ]] && echo "$p" && return 0; done; die "cannot allocate random port"; }

usage(){
  cat <<EOF
Usage: sudo ./${SCRIPT_NAME} <install|uninstall|start|stop|get-protocol|status|health|dns-apply|dns-status|dns-repair|sync-clients> [options]
--public-port <port>
--panel-port <port>
--backend-port <port>
--ssh-port <port>
--bootstrap-socks-port <port>
--proxy-check-url <url>
--ipsec-client-dns <csv>
--dns-mode <hybrid|doh|udp>
--doh-endpoints <csv>
--dns-udp-only <true|false>
--vps-ip <ip-or-host>
--tunnel-user <name>
--tunnel-auth <host_key|password>
--panel-user <name>
--panel-password <password>
--panel-base-path <path>
--panel-domain <host>
--panel-domain-only <true|false>
--panel-ssl <true|false>
--panel-ssl-mode <letsencrypt|uploaded>
--panel-cert-file <path>
--panel-key-file <path>
--json
EOF
}

get_protocol_cmd(){
  echo "ipsec_l2tp_hwdsl2"
}

normalize_bool(){
  local value="${1:-false}"
  value="$(printf '%s' "$value" | tr '[:upper:]' '[:lower:]' | xargs)"
  case "$value" in
    true|1|yes|y) echo "true" ;;
    *) echo "false" ;;
  esac
}

normalize_panel_ssl_mode(){
  local mode="${1:-letsencrypt}"
  mode="$(printf '%s' "$mode" | tr '[:upper:]' '[:lower:]' | xargs)"
  if [[ "$mode" == "uploaded" ]]; then
    echo "uploaded"
    return 0
  fi
  echo "letsencrypt"
}

load_omnipanel_common(){
  local candidate
  for candidate in "$OMNIPANEL_COMMON_SCRIPT" "/usr/local/lib/omnirelay/omnipanel-common.sh" "$(dirname "$0")/setup_omnirelay_omnipanel_common.sh"; do
    if [[ -f "$candidate" ]]; then
      # shellcheck source=/dev/null
      source "$candidate"
      return 0
    fi
  done
  die "Shared OmniPanel helper script not found. Expected ${OMNIPANEL_COMMON_SCRIPT}."
}

configure_proxy(){
  local proxy_url="socks5h://127.0.0.1:${BOOTSTRAP_SOCKS_PORT}"
  export ALL_PROXY="$proxy_url" HTTPS_PROXY="$proxy_url" HTTP_PROXY="$proxy_url" NO_PROXY="127.0.0.1,localhost"
}

ipsec_global_managed_paths(){
  cat <<'EOF'
/etc/ipsec.conf
/etc/ipsec.secrets
/etc/xl2tpd/xl2tpd.conf
/etc/ppp/options.xl2tpd
/etc/ppp/chap-secrets
EOF
}

backup_ipsec_global_files(){
  local backup_dir path key backup_file absent_marker parent
  backup_dir="${IPSEC_DIR}/global-backups"
  install -d -m 0700 "$backup_dir"
  while IFS= read -r path; do
    [[ -n "$path" ]] || continue
    key="${path#/}"
    key="${key//\//__}"
    backup_file="${backup_dir}/${key}"
    absent_marker="${backup_file}.absent"
    if [[ -e "$backup_file" || -f "$absent_marker" ]]; then
      continue
    fi
    if [[ -e "$path" ]]; then
      parent="$(dirname "$backup_file")"
      install -d -m 0700 "$parent"
      cp -a "$path" "$backup_file"
    else
      : > "$absent_marker"
      chmod 0600 "$absent_marker" || true
    fi
  done < <(ipsec_global_managed_paths)
}

restore_ipsec_global_files(){
  local backup_dir path key backup_file absent_marker parent
  backup_dir="${IPSEC_DIR}/global-backups"
  [[ -d "$backup_dir" ]] || return 0
  while IFS= read -r path; do
    [[ -n "$path" ]] || continue
    key="${path#/}"
    key="${key//\//__}"
    backup_file="${backup_dir}/${key}"
    absent_marker="${backup_file}.absent"
    if [[ -e "$backup_file" ]]; then
      parent="$(dirname "$path")"
      install -d -m 0755 "$parent"
      rm -rf "$path"
      cp -a "$backup_file" "$path"
    elif [[ -f "$absent_marker" ]]; then
      rm -rf "$path"
    fi
  done < <(ipsec_global_managed_paths)
  rm -rf "$backup_dir"
}
clear_proxy(){ unset ALL_PROXY HTTPS_PROXY HTTP_PROXY NO_PROXY || true; }
configure_apt_proxy(){
  cat > "$APT_PROXY_FILE" <<EOF
Acquire::http::Proxy "socks5h://127.0.0.1:${BOOTSTRAP_SOCKS_PORT}";
Acquire::https::Proxy "socks5h://127.0.0.1:${BOOTSTRAP_SOCKS_PORT}";
EOF
}

resolve_node_bin(){ command -v node >/dev/null 2>&1 && command -v node && return 0; command -v nodejs >/dev/null 2>&1 && command -v nodejs && return 0; return 1; }
detect_node_major(){ local bin; bin="$(resolve_node_bin 2>/dev/null || true)"; [[ -n "$bin" ]] || { echo 0; return 0; }; "$bin" -p "Number(process.versions.node.split('.')[0])" 2>/dev/null || echo 0; }

sync_time_via_bootstrap_socks(){
  local hdr remote now delta abs was_ntp
  configure_proxy
  hdr="$(curl --silent --show-error --insecure --max-time 20 --connect-timeout 10 --retry 0 --socks5-hostname "127.0.0.1:${BOOTSTRAP_SOCKS_PORT}" -I "$PROXY_CHECK_URL" 2>/dev/null | tr -d '\r' | awk 'tolower($1)=="date:"{$1="";sub(/^ /,"");print;exit}')"
  [[ -n "$hdr" ]] || return 1
  remote="$(date -u -d "$hdr" +%s 2>/dev/null || true)"
  [[ -n "$remote" ]] || return 1
  now="$(date -u +%s 2>/dev/null || echo 0)"
  delta=$(( remote - now ))
  abs=$delta; (( abs < 0 )) && abs=$(( -abs ))
  if (( abs <= 5 )); then
    log "Clock skew via SOCKS is ${abs}s; no clock adjustment needed."
    return 0
  fi
  was_ntp="$(timedatectl show -p NTP --value 2>/dev/null || true)"
  [[ "$was_ntp" == "yes" ]] && timedatectl set-ntp false >/dev/null 2>&1 || true
  date -u -s "@${remote}" >/dev/null 2>&1 || return 1
  command -v hwclock >/dev/null 2>&1 && hwclock --systohc >/dev/null 2>&1 || true
  [[ "$was_ntp" == "yes" ]] && timedatectl set-ntp true >/dev/null 2>&1 || true
  log "Adjusted system clock by ${delta}s via SOCKS-backed HTTPS date (${hdr})."
}

install_packages(){
  progress 12 "Installing required packages"
  configure_apt_proxy; configure_proxy
  progress 14 "Syncing VPS clock over SOCKS bootstrap (if needed)"
  sync_time_via_bootstrap_socks || log "Clock sync over SOCKS skipped/failed; continuing."
  apt-get -o Acquire::Retries=3 -o Acquire::http::Timeout=20 -o Acquire::https::Timeout=20 update -y
  DEBIAN_FRONTEND=noninteractive apt-get install -y ca-certificates curl jq tar gzip openssl python3 iptables redsocks nginx nodejs ppp xl2tpd strongswan sqlite3
  clear_proxy
}

ensure_nodejs_runtime(){
  local major; major="$(detect_node_major)"; (( major >= 18 )) && return 0
  progress 18 "Upgrading Node.js runtime to v20"
  configure_proxy
  curl -fSL "https://deb.nodesource.com/setup_20.x" -o /tmp/omnirelay-nodesource-setup.sh
  bash /tmp/omnirelay-nodesource-setup.sh
  DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends nodejs
  clear_proxy
  major="$(detect_node_major)"; (( major >= 18 )) || die "Node.js 18+ is required for OmniPanel."
}

ensure_clients_seed_file(){
  install -d -m 0755 "$(dirname "$IPSEC_CLIENTS_FILE")" "$IPSEC_DIR"
  [[ -f "$IPSEC_CLIENTS_FILE" ]] || jq -n --arg id "$(random_string 16)" --arg p "$(random_string 24)" '[{id:$id,email:"omni-client@local",enable:true,username:"l2tp_client",password:$p,totalGB:0,expiryTime:0}]' > "$IPSEC_CLIENTS_FILE"
}
ensure_shared_psk(){
  [[ -f "$IPSEC_PSK_FILE" ]] && IPSEC_SHARED_PSK="$(tr -d '\r\n' < "$IPSEC_PSK_FILE")"
  [[ -n "${IPSEC_SHARED_PSK:-}" ]] || IPSEC_SHARED_PSK="$(random_string 32)"
  printf '%s\n' "$IPSEC_SHARED_PSK" > "$IPSEC_PSK_FILE"
  chmod 0600 "$IPSEC_PSK_FILE"
}
set_gateway_file_access(){
  if id -u omnigateway >/dev/null 2>&1; then
    chown root:omnigateway "$IPSEC_PSK_FILE" "$IPSEC_CLIENTS_FILE" "$IPSEC_ACCOUNTING_DB" "$IPSEC_ACCOUNTING_HEARTBEAT" 2>/dev/null || true
    chmod 0640 "$IPSEC_PSK_FILE" "$IPSEC_CLIENTS_FILE" "$IPSEC_ACCOUNTING_DB" "$IPSEC_ACCOUNTING_HEARTBEAT" 2>/dev/null || true
  fi
}

escape_sql_literal(){ printf '%s' "$1" | sed "s/'/''/g"; }

init_ipsec_accounting_db(){
  install -d -m 0755 "$IPSEC_DIR"
  sqlite3 "$IPSEC_ACCOUNTING_DB" <<'SQL'
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
  ifname TEXT,
  pppd_pid INTEGER NOT NULL DEFAULT 0,
  last_rx INTEGER NOT NULL DEFAULT 0,
  last_tx INTEGER NOT NULL DEFAULT 0,
  last_seen_at INTEGER NOT NULL
);
SQL
  chmod 0640 "$IPSEC_ACCOUNTING_DB" 2>/dev/null || true
}

install_hwdsl2_stack(){
  progress 24 "Installing IPSec/L2TP runtime (hwdsl2)"
  local u p dns1 dns2
  u="$(jq -r 'first(.[] | select((.enable // true) == true) | .username) // empty' "$IPSEC_CLIENTS_FILE" 2>/dev/null || true)"
  p="$(jq -r 'first(.[] | select((.enable // true) == true) | .password) // empty' "$IPSEC_CLIENTS_FILE" 2>/dev/null || true)"
  [[ -n "$u" ]] || u="l2tp_client"
  [[ -n "$p" ]] || p="$(random_string 24)"
  dns1="$(echo "$IPSEC_CLIENT_DNS" | cut -d',' -f1 | xargs || true)"
  dns2="$(echo "$IPSEC_CLIENT_DNS" | cut -d',' -f2 | xargs || true)"
  configure_proxy
  curl -fSL "$HWD_SL2_SETUP_URL" -o /tmp/omnirelay-vpnsetup.sh
  clear_proxy
  chmod +x /tmp/omnirelay-vpnsetup.sh
  yes "" | env VPN_IPSEC_PSK="$IPSEC_SHARED_PSK" VPN_USER="$u" VPN_PASSWORD="$p" VPN_DNS_SRV1="${dns1:-1.1.1.1}" VPN_DNS_SRV2="${dns2:-8.8.8.8}" DEBIAN_FRONTEND=noninteractive bash /tmp/omnirelay-vpnsetup.sh || die "hwdsl2 setup-ipsec-vpn install failed."
  cat > /etc/ipsec.secrets <<EOF
%any  %any  : PSK "${IPSEC_SHARED_PSK}"
EOF
  chmod 0600 /etc/ipsec.secrets
}

write_redsocks_config(){
  cat > "$IPSEC_REDSOCKS_CONFIG" <<EOF
base { log_debug = off; log_info = on; daemon = off; redirector = iptables; }
redsocks { local_ip = 127.0.0.1; local_port = ${IPSEC_REDSOCKS_LOCAL_PORT}; ip = 127.0.0.1; port = ${BACKEND_PORT}; type = socks5; }
EOF
}
enable_ip_forward(){ echo "net.ipv4.ip_forward=1" > /etc/sysctl.d/99-omnirelay-ipsec.conf; sysctl -q -w net.ipv4.ip_forward=1 || true; }

write_traffic_scripts(){
  cat > "$IPSEC_RULES_APPLY_SCRIPT" <<EOF
#!/usr/bin/env bash
set -Eeuo pipefail
IPT="\$(command -v iptables || true)"; [[ -n "\$IPT" ]] || exit 0
\$IPT -t nat -N OMNIRELAY_L2TP_TCP 2>/dev/null || true
\$IPT -t nat -F OMNIRELAY_L2TP_TCP
\$IPT -t nat -A OMNIRELAY_L2TP_TCP -d 127.0.0.0/8 -j RETURN
\$IPT -t nat -A OMNIRELAY_L2TP_TCP -p tcp -j REDIRECT --to-ports ${IPSEC_REDSOCKS_LOCAL_PORT}
\$IPT -t nat -C PREROUTING -i ppp+ -p tcp -j OMNIRELAY_L2TP_TCP 2>/dev/null || \$IPT -t nat -A PREROUTING -i ppp+ -p tcp -j OMNIRELAY_L2TP_TCP
\$IPT -N OMNIRELAY_L2TP_UDP_BLOCK 2>/dev/null || true
\$IPT -F OMNIRELAY_L2TP_UDP_BLOCK
\$IPT -A OMNIRELAY_L2TP_UDP_BLOCK -p udp -j REJECT
\$IPT -C FORWARD -i ppp+ -j OMNIRELAY_L2TP_UDP_BLOCK 2>/dev/null || \$IPT -A FORWARD -i ppp+ -j OMNIRELAY_L2TP_UDP_BLOCK
EOF
  chmod 0755 "$IPSEC_RULES_APPLY_SCRIPT"
  cat > "$IPSEC_RULES_CLEAR_SCRIPT" <<EOF
#!/usr/bin/env bash
set -Eeuo pipefail
IPT="\$(command -v iptables || true)"; [[ -n "\$IPT" ]] || exit 0
\$IPT -t nat -D PREROUTING -i ppp+ -p tcp -j OMNIRELAY_L2TP_TCP 2>/dev/null || true
\$IPT -t nat -F OMNIRELAY_L2TP_TCP 2>/dev/null || true
\$IPT -t nat -X OMNIRELAY_L2TP_TCP 2>/dev/null || true
\$IPT -D FORWARD -i ppp+ -j OMNIRELAY_L2TP_UDP_BLOCK 2>/dev/null || true
\$IPT -F OMNIRELAY_L2TP_UDP_BLOCK 2>/dev/null || true
\$IPT -X OMNIRELAY_L2TP_UDP_BLOCK 2>/dev/null || true
EOF
  chmod 0755 "$IPSEC_RULES_CLEAR_SCRIPT"
}

refresh_chap_secrets_from_policy(){
  local chap_tmp row id username password enable esc_id policy enabled_policy total_limit expiry_ms used_bytes now_ms
  chap_tmp="$(mktemp)"
  printf '# OmniRelay managed IPSec/L2TP users\n' > "$chap_tmp"
  now_ms=$(( $(date +%s) * 1000 ))
  while IFS= read -r row; do
    id="$(jq -r '.id // empty' <<<"$row")"
    enable="$(jq -r '.enable // true' <<<"$row")"
    username="$(jq -r '.username // empty' <<<"$row")"
    password="$(jq -r '.password // empty' <<<"$row")"
    [[ -n "$id" && -n "$username" && -n "$password" && "$enable" == "true" ]] || continue
    esc_id="$(escape_sql_literal "$id")"
    policy="$(sqlite3 -csv -noheader "$IPSEC_ACCOUNTING_DB" "SELECT c.enabled,c.total_bytes_limit,c.expiry_unix_ms,COALESCE(u.used_bytes,0) FROM clients c LEFT JOIN usage_totals u ON u.client_id=c.client_id WHERE c.client_id='${esc_id}' LIMIT 1;" 2>/dev/null || true)"
    [[ -n "$policy" ]] || continue
    IFS=',' read -r enabled_policy total_limit expiry_ms used_bytes <<<"$policy"
    [[ "$enabled_policy" =~ ^[0-9]+$ ]] || enabled_policy=0
    [[ "$total_limit" =~ ^[0-9]+$ ]] || total_limit=0
    [[ "$expiry_ms" =~ ^[0-9]+$ ]] || expiry_ms=0
    [[ "$used_bytes" =~ ^[0-9]+$ ]] || used_bytes=0
    (( enabled_policy == 1 )) || continue
    if (( expiry_ms > 0 && now_ms >= expiry_ms )); then
      continue
    fi
    if (( total_limit > 0 && used_bytes >= total_limit )); then
      continue
    fi
    printf '"%s" l2tpd "%s" *\n' "$username" "$password" >> "$chap_tmp"
  done < <(jq -c '.[]' "$IPSEC_CLIENTS_FILE" 2>/dev/null || true)
  install -m 0600 "$chap_tmp" "$IPSEC_CHAP_SECRETS"
  rm -f "$chap_tmp"
}

write_ipsec_ppp_accounting_hooks(){
  install -d -m 0755 /etc/ppp/ip-up.d /etc/ppp/ip-down.d
  cat > "$IPSEC_PPP_HOOK_UP" <<EOF
#!/usr/bin/env bash
set -Eeuo pipefail
DB="${IPSEC_ACCOUNTING_DB}"
IFNAME="\${1:-\${IFNAME:-}}"
USERNAME="\${PEERNAME:-}"
[[ -n "\$IFNAME" && "\$IFNAME" == ppp* && -n "\$USERNAME" && -f "\$DB" ]] || exit 0
escape_sql_literal(){ printf '%s' "\$1" | sed "s/'/''/g"; }
esc_user="\$(escape_sql_literal "\$USERNAME")"
client_id="\$(sqlite3 -noheader "\$DB" "SELECT client_id FROM clients WHERE username='\$esc_user' LIMIT 1;" 2>/dev/null || true)"
[[ -n "\$client_id" ]] || exit 0
session_key="\${client_id}|\${IFNAME}"
pppd_pid="\${PPPD_PID:-0}"
if [[ ! "\$pppd_pid" =~ ^[0-9]+$ || "\$pppd_pid" -le 1 ]]; then
  pppd_pid="\$(ps -eo pid,args | awk -v ifn="\$IFNAME" '/[p]ppd/ && index(\$0,ifn){print \$1; exit}')"
fi
[[ "\$pppd_pid" =~ ^[0-9]+$ ]] || pppd_pid=0
rx=0; tx=0
[[ -r "/sys/class/net/\$IFNAME/statistics/rx_bytes" ]] && rx="\$(cat "/sys/class/net/\$IFNAME/statistics/rx_bytes" 2>/dev/null || echo 0)"
[[ -r "/sys/class/net/\$IFNAME/statistics/tx_bytes" ]] && tx="\$(cat "/sys/class/net/\$IFNAME/statistics/tx_bytes" 2>/dev/null || echo 0)"
[[ "\$rx" =~ ^[0-9]+$ ]] || rx=0
[[ "\$tx" =~ ^[0-9]+$ ]] || tx=0
now="\$(date +%s)"
esc_client_id="\$(escape_sql_literal "\$client_id")"
esc_session="\$(escape_sql_literal "\$session_key")"
esc_ifname="\$(escape_sql_literal "\$IFNAME")"
sqlite3 "\$DB" "INSERT OR IGNORE INTO usage_totals(client_id,used_bytes,updated_at) VALUES('\$esc_client_id',0,\$now);" >/dev/null 2>&1 || true
sqlite3 "\$DB" "INSERT INTO session_counters(session_key,client_id,ifname,pppd_pid,last_rx,last_tx,last_seen_at) VALUES('\$esc_session','\$esc_client_id','\$esc_ifname',\$pppd_pid,\$rx,\$tx,\$now) ON CONFLICT(session_key) DO UPDATE SET ifname=excluded.ifname,pppd_pid=excluded.pppd_pid,last_rx=excluded.last_rx,last_tx=excluded.last_tx,last_seen_at=excluded.last_seen_at;" >/dev/null 2>&1 || true
exit 0
EOF
  chmod 0755 "$IPSEC_PPP_HOOK_UP"

  cat > "$IPSEC_PPP_HOOK_DOWN" <<EOF
#!/usr/bin/env bash
set -Eeuo pipefail
DB="${IPSEC_ACCOUNTING_DB}"
IFNAME="\${1:-\${IFNAME:-}}"
[[ -n "\$IFNAME" && "\$IFNAME" == ppp* && -f "\$DB" ]] || exit 0
escape_sql_literal(){ printf '%s' "\$1" | sed "s/'/''/g"; }
esc_ifname="\$(escape_sql_literal "\$IFNAME")"
sqlite3 "\$DB" "DELETE FROM session_counters WHERE ifname='\$esc_ifname';" >/dev/null 2>&1 || true
exit 0
EOF
  chmod 0755 "$IPSEC_PPP_HOOK_DOWN"
}

write_ipsec_accounting_script(){
  cat > "$IPSEC_ACCOUNTING_SCRIPT" <<EOF
#!/usr/bin/env bash
set -Eeuo pipefail
DB="${IPSEC_ACCOUNTING_DB}"
CLIENTS_FILE="${IPSEC_CLIENTS_FILE}"
CHAP_FILE="${IPSEC_CHAP_SECRETS}"
HEARTBEAT_FILE="${IPSEC_ACCOUNTING_HEARTBEAT}"
STALE_AFTER_SEC=600

escape_sql_literal(){ printf '%s' "\$1" | sed "s/'/''/g"; }

write_heartbeat(){
  local ok_flag="\$1" error_msg="\$2" now_epoch
  now_epoch="\$(date +%s)"
  jq -n --argjson ok "\$ok_flag" --arg err "\$error_msg" --argjson now "\$now_epoch" '{ok:\$ok,error:\$err,runAtEpoch:\$now,runAtUtc:(\$now|todate)}' > "\${HEARTBEAT_FILE}.tmp"
  mv -f "\${HEARTBEAT_FILE}.tmp" "\$HEARTBEAT_FILE"
  chmod 0640 "\$HEARTBEAT_FILE" || true
}

refresh_chap(){
  local chap_tmp row id username password enable esc_id policy enabled_policy total_limit expiry_ms used_bytes now_ms
  chap_tmp="\$(mktemp)"
  printf '# OmniRelay managed IPSec/L2TP users\n' > "\$chap_tmp"
  now_ms=\$(( \$(date +%s) * 1000 ))
  while IFS= read -r row; do
    id="\$(jq -r '.id // empty' <<<"\$row")"
    enable="\$(jq -r '.enable // true' <<<"\$row")"
    username="\$(jq -r '.username // empty' <<<"\$row")"
    password="\$(jq -r '.password // empty' <<<"\$row")"
    [[ -n "\$id" && -n "\$username" && -n "\$password" && "\$enable" == "true" ]] || continue
    esc_id="\$(escape_sql_literal "\$id")"
    policy="\$(sqlite3 -csv -noheader "\$DB" "SELECT c.enabled,c.total_bytes_limit,c.expiry_unix_ms,COALESCE(u.used_bytes,0) FROM clients c LEFT JOIN usage_totals u ON u.client_id=c.client_id WHERE c.client_id='\${esc_id}' LIMIT 1;" 2>/dev/null || true)"
    [[ -n "\$policy" ]] || continue
    IFS=',' read -r enabled_policy total_limit expiry_ms used_bytes <<<"\$policy"
    [[ "\$enabled_policy" =~ ^[0-9]+$ ]] || enabled_policy=0
    [[ "\$total_limit" =~ ^[0-9]+$ ]] || total_limit=0
    [[ "\$expiry_ms" =~ ^[0-9]+$ ]] || expiry_ms=0
    [[ "\$used_bytes" =~ ^[0-9]+$ ]] || used_bytes=0
    (( enabled_policy == 1 )) || continue
    if (( expiry_ms > 0 && now_ms >= expiry_ms )); then
      continue
    fi
    if (( total_limit > 0 && used_bytes >= total_limit )); then
      continue
    fi
    printf '"%s" l2tpd "%s" *\n' "\$username" "\$password" >> "\$chap_tmp"
  done < <(jq -c '.[]' "\$CLIENTS_FILE" 2>/dev/null || true)
  install -m 0600 "\$chap_tmp" "\$CHAP_FILE"
  rm -f "\$chap_tmp"
}

main(){
  declare -A delta_by_client=()
  local now_sec now_ms cutoff
  now_sec="\$(date +%s)"
  now_ms=\$(( now_sec * 1000 ))
  cutoff=\$(( now_sec - STALE_AFTER_SEC ))

  while IFS=',' read -r session_key client_id ifname pppd_pid last_rx last_tx; do
    [[ -n "\$session_key" && -n "\$client_id" && -n "\$ifname" ]] || continue
    [[ "\$last_rx" =~ ^[0-9]+$ ]] || last_rx=0
    [[ "\$last_tx" =~ ^[0-9]+$ ]] || last_tx=0
    [[ "\$pppd_pid" =~ ^[0-9]+$ ]] || pppd_pid=0
    if [[ ! -r "/sys/class/net/\$ifname/statistics/rx_bytes" || ! -r "/sys/class/net/\$ifname/statistics/tx_bytes" ]]; then
      continue
    fi
    curr_rx="\$(cat "/sys/class/net/\$ifname/statistics/rx_bytes" 2>/dev/null || echo 0)"
    curr_tx="\$(cat "/sys/class/net/\$ifname/statistics/tx_bytes" 2>/dev/null || echo 0)"
    [[ "\$curr_rx" =~ ^[0-9]+$ ]] || curr_rx=0
    [[ "\$curr_tx" =~ ^[0-9]+$ ]] || curr_tx=0
    delta_rx=\$(( curr_rx - last_rx )); (( delta_rx < 0 )) && delta_rx=0
    delta_tx=\$(( curr_tx - last_tx )); (( delta_tx < 0 )) && delta_tx=0
    delta_total=\$(( delta_rx + delta_tx ))
    delta_by_client["\$client_id"]=\$(( \${delta_by_client["\$client_id"]:-0} + delta_total ))
    esc_session="\$(escape_sql_literal "\$session_key")"
    sqlite3 "\$DB" "UPDATE session_counters SET last_rx=\$curr_rx,last_tx=\$curr_tx,last_seen_at=\$now_sec WHERE session_key='\$esc_session';" >/dev/null 2>&1 || true
  done < <(sqlite3 -csv -noheader "\$DB" "SELECT session_key,client_id,ifname,pppd_pid,last_rx,last_tx FROM session_counters;" 2>/dev/null || true)

  sql_tmp="\$(mktemp)"
  {
    echo "PRAGMA busy_timeout=5000;"
    echo "BEGIN IMMEDIATE;"
    for client_id in "\${!delta_by_client[@]}"; do
      delta="\${delta_by_client["\$client_id"]}"
      (( delta > 0 )) || continue
      esc_client="\$(escape_sql_literal "\$client_id")"
      echo "INSERT INTO usage_totals(client_id,used_bytes,updated_at) VALUES('\$esc_client',0,\$now_sec) ON CONFLICT(client_id) DO NOTHING;"
      echo "UPDATE usage_totals SET used_bytes = used_bytes + \$delta, updated_at = \$now_sec WHERE client_id='\$esc_client';"
    done
    echo "DELETE FROM session_counters WHERE last_seen_at < \$cutoff;"
    echo "COMMIT;"
  } > "\$sql_tmp"
  sqlite3 "\$DB" < "\$sql_tmp"
  rm -f "\$sql_tmp"

  while IFS=',' read -r session_key client_id ifname pppd_pid enabled_policy total_limit expiry_ms used_bytes; do
    [[ "\$enabled_policy" =~ ^[0-9]+$ ]] || enabled_policy=0
    [[ "\$total_limit" =~ ^[0-9]+$ ]] || total_limit=0
    [[ "\$expiry_ms" =~ ^[0-9]+$ ]] || expiry_ms=0
    [[ "\$used_bytes" =~ ^[0-9]+$ ]] || used_bytes=0
    disconnect=0
    (( enabled_policy == 1 )) || disconnect=1
    if (( expiry_ms > 0 && now_ms >= expiry_ms )); then
      disconnect=1
    fi
    if (( total_limit > 0 && used_bytes >= total_limit )); then
      disconnect=1
    fi
    (( disconnect == 1 )) || continue
    if [[ "\$pppd_pid" =~ ^[0-9]+$ ]] && (( pppd_pid > 1 )); then
      kill -TERM "\$pppd_pid" >/dev/null 2>&1 || true
    fi
    if [[ -n "\$ifname" ]]; then
      ip link set "\$ifname" down >/dev/null 2>&1 || true
    fi
  done < <(sqlite3 -csv -noheader "\$DB" "SELECT s.session_key,s.client_id,s.ifname,s.pppd_pid,c.enabled,c.total_bytes_limit,c.expiry_unix_ms,COALESCE(u.used_bytes,0) FROM session_counters s JOIN clients c ON c.client_id=s.client_id LEFT JOIN usage_totals u ON u.client_id=s.client_id;" 2>/dev/null || true)

  refresh_chap
  write_heartbeat true ""
}

if main 2>/tmp/omnirelay-ipsec-accounting.err; then
  exit 0
fi
err_text="\$(tr '\n' ' ' </tmp/omnirelay-ipsec-accounting.err | sed 's/"/\\"/g' | xargs || true)"
rm -f /tmp/omnirelay-ipsec-accounting.err
write_heartbeat false "\${err_text:-accounting script failed}"
exit 1
EOF
  chmod 0750 "$IPSEC_ACCOUNTING_SCRIPT"
}

setup_runtime_services(){
  progress 38 "Setting up IPSec/L2TP runtime services"
  local redsocks_bin; redsocks_bin="$(command -v redsocks || true)"; [[ -n "$redsocks_bin" ]] || die "redsocks binary not found."
  cat > "/etc/systemd/system/${IPSEC_REDSOCKS_SERVICE}.service" <<EOF
[Unit]
Description=OmniRelay redsocks bridge
After=network-online.target
Wants=network-online.target
[Service]
Type=simple
ExecStart=${redsocks_bin} -c ${IPSEC_REDSOCKS_CONFIG}
Restart=always
RestartSec=2
[Install]
WantedBy=multi-user.target
EOF
  cat > "/etc/systemd/system/${IPSEC_RULES_SERVICE}.service" <<EOF
[Unit]
Description=OmniRelay IPSec/L2TP traffic rules
After=network-online.target ${IPSEC_REDSOCKS_SERVICE}.service ipsec.service xl2tpd.service
Wants=network-online.target ${IPSEC_REDSOCKS_SERVICE}.service ipsec.service xl2tpd.service
[Service]
Type=oneshot
RemainAfterExit=yes
ExecStart=${IPSEC_RULES_APPLY_SCRIPT}
ExecStop=${IPSEC_RULES_CLEAR_SCRIPT}
[Install]
WantedBy=multi-user.target
EOF
  cat > "/etc/systemd/system/${IPSEC_ACCOUNTING_SERVICE}.service" <<EOF
[Unit]
Description=OmniRelay IPSec/L2TP accounting sync
After=network-online.target ipsec.service xl2tpd.service
Wants=network-online.target ipsec.service xl2tpd.service
[Service]
Type=oneshot
ExecStart=/usr/bin/env bash ${IPSEC_ACCOUNTING_SCRIPT}
EOF
  cat > "/etc/systemd/system/${IPSEC_ACCOUNTING_TIMER}" <<EOF
[Unit]
Description=Run OmniRelay IPSec/L2TP accounting sync every 30 seconds
[Timer]
OnBootSec=45s
OnUnitActiveSec=30s
AccuracySec=5s
Unit=${IPSEC_ACCOUNTING_SERVICE}.service
Persistent=true
[Install]
WantedBy=timers.target
EOF
  systemctl daemon-reload
  systemctl enable --now "$IPSEC_REDSOCKS_SERVICE"
  systemctl enable --now ipsec >/dev/null 2>&1 || systemctl enable --now strongswan-starter >/dev/null 2>&1 || true
  systemctl enable --now xl2tpd >/dev/null 2>&1 || true
  systemctl enable --now "$IPSEC_RULES_SERVICE"
  systemctl enable --now "$IPSEC_ACCOUNTING_TIMER"
  systemctl start "$IPSEC_ACCOUNTING_SERVICE" || true
}

gen_cert(){ mkdir -p "${METADATA_DIR}/certs"; chmod 0700 "${METADATA_DIR}/certs"; local server="${VPS_IP:-$(hostname -I 2>/dev/null | awk '{print $1}')}" ; [[ -n "$server" ]] || server="localhost"; openssl req -x509 -nodes -newkey rsa:2048 -days 825 -keyout "${METADATA_DIR}/certs/omnipanel.key" -out "${METADATA_DIR}/certs/omnipanel.crt" -subj "/CN=${server}" >/dev/null 2>&1; chmod 0600 "${METADATA_DIR}/certs/omnipanel.key"; }
resolve_omnipanel_internal_port(){ local p; p="$(awk -F= '/^PORT=/{print $2; exit}' "$OMNIPANEL_ENV_FILE" 2>/dev/null | tr -d '[:space:]' || true)"; [[ "$p" =~ ^[0-9]+$ ]] && (( p >= 1 && p <= 65535 )) && { echo "$p"; return 0; }; [[ "${OMNIPANEL_INTERNAL_PORT:-0}" =~ ^[0-9]+$ ]] && (( OMNIPANEL_INTERNAL_PORT >= 1 && OMNIPANEL_INTERNAL_PORT <= 65535 )) && { echo "$OMNIPANEL_INTERNAL_PORT"; return 0; }; return 1; }
wait_omnipanel_ready(){ local p i c; p="$(resolve_omnipanel_internal_port || true)"; [[ -n "$p" ]] || die "OmniPanel internal port is not set."; for i in $(seq 1 30); do c="$(curl --noproxy '*' --silent --output /dev/null --write-out '%{http_code}' --max-time 4 "http://127.0.0.1:${p}/" 2>/dev/null || true)"; case "$c" in 200|301|302|307|308|401|403) return 0 ;; esac; sleep 1; done; systemctl --no-pager --full status "$OMNIPANEL_SERVICE" 2>&1 || true; journalctl -u "$OMNIPANEL_SERVICE" -n 80 --no-pager 2>&1 || true; die "OmniPanel service failed to start."; }
verify_nginx_panel_proxy(){ local i c; for i in $(seq 1 20); do c="$(curl --noproxy '*' --silent --insecure --output /dev/null --write-out '%{http_code}' --max-time 6 "https://127.0.0.1:${PANEL_PORT}/" 2>/dev/null || true)"; case "$c" in 200|301|302|307|308|401|403) return 0 ;; esac; sleep 1; done; die "Nginx HTTPS reverse proxy check failed on 127.0.0.1:${PANEL_PORT}."; }

deploy_panel(){
  progress 60 "Deploying OmniPanel artifact"
  install -d -m 0755 "$OMNIPANEL_APP_DIR" "$OMNIPANEL_APP_DIR/releases"
  local rel dir node_bin
  rel="$(date +%Y%m%d%H%M%S)"; dir="${OMNIPANEL_APP_DIR}/releases/${rel}"; mkdir -p "$dir"
  configure_proxy; curl -fSL "https://omnirelay.net/download/omni-gateway" -o /tmp/omni-gateway.tar.gz; clear_proxy
  tar -xzf /tmp/omni-gateway.tar.gz -C "$dir"
  [[ -f "$dir/server.js" ]] || { nd="$(find "$dir" -mindepth 1 -maxdepth 1 -type d | head -n1 || true)"; [[ -n "$nd" ]] && cp -a "$nd"/. "$dir"/; }
  [[ -f "$dir/server.js" ]] || die "omnipanel artifact missing server.js"
  node_bin="$(resolve_node_bin || true)"; [[ -n "$node_bin" && -x "$node_bin" ]] || die "Node.js executable not found."
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
OMNIRELAY_ACTIVE_PROTOCOL=ipsec_l2tp_hwdsl2
PANEL_PUBLIC_PORT=${PANEL_PORT}
PANEL_PUBLIC_HOST=${PANEL_DOMAIN:-${VPS_IP:-$(hostname -I 2>/dev/null | awk '{print $1}')}}
IPSEC_L2TP_CLIENTS_FILE=${IPSEC_CLIENTS_FILE}
IPSEC_L2TP_PSK_FILE=${IPSEC_PSK_FILE}
IPSEC_L2TP_ACCOUNTING_DB=${IPSEC_ACCOUNTING_DB}
IPSEC_L2TP_SYNC_COMMAND=${IPSEC_SYNC_COMMAND}
EOF
  cat > "/etc/systemd/system/${OMNIPANEL_SERVICE}.service" <<EOF
[Unit]
Description=OmniRelay OmniPanel
After=network.target ipsec.service xl2tpd.service
Wants=ipsec.service xl2tpd.service
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
  echo "omnigateway ALL=(root) NOPASSWD:/usr/local/sbin/omnirelay-gatewayctl sync-clients" > /etc/sudoers.d/omnigateway-ipsec
  chmod 0440 /etc/sudoers.d/omnigateway-ipsec
  ln -sfn "$dir" "${OMNIPANEL_APP_DIR}/current"
  chown -R omnigateway:omnigateway "$OMNIPANEL_APP_DIR"
  chown omnigateway:omnigateway "$PANEL_AUTH_FILE" || true
  chmod 0640 "$PANEL_AUTH_FILE" || true
  set_gateway_file_access
  systemctl daemon-reload
  systemctl enable --now "$OMNIPANEL_SERVICE"
}

configure_nginx(){
  progress 76 "Configuring nginx HTTPS reverse-proxy for OmniPanel"
  local p host
  p="$(resolve_omnipanel_internal_port || true)"; [[ -n "$p" ]] || die "Cannot determine OmniPanel internal port for nginx config."
  host="${VPS_IP:-$(hostname -I 2>/dev/null | awk '{print $1}')}"
  host="${host%% *}"
  load_omnipanel_common
  omnipanel_configure_nginx_proxy \
    "$PANEL_PORT" \
    "$p" \
    "$METADATA_DIR" \
    "$PANEL_DOMAIN" \
    "$PANEL_DOMAIN_ONLY" \
    "$PANEL_SSL_ENABLED" \
    "$PANEL_SSL_MODE" \
    "$PANEL_CERT_FILE" \
    "$PANEL_KEY_FILE" \
    "$host" \
    "$VPS_IP"
}

configure_host_firewall(){
  command -v ufw >/dev/null 2>&1 || return 0
  ufw status 2>/dev/null | grep -q "^Status: active" || return 0
  ufw allow "${PANEL_PORT}/tcp" >/dev/null 2>&1 || true
  ufw allow "${SSH_PORT}/tcp" >/dev/null 2>&1 || true
  ufw allow "${IPSEC_IKE_PORT}/udp" >/dev/null 2>&1 || true
  ufw allow "${IPSEC_NATT_PORT}/udp" >/dev/null 2>&1 || true
  ufw allow "${IPSEC_L2TP_PORT}/udp" >/dev/null 2>&1 || true
  if [[ "$PANEL_SSL_ENABLED" == "true" && "$PANEL_SSL_MODE" == "letsencrypt" ]]; then
    ufw allow "80/tcp" >/dev/null 2>&1 || true
  fi
}

write_metadata(){
  progress 88 "Persisting managed gateway metadata"
  jq -n \
    --arg proto "ipsec_l2tp_hwdsl2" \
    --arg vps "$VPS_IP" \
    --arg panel "$PANEL_PORT" \
    --arg user "$PANEL_USER" \
    --arg panelDomain "$PANEL_DOMAIN" \
    --arg panelDomainOnly "$PANEL_DOMAIN_ONLY" \
    --arg panelSslEnabled "$PANEL_SSL_ENABLED" \
    --arg panelSslMode "$PANEL_SSL_MODE" \
    --arg clientsFile "$IPSEC_CLIENTS_FILE" \
    --arg pskFile "$IPSEC_PSK_FILE" \
    --arg accountingDb "$IPSEC_ACCOUNTING_DB" \
    --arg intp "$OMNIPANEL_INTERNAL_PORT" \
    --arg clientDns "$IPSEC_CLIENT_DNS" \
    '{
      active_protocol:$proto,
      vps_ip:$vps,
      public_port:1701,
      omnipanel_public_port:($panel|tonumber),
      omnipanel_internal_port:($intp|tonumber),
      omnipanel_username:$user,
      omnipanel_domain:$panelDomain,
      omnipanel_domain_only:($panelDomainOnly == "true"),
      omnipanel_ssl_enabled:($panelSslEnabled == "true"),
      omnipanel_ssl_mode:$panelSslMode,
      ipsec_l2tp:{
        ports:{ike:500,natt:4500,l2tp:1701},
        clients_file:$clientsFile,
        psk_file:$pskFile,
        accounting_db:$accountingDb,
        client_dns:$clientDns
      },
      created_at_utc:(now|todate)
    }' > "$METADATA_FILE"
  chmod 0600 "$METADATA_FILE"
}

dns_apply(){ progress 94 "Applying DNS-through-tunnel profile"; jq -n --arg m "$DNS_MODE" --arg d "$DOH_ENDPOINTS" --argjson u "$( [[ "$DNS_UDP_ONLY" == "true" ]] && echo true || echo false )" '{mode:$m,dohEndpoints:$d,dnsUdpOnly:$u,updatedAtUtc:(now|todate)}' > "$DNS_PROFILE_FILE"; progress 100 "DNS profile applied"; }
dns_status_json(){ local cfg rule mode doh udpOnly udp53 path; if [[ -f "$DNS_PROFILE_FILE" ]]; then cfg=true; rule=true; mode="$(jq -r '.mode' "$DNS_PROFILE_FILE")"; doh="$(jq -r '.dohEndpoints' "$DNS_PROFILE_FILE")"; udpOnly="$(jq -r '.dnsUdpOnly' "$DNS_PROFILE_FILE")"; else cfg=false; rule=false; mode=unknown; doh=""; udpOnly=false; fi; udp53="$(check_listener 53)"; path=false; [[ "$cfg" == true && "$rule" == true ]] && path=true; printf '{"dnsConfigPresent":%s,"dnsRuleActive":%s,"dohReachableViaTunnel":%s,"udp53PathReady":%s,"dnsPathHealthy":%s,"dnsMode":"%s","dnsUdpOnly":%s,"dohEndpoints":"%s"}\n' "$cfg" "$rule" "$cfg" "$udp53" "$path" "$mode" "$udpOnly" "$(printf '%s' "$doh" | sed 's/"/\\"/g')"; }
dns_status(){ dns_status_json; }
dns_repair(){ progress 94 "Repairing DNS profile"; dns_apply; }

sync_clients_cmd(){
  require_root
  ensure_clients_seed_file
  ensure_shared_psk
  init_ipsec_accounting_db
  write_ipsec_ppp_accounting_hooks
  write_ipsec_accounting_script
  local row id email enable username password total_gb_raw total_gb_bytes expiry_raw expiry_unix_ms now_sec sql_tmp enable_int
  now_sec="$(date +%s)"
  sql_tmp="$(mktemp)"
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
    printf "INSERT OR IGNORE INTO sync_keep(client_id) VALUES('%s');\n" "$(printf '%s' "$id" | sed "s/'/''/g")" >> "$sql_tmp"
    printf "INSERT INTO clients(client_id,username,enabled,total_bytes_limit,expiry_unix_ms,created_at,updated_at) VALUES('%s','%s',%s,%s,%s,%s,%s) ON CONFLICT(client_id) DO UPDATE SET username=excluded.username,enabled=excluded.enabled,total_bytes_limit=excluded.total_bytes_limit,expiry_unix_ms=excluded.expiry_unix_ms,updated_at=excluded.updated_at;\n" \
      "$(printf '%s' "$id" | sed "s/'/''/g")" \
      "$(printf '%s' "$username" | sed "s/'/''/g")" \
      "$enable_int" "$total_gb_bytes" "$expiry_unix_ms" "$now_sec" "$now_sec" >> "$sql_tmp"
    printf "INSERT OR IGNORE INTO usage_totals(client_id,used_bytes,updated_at) VALUES('%s',0,%s);\n" \
      "$(printf '%s' "$id" | sed "s/'/''/g")" "$now_sec" >> "$sql_tmp"
    printf "UPDATE usage_totals SET updated_at=%s WHERE client_id='%s';\n" \
      "$now_sec" "$(printf '%s' "$id" | sed "s/'/''/g")" >> "$sql_tmp"
  done < <(jq -c '.[]' "$IPSEC_CLIENTS_FILE" 2>/dev/null || true)
  cat >> "$sql_tmp" <<'SQL'
DELETE FROM clients WHERE client_id NOT IN (SELECT client_id FROM sync_keep);
DELETE FROM usage_totals WHERE client_id NOT IN (SELECT client_id FROM sync_keep);
DELETE FROM session_counters WHERE client_id NOT IN (SELECT client_id FROM sync_keep);
DROP TABLE sync_keep;
COMMIT;
SQL
  sqlite3 "$IPSEC_ACCOUNTING_DB" < "$sql_tmp"
  rm -f "$sql_tmp"
  refresh_chap_secrets_from_policy
  cat > /etc/ipsec.secrets <<EOF
%any  %any  : PSK "${IPSEC_SHARED_PSK}"
EOF
  chmod 0600 /etc/ipsec.secrets
  set_gateway_file_access
  systemctl daemon-reload
  systemctl restart "$IPSEC_REDSOCKS_SERVICE"
  systemctl restart ipsec >/dev/null 2>&1 || systemctl restart strongswan-starter >/dev/null 2>&1 || true
  systemctl restart xl2tpd
  systemctl restart "$IPSEC_RULES_SERVICE"
  systemctl start "$IPSEC_ACCOUNTING_SERVICE" >/dev/null 2>&1 || true
  chown root:omnigateway "$IPSEC_ACCOUNTING_HEARTBEAT" 2>/dev/null || true
  chmod 0640 "$IPSEC_ACCOUNTING_HEARTBEAT" 2>/dev/null || true
  local ipsec_state xl2tpd_state
  ipsec_state="$(systemctl is-active ipsec 2>/dev/null || systemctl is-active strongswan-starter 2>/dev/null || echo inactive)"
  xl2tpd_state="$(systemctl is-active xl2tpd 2>/dev/null || echo inactive)"
  [[ "$ipsec_state" == "active" ]] || die "IPSec service is not active after syncing clients."
  [[ "$xl2tpd_state" == "active" ]] || die "xl2tpd service is not active after syncing clients."
  echo "ok"
}

status_cmd(){
  local sshState ipsecState xl2tpdState redsocksState panelState nginxState fail2 backendListener publicListener panelListener internalListener dns iport ike natt l2tp
  local accountingTimerState accountingServiceState accountingDbReady accountingHealthy hbOk hbEpoch nowEpoch
  sshState="$(systemctl is-active ssh 2>/dev/null || systemctl is-active sshd 2>/dev/null || echo inactive)"
  ipsecState="$(systemctl is-active ipsec 2>/dev/null || systemctl is-active strongswan-starter 2>/dev/null || echo inactive)"
  xl2tpdState="$(systemctl is-active xl2tpd 2>/dev/null || echo inactive)"
  redsocksState="$(systemctl is-active "$IPSEC_REDSOCKS_SERVICE" 2>/dev/null || echo inactive)"
  accountingTimerState="$(systemctl is-active "$IPSEC_ACCOUNTING_TIMER" 2>/dev/null || echo inactive)"
  accountingServiceState="$(systemctl is-active "$IPSEC_ACCOUNTING_SERVICE" 2>/dev/null || echo inactive)"
  panelState="$(systemctl is-active "$OMNIPANEL_SERVICE" 2>/dev/null || echo inactive)"
  nginxState="$(systemctl is-active nginx 2>/dev/null || echo inactive)"
  fail2="disabled"
  iport="$(jq -r '.omnipanel_internal_port // 0' "$METADATA_FILE" 2>/dev/null || echo 0)"
  backendListener="$(check_listener "$BACKEND_PORT")"
  ike="$(check_udp_listener "$IPSEC_IKE_PORT")"; natt="$(check_udp_listener "$IPSEC_NATT_PORT")"; l2tp="$(check_udp_listener "$IPSEC_L2TP_PORT")"
  [[ "$ike" == "true" && "$natt" == "true" && "$l2tp" == "true" ]] && publicListener="true" || publicListener="false"
  panelListener="$(check_listener "$PANEL_PORT")"
  if [[ "$iport" =~ ^[0-9]+$ ]] && (( iport > 0 )); then internalListener="$(check_listener "$iport")"; else internalListener="false"; fi
  dns="$(dns_status_json)"
  accountingDbReady=false
  sqlite3 "$IPSEC_ACCOUNTING_DB" "SELECT 1;" >/dev/null 2>&1 && accountingDbReady=true || true
  hbOk=false
  hbEpoch=0
  if [[ -f "$IPSEC_ACCOUNTING_HEARTBEAT" ]]; then
    hbOk="$(jq -r '.ok // false' "$IPSEC_ACCOUNTING_HEARTBEAT" 2>/dev/null || echo false)"
    hbEpoch="$(jq -r '.runAtEpoch // 0' "$IPSEC_ACCOUNTING_HEARTBEAT" 2>/dev/null || echo 0)"
  fi
  nowEpoch="$(date +%s)"
  accountingHealthy=false
  if [[ "$accountingTimerState" == "active" && "$accountingDbReady" == "true" && "$hbOk" == "true" && "$hbEpoch" =~ ^[0-9]+$ ]]; then
    if (( hbEpoch > 0 && (nowEpoch - hbEpoch) <= 120 )); then
      accountingHealthy=true
    fi
  fi
  printf '{"activeProtocol":"ipsec_l2tp_hwdsl2","sshState":"%s","xuiState":"inactive","singBoxState":"inactive","openVpnState":"inactive","ipsecState":"%s","xl2tpdState":"%s","omniPanelState":"%s","nginxState":"%s","fail2banState":"%s","backendPort":%s,"publicPort":%s,"panelPort":%s,"omniPanelInternalPort":%s,"xuiPanelPort":0,"backendListener":%s,"publicListener":%s,"panelListener":%s,"omniPanelInternalListener":%s,"inboundId":"","dnsConfigPresent":%s,"dnsRuleActive":%s,"dohReachableViaTunnel":%s,"udp53PathReady":%s,"dnsPathHealthy":%s,"dnsMode":"%s","dnsUdpOnly":%s,"dohEndpoints":"%s","redsocksState":"%s","ipsecAccountingTimerState":"%s","ipsecAccountingServiceState":"%s","ipsecAccountingDbReady":%s,"ipsecAccountingHealthy":%s}\n' "$sshState" "$ipsecState" "$xl2tpdState" "$panelState" "$nginxState" "$fail2" "$BACKEND_PORT" "$IPSEC_L2TP_PORT" "$PANEL_PORT" "$iport" "$backendListener" "$publicListener" "$panelListener" "$internalListener" "$(jq -r '.dnsConfigPresent' <<<"$dns")" "$(jq -r '.dnsRuleActive' <<<"$dns")" "$(jq -r '.dohReachableViaTunnel' <<<"$dns")" "$(jq -r '.udp53PathReady' <<<"$dns")" "$(jq -r '.dnsPathHealthy' <<<"$dns")" "$(jq -r '.dnsMode' <<<"$dns")" "$(jq -r '.dnsUdpOnly' <<<"$dns")" "$(jq -r '.dohEndpoints' <<<"$dns" | sed 's/"/\\"/g')" "$redsocksState" "$accountingTimerState" "$accountingServiceState" "$accountingDbReady" "$accountingHealthy"
}

health_cmd(){
  local status healthy dnsLastError redsocksState
  status="$(status_cmd)"; healthy=true
  [[ "$(jq -r '.sshState' <<<"$status")" == "active" ]] || healthy=false
  [[ "$(jq -r '.ipsecState' <<<"$status")" == "active" ]] || healthy=false
  [[ "$(jq -r '.xl2tpdState' <<<"$status")" == "active" ]] || healthy=false
  [[ "$(jq -r '.omniPanelState' <<<"$status")" == "active" ]] || healthy=false
  [[ "$(jq -r '.nginxState' <<<"$status")" == "active" ]] || healthy=false
  [[ "$(jq -r '.backendListener' <<<"$status")" == "true" ]] || healthy=false
  [[ "$(jq -r '.publicListener' <<<"$status")" == "true" ]] || healthy=false
  [[ "$(jq -r '.panelListener' <<<"$status")" == "true" ]] || healthy=false
  [[ "$(jq -r '.omniPanelInternalListener' <<<"$status")" == "true" ]] || healthy=false
  [[ "$(jq -r '.dnsPathHealthy' <<<"$status")" == "true" ]] || healthy=false
  redsocksState="$(jq -r '.redsocksState // "inactive"' <<<"$status")"; [[ "$redsocksState" == "active" ]] || healthy=false
  [[ "$(jq -r '.ipsecAccountingHealthy // false' <<<"$status")" == "true" ]] || healthy=false
  dnsLastError=""
  [[ "$(jq -r '.dnsConfigPresent' <<<"$status")" == "true" ]] || dnsLastError="dnsConfigMissing"
  [[ "$(jq -r '.dnsRuleActive' <<<"$status")" == "true" ]] || dnsLastError="dnsRuleInactive"
  jq -c --argjson healthy "$( [[ "$healthy" == true ]] && echo true || echo false )" --arg dnsLastError "$dnsLastError" 'del(.redsocksState) + {healthy:$healthy,dnsLastError:$dnsLastError}' <<<"$status"
}

install_cmd(){
  require_root
  progress 3 "Validating platform"
  install -d -m 0755 "$METADATA_DIR"
  install -m 0755 "$0" /usr/local/sbin/omnirelay-gatewayctl
  if [[ -f "$OMNIPANEL_COMMON_SCRIPT" ]]; then
    install -d -m 0755 /usr/local/lib/omnirelay
    install -m 0755 "$OMNIPANEL_COMMON_SCRIPT" /usr/local/lib/omnirelay/omnipanel-common.sh
  fi
  if [[ -x "$IPSEC_RULES_CLEAR_SCRIPT" ]]; then
    "$IPSEC_RULES_CLEAR_SCRIPT" >/dev/null 2>&1 || true
  fi
  systemctl disable --now x-ui 2>/dev/null || true
  systemctl disable --now omnirelay-singbox 2>/dev/null || true
  systemctl disable --now omnirelay-openvpn omnirelay-openvpn-accounting.service omnirelay-openvpn-accounting.timer 2>/dev/null || true
  systemctl disable --now "$IPSEC_ACCOUNTING_TIMER" "$IPSEC_ACCOUNTING_SERVICE" "$IPSEC_REDSOCKS_SERVICE" "$IPSEC_RULES_SERVICE" 2>/dev/null || true
  rm -f \
    /etc/systemd/system/x-ui.service \
    /etc/systemd/system/omnirelay-singbox.service \
    /etc/systemd/system/omnirelay-openvpn.service \
    /etc/systemd/system/omnirelay-openvpn-accounting.service \
    /etc/systemd/system/omnirelay-openvpn-accounting.timer \
    /etc/systemd/system/${IPSEC_REDSOCKS_SERVICE}.service \
    /etc/systemd/system/${IPSEC_RULES_SERVICE}.service \
    /etc/systemd/system/${IPSEC_ACCOUNTING_SERVICE}.service \
    /etc/systemd/system/${IPSEC_ACCOUNTING_TIMER}
  rm -f /etc/sudoers.d/omnigateway-singbox /etc/sudoers.d/omnigateway-openvpn /etc/sudoers.d/omnigateway-ipsec
  rm -f /etc/sysctl.d/99-omnirelay-openvpn.conf /etc/sysctl.d/99-omnirelay-ipsec.conf /var/log/openvpn/omnirelay-status.log
  rm -f "$IPSEC_PPP_HOOK_UP" "$IPSEC_PPP_HOOK_DOWN"
  systemctl daemon-reload || true
  install_packages
  ensure_nodejs_runtime
  ensure_clients_seed_file
  ensure_shared_psk
  init_ipsec_accounting_db
  backup_ipsec_global_files
  install_hwdsl2_stack
  write_redsocks_config
  enable_ip_forward
  write_traffic_scripts
  write_ipsec_ppp_accounting_hooks
  write_ipsec_accounting_script
  setup_runtime_services
  deploy_panel
  sync_clients_cmd
  wait_omnipanel_ready
  configure_nginx
  configure_host_firewall
  write_metadata
  dns_apply
  progress 100 "Gateway install completed"
  local endpoint scheme
  endpoint="${PANEL_DOMAIN:-${VPS_IP:-$(hostname -I 2>/dev/null | awk '{print $1}')}}"
  endpoint="${endpoint%% *}"; [[ -n "$endpoint" ]] || endpoint="<VPS_IP_OR_HOSTNAME>"
  scheme="http"
  [[ "$PANEL_SSL_ENABLED" == "true" ]] && scheme="https"
  log "OmniPanel URL: ${scheme}://${endpoint}:${PANEL_PORT}/ | Username: ${PANEL_USER} | Password: ${PANEL_PASSWORD}"
}

start_cmd(){
  require_root
  progress 96 "Starting gateway services"
  systemctl enable --now "$IPSEC_REDSOCKS_SERVICE" >/dev/null 2>&1 || true
  systemctl enable --now ipsec >/dev/null 2>&1 || systemctl enable --now strongswan-starter >/dev/null 2>&1 || true
  systemctl enable --now xl2tpd >/dev/null 2>&1 || true
  systemctl enable --now "$IPSEC_RULES_SERVICE" >/dev/null 2>&1 || true
  systemctl enable --now "$IPSEC_ACCOUNTING_TIMER" >/dev/null 2>&1 || true
  systemctl start "$IPSEC_ACCOUNTING_SERVICE" >/dev/null 2>&1 || true
  systemctl enable --now "$OMNIPANEL_SERVICE" nginx >/dev/null 2>&1 || true
  progress 100 "Gateway start completed"
}

stop_cmd(){
  require_root
  progress 96 "Stopping gateway services"
  systemctl stop "$IPSEC_ACCOUNTING_TIMER" "$IPSEC_ACCOUNTING_SERVICE" "$OMNIPANEL_SERVICE" "$IPSEC_RULES_SERVICE" xl2tpd "$IPSEC_REDSOCKS_SERVICE" nginx >/dev/null 2>&1 || true
  systemctl stop ipsec >/dev/null 2>&1 || systemctl stop strongswan-starter >/dev/null 2>&1 || true
  progress 100 "Gateway stop completed"
}

uninstall_cmd(){
  require_root
  progress 96 "Uninstalling gateway"
  systemctl disable --now "$IPSEC_ACCOUNTING_TIMER" "$IPSEC_ACCOUNTING_SERVICE" "$OMNIPANEL_SERVICE" "$IPSEC_RULES_SERVICE" xl2tpd "$IPSEC_REDSOCKS_SERVICE" nginx >/dev/null 2>&1 || true
  systemctl disable --now ipsec >/dev/null 2>&1 || systemctl disable --now strongswan-starter >/dev/null 2>&1 || true
  systemctl disable --now x-ui omnirelay-singbox omnirelay-openvpn omnirelay-openvpn-accounting.timer omnirelay-openvpn-accounting.service >/dev/null 2>&1 || true
  if [[ -x "$IPSEC_RULES_CLEAR_SCRIPT" ]]; then
    "$IPSEC_RULES_CLEAR_SCRIPT" >/dev/null 2>&1 || true
  fi
  rm -f \
    /etc/systemd/system/${OMNIPANEL_SERVICE}.service \
    /etc/systemd/system/${IPSEC_REDSOCKS_SERVICE}.service \
    /etc/systemd/system/${IPSEC_RULES_SERVICE}.service \
    /etc/systemd/system/${IPSEC_ACCOUNTING_SERVICE}.service \
    /etc/systemd/system/${IPSEC_ACCOUNTING_TIMER} \
    /etc/systemd/system/x-ui.service \
    /etc/systemd/system/omnirelay-singbox.service \
    /etc/systemd/system/omnirelay-openvpn.service \
    /etc/systemd/system/omnirelay-openvpn-accounting.service \
    /etc/systemd/system/omnirelay-openvpn-accounting.timer \
    /etc/systemd/system/omnirelay-redsocks.service
  rm -f /etc/nginx/sites-enabled/omnirelay-omnipanel.conf /etc/nginx/sites-available/omnirelay-omnipanel.conf /etc/sudoers.d/omnigateway-ipsec /etc/sudoers.d/omnigateway-openvpn /etc/sudoers.d/omnigateway-singbox /usr/local/sbin/omnirelay-gatewayctl /etc/sysctl.d/99-omnirelay-ipsec.conf /etc/sysctl.d/99-omnirelay-openvpn.conf /var/log/openvpn/omnirelay-status.log
  rm -f /usr/local/lib/omnirelay/omnipanel-common.sh
  rm -f "$IPSEC_PPP_HOOK_UP" "$IPSEC_PPP_HOOK_DOWN"
  restore_ipsec_global_files
  rm -rf "$METADATA_DIR" "$OMNIPANEL_APP_DIR"
  systemctl daemon-reload
  progress 100 "Gateway uninstall completed"
}

parse_args(){
  if [[ $# -gt 0 && ! "$1" =~ ^- ]]; then COMMAND="$1"; shift; fi
  while [[ $# -gt 0 ]]; do
    case "$1" in
      --public-port) PUBLIC_PORT="${2:-}"; shift 2 ;;
      --panel-port) PANEL_PORT="${2:-}"; shift 2 ;;
      --backend-port) BACKEND_PORT="${2:-}"; shift 2 ;;
      --ssh-port) SSH_PORT="${2:-}"; shift 2 ;;
      --bootstrap-socks-port) BOOTSTRAP_SOCKS_PORT="${2:-}"; shift 2 ;;
      --proxy-check-url) PROXY_CHECK_URL="${2:-}"; shift 2 ;;
      --ipsec-client-dns) IPSEC_CLIENT_DNS="${2:-}"; shift 2 ;;
      --dns-mode) DNS_MODE="${2:-}"; shift 2 ;;
      --doh-endpoints) DOH_ENDPOINTS="${2:-}"; shift 2 ;;
      --dns-udp-only) DNS_UDP_ONLY="${2:-}"; shift 2 ;;
      --vps-ip) VPS_IP="${2:-}"; shift 2 ;;
      --tunnel-user) TUNNEL_USER="${2:-}"; shift 2 ;;
      --tunnel-auth) TUNNEL_AUTH="${2:-}"; shift 2 ;;
      --panel-user) PANEL_USER="${2:-}"; shift 2 ;;
      --panel-password) PANEL_PASSWORD="${2:-}"; shift 2 ;;
      --panel-base-path) PANEL_BASE_PATH="${2:-}"; shift 2 ;;
      --panel-domain) PANEL_DOMAIN="${2:-}"; shift 2 ;;
      --panel-domain-only) PANEL_DOMAIN_ONLY="${2:-}"; shift 2 ;;
      --panel-ssl) PANEL_SSL_ENABLED="${2:-}"; shift 2 ;;
      --panel-ssl-mode) PANEL_SSL_MODE="${2:-}"; shift 2 ;;
      --panel-cert-file) PANEL_CERT_FILE="${2:-}"; shift 2 ;;
      --panel-key-file) PANEL_KEY_FILE="${2:-}"; shift 2 ;;
      --json) shift ;;
      -h|--help) usage; exit 0 ;;
      *) die "Unknown option: $1" ;;
    esac
  done
}

main(){
  parse_args "$@"
  PANEL_DOMAIN_ONLY="$(normalize_bool "$PANEL_DOMAIN_ONLY")"
  PANEL_SSL_ENABLED="$(normalize_bool "$PANEL_SSL_ENABLED")"
  PANEL_SSL_MODE="$(normalize_panel_ssl_mode "$PANEL_SSL_MODE")"
  PANEL_DOMAIN="$(printf '%s' "$PANEL_DOMAIN" | xargs)"
  validate_port "$PUBLIC_PORT" "--public-port"
  validate_port "$PANEL_PORT" "--panel-port"
  validate_port "$BACKEND_PORT" "--backend-port"
  validate_port "$SSH_PORT" "--ssh-port"
  validate_port "$BOOTSTRAP_SOCKS_PORT" "--bootstrap-socks-port"
  [[ "$DNS_MODE" == "hybrid" || "$DNS_MODE" == "doh" || "$DNS_MODE" == "udp" ]] || die "--dns-mode must be hybrid|doh|udp"
  [[ "$DNS_UDP_ONLY" == "true" || "$DNS_UDP_ONLY" == "false" ]] || die "--dns-udp-only must be true or false"
  if [[ "$COMMAND" == "install" ]]; then
    if [[ "$PANEL_DOMAIN_ONLY" == "true" && -z "$PANEL_DOMAIN" ]]; then
      die "--panel-domain is required when --panel-domain-only=true."
    fi
    if [[ "$PANEL_SSL_ENABLED" == "true" && -z "$PANEL_DOMAIN" ]]; then
      die "--panel-domain is required when --panel-ssl=true."
    fi
    if [[ "$PANEL_SSL_ENABLED" == "true" && "$PANEL_SSL_MODE" == "uploaded" ]]; then
      [[ -n "$PANEL_CERT_FILE" ]] || die "--panel-cert-file is required for --panel-ssl-mode uploaded."
      [[ -n "$PANEL_KEY_FILE" ]] || die "--panel-key-file is required for --panel-ssl-mode uploaded."
      [[ -f "$PANEL_CERT_FILE" ]] || die "--panel-cert-file not found: ${PANEL_CERT_FILE}"
      [[ -f "$PANEL_KEY_FILE" ]] || die "--panel-key-file not found: ${PANEL_KEY_FILE}"
    fi
  fi
  if [[ "$PUBLIC_PORT" != "$IPSEC_L2TP_PORT" ]]; then log "Ignoring --public-port=${PUBLIC_PORT}; IPSec/L2TP uses fixed UDP ports 500/4500/1701."; PUBLIC_PORT="$IPSEC_L2TP_PORT"; fi
  [[ "$PANEL_PORT" != "$IPSEC_L2TP_PORT" ]] || die "--panel-port must differ from fixed IPSec/L2TP port ${IPSEC_L2TP_PORT}."
  case "$COMMAND" in
    install) install_cmd ;;
    uninstall) uninstall_cmd ;;
    start) start_cmd ;;
    stop) stop_cmd ;;
    get-protocol) get_protocol_cmd ;;
    status) status_cmd ;;
    health) health_cmd ;;
    dns-apply) dns_apply ;;
    dns-status) dns_status ;;
    dns-repair) dns_repair ;;
    sync-clients) sync_clients_cmd ;;
    *) die "Unknown command: ${COMMAND}. Expected install|uninstall|start|stop|get-protocol|status|health|dns-apply|dns-status|dns-repair|sync-clients" ;;
  esac
}

main "$@"
