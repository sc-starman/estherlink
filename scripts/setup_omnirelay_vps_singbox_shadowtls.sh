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
CAMOUFLAGE_SERVER="www.apple.com:443"
DNS_MODE="hybrid"
DOH_ENDPOINTS="https://1.1.1.1/dns-query,https://8.8.8.8/dns-query"
DNS_UDP_ONLY="true"
TUNNEL_USER=""
TUNNEL_AUTH="host_key"
PANEL_USER=""
PANEL_PASSWORD=""
PANEL_BASE_PATH=""
PANEL_DOMAIN=""
PANEL_DOMAIN_ONLY="false"
PANEL_SSL_ENABLED="false"
PANEL_SSL_MODE="letsencrypt"
PANEL_CERT_FILE=""
PANEL_KEY_FILE=""
GATEWAY_SNI=""
GATEWAY_TARGET=""

METADATA_DIR="/etc/omnirelay/gateway"
METADATA_FILE="${METADATA_DIR}/metadata.json"
DNS_PROFILE_FILE="${METADATA_DIR}/dns_profile.json"
OMNIPANEL_ENV_FILE="${METADATA_DIR}/omnipanel.env"
SINGBOX_DIR="${METADATA_DIR}/singbox"
SINGBOX_CONFIG_FILE="${SINGBOX_DIR}/config.json"
OMNIPANEL_APP_DIR="/opt/omnirelay/omni-gateway"
OMNIPANEL_COMMON_SCRIPT="/tmp/omnirelay-omnipanel-common.sh"
SHADOWTLS_CLIENTS_FILE="${OMNIPANEL_APP_DIR}/shadowtls_clients.json"
PANEL_AUTH_FILE="${OMNIPANEL_APP_DIR}/panel-auth.json"
SINGBOX_SERVICE="omnirelay-singbox"
OMNIPANEL_SERVICE="omnirelay-omnipanel"
SINGBOX_BIN="/usr/local/bin/sing-box"
SINGBOX_VERSION="1.11.8"
SINGBOX_RELOAD_COMMAND="/usr/bin/sudo -n /usr/local/sbin/omnirelay-gatewayctl sync-clients"
OMNIPANEL_INTERNAL_PORT=0
STATUS_JSON=0
HEALTH_JSON=0
APT_PROXY_FILE="/etc/apt/apt.conf.d/99-omnirelay-socks"

log(){ printf '[%s] %s\n' "$(date -u +'%Y-%m-%dT%H:%M:%SZ')" "$*"; }
die(){ printf '[%s] ERROR: %s\n' "$(date -u +'%Y-%m-%dT%H:%M:%SZ')" "$*" >&2; exit 1; }
progress(){ local pct="$1"; shift; printf 'OMNIRELAY_PROGRESS:%s:%s\n' "$pct" "$*"; }
require_root(){ (( EUID == 0 )) || die "This command requires root."; }
validate_port(){ [[ "$1" =~ ^[0-9]+$ ]] || die "$2 must be integer"; (( $1>=1 && $1<=65535 )) || die "$2 out of range"; }

usage(){ cat <<EOF
Usage: sudo ./${SCRIPT_NAME} <install|uninstall|start|stop|get-protocol|status|health|dns-apply|dns-status|dns-repair|sync-clients> [options]
--public-port <port>
--panel-port <port>
--backend-port <port>
--ssh-port <port>
--bootstrap-socks-port <port>
--proxy-check-url <url>
--camouflage-server <host:port>
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

random_string(){ LC_ALL=C tr -dc 'a-zA-Z0-9' </dev/urandom | head -c "$1" || true; }
random_base64(){ openssl rand -base64 "$1" | tr -d '\r\n'; }
check_listener(){ ss -lnt "( sport = :$1 )" 2>/dev/null | awk 'NR>1{print}' | grep -q . && echo true || echo false; }
choose_port(){ for i in $(seq 22000 52000); do p=$((RANDOM%30000+22000)); [[ "$(check_listener "$p")" == "false" ]] && echo "$p" && return 0; done; die "cannot allocate random port"; }
ensure_meta(){ install -d -m 0755 "$METADATA_DIR"; }

normalize_bool(){
  local value
  value="$(printf '%s' "$1" | tr '[:upper:]' '[:lower:]' | xargs)"
  case "$value" in
    true|1|yes|y) printf 'true' ;;
    false|0|no|n) printf 'false' ;;
    *) die "Boolean value expected but received '${1}'." ;;
  esac
}

