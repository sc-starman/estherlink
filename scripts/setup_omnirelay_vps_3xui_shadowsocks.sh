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
TUNNEL_USER="omnirelay"
TUNNEL_AUTH_METHOD="both"
BOOTSTRAP_SOCKS_PORT=16080
PROXY_CHECK_URL="https://deb.debian.org/"
DNS_MODE="hybrid"
DOH_ENDPOINTS="https://1.1.1.1/dns-query,https://8.8.8.8/dns-query"
DNS_UDP_ONLY="true"
VPS_IP=""
PUBKEY=""
PUBKEY_FILE=""
PANEL_USER=""
PANEL_PASSWORD=""
PANEL_BASE_PATH=""
GATEWAY_SNI=""
GATEWAY_TARGET=""
XUI_PANEL_PORT=0
OMNIPANEL_INTERNAL_PORT=0
INBOUND_ID=""
SESSION_SECRET=""
XUI_API_SCHEME="http"
METADATA_DIR="/etc/omnirelay/gateway"
METADATA_FILE="/etc/omnirelay/gateway/metadata.json"
OMNIPANEL_ENV_FILE="/etc/omnirelay/gateway/omnipanel.env"
OMNIPANEL_SERVICE_NAME="omnirelay-omnipanel"
OMNIPANEL_APP_DIR="/opt/omnirelay/omni-gateway"
XUI_COOKIE_JAR=""
STATUS_JSON=0
HEALTH_JSON=0
SSH_SERVICE=""

log() {
  printf '[%s] %s\n' "$(date -u +'%Y-%m-%dT%H:%M:%SZ')" "$*"
}

die() {
  printf '[%s] ERROR: %s\n' "$(date -u +'%Y-%m-%dT%H:%M:%SZ')" "$*" >&2
  exit 1
}

progress() {
  local pct="$1"
  shift
  printf 'OMNIRELAY_PROGRESS:%s:%s\n' "$pct" "$*"
}

usage() {
  cat <<EOF
Usage: sudo ./${SCRIPT_NAME} <command> [options]

Commands:
  install      Install/configure online 3x-ui gateway components
  uninstall    Remove x-ui gateway components
  start        Start gateway services
  stop         Stop gateway services
  get-protocol Print current active protocol id
  status       Show gateway service status
  health       Run gateway operational checks
  dns-apply    Apply OmniRelay DNS-through-tunnel profile
  dns-status   Check DNS profile presence/readiness
  dns-repair   Restore/repair DNS profile
  sync-clients Compatibility no-op (3x-ui manages clients via API)

Options:
  --public-port <port>          Public client ingress port for 3x-ui/Xray inbound (default: ${PUBLIC_PORT})
  --panel-port <port>           OmniPanel public HTTPS port (default: ${PANEL_PORT})
  --backend-port <port>         Loopback port for reverse tunnel endpoint (default: ${BACKEND_PORT})
  --ssh-port <port>             SSH port used by the Windows client to connect to VPS (default: ${SSH_PORT}); does not modify local sshd listen port
  --tunnel-user <name>          SSH tunnel user (default: ${TUNNEL_USER})
  --tunnel-auth <method>        host_key | password | both (default: ${TUNNEL_AUTH_METHOD})
  --bootstrap-socks-port <port> VPS loopback SOCKS5 port used for bootstrap install traffic (default: ${BOOTSTRAP_SOCKS_PORT})
  --proxy-check-url <url>       URL used for SOCKS egress validation (default: ${PROXY_CHECK_URL})
  --dns-mode <mode>             hybrid | doh | udp (default: ${DNS_MODE})
  --doh-endpoints <csv>         Comma-separated DoH endpoints (default: ${DOH_ENDPOINTS})
  --dns-udp-only <true|false>   Allow only DNS UDP/53 and block other UDP (default: ${DNS_UDP_ONLY})
  --vps-ip <ip-or-host>         VPS IP/hostname for summary output
  --pubkey '<ssh-pubkey>'       Add one SSH public key to tunnel user authorized_keys
  --pubkey-file <path>          Read SSH public key from file and add it
  --panel-user <name>           Panel username (default: generated)
  --panel-password <value>      Panel password (default: generated)
  --panel-base-path <path>      Hidden 3x-ui panel base path (default: generated random path)
  --json                        For status/health commands output compact JSON
  -h, --help                    Show this help
EOF
}

validate_port() {
  local value="$1"
  local name="$2"
  value="$(printf '%s' "$value" | tr -d '\r\n' | xargs)"
  value="${value#\"}"
  value="${value%\"}"
  value="${value#\'}"
  value="${value%\'}"
  [[ "$value" =~ ^[0-9]+$ ]] || die "${name} must be an integer (received: ${value})."
  (( value >= 1 && value <= 65535 )) || die "${name} must be between 1 and 65535."
  printf '%s' "$value"
}

require_root() {
  (( EUID == 0 )) || die "This command requires root. Re-run with sudo."
}

detect_ssh_service() {
  if systemctl cat ssh >/dev/null 2>&1; then
    SSH_SERVICE="ssh"
    return 0
  fi

  if systemctl cat sshd >/dev/null 2>&1; then
    SSH_SERVICE="sshd"
    return 0
  fi

  if systemctl list-unit-files 2>/dev/null | awk '{print $1}' | grep -qx "ssh.service"; then
    SSH_SERVICE="ssh"
    return 0
  fi

  if systemctl list-unit-files 2>/dev/null | awk '{print $1}' | grep -qx "sshd.service"; then
    SSH_SERVICE="sshd"
    return 0
  fi

  return 1
}

require_existing_sshd() {
  command -v sshd >/dev/null 2>&1 || die "OpenSSH server is required but not installed. Install openssh-server first."
  detect_ssh_service || die "OpenSSH service unit not found. Ensure sshd is installed and managed by systemd."
}

random_string() {
  local len="$1"
  LC_ALL=C tr -dc 'a-zA-Z0-9' </dev/urandom | head -c "$len" || true
}

random_hex() {
  local bytes="$1"
  openssl rand -hex "$bytes" 2>/dev/null | tr -d '\r\n'
}

random_uuid() {
  if [[ -f /proc/sys/kernel/random/uuid ]]; then
    cat /proc/sys/kernel/random/uuid
    return
  fi
  uuidgen
}

choose_random_port() {
  local low="$1"
  local high="$2"
  shift 2
  local avoid=("$@")
  local candidate tries=0 in_use conflict

  while (( tries < 128 )); do
    candidate="$(( RANDOM % (high - low + 1) + low ))"
    conflict=0
    for in_use in "${avoid[@]}"; do
      if [[ -n "$in_use" && "$candidate" == "$in_use" ]]; then
        conflict=1
        break
      fi
    done

    if (( conflict == 1 )); then
      ((tries++))
      continue
    fi

    if ! ss -lnt "( sport = :${candidate} )" 2>/dev/null | awk 'NR>1 {print $0}' | grep -q .; then
      printf '%s' "$candidate"
      return 0
    fi

    ((tries++))
  done

  die "Unable to allocate a free random port in range ${low}-${high}."
}

backup_file() {
  local target="$1"
  if [[ -f "$target" ]]; then
    local ts
    ts="$(date +%Y%m%d%H%M%S)"
    cp -a "$target" "${target}.bak.${ts}"
  fi
}

write_file_if_changed() {
  local target="$1"
  local mode="$2"
  local owner="$3"
  local group="$4"
  local content="$5"
  local tmp

  tmp="$(mktemp)"
  printf '%s\n' "$content" > "$tmp"

  if [[ -f "$target" ]] && cmp -s "$tmp" "$target"; then
    rm -f "$tmp"
    return 1
  fi

  if [[ -f "$target" ]]; then
    backup_file "$target"
  fi

  install -D -m "$mode" -o "$owner" -g "$group" "$tmp" "$target"
  rm -f "$tmp"
  return 0
}

disable_proxy_env() {
  unset ALL_PROXY HTTPS_PROXY HTTP_PROXY
  unset all_proxy https_proxy http_proxy
  export NO_PROXY="127.0.0.1,localhost"
  export no_proxy="127.0.0.1,localhost"
}

json_escape() {
  local value="$1"
  value="${value//\\/\\\\}"
  value="${value//\"/\\\"}"
  value="${value//$'\n'/ }"
  value="${value//$'\r'/ }"
  printf '%s' "$value"
}

validate_dns_mode() {
  local value="$1"
  case "$value" in
    hybrid|doh|udp) printf '%s' "$value" ;;
    *) die "--dns-mode must be one of: hybrid|doh|udp" ;;
  esac
}

normalize_bool() {
  local value
  value="$(printf '%s' "$1" | tr '[:upper:]' '[:lower:]' | xargs)"
  case "$value" in
    true|1|yes|y) printf 'true' ;;
    false|0|no|n) printf 'false' ;;
    *) die "--dns-udp-only must be true or false" ;;
  esac
}

configure_online_proxy_env() {
  export ALL_PROXY="socks5h://127.0.0.1:${BOOTSTRAP_SOCKS_PORT}"
  export HTTPS_PROXY="$ALL_PROXY"
  export HTTP_PROXY="$ALL_PROXY"
}

