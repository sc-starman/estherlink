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

IPSEC_DIR="${METADATA_DIR}/ipsec"
IPSEC_CLIENTS_FILE="${OMNIPANEL_APP_DIR}/ipsec_l2tp_clients.json"
IPSEC_PSK_FILE="${IPSEC_DIR}/shared_psk"
IPSEC_REDSOCKS_SERVICE="omnirelay-redsocks"
IPSEC_REDSOCKS_CONFIG="${IPSEC_DIR}/redsocks.conf"
IPSEC_REDSOCKS_LOCAL_PORT=12345
IPSEC_RULES_SERVICE="omnirelay-ipsec-rules"
IPSEC_RULES_APPLY_SCRIPT="${IPSEC_DIR}/apply-rules.sh"
IPSEC_RULES_CLEAR_SCRIPT="${IPSEC_DIR}/clear-rules.sh"

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
Usage: sudo ./${SCRIPT_NAME} <install|uninstall|start|stop|status|health|dns-apply|dns-status|dns-repair|sync-clients> [options]
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
--json
EOF
}

configure_proxy(){
  local proxy_url="socks5h://127.0.0.1:${BOOTSTRAP_SOCKS_PORT}"
  export ALL_PROXY="$proxy_url" HTTPS_PROXY="$proxy_url" HTTP_PROXY="$proxy_url" NO_PROXY="127.0.0.1,localhost"
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
  DEBIAN_FRONTEND=noninteractive apt-get install -y ca-certificates curl jq tar gzip openssl python3 iptables redsocks nginx nodejs ppp xl2tpd strongswan
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
  [[ -f "$IPSEC_CLIENTS_FILE" ]] || jq -n --arg id "$(random_string 16)" --arg p "$(random_string 24)" '[{id:$id,email:"omni-client@local",enable:true,username:"l2tp_client",password:$p}]' > "$IPSEC_CLIENTS_FILE"
}
ensure_shared_psk(){
  [[ -f "$IPSEC_PSK_FILE" ]] && IPSEC_SHARED_PSK="$(tr -d '\r\n' < "$IPSEC_PSK_FILE")"
  [[ -n "${IPSEC_SHARED_PSK:-}" ]] || IPSEC_SHARED_PSK="$(random_string 32)"
  printf '%s\n' "$IPSEC_SHARED_PSK" > "$IPSEC_PSK_FILE"
  chmod 0600 "$IPSEC_PSK_FILE"
}
set_gateway_file_access(){
  if id -u omnigateway >/dev/null 2>&1; then
    chown root:omnigateway "$IPSEC_PSK_FILE" "$IPSEC_CLIENTS_FILE" 2>/dev/null || true
    chmod 0640 "$IPSEC_PSK_FILE" "$IPSEC_CLIENTS_FILE" 2>/dev/null || true
  fi
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
  systemctl daemon-reload
  systemctl enable --now "$IPSEC_REDSOCKS_SERVICE"
  systemctl enable --now ipsec >/dev/null 2>&1 || systemctl enable --now strongswan-starter >/dev/null 2>&1 || true
  systemctl enable --now xl2tpd >/dev/null 2>&1 || true
  systemctl enable --now "$IPSEC_RULES_SERVICE"
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
PANEL_PUBLIC_HOST=${VPS_IP:-$(hostname -I 2>/dev/null | awk '{print $1}')}
IPSEC_L2TP_CLIENTS_FILE=${IPSEC_CLIENTS_FILE}
IPSEC_L2TP_PSK_FILE=${IPSEC_PSK_FILE}
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
  local p; p="$(resolve_omnipanel_internal_port || true)"; [[ -n "$p" ]] || die "Cannot determine OmniPanel internal port for nginx config."
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
        proxy_pass http://127.0.0.1:${p};
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

configure_host_firewall(){ command -v ufw >/dev/null 2>&1 || return 0; ufw status 2>/dev/null | grep -q "^Status: active" || return 0; ufw allow "${PANEL_PORT}/tcp" >/dev/null 2>&1 || true; ufw allow "${SSH_PORT}/tcp" >/dev/null 2>&1 || true; ufw allow "${IPSEC_IKE_PORT}/udp" >/dev/null 2>&1 || true; ufw allow "${IPSEC_NATT_PORT}/udp" >/dev/null 2>&1 || true; ufw allow "${IPSEC_L2TP_PORT}/udp" >/dev/null 2>&1 || true; }

write_metadata(){
  progress 88 "Persisting managed gateway metadata"
  jq -n --arg proto "ipsec_l2tp_hwdsl2" --arg vps "$VPS_IP" --arg panel "$PANEL_PORT" --arg user "$PANEL_USER" --arg clientsFile "$IPSEC_CLIENTS_FILE" --arg pskFile "$IPSEC_PSK_FILE" --arg intp "$OMNIPANEL_INTERNAL_PORT" --arg clientDns "$IPSEC_CLIENT_DNS" '{active_protocol:$proto,vps_ip:$vps,public_port:1701,omnipanel_public_port:($panel|tonumber),omnipanel_internal_port:($intp|tonumber),omnipanel_username:$user,ipsec_l2tp:{ports:{ike:500,natt:4500,l2tp:1701},clients_file:$clientsFile,psk_file:$pskFile,client_dns:$clientDns},created_at_utc:(now|todate)}' > "$METADATA_FILE"
  chmod 0600 "$METADATA_FILE"
}

dns_apply(){ progress 94 "Applying DNS-through-tunnel profile"; jq -n --arg m "$DNS_MODE" --arg d "$DOH_ENDPOINTS" --argjson u "$( [[ "$DNS_UDP_ONLY" == "true" ]] && echo true || echo false )" '{mode:$m,dohEndpoints:$d,dnsUdpOnly:$u,updatedAtUtc:(now|todate)}' > "$DNS_PROFILE_FILE"; progress 100 "DNS profile applied"; }
dns_status_json(){ local cfg rule mode doh udpOnly udp53 path; if [[ -f "$DNS_PROFILE_FILE" ]]; then cfg=true; rule=true; mode="$(jq -r '.mode' "$DNS_PROFILE_FILE")"; doh="$(jq -r '.dohEndpoints' "$DNS_PROFILE_FILE")"; udpOnly="$(jq -r '.dnsUdpOnly' "$DNS_PROFILE_FILE")"; else cfg=false; rule=false; mode=unknown; doh=""; udpOnly=false; fi; udp53="$(check_listener 53)"; path=false; [[ "$cfg" == true && "$rule" == true ]] && path=true; printf '{"dnsConfigPresent":%s,"dnsRuleActive":%s,"dohReachableViaTunnel":%s,"udp53PathReady":%s,"dnsPathHealthy":%s,"dnsMode":"%s","dnsUdpOnly":%s,"dohEndpoints":"%s"}\n' "$cfg" "$rule" "$cfg" "$udp53" "$path" "$mode" "$udpOnly" "$(printf '%s' "$doh" | sed 's/"/\\"/g')"; }
dns_status(){ dns_status_json; }
dns_repair(){ progress 94 "Repairing DNS profile"; dns_apply; }

sync_clients_cmd(){
  require_root
  ensure_clients_seed_file; ensure_shared_psk
  local chap_tmp row enable username password
  chap_tmp="$(mktemp)"; printf '# OmniRelay managed IPSec/L2TP users\n' > "$chap_tmp"
  while IFS= read -r row; do
    enable="$(jq -r '.enable // true' <<<"$row")"; username="$(jq -r '.username // empty' <<<"$row")"; password="$(jq -r '.password // empty' <<<"$row")"
    [[ "$enable" == "true" && -n "$username" && -n "$password" ]] || continue
    printf '"%s" l2tpd "%s" *\n' "$username" "$password" >> "$chap_tmp"
  done < <(jq -c '.[]' "$IPSEC_CLIENTS_FILE" 2>/dev/null || true)
  install -m 0600 "$chap_tmp" /etc/ppp/chap-secrets; rm -f "$chap_tmp"
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
  local ipsec_state xl2tpd_state
  ipsec_state="$(systemctl is-active ipsec 2>/dev/null || systemctl is-active strongswan-starter 2>/dev/null || echo inactive)"
  xl2tpd_state="$(systemctl is-active xl2tpd 2>/dev/null || echo inactive)"
  [[ "$ipsec_state" == "active" ]] || die "IPSec service is not active after syncing clients."
  [[ "$xl2tpd_state" == "active" ]] || die "xl2tpd service is not active after syncing clients."
  echo "ok"
}

status_cmd(){
  local sshState ipsecState xl2tpdState redsocksState panelState nginxState fail2 backendListener publicListener panelListener internalListener dns iport ike natt l2tp
  sshState="$(systemctl is-active ssh 2>/dev/null || systemctl is-active sshd 2>/dev/null || echo inactive)"
  ipsecState="$(systemctl is-active ipsec 2>/dev/null || systemctl is-active strongswan-starter 2>/dev/null || echo inactive)"
  xl2tpdState="$(systemctl is-active xl2tpd 2>/dev/null || echo inactive)"
  redsocksState="$(systemctl is-active "$IPSEC_REDSOCKS_SERVICE" 2>/dev/null || echo inactive)"
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
  printf '{"activeProtocol":"ipsec_l2tp_hwdsl2","sshState":"%s","xuiState":"inactive","singBoxState":"inactive","openVpnState":"inactive","ipsecState":"%s","xl2tpdState":"%s","omniPanelState":"%s","nginxState":"%s","fail2banState":"%s","backendPort":%s,"publicPort":%s,"panelPort":%s,"omniPanelInternalPort":%s,"xuiPanelPort":0,"backendListener":%s,"publicListener":%s,"panelListener":%s,"omniPanelInternalListener":%s,"inboundId":"","dnsConfigPresent":%s,"dnsRuleActive":%s,"dohReachableViaTunnel":%s,"udp53PathReady":%s,"dnsPathHealthy":%s,"dnsMode":"%s","dnsUdpOnly":%s,"dohEndpoints":"%s","redsocksState":"%s"}\n' "$sshState" "$ipsecState" "$xl2tpdState" "$panelState" "$nginxState" "$fail2" "$BACKEND_PORT" "$IPSEC_L2TP_PORT" "$PANEL_PORT" "$iport" "$backendListener" "$publicListener" "$panelListener" "$internalListener" "$(jq -r '.dnsConfigPresent' <<<"$dns")" "$(jq -r '.dnsRuleActive' <<<"$dns")" "$(jq -r '.dohReachableViaTunnel' <<<"$dns")" "$(jq -r '.udp53PathReady' <<<"$dns")" "$(jq -r '.dnsPathHealthy' <<<"$dns")" "$(jq -r '.dnsMode' <<<"$dns")" "$(jq -r '.dnsUdpOnly' <<<"$dns")" "$(jq -r '.dohEndpoints' <<<"$dns" | sed 's/"/\\"/g')" "$redsocksState"
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
  systemctl disable --now x-ui 2>/dev/null || true
  systemctl disable --now omnirelay-singbox 2>/dev/null || true
  systemctl disable --now omnirelay-openvpn 2>/dev/null || true
  systemctl disable --now "$IPSEC_REDSOCKS_SERVICE" "$IPSEC_RULES_SERVICE" 2>/dev/null || true
  rm -f /etc/systemd/system/x-ui.service /etc/systemd/system/omnirelay-singbox.service /etc/systemd/system/omnirelay-openvpn.service /etc/systemd/system/${IPSEC_REDSOCKS_SERVICE}.service /etc/systemd/system/${IPSEC_RULES_SERVICE}.service
  rm -f /etc/sudoers.d/omnigateway-singbox /etc/sudoers.d/omnigateway-openvpn /etc/sudoers.d/omnigateway-ipsec
  systemctl daemon-reload || true
  install_packages
  ensure_nodejs_runtime
  ensure_clients_seed_file
  ensure_shared_psk
  install_hwdsl2_stack
  write_redsocks_config
  enable_ip_forward
  write_traffic_scripts
  setup_runtime_services
  deploy_panel
  sync_clients_cmd
  wait_omnipanel_ready
  configure_nginx
  configure_host_firewall
  write_metadata
  dns_apply
  progress 100 "Gateway install completed"
  local endpoint; endpoint="${VPS_IP:-$(hostname -I 2>/dev/null | awk '{print $1}')}"
  endpoint="${endpoint%% *}"; [[ -n "$endpoint" ]] || endpoint="<VPS_IP_OR_HOSTNAME>"
  log "OmniPanel URL: https://${endpoint}:${PANEL_PORT}/ | Username: ${PANEL_USER} | Password: ${PANEL_PASSWORD}"
}
start_cmd(){ require_root; progress 96 "Starting gateway services"; systemctl enable --now "$IPSEC_REDSOCKS_SERVICE" >/dev/null 2>&1 || true; systemctl enable --now ipsec >/dev/null 2>&1 || systemctl enable --now strongswan-starter >/dev/null 2>&1 || true; systemctl enable --now xl2tpd >/dev/null 2>&1 || true; systemctl enable --now "$IPSEC_RULES_SERVICE" >/dev/null 2>&1 || true; systemctl enable --now "$OMNIPANEL_SERVICE" nginx >/dev/null 2>&1 || true; progress 100 "Gateway start completed"; }
stop_cmd(){ require_root; progress 96 "Stopping gateway services"; systemctl stop "$OMNIPANEL_SERVICE" "$IPSEC_RULES_SERVICE" xl2tpd "$IPSEC_REDSOCKS_SERVICE" nginx >/dev/null 2>&1 || true; systemctl stop ipsec >/dev/null 2>&1 || systemctl stop strongswan-starter >/dev/null 2>&1 || true; progress 100 "Gateway stop completed"; }
uninstall_cmd(){ require_root; progress 96 "Uninstalling gateway"; systemctl disable --now "$OMNIPANEL_SERVICE" "$IPSEC_RULES_SERVICE" xl2tpd "$IPSEC_REDSOCKS_SERVICE" nginx >/dev/null 2>&1 || true; systemctl disable --now ipsec >/dev/null 2>&1 || systemctl disable --now strongswan-starter >/dev/null 2>&1 || true; rm -f /etc/systemd/system/${OMNIPANEL_SERVICE}.service /etc/systemd/system/${IPSEC_REDSOCKS_SERVICE}.service /etc/systemd/system/${IPSEC_RULES_SERVICE}.service /etc/nginx/sites-enabled/omnirelay-omnipanel.conf /etc/nginx/sites-available/omnirelay-omnipanel.conf /etc/sudoers.d/omnigateway-ipsec /usr/local/sbin/omnirelay-gatewayctl /etc/sysctl.d/99-omnirelay-ipsec.conf; rm -rf "$METADATA_DIR" "$OMNIPANEL_APP_DIR"; systemctl daemon-reload; progress 100 "Gateway uninstall completed"; }

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
      --json) shift ;;
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
  if [[ "$PUBLIC_PORT" != "$IPSEC_L2TP_PORT" ]]; then log "Ignoring --public-port=${PUBLIC_PORT}; IPSec/L2TP uses fixed UDP ports 500/4500/1701."; PUBLIC_PORT="$IPSEC_L2TP_PORT"; fi
  [[ "$PANEL_PORT" != "$IPSEC_L2TP_PORT" ]] || die "--panel-port must differ from fixed IPSec/L2TP port ${IPSEC_L2TP_PORT}."
  case "$COMMAND" in
    install) install_cmd ;;
    uninstall) uninstall_cmd ;;
    start) start_cmd ;;
    stop) stop_cmd ;;
    status) status_cmd ;;
    health) health_cmd ;;
    dns-apply) dns_apply ;;
    dns-status) dns_status ;;
    dns-repair) dns_repair ;;
    sync-clients) sync_clients_cmd ;;
    *) die "Unknown command: ${COMMAND}. Expected install|uninstall|start|stop|status|health|dns-apply|dns-status|dns-repair|sync-clients" ;;
  esac
}

main "$@"