load_omnipanel_common(){
  local candidate
  for candidate in "$OMNIPANEL_COMMON_SCRIPT" "/usr/local/lib/omnirelay/omnipanel-common.sh" "$(dirname "$0")/setup_omnirelay_omnipanel_common.sh"; do
    if [[ -f "$candidate" ]]; then
      # shellcheck disable=SC1090
      source "$candidate"
      return 0
    fi
  done
  die "Shared OmniPanel helper script not found. Expected ${OMNIPANEL_COMMON_SCRIPT}."
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
  if command -v node >/dev/null 2>&1; then
    command -v node
    return 0
  fi
  if command -v nodejs >/dev/null 2>&1; then
    command -v nodejs
    return 0
  fi
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
  if ! date -u -s "@${remote_epoch}" >/dev/null 2>&1; then
    [[ "$was_ntp" == "yes" ]] && timedatectl set-ntp true >/dev/null 2>&1 || true
    return 1
  fi
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
  apt-get install -y curl jq tar gzip ca-certificates openssl nginx
  clear_proxy
}

ensure_nodejs_runtime(){
  local major
  major="$(detect_node_major)"
  if (( major >= 18 )); then
    return 0
  fi

  progress 18 "Installing Node.js runtime"
  configure_proxy
  DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends nodejs || true
  clear_proxy

  major="$(detect_node_major)"
  if (( major >= 18 )); then
    return 0
  fi

  progress 20 "Upgrading Node.js runtime to v20"
  configure_proxy
  curl -fSL "https://deb.nodesource.com/setup_20.x" -o /tmp/omnirelay-nodesource-setup.sh
  bash /tmp/omnirelay-nodesource-setup.sh
  DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends nodejs
  clear_proxy

  major="$(detect_node_major)"
  (( major >= 18 )) || die "Node.js 18+ is required for OmniPanel, but detected version is too old."
}

install_singbox(){
  progress 24 "Installing sing-box"
  arch="amd64"; [[ "$(uname -m)" =~ ^(aarch64|arm64)$ ]] && arch="arm64"
  url="https://github.com/SagerNet/sing-box/releases/download/v${SINGBOX_VERSION}/sing-box-${SINGBOX_VERSION}-linux-${arch}.tar.gz"
  tmp="/tmp/sing-box.tar.gz"; dir="/tmp/sing-box-${SINGBOX_VERSION}-${arch}"
  configure_proxy; curl -fSL "$url" -o "$tmp"; clear_proxy
  rm -rf "$dir"; mkdir -p "$dir"; tar -xzf "$tmp" -C "$dir"
  bin="$(find "$dir" -type f -name sing-box | head -n1 || true)"; [[ -n "$bin" ]] || die "sing-box binary missing"
  install -m 0755 "$bin" "$SINGBOX_BIN"
}

write_singbox_config(){
  install -d -m 0755 "$SINGBOX_DIR"
  install -d -m 0755 "$(dirname "$SHADOWTLS_CLIENTS_FILE")"
  if [[ ! -f "$SHADOWTLS_CLIENTS_FILE" ]]; then
    jq -n --arg id "$(random_string 16)" --arg ss "$(random_base64 16)" --arg st "$(random_string 32)" '[{id:$id,email:"omni-client@local",enable:true,ssPassword:$ss,shadowTlsPassword:$st}]' > "$SHADOWTLS_CLIENTS_FILE"
  fi
  if [[ "$CAMOUFLAGE_SERVER" == *:* ]]; then
    host="${CAMOUFLAGE_SERVER%:*}"
    cport="${CAMOUFLAGE_SERVER##*:}"
  else
    host="$CAMOUFLAGE_SERVER"
    cport="443"
  fi
  [[ "$cport" =~ ^[0-9]+$ ]] || cport="443"
  stUsers="$(jq -c '[.[]|select(.enable==true)|{name:(.email//.id),password:.shadowTlsPassword}]' "$SHADOWTLS_CLIENTS_FILE")"
  ssUsers="$(jq -c '[.[]|select(.enable==true)|{name:(.email//.id),password:.ssPassword}]' "$SHADOWTLS_CLIENTS_FILE")"
  jq -n --argjson p "$PUBLIC_PORT" --arg host "$host" --argjson hport "${cport:-443}" --arg srv "$(random_base64 16)" --argjson st "$stUsers" --argjson ss "$ssUsers" '{log:{level:"warn"},inbounds:[{type:"shadowtls",tag:"shadowtls-in",listen:"::",listen_port:$p,version:3,users:$st,handshake:{server:$host,server_port:$hport},detour:"ss-inner"},{type:"shadowsocks",tag:"ss-inner",listen:"127.0.0.1",listen_port:32080,method:"2022-blake3-aes-128-gcm",password:$srv,users:$ss}],outbounds:[{type:"direct",tag:"direct"}],route:{final:"direct"}}' > "$SINGBOX_CONFIG_FILE"
}