sync_time_via_bootstrap_socks() {
  local date_header remote_epoch now_epoch delta abs_delta was_ntp
  was_ntp=""

  configure_online_proxy_env
  date_header="$(curl --silent --show-error --insecure --max-time 20 --connect-timeout 10 --retry 0 \
    --socks5-hostname "127.0.0.1:${BOOTSTRAP_SOCKS_PORT}" -I "$PROXY_CHECK_URL" 2>/dev/null \
    | tr -d '\r' \
    | awk 'tolower($1)=="date:"{$1="";sub(/^ /,"");print;exit}')"
  [[ -n "$date_header" ]] || return 1

  remote_epoch="$(date -u -d "$date_header" +%s 2>/dev/null || true)"
  [[ -n "$remote_epoch" ]] || return 1

  now_epoch="$(date -u +%s 2>/dev/null || echo 0)"
  delta=$(( remote_epoch - now_epoch ))
  abs_delta=$delta
  if (( abs_delta < 0 )); then
    abs_delta=$(( -abs_delta ))
  fi

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

verify_bootstrap_socks() {
  progress 8 "Checking bootstrap SOCKS endpoint"
  local retries=24
  local wait_sec=5
  local listener_ok=0
  local curl_ok=0
  local time_sync_attempted=0
  local curl_err=""

  for ((i=1; i<=retries; i++)); do
    if ss -lnt "( sport = :${BOOTSTRAP_SOCKS_PORT} )" 2>/dev/null | awk 'NR>1 {print $0}' | grep -q .; then
      listener_ok=1
    else
      listener_ok=0
    fi

    if (( listener_ok == 1 )); then
      configure_online_proxy_env
      if curl --fail --silent --show-error --max-time 20 --socks5-hostname "127.0.0.1:${BOOTSTRAP_SOCKS_PORT}" "$PROXY_CHECK_URL" >/dev/null 2>/tmp/omnirelay-bootstrap-curl.err; then
        rm -f /tmp/omnirelay-bootstrap-curl.err
        curl_ok=1
        break
      fi
      curl_err="$(tr -d '\r' </tmp/omnirelay-bootstrap-curl.err 2>/dev/null || true)"
      rm -f /tmp/omnirelay-bootstrap-curl.err
      [[ -n "$curl_err" ]] && printf '%s\n' "$curl_err"
      if (( time_sync_attempted == 0 )) && printf '%s' "$curl_err" | grep -qi "certificate is not yet valid"; then
        log "Detected TLS clock skew during SOCKS probe; attempting clock sync via tunnel."
        if sync_time_via_bootstrap_socks; then
          time_sync_attempted=1
          continue
        fi
        log "Clock sync attempt via SOCKS failed; continuing retries."
        time_sync_attempted=1
      fi
    fi

    log "Bootstrap SOCKS not ready on 127.0.0.1:${BOOTSTRAP_SOCKS_PORT} (attempt ${i}/${retries}, listener_present=${listener_ok}), waiting ${wait_sec}s..."
    sleep "$wait_sec"
  done

  (( listener_ok == 1 )) || \
    die "Bootstrap SOCKS endpoint is not reachable on 127.0.0.1:${BOOTSTRAP_SOCKS_PORT}. Ensure Windows relay/tunnel are running and sshd permits remote forward for this port."
  (( curl_ok == 1 )) || \
    die "Bootstrap SOCKS listener exists on 127.0.0.1:${BOOTSTRAP_SOCKS_PORT}, but internet egress check failed against ${PROXY_CHECK_URL}."

  progress 14 "Validated internet egress over SOCKS"
}

configure_apt_proxy() {
  local apt_proxy_file="/etc/apt/apt.conf.d/99-omnirelay-socks"
  local content
  content="Acquire::http::Proxy \"socks5h://127.0.0.1:${BOOTSTRAP_SOCKS_PORT}\";
Acquire::https::Proxy \"socks5h://127.0.0.1:${BOOTSTRAP_SOCKS_PORT}\";"
  write_file_if_changed "$apt_proxy_file" 0644 root root "$content" || true
}

install_packages_online() {
  progress 20 "Configuring apt to use SOCKS bootstrap"
  configure_apt_proxy
  progress 22 "Syncing VPS clock over SOCKS bootstrap (if needed)"
  sync_time_via_bootstrap_socks || log "Clock sync over SOCKS skipped/failed; continuing."

  progress 26 "Updating apt indexes"
  DEBIAN_FRONTEND=noninteractive apt-get update

  progress 34 "Installing required packages"
  DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends \
    curl \
    ca-certificates \
    tar \
    gzip \
    jq \
    openssl \
    python3 \
    sqlite3 \
    nginx \
    nodejs
}

ensure_nodejs_runtime() {
  local major
  major=0
  if command -v node >/dev/null 2>&1; then
    major="$(node -p "Number(process.versions.node.split('.')[0])" 2>/dev/null || echo 0)"
  fi

  if (( major >= 18 )); then
    return 0
  fi

  progress 36 "Upgrading Node.js runtime to v20"
  configure_online_proxy_env
  curl --fail --silent --show-error --location "https://deb.nodesource.com/setup_20.x" --output /tmp/omnirelay-nodesource-setup.sh
  bash /tmp/omnirelay-nodesource-setup.sh
  DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends nodejs

  major=0
  if command -v node >/dev/null 2>&1; then
    major="$(node -p "Number(process.versions.node.split('.')[0])" 2>/dev/null || echo 0)"
  fi

  (( major >= 18 )) || die "Node.js 18+ is required for OmniPanel, but detected version is too old."
}

setup_tunnel_user() {
  progress 40 "Ensuring tunnel user exists and has shell access"

  if ! id -u "$TUNNEL_USER" >/dev/null 2>&1; then
    useradd --create-home --home-dir "/home/${TUNNEL_USER}" --shell /bin/bash "$TUNNEL_USER"
  fi

  usermod -s /bin/bash "$TUNNEL_USER" || true

  install -d -m 0700 -o "$TUNNEL_USER" -g "$TUNNEL_USER" "/home/${TUNNEL_USER}/.ssh"
  touch "/home/${TUNNEL_USER}/.ssh/authorized_keys"
  chown "$TUNNEL_USER:$TUNNEL_USER" "/home/${TUNNEL_USER}/.ssh/authorized_keys"
  chmod 0600 "/home/${TUNNEL_USER}/.ssh/authorized_keys"

  if [[ -n "$PUBKEY" ]]; then
    local auth_file="/home/${TUNNEL_USER}/.ssh/authorized_keys"
    local key_blob=""
    key_blob="$(printf '%s' "$PUBKEY" | awk '{print $2}')"
    if [[ -n "$key_blob" ]]; then
      local tmp_auth
      tmp_auth="$(mktemp)"
      awk -v blob="$key_blob" '
        function keyblob(line, n, i, t) {
          n = split(line, t, /[[:space:]]+/)
          for (i = 1; i <= n; i++) {
            if (t[i] ~ /^(ssh-|ecdsa-|sk-)/) {
              if (i + 1 <= n) {
                return t[i + 1]
              }
              return ""
            }
          }
          return ""
        }
        {
          if (keyblob($0) != blob) {
            print $0
          }
        }
      ' "$auth_file" > "$tmp_auth"
      cat "$tmp_auth" > "$auth_file"
      rm -f "$tmp_auth"
    fi

    local restricted_key
    restricted_key="restrict,port-forwarding ${PUBKEY}"
    if ! grep -Fqx "$restricted_key" "$auth_file"; then
      printf '%s\n' "$restricted_key" >> "$auth_file"
      log "Added provided SSH public key for ${TUNNEL_USER}."
    fi

    chown "$TUNNEL_USER:$TUNNEL_USER" "$auth_file"
    chmod 0600 "$auth_file"
  fi

  if [[ "$TUNNEL_AUTH_METHOD" == "host_key" ]]; then
    passwd -l "$TUNNEL_USER" >/dev/null 2>&1 || true
  fi
}

configure_sshd() {
  progress 48 "Configuring sshd for reverse tunnel"

  local sshd_dropin="/etc/ssh/sshd_config.d/99-omnirelay.conf"
  local auth_block=""

  case "$TUNNEL_AUTH_METHOD" in
    host_key)
      auth_block="    PasswordAuthentication no
    KbdInteractiveAuthentication no
    AuthenticationMethods publickey
    PubkeyAuthentication yes"
      ;;
    password)
      auth_block="    PasswordAuthentication yes
    KbdInteractiveAuthentication no
    AuthenticationMethods password
    PubkeyAuthentication no"
      ;;
    both)
      auth_block="    PasswordAuthentication yes
    KbdInteractiveAuthentication no
    AuthenticationMethods any
    PubkeyAuthentication yes"
      ;;
    *)
      die "Invalid --tunnel-auth value: ${TUNNEL_AUTH_METHOD}. Expected host_key|password|both."
      ;;
  esac

  local content
  content="# Managed by ${SCRIPT_NAME}
# NOTE: We intentionally do not set 'Port' here.
# In many deployments external SSH port != VPS internal sshd port
# (NAT/port-forwarding). Forcing Port from UI value would break access.
AllowTcpForwarding yes
GatewayPorts no
PermitTunnel no
ClientAliveInterval 30
ClientAliveCountMax 3

Match User ${TUNNEL_USER}
${auth_block}
    PermitTTY yes
    X11Forwarding no
    AllowAgentForwarding no
    AllowStreamLocalForwarding no
    AllowTcpForwarding remote
    GatewayPorts no
    PermitTunnel no
    PermitListen any
"

  local changed=0
  if write_file_if_changed "$sshd_dropin" 0644 root root "$content"; then
    changed=1
  fi

  sshd -t
  systemctl enable --now "$SSH_SERVICE"
  if (( changed == 1 )); then
    log "sshd configuration changed; restarting ${SSH_SERVICE}."
    systemctl restart "$SSH_SERVICE"
  else
    log "sshd configuration unchanged; skipping ${SSH_SERVICE} restart."
  fi
}

install_3xui_online() {
  progress 58 "Installing/updating 3x-ui via SOCKS egress"
  configure_online_proxy_env

  local installer="/tmp/3x-ui-install.sh"
  curl --fail --silent --show-error --location \
    "https://raw.githubusercontent.com/MHSanaei/3x-ui/master/install.sh" \
    --output "$installer"

  # Upstream installer runs interactive post-install panel/SSL bootstrap in config_after_install.
  # We disable that step and manage panel settings ourselves (HTTPS port/base path/user/password/cert).
  sed -i 's/^[[:space:]]*config_after_install[[:space:]]*$/# config_after_install disabled by omnirelay-gatewayctl/' "$installer"

  chmod +x "$installer"
  bash "$installer" <<'EOF'
y
EOF

  systemctl enable --now x-ui
  systemctl restart x-ui
}

detect_vps_ip() {
  ip -4 route get 1.1.1.1 2>/dev/null | awk '/src/ {for(i=1;i<=NF;i++) if($i=="src") {print $(i+1); exit}}'
}

apply_hidden_panel_settings() {
  /usr/local/x-ui/x-ui setting -username "$PANEL_USER" -password "$PANEL_PASSWORD" -resetTwoFactor true >/dev/null
  /usr/local/x-ui/x-ui setting -port "$XUI_PANEL_PORT" >/dev/null
  /usr/local/x-ui/x-ui setting -webBasePath "$PANEL_BASE_PATH" >/dev/null
  /usr/local/x-ui/x-ui setting -listenIP "127.0.0.1" >/dev/null
}

configure_panel_credentials() {
  progress 68 "Configuring hidden 3x-ui panel credentials"
  local canonical_base_path panel_probe_code

  if [[ -z "$PANEL_USER" ]]; then
    PANEL_USER="omniadmin_$(random_string 6)"
  fi
  if [[ -z "$PANEL_PASSWORD" ]]; then
    PANEL_PASSWORD="$(random_string 24)"
  fi
  if [[ -z "$PANEL_BASE_PATH" ]]; then
    PANEL_BASE_PATH="$(random_string 18)"
  fi

  PANEL_BASE_PATH="${PANEL_BASE_PATH#/}"
  PANEL_BASE_PATH="${PANEL_BASE_PATH%/}"
  [[ -n "$PANEL_BASE_PATH" ]] || die "Panel base path cannot be empty."
  canonical_base_path="${PANEL_BASE_PATH}"
  XUI_PANEL_PORT="$(choose_random_port 12000 48000 "$PUBLIC_PORT" "$PANEL_PORT" "$BACKEND_PORT" "$BOOTSTRAP_SOCKS_PORT")"

  apply_hidden_panel_settings

  systemctl restart x-ui
  sleep 2

  # Best-effort local probe so operators can distinguish path mismatch from process readiness issues.
  disable_proxy_env
  panel_probe_code="$(curl --noproxy '*' --silent --output /dev/null --write-out '%{http_code}' "http://127.0.0.1:${XUI_PANEL_PORT}/${canonical_base_path}/" || true)"
  if [[ "$panel_probe_code" == "404" || "$panel_probe_code" == "000" || -z "$panel_probe_code" ]]; then
    log "WARNING: Hidden panel local probe returned HTTP ${panel_probe_code} at http://127.0.0.1:${XUI_PANEL_PORT}/${canonical_base_path}/"
  fi
}