setup_services(){
  cat > "/etc/systemd/system/${SINGBOX_SERVICE}.service" <<EOF
[Unit]
Description=OmniRelay sing-box (ShadowTLS+Shadowsocks)
After=network-online.target
Wants=network-online.target
[Service]
Type=simple
ExecStart=${SINGBOX_BIN} run -c ${SINGBOX_CONFIG_FILE}
Restart=always
RestartSec=3
[Install]
WantedBy=multi-user.target
EOF
  systemctl daemon-reload
  systemctl enable --now "$SINGBOX_SERVICE"
}
gen_cert(){
  cert_dir="${METADATA_DIR}/certs"; cert="${cert_dir}/omnipanel.crt"; key="${cert_dir}/omnipanel.key"; mkdir -p "$cert_dir"; chmod 0700 "$cert_dir"
  server="${VPS_IP:-$(hostname -I 2>/dev/null | awk '{print $1}') }"; [[ -n "$server" ]] || server="localhost"
  openssl req -x509 -nodes -newkey rsa:2048 -days 825 -keyout "$key" -out "$cert" -subj "/CN=${server}" >/dev/null 2>&1
  chmod 0600 "$key"; chmod 0644 "$cert"
}

read_omnipanel_internal_port(){
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
  port="$(read_omnipanel_internal_port || true)"
  [[ -n "$port" ]] || die "OmniPanel internal port is not set."
  restarted=0
  for i in $(seq 1 30); do
    code="$(curl --noproxy '*' --silent --output /dev/null --write-out '%{http_code}' --max-time 4 "http://127.0.0.1:${port}/" 2>/dev/null || true)"
    case "$code" in
      200|301|302|307|308|401|403)
        return 0
        ;;
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
      200|301|302|307|308|401|403)
        return 0
        ;;
    esac
    sleep 1
  done
  die "Nginx HTTPS reverse proxy check failed on 127.0.0.1:${PANEL_PORT}."
}

deploy_panel(){
  local node_bin
  progress 60 "Deploying OmniPanel artifact"
  install -d -m 0755 "$OMNIPANEL_APP_DIR" "$OMNIPANEL_APP_DIR/releases"
  rel="$(date +%Y%m%d%H%M%S)"; dir="${OMNIPANEL_APP_DIR}/releases/${rel}"; mkdir -p "$dir"
  configure_proxy; curl -fSL "https://omnirelay.net/download/omni-gateway" -o /tmp/omni-gateway.tar.gz; clear_proxy
  tar -xzf /tmp/omni-gateway.tar.gz -C "$dir"
  if [[ ! -f "$dir/server.js" ]]; then nd="$(find "$dir" -mindepth 1 -maxdepth 1 -type d | head -n1 || true)"; [[ -n "$nd" ]] && cp -a "$nd"/. "$dir"/; fi
  [[ -f "$dir/server.js" ]] || die "omnipanel artifact missing server.js"
  node_bin="$(resolve_node_bin || true)"
  [[ -n "$node_bin" && -x "$node_bin" ]] || die "Node.js executable not found. Install node/nodejs and retry."

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
OMNIRELAY_ACTIVE_PROTOCOL=shadowtls_v3_shadowsocks_singbox
PANEL_PUBLIC_PORT=${PANEL_PORT}
PANEL_PUBLIC_HOST=${VPS_IP:-$(hostname -I 2>/dev/null | awk '{print $1}')}
SHADOWTLS_PUBLIC_PORT=${PUBLIC_PORT}
SHADOWTLS_CAMOUFLAGE_SERVER=${CAMOUFLAGE_SERVER}
SHADOWTLS_CLIENTS_FILE=${SHADOWTLS_CLIENTS_FILE}
SINGBOX_RELOAD_COMMAND=${SINGBOX_RELOAD_COMMAND}
EOF

  cat > "/etc/systemd/system/${OMNIPANEL_SERVICE}.service" <<EOF
[Unit]
Description=OmniRelay OmniPanel
After=network.target ${SINGBOX_SERVICE}.service
Requires=${SINGBOX_SERVICE}.service
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

  echo "omnigateway ALL=(root) NOPASSWD:/usr/local/sbin/omnirelay-gatewayctl sync-clients" > /etc/sudoers.d/omnigateway-singbox
  chmod 0440 /etc/sudoers.d/omnigateway-singbox

  ln -sfn "$dir" "${OMNIPANEL_APP_DIR}/current"
  chown -R omnigateway:omnigateway "$OMNIPANEL_APP_DIR"
  chown omnigateway:omnigateway "$SHADOWTLS_CLIENTS_FILE" "$PANEL_AUTH_FILE"
  chmod 0640 "$SHADOWTLS_CLIENTS_FILE" "$PANEL_AUTH_FILE"
  systemctl daemon-reload
  systemctl enable --now "$OMNIPANEL_SERVICE"
}

configure_nginx(){
  local panel_internal_port
  progress 76 "Configuring nginx reverse-proxy for OmniPanel"
  panel_internal_port="$(read_omnipanel_internal_port || true)"
  [[ -n "$panel_internal_port" ]] || die "Cannot determine OmniPanel internal port for nginx config."
  load_omnipanel_common
  omnipanel_configure_nginx_proxy \
    "$PANEL_PORT" \
    "$panel_internal_port" \
    "$METADATA_DIR" \
    "$PANEL_DOMAIN" \
    "$PANEL_DOMAIN_ONLY" \
    "$PANEL_SSL_ENABLED" \
    "$PANEL_SSL_MODE" \
    "$PANEL_CERT_FILE" \
    "$PANEL_KEY_FILE" \
    "$(hostname -I 2>/dev/null | awk '{print $1}')" \
    "$VPS_IP"
}

configure_host_firewall(){
  if ! command -v ufw >/dev/null 2>&1; then
    return 0
  fi

  if ! ufw status 2>/dev/null | grep -q "^Status: active"; then
    return 0
  fi

  ufw allow "${PANEL_PORT}/tcp" >/dev/null 2>&1 || true
  ufw allow "${PUBLIC_PORT}/tcp" >/dev/null 2>&1 || true
  ufw allow "${SSH_PORT}/tcp" >/dev/null 2>&1 || true
  if [[ "$PANEL_SSL_ENABLED" == "true" && "$PANEL_SSL_MODE" == "letsencrypt" ]]; then
    ufw allow "80/tcp" >/dev/null 2>&1 || true
  fi
}

write_metadata(){
  progress 88 "Persisting managed gateway metadata"
  jq -n --arg proto "shadowtls_v3_shadowsocks_singbox" --arg port "$PUBLIC_PORT" --arg panel "$PANEL_PORT" --arg user "$PANEL_USER" --arg camo "$CAMOUFLAGE_SERVER" --arg clientsFile "$SHADOWTLS_CLIENTS_FILE" --arg intp "$OMNIPANEL_INTERNAL_PORT" '{active_protocol:$proto,public_port:($port|tonumber),omnipanel_public_port:($panel|tonumber),omnipanel_internal_port:($intp|tonumber),omnipanel_username:$user,shadowtls:{camouflage_server:$camo,clients_file:$clientsFile},created_at_utc:(now|todate)}' > "$METADATA_FILE"
  chmod 0600 "$METADATA_FILE"
}