reset_stale_xray_template_config() {
  progress 82 "Cleaning stale Xray template settings"

  local db_path="/etc/x-ui/x-ui.db"
  if [[ ! -f "$db_path" ]]; then
    return 0
  fi

  python3 - "$db_path" <<'PY'
import sqlite3
import sys

db_path = sys.argv[1]
conn = sqlite3.connect(db_path)
cur = conn.cursor()
cur.execute("SELECT name FROM sqlite_master WHERE type='table' AND name='settings';")
if cur.fetchone() is not None:
    cur.execute("DELETE FROM settings WHERE key = ?", ("xrayTemplateConfig",))
conn.commit()
conn.close()
PY

  rm -f /etc/x-ui/omnirelay-xray-template.json || true
  systemctl restart x-ui
}

ensure_metadata_dir() {
  install -d -m 0700 -o root -g root "$METADATA_DIR"
}

xui_api_base_url() {
  printf '%s://127.0.0.1:%s/%s' "$XUI_API_SCHEME" "$XUI_PANEL_PORT" "$PANEL_BASE_PATH"
}

xui_wait_for_panel_ready() {
  local retries=90
  local probe_paths=()
  local probe_path code
  PANEL_BASE_PATH="${PANEL_BASE_PATH#/}"
  PANEL_BASE_PATH="${PANEL_BASE_PATH%/}"
  probe_paths=("/${PANEL_BASE_PATH}/" "/${PANEL_BASE_PATH}" "/")

  disable_proxy_env
  for ((i=1; i<=retries; i++)); do
    for probe_path in "${probe_paths[@]}"; do
      code="$(curl --noproxy '*' --silent --insecure --max-time 4 --output /dev/null --write-out '%{http_code}' "http://127.0.0.1:${XUI_PANEL_PORT}${probe_path}" 2>/dev/null || true)"
      if [[ -n "$code" && "$code" != "000" ]]; then
        return 0
      fi
      code="$(curl --noproxy '*' --silent --insecure --max-time 4 --output /dev/null --write-out '%{http_code}' "https://127.0.0.1:${XUI_PANEL_PORT}${probe_path}" 2>/dev/null || true)"
      if [[ -n "$code" && "$code" != "000" ]]; then
        return 0
      fi
    done
    sleep 1
  done

  return 1
}

xui_detect_api_scheme() {
  local retries=45
  local login_paths=()
  local login_path code
  PANEL_BASE_PATH="${PANEL_BASE_PATH#/}"
  PANEL_BASE_PATH="${PANEL_BASE_PATH%/}"
  login_paths=("/${PANEL_BASE_PATH}/login/" "/${PANEL_BASE_PATH}/login" "/${PANEL_BASE_PATH}/" "/")

  disable_proxy_env

  for ((i=1; i<=retries; i++)); do
    # Prefer HTTPS first because x-ui may enforce HTTPS with redirects.
    for login_path in "${login_paths[@]}"; do
      code="$(curl --noproxy '*' --silent --insecure --max-time 4 --output /dev/null --write-out '%{http_code}' "https://127.0.0.1:${XUI_PANEL_PORT}${login_path}" 2>/dev/null || true)"
      if [[ -n "$code" && "$code" != "000" ]]; then
        XUI_API_SCHEME="https"
        return 0
      fi
    done

    for login_path in "${login_paths[@]}"; do
      code="$(curl --noproxy '*' --silent --insecure --max-time 4 --output /dev/null --write-out '%{http_code}' "http://127.0.0.1:${XUI_PANEL_PORT}${login_path}" 2>/dev/null || true)"
      if [[ -n "$code" && "$code" != "000" ]]; then
        XUI_API_SCHEME="http"
        return 0
      fi
    done

    sleep 1
  done

  return 1
}

xui_api_login() {
  progress 74 "Authenticating with hidden 3x-ui API"
  local login_url status retries=20 login_success body_success body_msg
  local fallback_user fallback_pass update_url update_resp update_success update_msg
  xui_wait_for_panel_ready || log "WARNING: Hidden panel did not become ready within expected time on 127.0.0.1:${XUI_PANEL_PORT}."
  xui_detect_api_scheme || die "Unable to detect hidden 3x-ui API scheme on 127.0.0.1:${XUI_PANEL_PORT}."
  login_url="$(xui_api_base_url)/login/"
  XUI_COOKIE_JAR="$(mktemp)"
  disable_proxy_env
  login_success=0

  for ((i=1; i<=retries; i++)); do
    status="$(curl --noproxy '*' --silent --show-error --insecure --output /tmp/omnirelay-xui-login.json --write-out '%{http_code}' \
      --location \
      --cookie-jar "$XUI_COOKIE_JAR" \
      --cookie "$XUI_COOKIE_JAR" \
      --data-urlencode "username=${PANEL_USER}" \
      --data-urlencode "password=${PANEL_PASSWORD}" \
      --data-urlencode "twoFactorCode=" \
      "$login_url" || true)"
    if grep -q '3x-ui' "$XUI_COOKIE_JAR" 2>/dev/null; then
      login_success=1
      break
    fi
    if [[ "$status" == "200" || "$status" == "204" ]]; then
      body_success="$(jq -r '.success // empty' /tmp/omnirelay-xui-login.json 2>/dev/null || true)"
      body_msg="$(jq -r '.msg // empty' /tmp/omnirelay-xui-login.json 2>/dev/null || true)"
      if [[ "$body_success" == "true" ]]; then
        login_success=1
        break
      fi
      if [[ "$i" -eq 3 ]]; then
        log "Hidden API login not accepted yet (status=${status}, success=${body_success:-n/a}, msg=${body_msg:-n/a}); reapplying panel settings and restarting x-ui once."
        apply_hidden_panel_settings
        systemctl restart x-ui
        sleep 2
        xui_detect_api_scheme || true
      fi
    fi
    sleep 2
  done

  if [[ "$login_success" != "1" ]]; then
    fallback_user="admin"
    fallback_pass="admin"
    log "Primary generated credentials were rejected; trying fallback login with default bootstrap credentials."
    for ((i=1; i<=8; i++)); do
      status="$(curl --noproxy '*' --silent --show-error --insecure --output /tmp/omnirelay-xui-login.json --write-out '%{http_code}' \
        --location \
        --cookie-jar "$XUI_COOKIE_JAR" \
        --cookie "$XUI_COOKIE_JAR" \
        --data-urlencode "username=${fallback_user}" \
        --data-urlencode "password=${fallback_pass}" \
        --data-urlencode "twoFactorCode=" \
        "$login_url" || true)"
      if grep -q '3x-ui' "$XUI_COOKIE_JAR" 2>/dev/null; then
        login_success=1
        break
      fi
      sleep 2
    done
  fi

  if [[ "$login_success" == "1" ]] && ! grep -q '3x-ui' "$XUI_COOKIE_JAR" 2>/dev/null; then
    login_success=0
  fi

  if [[ "$login_success" == "1" ]]; then
    # If we only got in using fallback credentials, immediately rotate to generated credentials.
    if [[ "${fallback_user:-}" == "admin" ]] && [[ "${fallback_pass:-}" == "admin" ]] && [[ "$PANEL_USER" != "admin" || "$PANEL_PASSWORD" != "admin" ]]; then
      update_url="$(xui_api_base_url)/panel/setting/updateUser"
      update_resp="$(curl --noproxy '*' --silent --show-error --insecure --location \
        --cookie "$XUI_COOKIE_JAR" \
        --cookie-jar "$XUI_COOKIE_JAR" \
        --data-urlencode "oldUsername=admin" \
        --data-urlencode "oldPassword=admin" \
        --data-urlencode "newUsername=${PANEL_USER}" \
        --data-urlencode "newPassword=${PANEL_PASSWORD}" \
        "$update_url" || true)"
      update_success="$(printf '%s' "$update_resp" | jq -r '.success // empty' 2>/dev/null || true)"
      update_msg="$(printf '%s' "$update_resp" | jq -r '.msg // empty' 2>/dev/null || true)"
      [[ "$update_success" == "true" ]] || die "Authenticated with fallback credentials, but failed to rotate panel credentials (msg=${update_msg:-n/a})."

      rm -f "$XUI_COOKIE_JAR"
      XUI_COOKIE_JAR="$(mktemp)"
      login_success=0
      for ((i=1; i<=8; i++)); do
        status="$(curl --noproxy '*' --silent --show-error --insecure --output /tmp/omnirelay-xui-login.json --write-out '%{http_code}' \
          --location \
          --cookie-jar "$XUI_COOKIE_JAR" \
          --cookie "$XUI_COOKIE_JAR" \
          --data-urlencode "username=${PANEL_USER}" \
          --data-urlencode "password=${PANEL_PASSWORD}" \
          --data-urlencode "twoFactorCode=" \
          "$login_url" || true)"
        if grep -q '3x-ui' "$XUI_COOKIE_JAR" 2>/dev/null; then
          login_success=1
          break
        fi
        sleep 2
      done
    fi
  fi

  if [[ "$login_success" != "1" ]]; then
    body_success="$(jq -r '.success // empty' /tmp/omnirelay-xui-login.json 2>/dev/null || true)"
    body_msg="$(jq -r '.msg // empty' /tmp/omnirelay-xui-login.json 2>/dev/null || true)"
    die "Failed to authenticate against hidden 3x-ui API (status=${status:-n/a}, success=${body_success:-n/a}, msg=${body_msg:-n/a})."
  fi
}

generate_short_ids_json() {
  python3 - <<'PY'
import json
import random
import secrets

ids = []
for _ in range(8):
    ids.append(secrets.token_hex(random.randint(1, 8)))
print(json.dumps(ids))
PY
}