dns_apply(){ progress 94 "Applying DNS-through-tunnel profile"; jq -n --arg m "$DNS_MODE" --arg d "$DOH_ENDPOINTS" --argjson u "$( [[ "$DNS_UDP_ONLY" == "true" ]] && echo true || echo false )" '{mode:$m,dohEndpoints:$d,dnsUdpOnly:$u,updatedAtUtc:(now|todate)}' > "$DNS_PROFILE_FILE"; progress 100 "DNS profile applied"; }
dns_status(){ if [[ -f "$DNS_PROFILE_FILE" ]]; then cfg=true; rule=true; mode="$(jq -r '.mode' "$DNS_PROFILE_FILE")"; doh="$(jq -r '.dohEndpoints' "$DNS_PROFILE_FILE")"; udpOnly="$(jq -r '.dnsUdpOnly' "$DNS_PROFILE_FILE")"; else cfg=false; rule=false; mode=unknown; doh=""; udpOnly=false; fi; udp53="$(check_listener 53)"; path=false; [[ "$cfg" == true && "$rule" == true ]] && path=true; printf '{"dnsConfigPresent":%s,"dnsRuleActive":%s,"dohReachableViaTunnel":%s,"udp53PathReady":%s,"dnsPathHealthy":%s,"dnsMode":"%s","dnsUdpOnly":%s,"dohEndpoints":"%s"}\n' "$cfg" "$rule" "$cfg" "$udp53" "$path" "$mode" "$udpOnly" "$(printf '%s' "$doh" | sed 's/"/\\"/g')"; }
dns_repair(){ progress 94 "Repairing DNS profile"; dns_apply; }

status_cmd(){ sshState="$(systemctl is-active ssh 2>/dev/null || systemctl is-active sshd 2>/dev/null || echo inactive)"; sing="$(systemctl is-active "$SINGBOX_SERVICE" 2>/dev/null || echo inactive)"; panel="$(systemctl is-active "$OMNIPANEL_SERVICE" 2>/dev/null || echo inactive)"; nginxState="$(systemctl is-active nginx 2>/dev/null || echo inactive)"; fail2="$(systemctl is-active fail2ban 2>/dev/null || echo disabled)"; pub="$(jq -r '.public_port // 0' "$METADATA_FILE" 2>/dev/null || echo 0)"; pport="$(jq -r '.omnipanel_public_port // 0' "$METADATA_FILE" 2>/dev/null || echo 0)"; iport="$(jq -r '.omnipanel_internal_port // 0' "$METADATA_FILE" 2>/dev/null || echo 0)"; dns="$(STATUS_JSON=1 dns_status)"; printf '{"activeProtocol":"shadowtls_v3_shadowsocks_singbox","sshState":"%s","xuiState":"inactive","singBoxState":"%s","omniPanelState":"%s","nginxState":"%s","fail2banState":"%s","backendPort":%s,"publicPort":%s,"panelPort":%s,"omniPanelInternalPort":%s,"xuiPanelPort":0,"backendListener":%s,"publicListener":%s,"panelListener":%s,"omniPanelInternalListener":%s,"inboundId":"","dnsConfigPresent":%s,"dnsRuleActive":%s,"dohReachableViaTunnel":%s,"udp53PathReady":%s,"dnsPathHealthy":%s,"dnsMode":"%s","dnsUdpOnly":%s,"dohEndpoints":"%s"}\n' "$sshState" "$sing" "$panel" "$nginxState" "$fail2" "$BACKEND_PORT" "$pub" "$pport" "$iport" "$(check_listener "$BACKEND_PORT")" "$(check_listener "$pub")" "$(check_listener "$pport")" "$(check_listener "$iport")" "$(jq -r '.dnsConfigPresent' <<<"$dns")" "$(jq -r '.dnsRuleActive' <<<"$dns")" "$(jq -r '.dohReachableViaTunnel' <<<"$dns")" "$(jq -r '.udp53PathReady' <<<"$dns")" "$(jq -r '.dnsPathHealthy' <<<"$dns")" "$(jq -r '.dnsMode' <<<"$dns")" "$(jq -r '.dnsUdpOnly' <<<"$dns")" "$(jq -r '.dohEndpoints' <<<"$dns" | sed 's/"/\\"/g')"; }
health_cmd(){ s="$(STATUS_JSON=1 status_cmd)"; h=true; [[ "$(jq -r '.sshState' <<<"$s")" == "active" ]] || h=false; [[ "$(jq -r '.singBoxState' <<<"$s")" == "active" ]] || h=false; [[ "$(jq -r '.omniPanelState' <<<"$s")" == "active" ]] || h=false; [[ "$(jq -r '.nginxState' <<<"$s")" == "active" ]] || h=false; jq -c --argjson healthy "$( [[ "$h" == true ]] && echo true || echo false )" '. + {healthy:$healthy,dnsLastError:""}' <<<"$s"; }