find_existing_managed_inbound_id() {
  local api_base="$1"
  local list_resp existing_id existing_proto

  list_resp="$(curl --noproxy '*' --silent --show-error --insecure --cookie "$XUI_COOKIE_JAR" "${api_base}/inbounds/list" 2>/dev/null || true)"
  if [[ -z "$list_resp" ]]; then
    return 1
  fi

  existing_id="$(printf '%s' "$list_resp" | jq -r --argjson port "$PUBLIC_PORT" '
    if .success != true then
      empty
    else
      (.obj // []) as $inbounds |
      (
        ($inbounds | map(select(((.port | tonumber?) == $port) and (.remark == "omni-relay"))) | .[0].id) //
        ($inbounds | map(select((.port | tonumber?) == $port)) | .[0].id) //
        empty
      )
    end
  ' 2>/dev/null || true)"

  if [[ -n "$existing_id" && "$existing_id" != "null" ]]; then
    existing_proto="$(printf '%s' "$list_resp" | jq -r --arg id "$existing_id" '
      if .success != true then
        empty
      else
        ((.obj // []) | map(select((.id|tostring) == $id)) | .[0].protocol) // empty
      end
    ' 2>/dev/null || true)"

    if [[ "$existing_proto" == "shadowsocks" ]]; then
      INBOUND_ID="$existing_id"
      return 0
    fi

    log "Inbound on port ${PUBLIC_PORT} uses protocol '${existing_proto:-unknown}', replacing with managed Shadowsocks inbound."
    local delete_resp delete_ok
    delete_resp="$(curl --noproxy '*' --silent --show-error --insecure --cookie "$XUI_COOKIE_JAR" \
      --request POST \
      "${api_base}/inbounds/del/${existing_id}" 2>/dev/null || true)"
    delete_ok="$(printf '%s' "$delete_resp" | jq -r '.success // empty' 2>/dev/null || true)"
    if [[ "$delete_ok" != "true" ]]; then
      delete_resp="$(curl --noproxy '*' --silent --show-error --insecure --cookie "$XUI_COOKIE_JAR" \
        "${api_base}/inbounds/del/${existing_id}" 2>/dev/null || true)"
      delete_ok="$(printf '%s' "$delete_resp" | jq -r '.success // empty' 2>/dev/null || true)"
    fi
    [[ "$delete_ok" == "true" ]] || die "Failed to delete existing 3x-ui inbound id=${existing_id} during protocol replacement."
    INBOUND_ID=""
  fi

  return 1
}

provision_managed_inbound() {
  progress 78 "Creating managed Shadowsocks inbound"
  local api_base client_uuid sub_id ss_password
  local settings_json stream_settings_json sniffing_json add_resp success add_msg

  xui_api_login
  api_base="$(xui_api_base_url)/panel/api"
  disable_proxy_env

  if find_existing_managed_inbound_id "$api_base"; then
    log "Reusing existing managed inbound on port ${PUBLIC_PORT} (inbound_id=${INBOUND_ID})."
    return 0
  fi

  client_uuid="$(random_uuid)"
  sub_id="$(random_string 16 | tr '[:upper:]' '[:lower:]')"
  ss_password="$(random_string 24)"

  settings_json="$(jq -cn \
    --arg uuid "$client_uuid" \
    --arg subId "$sub_id" \
    --arg password "$ss_password" \
    --arg method "chacha20-ietf-poly1305" \
    '{
      clients: [
        {
          id: $uuid,
          email: "OmniRelayAdmin",
          password: $password,
          method: "",
          limitIp: 0,
          totalGB: 0,
          expiryTime: 0,
          enable: true,
          tgId: "",
          subId: $subId,
          comment: "",
          reset: 0
        }
      ],
      method: $method,
      network: "tcp",
      encryption: "none"
    }')"

  stream_settings_json='{"network":"tcp","security":"none","externalProxy":[]}'

  sniffing_json='{"enabled":false,"destOverride":["http","tls","quic","fakedns"],"metadataOnly":false,"routeOnly":false}'
  add_resp="$(curl --noproxy '*' --fail --silent --show-error --insecure --cookie "$XUI_COOKIE_JAR" \
    --header 'Content-Type: application/x-www-form-urlencoded; charset=UTF-8' \
    --data-urlencode "up=0" \
    --data-urlencode "down=0" \
    --data-urlencode "total=0" \
    --data-urlencode "remark=omni-relay" \
    --data-urlencode "enable=true" \
    --data-urlencode "expiryTime=0" \
    --data-urlencode "trafficReset=never" \
    --data-urlencode "lastTrafficResetTime=0" \
    --data-urlencode "listen=" \
    --data-urlencode "port=${PUBLIC_PORT}" \
    --data-urlencode "protocol=shadowsocks" \
    --data-urlencode "settings=${settings_json}" \
    --data-urlencode "streamSettings=${stream_settings_json}" \
    --data-urlencode "sniffing=${sniffing_json}" \
    "${api_base}/inbounds/add")"

  success="$(printf '%s' "$add_resp" | jq -r '.success // false')"
  if [[ "$success" != "true" ]]; then
    add_msg="$(printf '%s' "$add_resp" | jq -r '.msg // "unknown error"' 2>/dev/null || printf 'unknown error')"
    if [[ "$add_msg" == *"Duplicate email"* ]]; then
      log "Managed default client email already exists; retrying inbound creation without pre-seeded client."
      settings_json="$(jq -cn \
        --arg method "chacha20-ietf-poly1305" \
        '{
          clients: [],
          method: $method,
          network: "tcp",
          encryption: "none"
        }')"
      add_resp="$(curl --noproxy '*' --fail --silent --show-error --insecure --cookie "$XUI_COOKIE_JAR" \
        --header 'Content-Type: application/x-www-form-urlencoded; charset=UTF-8' \
        --data-urlencode "up=0" \
        --data-urlencode "down=0" \
        --data-urlencode "total=0" \
        --data-urlencode "remark=omni-relay" \
        --data-urlencode "enable=true" \
        --data-urlencode "expiryTime=0" \
        --data-urlencode "trafficReset=never" \
        --data-urlencode "lastTrafficResetTime=0" \
        --data-urlencode "listen=" \
        --data-urlencode "port=${PUBLIC_PORT}" \
        --data-urlencode "protocol=shadowsocks" \
        --data-urlencode "settings=${settings_json}" \
        --data-urlencode "streamSettings=${stream_settings_json}" \
        --data-urlencode "sniffing=${sniffing_json}" \
        "${api_base}/inbounds/add")"
      success="$(printf '%s' "$add_resp" | jq -r '.success // false')"
      add_msg="$(printf '%s' "$add_resp" | jq -r '.msg // "unknown error"' 2>/dev/null || printf 'unknown error')"
    fi
    if [[ "$add_msg" == *"Port already exists"* ]]; then
      if find_existing_managed_inbound_id "$api_base"; then
        log "Detected existing inbound after port-conflict; reusing inbound_id=${INBOUND_ID}."
        return 0
      fi
    fi
    [[ "$success" == "true" ]] || die "Failed to create managed inbound via 3x-ui API: ${add_msg}"
  fi

  INBOUND_ID="$(printf '%s' "$add_resp" | jq -r '.obj.id // empty')"
  [[ -n "$INBOUND_ID" && "$INBOUND_ID" != "null" ]] || die "Unable to capture managed inbound id from 3x-ui response."
}

generate_panel_tls_cert() {
  local cert_dir cert_path key_path server_name detected_ip san_index tmp_cfg
  cert_dir="${METADATA_DIR}/certs"
  cert_path="${cert_dir}/omnipanel.crt"
  key_path="${cert_dir}/omnipanel.key"
  mkdir -p "$cert_dir"
  chmod 0700 "$cert_dir"

  detected_ip="$(detect_vps_ip || true)"
  server_name="$VPS_IP"
  if [[ -z "$server_name" ]]; then
    server_name="$detected_ip"
  fi
  if [[ -z "$server_name" ]]; then
    server_name="localhost"
  fi

  tmp_cfg="$(mktemp)"
  san_index=2
  cat > "$tmp_cfg" <<EOF
[req]
default_bits = 2048
prompt = no
default_md = sha256
distinguished_name = dn
x509_extensions = v3_req

[dn]
CN = ${server_name}

[v3_req]
keyUsage = critical, digitalSignature, keyEncipherment
extendedKeyUsage = serverAuth
subjectAltName = @alt_names

[alt_names]
DNS.1 = localhost
IP.1 = 127.0.0.1
EOF

  if [[ "$server_name" =~ ^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
    printf 'IP.%s = %s\n' "$san_index" "$server_name" >> "$tmp_cfg"
    ((san_index++))
  else
    printf 'DNS.%s = %s\n' "$san_index" "$server_name" >> "$tmp_cfg"
    ((san_index++))
  fi

  if [[ -n "$detected_ip" && "$detected_ip" != "$server_name" ]]; then
    printf 'IP.%s = %s\n' "$san_index" "$detected_ip" >> "$tmp_cfg"
  fi

  openssl req -x509 -nodes -newkey rsa:2048 -days 825 \
    -keyout "$key_path" \
    -out "$cert_path" \
    -config "$tmp_cfg" >/dev/null 2>&1
  rm -f "$tmp_cfg"

  chmod 0600 "$key_path"
  chmod 0644 "$cert_path"
  chown root:root "$key_path" "$cert_path"
}

deploy_omnipanel_artifact() {
  progress 88 "Deploying OmniPanel artifact"
  local download_url release_id release_dir tmp_tar current_dir nested_dir panel_public_host app_parent_dir panel_auth_file
  local panel_ready=false

  ensure_metadata_dir
  configure_online_proxy_env
  download_url="https://omnirelay.net/download/omni-gateway"
  tmp_tar="/tmp/omni-gateway.tar.gz"
  release_id="$(date +%Y%m%d%H%M%S)"
  release_dir="${OMNIPANEL_APP_DIR}/releases/${release_id}"
  current_dir="${OMNIPANEL_APP_DIR}/current"
  app_parent_dir="$(dirname "$OMNIPANEL_APP_DIR")"

  # Ensure systemd service user can traverse working directory path.
  install -d -m 0755 "$app_parent_dir"
  install -d -m 0755 "$OMNIPANEL_APP_DIR"
  install -d -m 0755 "${OMNIPANEL_APP_DIR}/releases"

  install -d -m 0755 "$release_dir"
  curl --fail --silent --show-error --location "$download_url" --output "$tmp_tar"
  tar -xzf "$tmp_tar" -C "$release_dir"

  if [[ ! -f "${release_dir}/server.js" ]]; then
    nested_dir="$(find "$release_dir" -mindepth 1 -maxdepth 1 -type d | head -n1 || true)"
    if [[ -n "$nested_dir" && -f "${nested_dir}/server.js" ]]; then
      cp -a "${nested_dir}/." "$release_dir/"
    fi
  fi
  [[ -f "${release_dir}/server.js" ]] || die "Downloaded OmniPanel artifact is invalid (missing server.js)."

  if ! id -u omnigateway >/dev/null 2>&1; then
    useradd --system --home "$OMNIPANEL_APP_DIR" --shell /usr/sbin/nologin omnigateway
  fi

  OMNIPANEL_INTERNAL_PORT="$(choose_random_port 22000 52000 "$PUBLIC_PORT" "$PANEL_PORT" "$BACKEND_PORT" "$BOOTSTRAP_SOCKS_PORT" "$XUI_PANEL_PORT")"
  SESSION_SECRET="$(openssl rand -hex 32 | tr -d '\r\n')"
  panel_public_host="$VPS_IP"
  if [[ -z "$panel_public_host" ]]; then
    panel_public_host="$(detect_vps_ip || true)"
  fi
  if [[ -z "$panel_public_host" ]]; then
    panel_public_host="localhost"
  fi
  panel_auth_file="${OMNIPANEL_APP_DIR}/panel-auth.json"
  jq -n --arg user "$PANEL_USER" --arg password "$PANEL_PASSWORD" '{username:$user,password:$password}' > "$panel_auth_file"

  cat > "$OMNIPANEL_ENV_FILE" <<EOF
NODE_ENV=production
HOSTNAME=127.0.0.1
PORT=${OMNIPANEL_INTERNAL_PORT}
SESSION_SECRET=${SESSION_SECRET}
NODE_TLS_REJECT_UNAUTHORIZED=0
OMNIPANEL_AUTH_FILE=${panel_auth_file}
OMNIPANEL_AUTH_USERNAME=${PANEL_USER}
OMNIPANEL_AUTH_PASSWORD=${PANEL_PASSWORD}
OMNIRELAY_ACTIVE_PROTOCOL=shadowsocks_3xui
XUI_BASE_URL=$(xui_api_base_url)
XUI_INBOUND_ID=${INBOUND_ID}
XUI_AUTH_USERNAME=${PANEL_USER}
XUI_AUTH_PASSWORD=${PANEL_PASSWORD}
PANEL_PUBLIC_PORT=${PANEL_PORT}
PANEL_PUBLIC_HOST=${panel_public_host}
EOF
  chmod 0600 "$OMNIPANEL_ENV_FILE"

  cat > "/etc/systemd/system/${OMNIPANEL_SERVICE_NAME}.service" <<EOF
[Unit]
Description=OmniRelay OmniPanel
After=network.target x-ui.service
Requires=x-ui.service

[Service]
Type=simple
User=omnigateway
Group=omnigateway
WorkingDirectory=${release_dir}
EnvironmentFile=${OMNIPANEL_ENV_FILE}
ExecStart=/usr/bin/node server.js
Restart=always
RestartSec=3
NoNewPrivileges=true
PrivateTmp=true

[Install]
WantedBy=multi-user.target
EOF

  ln -sfn "$release_dir" "$current_dir"
  chown -R omnigateway:omnigateway "$OMNIPANEL_APP_DIR"
  chown omnigateway:omnigateway "$panel_auth_file"
  chmod 0640 "$panel_auth_file"
  chmod 0755 "$app_parent_dir" "$OMNIPANEL_APP_DIR" "${OMNIPANEL_APP_DIR}/releases" "$release_dir"

  systemctl daemon-reload
  systemctl enable --now "$OMNIPANEL_SERVICE_NAME"
  systemctl restart "$OMNIPANEL_SERVICE_NAME"

  for ((i=1; i<=30; i++)); do
    if [[ "$(systemctl is-active "$OMNIPANEL_SERVICE_NAME" 2>/dev/null || true)" == "active" ]] && [[ "$(check_listener "$OMNIPANEL_INTERNAL_PORT")" == "true" ]]; then
      panel_ready=true
      break
    fi
    sleep 1
  done

  if [[ "$panel_ready" != "true" ]]; then
    log "OmniPanel service did not become ready on 127.0.0.1:${OMNIPANEL_INTERNAL_PORT}."
    log "---- omnipanel systemd status ----"
    systemctl --no-pager --full status "$OMNIPANEL_SERVICE_NAME" || true
    log "---- omnipanel journal (last 80 lines) ----"
    journalctl --no-pager -u "$OMNIPANEL_SERVICE_NAME" -n 80 || true
    die "OmniPanel service failed to start."
  fi
}

configure_nginx_for_omnipanel() {
  progress 92 "Configuring nginx HTTPS reverse-proxy for OmniPanel"
  local nginx_conf cert_path key_path panel_ready=false
  generate_panel_tls_cert

  cert_path="${METADATA_DIR}/certs/omnipanel.crt"
  key_path="${METADATA_DIR}/certs/omnipanel.key"
  nginx_conf="/etc/nginx/sites-available/omnirelay-omnipanel.conf"
  cat > "$nginx_conf" <<EOF
server {
    listen ${PANEL_PORT} ssl;
    server_name _;

    ssl_certificate ${cert_path};
    ssl_certificate_key ${key_path};
    ssl_session_cache shared:SSL:10m;
    ssl_session_timeout 1d;
    ssl_protocols TLSv1.2 TLSv1.3;

    location / {
        proxy_http_version 1.1;
        proxy_set_header Host \$host;
        proxy_set_header X-Forwarded-For \$proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto \$scheme;
        proxy_set_header Upgrade \$http_upgrade;
        proxy_set_header Connection "upgrade";
        proxy_pass http://127.0.0.1:${OMNIPANEL_INTERNAL_PORT};
    }
}
EOF

  ln -sfn "$nginx_conf" /etc/nginx/sites-enabled/omnirelay-omnipanel.conf
  rm -f /etc/nginx/sites-enabled/default || true
  nginx -t
  systemctl enable --now nginx
  systemctl restart nginx

  for ((i=1; i<=15; i++)); do
    if [[ "$(check_listener "$PANEL_PORT")" == "true" ]]; then
      panel_ready=true
      break
    fi
    sleep 1
  done

  [[ "$panel_ready" == "true" ]] || die "nginx did not open panel port ${PANEL_PORT}."
}

configure_host_firewall() {
  progress 94 "Configuring host firewall rules"

  if ! command -v ufw >/dev/null 2>&1; then
    return 0
  fi

  if ! ufw status 2>/dev/null | grep -q "^Status: active"; then
    return 0
  fi

  # Keep rules idempotent. ufw allow is safe to call repeatedly.
  ufw allow "${PANEL_PORT}/tcp" >/dev/null 2>&1 || true
  ufw allow "${PUBLIC_PORT}/tcp" >/dev/null 2>&1 || true
  ufw allow "${SSH_PORT}/tcp" >/dev/null 2>&1 || true
}

write_gateway_metadata() {
  progress 95 "Persisting managed gateway metadata"
  ensure_metadata_dir

  jq -n \
    --arg activeProtocol "shadowsocks_3xui" \
    --arg inboundId "$INBOUND_ID" \
    --arg xuiPort "$XUI_PANEL_PORT" \
    --arg xuiPath "$PANEL_BASE_PATH" \
    --arg xuiUser "$PANEL_USER" \
    --arg xuiPassword "$PANEL_PASSWORD" \
    --arg panelPort "$PANEL_PORT" \
    --arg panelInternalPort "$OMNIPANEL_INTERNAL_PORT" \
    --arg publicPort "$PUBLIC_PORT" \
    --arg gatewaySni "$GATEWAY_SNI" \
    --arg gatewayTarget "$GATEWAY_TARGET" \
    '{
      active_protocol: $activeProtocol,
      inbound_id: $inboundId,
      xui_panel_port: ($xuiPort | tonumber),
      xui_base_path: $xuiPath,
      xui_username: $xuiUser,
      xui_password: $xuiPassword,
      omnipanel_public_port: ($panelPort | tonumber),
      omnipanel_internal_port: ($panelInternalPort | tonumber),
      public_port: ($publicPort | tonumber),
      gateway_sni: $gatewaySni,
      gateway_target: $gatewayTarget,
      created_at_utc: now | todate
    }' > "$METADATA_FILE"

  chmod 0600 "$METADATA_FILE"
}

build_dns_json_array() {
  local csv="$1"
  IFS=',' read -r -a parts <<< "$csv"
  local out=""
  local count=0
  local item trimmed escaped

  for item in "${parts[@]}"; do
    trimmed="$(printf '%s' "$item" | xargs)"
    [[ -n "$trimmed" ]] || continue
    escaped="${trimmed//\\/\\\\}"
    escaped="${escaped//\"/\\\"}"
    if (( count > 0 )); then
      out="${out}, "
    fi
    out="${out}\"${escaped}\""
    ((count++))
  done

  if (( count == 0 )); then
    die "At least one DoH endpoint is required in --doh-endpoints."
  fi

  printf '%s' "$out"
}

render_xray_template() {
  local target="$1"
  local doh_array
  doh_array="$(build_dns_json_array "$DOH_ENDPOINTS")"

  local dns_rule_block='
      {
        "type": "field",
        "network": "udp",
        "port": "53",
        "outboundTag": "dns_out"
      },
      {
        "type": "field",
        "network": "tcp",
        "port": "53",
        "outboundTag": "dns_out"
      },
      {
        "type": "field",
        "network": "tcp",
        "outboundTag": "to_windows_http"
      }'

  if [[ "$DNS_MODE" == "doh" ]]; then
    dns_rule_block='
      {
        "type": "field",
        "network": "tcp",
        "port": "53",
        "outboundTag": "dns_out"
      },
      {
        "type": "field",
        "network": "udp",
        "outboundTag": "blocked"
      },
      {
        "type": "field",
        "network": "tcp",
        "outboundTag": "to_windows_http"
      }'
  elif [[ "$DNS_MODE" == "udp" ]]; then
    dns_rule_block='
      {
        "type": "field",
        "network": "udp",
        "port": "53",
        "outboundTag": "dns_out"
      },
      {
        "type": "field",
        "network": "tcp",
        "port": "53",
        "outboundTag": "dns_out"
      },
      {
        "type": "field",
        "network": "udp",
        "outboundTag": "blocked"
      },
      {
        "type": "field",
        "network": "tcp",
        "outboundTag": "to_windows_http"
      }'
  elif [[ "$DNS_UDP_ONLY" == "true" ]]; then
    dns_rule_block='
      {
        "type": "field",
        "network": "udp",
        "port": "53",
        "outboundTag": "dns_out"
      },
      {
        "type": "field",
        "network": "tcp",
        "port": "53",
        "outboundTag": "dns_out"
      },
      {
        "type": "field",
        "network": "udp",
        "outboundTag": "blocked"
      },
      {
        "type": "field",
        "network": "tcp",
        "outboundTag": "to_windows_http"
      }'
  fi

  cat > "$target" <<EOF
{
  "log": {
    "access": "none",
    "dnsLog": false,
    "error": "",
    "loglevel": "warning",
    "maskAddress": ""
  },
  "dns": {
    "servers": [${doh_array}],
    "queryStrategy": "UseIPv4"
  },
  "api": {
    "tag": "api",
    "services": [
      "HandlerService",
      "LoggerService",
      "StatsService"
    ]
  },
  "inbounds": [
    {
      "tag": "api",
      "listen": "127.0.0.1",
      "port": 62789,
      "protocol": "dokodemo-door",
      "settings": {
        "address": "127.0.0.1"
      }
    }
  ],
  "outbounds": [
    {
      "tag": "to_windows_http",
      "protocol": "http",
      "settings": {
        "servers": [
          {
            "address": "127.0.0.1",
            "port": ${BACKEND_PORT}
          }
        ]
      }
    },
    {
      "tag": "dns_out",
      "protocol": "dns",
      "settings": {}
    },
    {
      "tag": "direct",
      "protocol": "freedom",
      "settings": {}
    },
    {
      "tag": "blocked",
      "protocol": "blackhole",
      "settings": {}
    }
  ],
  "routing": {
    "domainStrategy": "IPIfNonMatch",
    "rules": [
      {
        "type": "field",
        "inboundTag": [
          "api"
        ],
        "outboundTag": "api"
      },${dns_rule_block}
    ]
  },
  "policy": {
    "levels": {
      "0": {
        "statsUserUplink": true,
        "statsUserDownlink": true
      }
    },
    "system": {
      "statsInboundUplink": true,
      "statsInboundDownlink": true,
      "statsOutboundUplink": true,
      "statsOutboundDownlink": true
    }
  },
  "stats": {}
}
EOF
}

read_setting_value() {
  local db_path="$1"
  local key="$2"
  python3 - "$db_path" "$key" <<'PY'
import sqlite3
import sys

db_path, key = sys.argv[1], sys.argv[2]
conn = sqlite3.connect(db_path)
cur = conn.cursor()
cur.execute("SELECT name FROM sqlite_master WHERE type='table' AND name='settings';")
if cur.fetchone() is None:
    print("")
    conn.close()
    raise SystemExit(0)
cur.execute("SELECT value FROM settings WHERE key = ?", (key,))
row = cur.fetchone()
print("" if row is None or row[0] is None else row[0])
conn.close()
PY
}

write_setting_value() {
  local db_path="$1"
  local key="$2"
  local value_file="$3"
  python3 - "$db_path" "$key" "$value_file" <<'PY'
import sqlite3
import sys
from pathlib import Path

db_path, key, value_file = sys.argv[1], sys.argv[2], sys.argv[3]
value = Path(value_file).read_text(encoding="utf-8")

conn = sqlite3.connect(db_path)
cur = conn.cursor()
cur.execute("SELECT name FROM sqlite_master WHERE type='table' AND name='settings';")
if cur.fetchone() is None:
    raise SystemExit("settings table not found")
cur.execute("SELECT id FROM settings WHERE key = ?", (key,))
row = cur.fetchone()
if row:
    cur.execute("UPDATE settings SET value = ? WHERE id = ?", (value, row[0]))
else:
    cur.execute("INSERT INTO settings(key, value) VALUES(?, ?)", (key, value))
conn.commit()
conn.close()
PY
}

apply_dns_profile() {
  progress 82 "Applying DNS-through-tunnel profile"
  local db_path="/etc/x-ui/x-ui.db"
  local template_path="/etc/x-ui/omnirelay-xray-template.json"
  local backup_dir="/etc/x-ui/backup"
  local backup_path
  local previous

  [[ -f "$db_path" ]] || die "x-ui DB not found at ${db_path}."
  mkdir -p "$backup_dir"

  previous="$(read_setting_value "$db_path" "xrayTemplateConfig" || true)"
  backup_path="${backup_dir}/xrayTemplateConfig.$(date +%Y%m%d%H%M%S).json"
  printf '%s' "$previous" > "$backup_path"

  render_xray_template "$template_path"

  if ! write_setting_value "$db_path" "xrayTemplateConfig" "$template_path"; then
    if [[ -f "$backup_path" ]]; then
      write_setting_value "$db_path" "xrayTemplateConfig" "$backup_path" || true
    fi
    die "Failed to write DNS/Xray template configuration."
  fi

  systemctl restart x-ui
}

dns_status_json() {
  local config_path="/usr/local/x-ui/bin/config.json"
  local db_path="/etc/x-ui/x-ui.db"
  local template_value dns_config_present dns_rule_active doh_reachable udp53_ready dns_path_healthy first_doh http_code

  template_value="$(read_setting_value "$db_path" "xrayTemplateConfig" || true)"
  dns_config_present=false
  dns_rule_active=false
  doh_reachable=false
  udp53_ready=false
  dns_path_healthy=false

  if [[ "$template_value" == *"dns_out"* && "$template_value" == *"to_windows_http"* ]]; then
    dns_config_present=true
  fi

  if [[ -f "$config_path" ]] && grep -q '"dns_out"' "$config_path" && grep -q '"to_windows_http"' "$config_path"; then
    dns_rule_active=true
  fi

  if [[ -f "$config_path" ]] && grep -q '"network"[[:space:]]*:[[:space:]]*"udp"' "$config_path" && grep -q '"port"[[:space:]]*:[[:space:]]*"53"' "$config_path"; then
    udp53_ready=true
  fi

  first_doh="$(printf '%s' "$DOH_ENDPOINTS" | cut -d',' -f1 | xargs)"
  if [[ -n "$first_doh" ]]; then
    configure_online_proxy_env
    http_code="$(curl --silent --show-error --max-time 15 --connect-timeout 10 --output /dev/null --write-out '%{http_code}' --socks5-hostname "127.0.0.1:${BOOTSTRAP_SOCKS_PORT}" "$first_doh" 2>/dev/null || true)"
    if [[ -n "$http_code" && "$http_code" != "000" ]]; then
      doh_reachable=true
    fi
  fi

  if [[ "$dns_config_present" == "true" && "$dns_rule_active" == "true" ]]; then
    case "$DNS_MODE" in
      hybrid)
        [[ "$doh_reachable" == "true" && "$udp53_ready" == "true" ]] && dns_path_healthy=true
        ;;
      doh)
        [[ "$doh_reachable" == "true" ]] && dns_path_healthy=true
        ;;
      udp)
        [[ "$udp53_ready" == "true" ]] && dns_path_healthy=true
        ;;
    esac
  fi

  printf '{"dnsMode":"%s","dnsUdpOnly":%s,"dohEndpoints":"%s","dnsConfigPresent":%s,"dnsRuleActive":%s,"dohReachableViaTunnel":%s,"udp53PathReady":%s,"dnsPathHealthy":%s}\n' \
    "$(json_escape "$DNS_MODE")" \
    "$DNS_UDP_ONLY" \
    "$(json_escape "$DOH_ENDPOINTS")" \
    "$dns_config_present" \
    "$dns_rule_active" \
    "$doh_reachable" \
    "$udp53_ready" \
    "$dns_path_healthy"
}

dns_apply_impl() {
  require_root
  verify_bootstrap_socks
  apply_dns_profile
  progress 100 "DNS profile applied"
}

dns_status_impl() {
  local json
  json="$(dns_status_json)"
  if (( STATUS_JSON == 1 || HEALTH_JSON == 1 )); then
    printf '%s\n' "$json"
    return
  fi
  cat <<EOF
DNS status:
${json}
EOF
}

dns_repair_impl() {
  require_root
  progress 40 "Repairing DNS profile"
  dns_apply_impl
}

sync_clients_impl() {
  require_root
  log "sync-clients is not required for shadowsocks_3xui; skipping."
  echo "ok"
}

get_protocol_impl() {
  echo "shadowsocks_3xui"
}

disable_haproxy() {
  if systemctl list-unit-files | grep -q '^haproxy\.service'; then
    systemctl disable --now haproxy || true
  fi
}

check_listener() {
  local port="$1"
  if ss -lnt "( sport = :${port} )" 2>/dev/null | awk 'NR>1 {print $0}' | grep -q .; then
    echo true
  else
    echo false
  fi
}

ensure_ssh_service_resolved() {
  if [[ -n "$SSH_SERVICE" ]]; then
    return 0
  fi

  detect_ssh_service || SSH_SERVICE="ssh"
}