sync_clients_cmd(){
  require_root
  write_singbox_config

  if [[ -x "$SINGBOX_BIN" ]]; then
    if ! "$SINGBOX_BIN" check -c "$SINGBOX_CONFIG_FILE" >/tmp/omnirelay-singbox-check.log 2>&1; then
      log "---- sing-box check output ----"
      sed -n '1,120p' /tmp/omnirelay-singbox-check.log >&2 || true
      die "sing-box config validation failed after syncing clients."
    fi
  fi

  if ! systemctl restart "$SINGBOX_SERVICE"; then
    log "systemctl restart ${SINGBOX_SERVICE} returned non-zero; verifying final service state."
  fi

  sleep 1
  if ! systemctl is-active --quiet "$SINGBOX_SERVICE"; then
    log "---- sing-box systemd status ----"
    systemctl --no-pager --full status "$SINGBOX_SERVICE" 2>&1 || true
    log "---- sing-box journal (last 80 lines) ----"
    journalctl -u "$SINGBOX_SERVICE" -n 80 --no-pager 2>&1 || true
    die "sing-box service is not active after syncing clients."
  fi

  echo "ok"
}

get_protocol_cmd(){ echo "shadowtls_v3_shadowsocks_singbox"; }

install_cmd(){
  require_root
  progress 3 "Validating platform"
  systemctl disable --now x-ui omnirelay-openvpn omnirelay-openvpn-accounting omnirelay-openvpn-accounting.timer omnirelay-redsocks omnirelay-ipsec-rules omnirelay-ipsec-accounting omnirelay-ipsec-accounting.timer 2>/dev/null || true
  systemctl disable --now ipsec xl2tpd strongswan-starter 2>/dev/null || true
  rm -f /etc/systemd/system/x-ui.service /etc/systemd/system/omnirelay-openvpn.service /etc/systemd/system/omnirelay-openvpn-accounting.service /etc/systemd/system/omnirelay-openvpn-accounting.timer /etc/systemd/system/omnirelay-redsocks.service /etc/systemd/system/omnirelay-ipsec-rules.service /etc/systemd/system/omnirelay-ipsec-accounting.service /etc/systemd/system/omnirelay-ipsec-accounting.timer
  rm -f /etc/sudoers.d/omnigateway-openvpn /etc/sudoers.d/omnigateway-ipsec
  rm -f /etc/sysctl.d/99-omnirelay-openvpn.conf /etc/sysctl.d/99-omnirelay-ipsec.conf
  rm -f /etc/ppp/ip-up.d/99-omnirelay-accounting /etc/ppp/ip-down.d/99-omnirelay-accounting
  rm -f /var/log/openvpn/omnirelay-status.log
  systemctl daemon-reload

  ensure_meta
  install -m 0755 "$0" /usr/local/sbin/omnirelay-gatewayctl
  install -d -m 0755 /usr/local/lib/omnirelay
  if [[ -f "$OMNIPANEL_COMMON_SCRIPT" ]]; then
    install -m 0755 "$OMNIPANEL_COMMON_SCRIPT" /usr/local/lib/omnirelay/omnipanel-common.sh
  fi
  install_packages
  ensure_nodejs_runtime
  install_singbox
  write_singbox_config
  setup_services
  deploy_panel
  wait_omnipanel_ready
  configure_nginx
  configure_host_firewall
  write_metadata
  dns_apply
  progress 100 "Gateway install completed"
  ip="${PANEL_DOMAIN:-${VPS_IP:-$(hostname -I 2>/dev/null | awk '{print $1}')}}"
  scheme="http"
  [[ "$PANEL_SSL_ENABLED" == "true" ]] && scheme="https"
  log "OmniPanel URL: ${scheme}://${ip}:${PANEL_PORT}/ | Username: ${PANEL_USER} | Password: ${PANEL_PASSWORD}"
}