metadata_value() {
  local key="$1"
  if [[ -f "$METADATA_FILE" ]]; then
    jq -r "$key // empty" "$METADATA_FILE" 2>/dev/null || true
  fi
}

status_impl() {
  ensure_ssh_service_resolved
  local ssh_state xui_state omnipanel_state nginx_state fail2ban_state tunnel_listener ingress_listener panel_listener socks_listener dns_json
  local omnipanel_internal_listener dns_config_present dns_rule_active doh_reachable udp53_ready dns_path_healthy dns_mode dns_udp_only doh_endpoints
  local metadata_inbound_id metadata_internal_port metadata_xui_panel_port

  ssh_state="$(systemctl is-active "$SSH_SERVICE" 2>/dev/null || echo inactive)"
  xui_state="$(systemctl is-active x-ui 2>/dev/null || echo inactive)"
  omnipanel_state="$(systemctl is-active "$OMNIPANEL_SERVICE_NAME" 2>/dev/null || echo inactive)"
  nginx_state="$(systemctl is-active nginx 2>/dev/null || echo inactive)"
  fail2ban_state="disabled"
  tunnel_listener="$(check_listener "$BACKEND_PORT")"
  ingress_listener="$(check_listener "$PUBLIC_PORT")"
  panel_listener="$(check_listener "$PANEL_PORT")"
  socks_listener="$(check_listener "$BOOTSTRAP_SOCKS_PORT")"
  metadata_internal_port="$(metadata_value '.omnipanel_internal_port')"
  metadata_xui_panel_port="$(metadata_value '.xui_panel_port')"
  metadata_inbound_id="$(metadata_value '.inbound_id')"
  if [[ -n "$metadata_internal_port" ]]; then
    omnipanel_internal_listener="$(check_listener "$metadata_internal_port")"
  else
    omnipanel_internal_listener="false"
  fi
  dns_json="$(dns_status_json)"
  dns_config_present="$(printf '%s' "$dns_json" | grep -o '"dnsConfigPresent":[^,}]*' | cut -d: -f2)"
  dns_rule_active="$(printf '%s' "$dns_json" | grep -o '"dnsRuleActive":[^,}]*' | cut -d: -f2)"
  doh_reachable="$(printf '%s' "$dns_json" | grep -o '"dohReachableViaTunnel":[^,}]*' | cut -d: -f2)"
  udp53_ready="$(printf '%s' "$dns_json" | grep -o '"udp53PathReady":[^,}]*' | cut -d: -f2)"
  dns_path_healthy="$(printf '%s' "$dns_json" | grep -o '"dnsPathHealthy":[^,}]*' | cut -d: -f2)"
  dns_mode="$(printf '%s' "$dns_json" | grep -o '"dnsMode":"[^"]*"' | sed 's/"dnsMode":"//;s/"$//')"
  dns_udp_only="$(printf '%s' "$dns_json" | grep -o '"dnsUdpOnly":[^,}]*' | cut -d: -f2)"
  doh_endpoints="$(printf '%s' "$dns_json" | grep -o '"dohEndpoints":"[^"]*"' | sed 's/"dohEndpoints":"//;s/"$//')"

  if (( STATUS_JSON == 1 )); then
    printf '{"activeProtocol":"shadowsocks_3xui","sshState":"%s","xuiState":"%s","singBoxState":"inactive","omniPanelState":"%s","nginxState":"%s","fail2banState":"%s","backendPort":%s,"publicPort":%s,"panelPort":%s,"omniPanelInternalPort":%s,"xuiPanelPort":%s,"bootstrapSocksPort":%s,"backendListener":%s,"publicListener":%s,"panelListener":%s,"omniPanelInternalListener":%s,"bootstrapSocksListener":%s,"inboundId":"%s","dnsConfigPresent":%s,"dnsRuleActive":%s,"dohReachableViaTunnel":%s,"udp53PathReady":%s,"dnsPathHealthy":%s,"dnsMode":"%s","dnsUdpOnly":%s,"dohEndpoints":"%s"}\n' \
      "$(json_escape "$ssh_state")" \
      "$(json_escape "$xui_state")" \
      "$(json_escape "$omnipanel_state")" \
      "$(json_escape "$nginx_state")" \
      "$(json_escape "$fail2ban_state")" \
      "$BACKEND_PORT" \
      "$PUBLIC_PORT" \
      "$PANEL_PORT" \
      "${metadata_internal_port:-0}" \
      "${metadata_xui_panel_port:-0}" \
      "$BOOTSTRAP_SOCKS_PORT" \
      "$tunnel_listener" \
      "$ingress_listener" \
      "$panel_listener" \
      "$omnipanel_internal_listener" \
      "$socks_listener" \
      "$(json_escape "$metadata_inbound_id")" \
      "$dns_config_present" \
      "$dns_rule_active" \
      "$doh_reachable" \
      "$udp53_ready" \
      "$dns_path_healthy" \
      "$(json_escape "$dns_mode")" \
      "$dns_udp_only" \
      "$(json_escape "$doh_endpoints")"
    return
  fi

  cat <<EOF
Gateway status:
  sshd:      ${ssh_state}
  x-ui:      ${xui_state}
  omniPanel: ${omnipanel_state}
  nginx:     ${nginx_state}
  fail2ban:  ${fail2ban_state}
  backend listener 127.0.0.1:${BACKEND_PORT}: ${tunnel_listener}
  public listener 0.0.0.0:${PUBLIC_PORT}: ${ingress_listener}
  omniPanel listener 0.0.0.0:${PANEL_PORT}: ${panel_listener}
  omniPanel internal 127.0.0.1:${metadata_internal_port:-0}: ${omnipanel_internal_listener}
  hidden 3x-ui panel port: ${metadata_xui_panel_port:-unknown}
  managed inbound id: ${metadata_inbound_id:-unknown}
  bootstrap socks 127.0.0.1:${BOOTSTRAP_SOCKS_PORT}: ${socks_listener}
  dns mode: ${dns_mode}
  dns udp only: ${dns_udp_only}
  doh endpoints: ${doh_endpoints}
  dns config present: ${dns_config_present}
  dns rule active: ${dns_rule_active}
  doh reachable via tunnel: ${doh_reachable}
  udp53 path ready: ${udp53_ready}
EOF
}

health_impl() {
  ensure_ssh_service_resolved
  progress 5 "Running gateway health checks"
  local ssh_state xui_state omnipanel_state nginx_state fail2ban_state tunnel_listener ingress_listener panel_listener socks_listener healthy dns_json
  local omnipanel_internal_listener metadata_internal_port metadata_xui_panel_port metadata_inbound_id
  local dns_config_present dns_rule_active doh_reachable udp53_ready dns_path_healthy dns_mode dns_udp_only doh_endpoints dns_last_error

  ssh_state="$(systemctl is-active "$SSH_SERVICE" 2>/dev/null || echo inactive)"
  xui_state="$(systemctl is-active x-ui 2>/dev/null || echo inactive)"
  omnipanel_state="$(systemctl is-active "$OMNIPANEL_SERVICE_NAME" 2>/dev/null || echo inactive)"
  nginx_state="$(systemctl is-active nginx 2>/dev/null || echo inactive)"
  fail2ban_state="disabled"

  progress 45 "Checking expected listeners"
  tunnel_listener="$(check_listener "$BACKEND_PORT")"
  ingress_listener="$(check_listener "$PUBLIC_PORT")"
  panel_listener="$(check_listener "$PANEL_PORT")"
  socks_listener="$(check_listener "$BOOTSTRAP_SOCKS_PORT")"
  metadata_internal_port="$(metadata_value '.omnipanel_internal_port')"
  metadata_xui_panel_port="$(metadata_value '.xui_panel_port')"
  metadata_inbound_id="$(metadata_value '.inbound_id')"
  if [[ -n "$metadata_internal_port" ]]; then
    omnipanel_internal_listener="$(check_listener "$metadata_internal_port")"
  else
    omnipanel_internal_listener="false"
  fi
  dns_json="$(dns_status_json)"
  dns_config_present="$(printf '%s' "$dns_json" | grep -o '"dnsConfigPresent":[^,}]*' | cut -d: -f2)"
  dns_rule_active="$(printf '%s' "$dns_json" | grep -o '"dnsRuleActive":[^,}]*' | cut -d: -f2)"
  doh_reachable="$(printf '%s' "$dns_json" | grep -o '"dohReachableViaTunnel":[^,}]*' | cut -d: -f2)"
  udp53_ready="$(printf '%s' "$dns_json" | grep -o '"udp53PathReady":[^,}]*' | cut -d: -f2)"
  dns_path_healthy="$(printf '%s' "$dns_json" | grep -o '"dnsPathHealthy":[^,}]*' | cut -d: -f2)"
  dns_mode="$(printf '%s' "$dns_json" | grep -o '"dnsMode":"[^"]*"' | sed 's/"dnsMode":"//;s/"$//')"
  dns_udp_only="$(printf '%s' "$dns_json" | grep -o '"dnsUdpOnly":[^,}]*' | cut -d: -f2)"
  doh_endpoints="$(printf '%s' "$dns_json" | grep -o '"dohEndpoints":"[^"]*"' | sed 's/"dohEndpoints":"//;s/"$//')"

  healthy=true
  [[ "$ssh_state" == "active" ]] || healthy=false
  [[ "$xui_state" == "active" ]] || healthy=false
  [[ "$omnipanel_state" == "active" ]] || healthy=false
  [[ "$nginx_state" == "active" ]] || healthy=false
  [[ "$panel_listener" == "true" ]] || healthy=false
  [[ "$omnipanel_internal_listener" == "true" ]] || healthy=false
  [[ "$tunnel_listener" == "true" ]] || healthy=false
  [[ "$socks_listener" == "true" ]] || healthy=false
  [[ "$dns_path_healthy" == "true" ]] || healthy=false
  dns_last_error=""
  [[ "$dns_config_present" == "true" ]] || dns_last_error="dnsConfigMissing"
  if [[ "$dns_rule_active" != "true" ]]; then dns_last_error="dnsRuleInactive"; fi
  case "$dns_mode" in
    hybrid)
      if [[ "$doh_reachable" != "true" ]]; then dns_last_error="dohUnreachableViaTunnel"; fi
      if [[ "$udp53_ready" != "true" ]]; then dns_last_error="udp53PathNotReady"; fi
      ;;
    doh)
      if [[ "$doh_reachable" != "true" ]]; then dns_last_error="dohUnreachableViaTunnel"; fi
      ;;
    udp)
      if [[ "$udp53_ready" != "true" ]]; then dns_last_error="udp53PathNotReady"; fi
      ;;
  esac

  progress 100 "Health checks completed"

  if (( HEALTH_JSON == 1 )); then
    printf '{"healthy":%s,"activeProtocol":"shadowsocks_3xui","sshState":"%s","xuiState":"%s","singBoxState":"inactive","omniPanelState":"%s","nginxState":"%s","fail2banState":"%s","backendPort":%s,"publicPort":%s,"panelPort":%s,"omniPanelInternalPort":%s,"xuiPanelPort":%s,"bootstrapSocksPort":%s,"backendListener":%s,"publicListener":%s,"panelListener":%s,"omniPanelInternalListener":%s,"bootstrapSocksListener":%s,"inboundId":"%s","dnsConfigPresent":%s,"dnsRuleActive":%s,"dohReachableViaTunnel":%s,"udp53PathReady":%s,"dnsPathHealthy":%s,"dnsMode":"%s","dnsUdpOnly":%s,"dohEndpoints":"%s","dnsLastError":"%s"}\n' \
      "$healthy" \
      "$(json_escape "$ssh_state")" \
      "$(json_escape "$xui_state")" \
      "$(json_escape "$omnipanel_state")" \
      "$(json_escape "$nginx_state")" \
      "$(json_escape "$fail2ban_state")" \
      "$BACKEND_PORT" \
      "$PUBLIC_PORT" \
      "$PANEL_PORT" \
      "${metadata_internal_port:-0}" \
      "${metadata_xui_panel_port:-0}" \
      "$BOOTSTRAP_SOCKS_PORT" \
      "$tunnel_listener" \
      "$ingress_listener" \
      "$panel_listener" \
      "$omnipanel_internal_listener" \
      "$socks_listener" \
      "$(json_escape "$metadata_inbound_id")" \
      "$dns_config_present" \
      "$dns_rule_active" \
      "$doh_reachable" \
      "$udp53_ready" \
      "$dns_path_healthy" \
      "$(json_escape "$dns_mode")" \
      "$dns_udp_only" \
      "$(json_escape "$doh_endpoints")" \
      "$(json_escape "$dns_last_error")"
    return
  fi

  cat <<EOF
Gateway health: ${healthy}
  sshd:      ${ssh_state}
  x-ui:      ${xui_state}
  omniPanel: ${omnipanel_state}
  nginx:     ${nginx_state}
  fail2ban:  ${fail2ban_state}
  backend listener 127.0.0.1:${BACKEND_PORT}: ${tunnel_listener}
  public listener 0.0.0.0:${PUBLIC_PORT}: ${ingress_listener}
  omniPanel listener 0.0.0.0:${PANEL_PORT}: ${panel_listener}
  omniPanel internal 127.0.0.1:${metadata_internal_port:-0}: ${omnipanel_internal_listener}
  hidden 3x-ui panel port: ${metadata_xui_panel_port:-unknown}
  managed inbound id: ${metadata_inbound_id:-unknown}
  bootstrap socks 127.0.0.1:${BOOTSTRAP_SOCKS_PORT}: ${socks_listener}
  dns config present: ${dns_config_present}
  dns rule active: ${dns_rule_active}
  doh reachable via tunnel: ${doh_reachable}
  udp53 path ready: ${udp53_ready}
  dns last error: ${dns_last_error}
EOF
}

start_impl() {
  require_root
  ensure_ssh_service_resolved
  progress 12 "Starting sshd"
  systemctl start "$SSH_SERVICE"
  progress 45 "Starting x-ui"
  systemctl start x-ui
  progress 78 "Starting OmniPanel"
  systemctl start "$OMNIPANEL_SERVICE_NAME" || true
  progress 90 "Ensuring nginx is running"
  systemctl start nginx || true
  progress 100 "Gateway services started"
}

stop_impl() {
  require_root
  progress 20 "Stopping OmniPanel"
  systemctl stop "$OMNIPANEL_SERVICE_NAME" || true
  progress 60 "Stopping x-ui"
  systemctl stop x-ui || true
  progress 100 "Gateway service stop completed"
}

uninstall_impl() {
  require_root
  progress 8 "Stopping OmniPanel and x-ui"
  systemctl disable --now "$OMNIPANEL_SERVICE_NAME" || true
  systemctl disable --now x-ui || true
  systemctl disable --now omnirelay-singbox omnirelay-openvpn omnirelay-openvpn-accounting omnirelay-openvpn-accounting.timer omnirelay-redsocks omnirelay-ipsec-rules omnirelay-ipsec-accounting omnirelay-ipsec-accounting.timer 2>/dev/null || true
  systemctl disable --now ipsec xl2tpd strongswan-starter 2>/dev/null || true

  progress 20 "Removing OmniPanel runtime"
  rm -rf "$OMNIPANEL_APP_DIR"
  rm -f "/etc/systemd/system/${OMNIPANEL_SERVICE_NAME}.service"
  rm -f /etc/systemd/system/omnirelay-singbox.service /etc/systemd/system/omnirelay-openvpn.service /etc/systemd/system/omnirelay-openvpn-accounting.service /etc/systemd/system/omnirelay-openvpn-accounting.timer /etc/systemd/system/omnirelay-redsocks.service /etc/systemd/system/omnirelay-ipsec-rules.service /etc/systemd/system/omnirelay-ipsec-accounting.service /etc/systemd/system/omnirelay-ipsec-accounting.timer
  rm -f "$OMNIPANEL_ENV_FILE"
  rm -f "$METADATA_FILE"
  rm -rf "$METADATA_DIR"
  systemctl daemon-reload || true

  progress 32 "Removing OmniPanel nginx configuration"
  rm -f /etc/nginx/sites-enabled/omnirelay-omnipanel.conf
  rm -f /etc/nginx/sites-available/omnirelay-omnipanel.conf
  nginx -t >/dev/null 2>&1 && systemctl reload nginx || true

  progress 50 "Removing x-ui files"
  rm -rf /usr/local/x-ui /etc/x-ui /var/log/x-ui
  rm -f /usr/bin/x-ui /etc/systemd/system/x-ui.service
  rm -f /etc/sudoers.d/omnigateway-singbox /etc/sudoers.d/omnigateway-openvpn /etc/sudoers.d/omnigateway-ipsec
  rm -f /etc/sysctl.d/99-omnirelay-openvpn.conf /etc/sysctl.d/99-omnirelay-ipsec.conf
  rm -f /etc/ppp/ip-up.d/99-omnirelay-accounting /etc/ppp/ip-down.d/99-omnirelay-accounting
  rm -f /var/log/openvpn/omnirelay-status.log
  rm -f /usr/local/sbin/omnirelay-gatewayctl

  progress 74 "Cleaning managed sshd drop-in"
  rm -f /etc/ssh/sshd_config.d/99-omnirelay.conf
  sshd -t || true
  if [[ -n "$SSH_SERVICE" ]]; then
    systemctl restart "$SSH_SERVICE" || true
  fi

  progress 100 "Gateway uninstall completed"
}

print_summary() {
  ensure_ssh_service_resolved
  local endpoint panel_url metadata_inbound_id metadata_xui_panel_port
  endpoint="$VPS_IP"
  if [[ -z "$endpoint" ]]; then
    endpoint="$(detect_vps_ip || true)"
  fi
  if [[ -z "$endpoint" ]]; then
    endpoint="<VPS_IP_OR_HOSTNAME>"
  fi

  panel_url="https://${endpoint}:${PANEL_PORT}/"
  metadata_inbound_id="$(metadata_value '.inbound_id')"
  metadata_xui_panel_port="$(metadata_value '.xui_panel_port')"

  cat <<EOF

============================================================
OmniRelay VPS 3x-ui online install complete.

Services:
  - sshd:      $(systemctl is-active "$SSH_SERVICE" || true)
  - x-ui:      $(systemctl is-active x-ui || true)
  - omnipanel: $(systemctl is-active "$OMNIPANEL_SERVICE_NAME" || true)
  - nginx:     $(systemctl is-active nginx || true)
  - fail2ban:  disabled

OmniPanel:
  URL:      ${panel_url}
  Username: ${PANEL_USER}
  Password: ${PANEL_PASSWORD}
  Inbound:  ${metadata_inbound_id:-unknown}

Hidden 3x-ui:
  Base path: /${PANEL_BASE_PATH}
  Loopback port: ${metadata_xui_panel_port:-unknown}

Traffic model:
  Client -> VPS:${PUBLIC_PORT} -> x-ui/Xray -> http://127.0.0.1:${BACKEND_PORT}
  -> SSH reverse tunnel -> Windows proxy
============================================================
EOF
}

install_impl() {
  require_root
  require_existing_sshd

  progress 3 "Validating platform"
  systemctl disable --now omnirelay-singbox omnirelay-openvpn omnirelay-openvpn-accounting omnirelay-openvpn-accounting.timer omnirelay-redsocks omnirelay-ipsec-rules omnirelay-ipsec-accounting omnirelay-ipsec-accounting.timer 2>/dev/null || true
  systemctl disable --now ipsec xl2tpd strongswan-starter 2>/dev/null || true
  rm -f /etc/systemd/system/omnirelay-singbox.service /etc/systemd/system/omnirelay-openvpn.service /etc/systemd/system/omnirelay-openvpn-accounting.service /etc/systemd/system/omnirelay-openvpn-accounting.timer /etc/systemd/system/omnirelay-redsocks.service /etc/systemd/system/omnirelay-ipsec-rules.service /etc/systemd/system/omnirelay-ipsec-accounting.service /etc/systemd/system/omnirelay-ipsec-accounting.timer
  rm -f /etc/sudoers.d/omnigateway-singbox /etc/sudoers.d/omnigateway-openvpn /etc/sudoers.d/omnigateway-ipsec
  rm -f /etc/sysctl.d/99-omnirelay-openvpn.conf /etc/sysctl.d/99-omnirelay-ipsec.conf
  rm -f /etc/ppp/ip-up.d/99-omnirelay-accounting /etc/ppp/ip-down.d/99-omnirelay-accounting
  rm -f /var/log/openvpn/omnirelay-status.log
  systemctl daemon-reload
  setup_tunnel_user
  configure_sshd
  progress 8 "Bootstrap SOCKS pre-check skipped; using live online install checks"
  install_packages_online
  ensure_nodejs_runtime
  disable_haproxy
  install_3xui_online
  configure_panel_credentials
  provision_managed_inbound
  deploy_omnipanel_artifact
  configure_nginx_for_omnipanel
  configure_host_firewall
  write_gateway_metadata
  reset_stale_xray_template_config
  apply_dns_profile
  install -m 0755 "$0" /usr/local/sbin/omnirelay-gatewayctl

  progress 100 "Gateway install completed"
  print_summary
}

parse_args() {
  if [[ $# -gt 0 ]] && [[ ! "$1" =~ ^- ]]; then
    COMMAND="$1"
    shift
  fi

  while [[ $# -gt 0 ]]; do
    case "$1" in
      --public-port)
        PUBLIC_PORT="${2:-}"
        shift 2
        ;;
      --panel-port)
        PANEL_PORT="${2:-}"
        shift 2
        ;;
      --backend-port)
        BACKEND_PORT="${2:-}"
        shift 2
        ;;
      --gateway-sni)
        GATEWAY_SNI="${2:-}"
        shift 2
        ;;
      --gateway-target)
        GATEWAY_TARGET="${2:-}"
        shift 2
        ;;
      --ssh-port)
        SSH_PORT="${2:-}"
        shift 2
        ;;
      --tunnel-user)
        TUNNEL_USER="${2:-}"
        shift 2
        ;;
      --tunnel-auth)
        TUNNEL_AUTH_METHOD="${2:-}"
        shift 2
        ;;
      --bootstrap-socks-port)
        BOOTSTRAP_SOCKS_PORT="${2:-}"
        shift 2
        ;;
      --proxy-check-url)
        PROXY_CHECK_URL="${2:-}"
        shift 2
        ;;
      --dns-mode)
        DNS_MODE="${2:-}"
        shift 2
        ;;
      --doh-endpoints)
        DOH_ENDPOINTS="${2:-}"
        shift 2
        ;;
      --dns-udp-only)
        DNS_UDP_ONLY="${2:-}"
        shift 2
        ;;
      --vps-ip)
        VPS_IP="${2:-}"
        shift 2
        ;;
      --pubkey)
        PUBKEY="${2:-}"
        shift 2
        ;;
      --pubkey-file)
        PUBKEY_FILE="${2:-}"
        shift 2
        ;;
      --panel-user)
        PANEL_USER="${2:-}"
        shift 2
        ;;
      --panel-password)
        PANEL_PASSWORD="${2:-}"
        shift 2
        ;;
      --panel-base-path)
        PANEL_BASE_PATH="${2:-}"
        shift 2
        ;;
      --json)
        STATUS_JSON=1
        HEALTH_JSON=1
        shift
        ;;
      -h|--help)
        usage
        exit 0
        ;;
      *)
        die "Unknown option: $1"
        ;;
    esac
  done
}