start_cmd(){
  require_root
  progress 96 "Starting gateway services"
  systemctl enable --now "$SINGBOX_SERVICE" "$OMNIPANEL_SERVICE" nginx >/dev/null 2>&1 || true
  progress 100 "Gateway start completed"
}

stop_cmd(){
  require_root
  progress 96 "Stopping gateway services"
  systemctl stop "$OMNIPANEL_SERVICE" "$SINGBOX_SERVICE" nginx >/dev/null 2>&1 || true
  progress 100 "Gateway stop completed"
}

uninstall_cmd(){
  require_root
  progress 96 "Uninstalling gateway"
  systemctl disable --now "$OMNIPANEL_SERVICE" "$SINGBOX_SERVICE" nginx 2>/dev/null || true
  systemctl disable --now omnirelay-openvpn omnirelay-openvpn-accounting omnirelay-openvpn-accounting.timer omnirelay-redsocks omnirelay-ipsec-rules omnirelay-ipsec-accounting omnirelay-ipsec-accounting.timer 2>/dev/null || true
  systemctl disable --now ipsec xl2tpd strongswan-starter 2>/dev/null || true
  rm -f "/etc/systemd/system/${OMNIPANEL_SERVICE}.service" "/etc/systemd/system/${SINGBOX_SERVICE}.service" /etc/systemd/system/omnirelay-openvpn.service /etc/systemd/system/omnirelay-openvpn-accounting.service /etc/systemd/system/omnirelay-openvpn-accounting.timer /etc/systemd/system/omnirelay-redsocks.service /etc/systemd/system/omnirelay-ipsec-rules.service /etc/systemd/system/omnirelay-ipsec-accounting.service /etc/systemd/system/omnirelay-ipsec-accounting.timer
  rm -f /etc/nginx/sites-enabled/omnirelay-omnipanel.conf /etc/nginx/sites-available/omnirelay-omnipanel.conf
  rm -f /etc/sudoers.d/omnigateway-singbox /etc/sudoers.d/omnigateway-openvpn /etc/sudoers.d/omnigateway-ipsec /usr/local/sbin/omnirelay-gatewayctl /usr/local/lib/omnirelay/omnipanel-common.sh
  rm -f /etc/sysctl.d/99-omnirelay-openvpn.conf /etc/sysctl.d/99-omnirelay-ipsec.conf
  rm -f /etc/ppp/ip-up.d/99-omnirelay-accounting /etc/ppp/ip-down.d/99-omnirelay-accounting
  rm -f /var/log/openvpn/omnirelay-status.log
  rm -f "$SINGBOX_BIN"
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
      --camouflage-server) CAMOUFLAGE_SERVER="${2:-}"; shift 2 ;;
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
      --gateway-sni) GATEWAY_SNI="${2:-}"; shift 2 ;;
      --gateway-target) GATEWAY_TARGET="${2:-}"; shift 2 ;;
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
  PANEL_DOMAIN="$(printf '%s' "$PANEL_DOMAIN" | tr -d '\r\n' | xargs)"
  PANEL_DOMAIN_ONLY="$(normalize_bool "$PANEL_DOMAIN_ONLY")"
  PANEL_SSL_ENABLED="$(normalize_bool "$PANEL_SSL_ENABLED")"
  PANEL_SSL_MODE="$(printf '%s' "$PANEL_SSL_MODE" | tr '[:upper:]' '[:lower:]' | xargs)"
  [[ "$PANEL_SSL_MODE" == "uploaded" ]] || PANEL_SSL_MODE="letsencrypt"

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

  case "$COMMAND" in
    install) install_cmd ;;
    uninstall) uninstall_cmd ;;
    start) start_cmd ;;
    stop) stop_cmd ;;
    get-protocol) get_protocol_cmd ;;
    status) STATUS_JSON=1; status_cmd ;;
    health) HEALTH_JSON=1; status_cmd >/dev/null 2>&1 || true; health_cmd ;;
    dns-apply) dns_apply ;;
    dns-status) STATUS_JSON=1; dns_status ;;
    dns-repair) dns_repair ;;
    sync-clients) sync_clients_cmd ;;
    *) die "Unknown command: ${COMMAND}. Expected install|uninstall|start|stop|get-protocol|status|health|dns-apply|dns-status|dns-repair|sync-clients" ;;
  esac
}
main "$@"