validate_common() {
  PUBLIC_PORT="$(validate_port "$PUBLIC_PORT" "--public-port")"
  PANEL_PORT="$(validate_port "$PANEL_PORT" "--panel-port")"
  BACKEND_PORT="$(validate_port "$BACKEND_PORT" "--backend-port")"
  SSH_PORT="$(validate_port "$SSH_PORT" "--ssh-port")"
  BOOTSTRAP_SOCKS_PORT="$(validate_port "$BOOTSTRAP_SOCKS_PORT" "--bootstrap-socks-port")"
  DNS_MODE="$(validate_dns_mode "$DNS_MODE")"
  DNS_UDP_ONLY="$(normalize_bool "$DNS_UDP_ONLY")"
  DOH_ENDPOINTS="$(printf '%s' "$DOH_ENDPOINTS" | tr -d '\r\n' | xargs)"
  [[ -n "$DOH_ENDPOINTS" ]] || die "--doh-endpoints cannot be empty."
  PROXY_CHECK_URL="$(printf '%s' "$PROXY_CHECK_URL" | tr -d '\r\n' | xargs)"
  PROXY_CHECK_URL="${PROXY_CHECK_URL#\"}"
  PROXY_CHECK_URL="${PROXY_CHECK_URL%\"}"
  PROXY_CHECK_URL="${PROXY_CHECK_URL#\'}"
  PROXY_CHECK_URL="${PROXY_CHECK_URL%\'}"
  GATEWAY_SNI="$(printf '%s' "$GATEWAY_SNI" | tr -d '\r\n' | xargs)"
  GATEWAY_TARGET="$(printf '%s' "$GATEWAY_TARGET" | tr -d '\r\n' | xargs)"

  if [[ "$PUBLIC_PORT" == "$PANEL_PORT" ]]; then
    die "--public-port and --panel-port must be different."
  fi

  if [[ "$BACKEND_PORT" == "$BOOTSTRAP_SOCKS_PORT" ]]; then
    die "--backend-port and --bootstrap-socks-port must be different."
  fi

  if [[ -n "$PUBKEY_FILE" ]]; then
    [[ -f "$PUBKEY_FILE" ]] || die "--pubkey-file not found: ${PUBKEY_FILE}"
    PUBKEY="$(tr -d '\r' < "$PUBKEY_FILE" | head -n1 | xargs)"
  fi

  case "$TUNNEL_AUTH_METHOD" in
    host_key|password|both) ;;
    *) die "Invalid --tunnel-auth value: ${TUNNEL_AUTH_METHOD}. Expected host_key|password|both." ;;
  esac

}

main() {
  parse_args "$@"
  validate_common

  case "$COMMAND" in
    install)
      install_impl
      ;;
    uninstall)
      uninstall_impl
      ;;
    start)
      start_impl
      ;;
    stop)
      stop_impl
      ;;
    get-protocol)
      get_protocol_impl
      ;;
    status)
      status_impl
      ;;
    health)
      health_impl
      ;;
    dns-apply)
      dns_apply_impl
      ;;
    dns-status)
      dns_status_impl
      ;;
    dns-repair)
      dns_repair_impl
      ;;
    sync-clients)
      sync_clients_impl
      ;;
    *)
      die "Unknown command: ${COMMAND}. Expected install|uninstall|start|stop|get-protocol|status|health|dns-apply|dns-status|dns-repair|sync-clients"
      ;;
  esac
}

main "$@"
