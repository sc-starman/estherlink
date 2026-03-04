#!/usr/bin/env bash
set -Eeuo pipefail
IFS=$'\n\t'
umask 027

SCRIPT_NAME="$(basename "$0")"

COMMAND="install"
BUNDLE_DIR=""
PUBLIC_PORT=443
PANEL_PORT=8443
BACKEND_PORT=15000
SSH_PORT=22
TUNNEL_USER="estherlink"
TUNNEL_AUTH_METHOD="both"
VPS_IP=""
PUBKEY=""
PUBKEY_FILE=""
PANEL_USER=""
PANEL_PASSWORD=""
PANEL_BASE_PATH=""
PANEL_CERT_FILE=""
PANEL_KEY_FILE=""
STATUS_JSON=0
HEALTH_JSON=0

log() {
  printf '[%s] %s\n' "$(date -u +'%Y-%m-%dT%H:%M:%SZ')" "$*"
}

warn() {
  printf '[%s] WARNING: %s\n' "$(date -u +'%Y-%m-%dT%H:%M:%SZ')" "$*" >&2
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
  install      Install/configure offline 3x-ui gateway components
  uninstall    Remove x-ui gateway components
  start        Start gateway services
  stop         Stop gateway services
  status       Show gateway service status
  health       Run gateway operational checks

Options:
  --bundle-dir <path>        Extracted offline bundle root (contains apt/, xui/, manifest.json)
  --public-port <port>       Public client ingress port for 3x-ui/Xray inbound (default: ${PUBLIC_PORT})
  --panel-port <port>        3x-ui panel HTTPS port (default: ${PANEL_PORT})
  --backend-port <port>      Loopback port for reverse tunnel endpoint (default: ${BACKEND_PORT})
  --ssh-port <port>          SSH port exposed on VPS/UFW (default: ${SSH_PORT})
  --tunnel-user <name>       SSH tunnel user (default: ${TUNNEL_USER})
  --tunnel-auth <method>     host_key | password | both (default: ${TUNNEL_AUTH_METHOD})
  --vps-ip <ip-or-host>      VPS IP/hostname for summary output
  --pubkey '<ssh-pubkey>'    Add one SSH public key to tunnel user authorized_keys
  --pubkey-file <path>       Read SSH public key from file and add it
  --panel-user <name>        Panel username (default: generated)
  --panel-password <value>   Panel password (default: generated)
  --panel-base-path <path>   Panel base path (default: generated random path)
  --panel-cert-file <path>   Existing panel TLS cert path (PEM). Requires --panel-key-file
  --panel-key-file <path>    Existing panel TLS key path (PEM). Requires --panel-cert-file
  --json                     For status/health commands output compact JSON
  -h, --help                 Show this help

Examples:
  sudo ./${SCRIPT_NAME} install --bundle-dir /tmp/omnirelay/bundle --public-port 443 --panel-port 8443
  sudo ./${SCRIPT_NAME} health --json --backend-port 15000
EOF
}

validate_port() {
  local value="$1"
  local name="$2"
  [[ "$value" =~ ^[0-9]+$ ]] || die "${name} must be an integer."
  (( value >= 1 && value <= 65535 )) || die "${name} must be between 1 and 65535."
}

require_root() {
  (( EUID == 0 )) || die "This command requires root. Re-run with sudo."
}

random_string() {
  local len="$1"
  LC_ALL=C tr -dc 'a-zA-Z0-9' </dev/urandom | head -c "$len"
}

detect_arch() {
  case "$(uname -m)" in
    x86_64|amd64) echo "amd64" ;;
    *) die "Unsupported architecture: $(uname -m). Offline bundle currently supports amd64 only." ;;
  esac
}

detect_codename() {
  local codename=""
  if [[ -f /etc/os-release ]]; then
    # shellcheck disable=SC1091
    source /etc/os-release
    codename="${VERSION_CODENAME:-}"
  fi

  case "$codename" in
    jammy|noble)
      echo "$codename"
      ;;
    *)
      die "Unsupported Ubuntu codename '${codename:-unknown}'. Supported: jammy, noble."
      ;;
  esac
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

json_escape() {
  local value="$1"
  value="${value//\\/\\\\}"
  value="${value//\"/\\\"}"
  value="${value//$'\n'/ }"
  value="${value//$'\r'/ }"
  printf '%s' "$value"
}

bundle_default_dir() {
  local script_dir
  script_dir="$(cd "$(dirname "$0")" && pwd)"
  printf '%s' "${script_dir}/bundle"
}

require_bundle() {
  if [[ -z "$BUNDLE_DIR" ]]; then
    BUNDLE_DIR="$(bundle_default_dir)"
  fi

  [[ -d "$BUNDLE_DIR" ]] || die "Bundle directory not found: $BUNDLE_DIR"
  [[ -f "$BUNDLE_DIR/manifest.json" ]] || die "Bundle manifest missing: $BUNDLE_DIR/manifest.json"
  [[ -d "$BUNDLE_DIR/xui" ]] || die "Bundle xui directory missing: $BUNDLE_DIR/xui"
  [[ -d "$BUNDLE_DIR/apt" ]] || die "Bundle apt directory missing: $BUNDLE_DIR/apt"
}

apt_get_local() {
  local apt_list="$1"
  shift
  apt-get \
    -o Dir::Etc::sourcelist="$apt_list" \
    -o Dir::Etc::sourceparts="-" \
    -o APT::Get::List-Cleanup="0" \
    "$@"
}

install_packages_offline() {
  local codename="$1"
  local apt_repo_dir="$BUNDLE_DIR/apt/$codename"
  local apt_list="/etc/apt/sources.list.d/omnirelay-offline.list"

  [[ -d "$apt_repo_dir" ]] || die "Offline apt repo for '${codename}' not found at ${apt_repo_dir}"
  [[ -f "$apt_repo_dir/Packages" || -f "$apt_repo_dir/Packages.gz" ]] || die "Offline apt repo metadata missing in ${apt_repo_dir}"

  progress 12 "Configuring local offline apt source"
  write_file_if_changed "$apt_list" 0644 root root "deb [trusted=yes] file:${apt_repo_dir} ./" || true

  progress 18 "Updating apt index from local bundle"
  apt_get_local "$apt_list" update

  progress 24 "Installing required packages from local bundle"
  DEBIAN_FRONTEND=noninteractive apt_get_local "$apt_list" install -y --no-install-recommends \
    openssh-server \
    fail2ban \
    ufw \
    curl \
    ca-certificates \
    tar \
    gzip \
    jq \
    openssl \
    python3
}

setup_tunnel_user() {
  progress 31 "Ensuring tunnel user exists and has shell access"

  if ! id -u "$TUNNEL_USER" >/dev/null 2>&1; then
    useradd --create-home --home-dir "/home/${TUNNEL_USER}" --shell /bin/bash "$TUNNEL_USER"
  fi

  usermod -s /bin/bash "$TUNNEL_USER" || true

  install -d -m 0700 -o "$TUNNEL_USER" -g "$TUNNEL_USER" "/home/${TUNNEL_USER}/.ssh"
  touch "/home/${TUNNEL_USER}/.ssh/authorized_keys"
  chown "$TUNNEL_USER:$TUNNEL_USER" "/home/${TUNNEL_USER}/.ssh/authorized_keys"
  chmod 0600 "/home/${TUNNEL_USER}/.ssh/authorized_keys"

  if [[ -n "$PUBKEY" ]]; then
    local restricted_key
    restricted_key="restrict,port-forwarding,permitlisten=\"127.0.0.1:${BACKEND_PORT}\" ${PUBKEY}"
    if ! grep -Fqx "$restricted_key" "/home/${TUNNEL_USER}/.ssh/authorized_keys"; then
      printf '%s\n' "$restricted_key" >> "/home/${TUNNEL_USER}/.ssh/authorized_keys"
      log "Added provided SSH public key for ${TUNNEL_USER}."
    fi
  fi

  if [[ "$TUNNEL_AUTH_METHOD" == "host_key" ]]; then
    passwd -l "$TUNNEL_USER" >/dev/null 2>&1 || true
  fi
}

configure_sshd() {
  progress 38 "Configuring sshd for reverse tunnel"

  local sshd_dropin="/etc/ssh/sshd_config.d/99-estherlink.conf"
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
    PermitListen 127.0.0.1:${BACKEND_PORT}
"

  write_file_if_changed "$sshd_dropin" 0644 root root "$content" || true

  sshd -t
  systemctl enable --now ssh
  systemctl restart ssh
}

install_3xui_offline() {
  progress 50 "Installing 3x-ui from offline bundle"

  local tar_path="$BUNDLE_DIR/xui/x-ui-linux-amd64.tar.gz"
  [[ -f "$tar_path" ]] || die "x-ui archive not found: ${tar_path}"

  local tmp
  tmp="$(mktemp -d)"
  tar -xzf "$tar_path" -C "$tmp"
  [[ -d "$tmp/x-ui" ]] || die "Extracted x-ui archive does not contain x-ui directory."

  systemctl disable --now x-ui >/dev/null 2>&1 || true
  rm -rf /usr/local/x-ui
  mkdir -p /usr/local
  mv "$tmp/x-ui" /usr/local/x-ui

  chmod +x /usr/local/x-ui/x-ui
  chmod +x /usr/local/x-ui/x-ui.sh
  find /usr/local/x-ui/bin -maxdepth 1 -type f -name 'xray-linux-*' -exec chmod +x {} \;

  install -m 0755 /usr/local/x-ui/x-ui.sh /usr/bin/x-ui

  local service_src="/usr/local/x-ui/x-ui.service.debian"
  if [[ ! -f "$service_src" ]]; then
    service_src="/usr/local/x-ui/x-ui.service"
  fi
  [[ -f "$service_src" ]] || die "No systemd service file found inside 3x-ui package."
  install -m 0644 "$service_src" /etc/systemd/system/x-ui.service

  mkdir -p /etc/x-ui
  chmod 0750 /etc/x-ui
  mkdir -p /var/log/x-ui

  systemctl daemon-reload
  systemctl enable --now x-ui
  systemctl restart x-ui

  rm -rf "$tmp"
}

detect_vps_ip() {
  ip -4 route get 1.1.1.1 2>/dev/null | awk '/src/ {for(i=1;i<=NF;i++) if($i=="src") {print $(i+1); exit}}'
}

configure_panel_credentials() {
  progress 62 "Configuring 3x-ui panel credentials"

  if [[ -z "$PANEL_USER" ]]; then
    PANEL_USER="omniadmin_$(random_string 6)"
  fi
  if [[ -z "$PANEL_PASSWORD" ]]; then
    PANEL_PASSWORD="$(openssl rand -base64 24 | tr -d '\n' | tr '/+' 'AB' | cut -c1-24)"
  fi
  if [[ -z "$PANEL_BASE_PATH" ]]; then
    PANEL_BASE_PATH="$(random_string 18)"
  fi

  PANEL_BASE_PATH="${PANEL_BASE_PATH#/}"
  PANEL_BASE_PATH="${PANEL_BASE_PATH%/}"
  [[ -n "$PANEL_BASE_PATH" ]] || die "Panel base path cannot be empty."

  /usr/local/x-ui/x-ui setting \
    -username "$PANEL_USER" \
    -password "$PANEL_PASSWORD" \
    -port "$PANEL_PORT" \
    -webBasePath "$PANEL_BASE_PATH" \
    -listenIP "0.0.0.0" >/dev/null
}

generate_panel_self_signed_cert() {
  local endpoint cn san

  endpoint="$VPS_IP"
  if [[ -z "$endpoint" ]]; then
    endpoint="$(detect_vps_ip || true)"
  fi
  [[ -n "$endpoint" ]] || endpoint="localhost"

  PANEL_CERT_FILE="/etc/x-ui/panel.crt"
  PANEL_KEY_FILE="/etc/x-ui/panel.key"

  cn="$endpoint"
  if [[ "$endpoint" =~ ^([0-9]{1,3}\.){3}[0-9]{1,3}$ ]]; then
    san="IP:${endpoint}"
  else
    san="DNS:${endpoint}"
  fi

  openssl req -x509 -nodes -newkey rsa:3072 -sha256 -days 365 \
    -keyout "$PANEL_KEY_FILE" \
    -out "$PANEL_CERT_FILE" \
    -subj "/CN=${cn}" \
    -addext "subjectAltName=${san}" >/dev/null 2>&1

  chmod 0600 "$PANEL_KEY_FILE"
  chmod 0644 "$PANEL_CERT_FILE"
}

configure_panel_tls() {
  progress 70 "Configuring 3x-ui panel TLS"

  if [[ -n "$PANEL_CERT_FILE" || -n "$PANEL_KEY_FILE" ]]; then
    [[ -n "$PANEL_CERT_FILE" && -n "$PANEL_KEY_FILE" ]] || die "Both --panel-cert-file and --panel-key-file are required together."
    [[ -f "$PANEL_CERT_FILE" ]] || die "--panel-cert-file not found: ${PANEL_CERT_FILE}"
    [[ -f "$PANEL_KEY_FILE" ]] || die "--panel-key-file not found: ${PANEL_KEY_FILE}"
  else
    generate_panel_self_signed_cert
  fi

  /usr/local/x-ui/x-ui cert -webCert "$PANEL_CERT_FILE" -webCertKey "$PANEL_KEY_FILE" >/dev/null
  systemctl restart x-ui
}

write_xray_template() {
  local target="$1"
  cat > "$target" <<EOF
{
  "log": {
    "access": "none",
    "dnsLog": false,
    "error": "",
    "loglevel": "warning",
    "maskAddress": ""
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
      "protocol": "tunnel",
      "settings": {
        "address": "127.0.0.1"
      }
    }
  ],
  "outbounds": [
    {
      "tag": "to_windows_tunnel_http",
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
      "tag": "blocked",
      "protocol": "blackhole",
      "settings": {}
    }
  ],
  "routing": {
    "domainStrategy": "AsIs",
    "rules": [
      {
        "type": "field",
        "inboundTag": [
          "api"
        ],
        "outboundTag": "api"
      },
      {
        "type": "field",
        "network": "udp",
        "outboundTag": "blocked"
      },
      {
        "type": "field",
        "outboundTag": "to_windows_tunnel_http"
      }
    ]
  },
  "stats": {}
}
EOF
}

apply_fail_closed_xray_template() {
  progress 78 "Applying fail-closed Xray template"

  local db_path="/etc/x-ui/x-ui.db"
  local template_path="/etc/x-ui/omnirelay-xray-template.json"

  write_xray_template "$template_path"

  for _ in {1..20}; do
    [[ -f "$db_path" ]] && break
    sleep 1
  done
  [[ -f "$db_path" ]] || die "3x-ui DB not found at ${db_path}."

  python3 - "$db_path" "$template_path" <<'PY'
import sqlite3
import sys
from pathlib import Path

db_path = sys.argv[1]
template_path = sys.argv[2]
value = Path(template_path).read_text(encoding="utf-8")

conn = sqlite3.connect(db_path)
cur = conn.cursor()
cur.execute("SELECT name FROM sqlite_master WHERE type='table' AND name='settings';")
if cur.fetchone() is None:
    raise SystemExit("settings table not found in 3x-ui database")

cur.execute("SELECT id FROM settings WHERE key = ?", ("xrayTemplateConfig",))
row = cur.fetchone()
if row:
    cur.execute("UPDATE settings SET value = ? WHERE id = ?", (value, row[0]))
else:
    cur.execute("INSERT INTO settings(key, value) VALUES(?, ?)", ("xrayTemplateConfig", value))

conn.commit()
conn.close()
PY

  systemctl restart x-ui
}

configure_fail2ban() {
  progress 84 "Configuring fail2ban"

  local jail_file="/etc/fail2ban/jail.d/estherlink-sshd.local"
  local content
  content="# Managed by ${SCRIPT_NAME}
[sshd]
enabled = true
port = ${SSH_PORT}
backend = systemd
maxretry = 5
findtime = 10m
bantime = 1h
bantime.increment = true
banaction = ufw
"

  write_file_if_changed "$jail_file" 0644 root root "$content" || true
  systemctl enable --now fail2ban
  systemctl restart fail2ban
}

configure_firewall() {
  progress 89 "Configuring firewall"

  ufw --force default deny incoming
  ufw --force default allow outgoing

  ufw allow "${SSH_PORT}/tcp" comment "SSH"
  ufw allow "${PUBLIC_PORT}/tcp" comment "OmniRelay client ingress"
  ufw allow "${PANEL_PORT}/tcp" comment "3x-ui panel"

  ufw --force enable
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

status_impl() {
  local ssh_state xui_state fail2ban_state tunnel_listener ingress_listener panel_listener
  ssh_state="$(systemctl is-active ssh 2>/dev/null || echo inactive)"
  xui_state="$(systemctl is-active x-ui 2>/dev/null || echo inactive)"
  fail2ban_state="$(systemctl is-active fail2ban 2>/dev/null || echo inactive)"
  tunnel_listener="$(check_listener "$BACKEND_PORT")"
  ingress_listener="$(check_listener "$PUBLIC_PORT")"
  panel_listener="$(check_listener "$PANEL_PORT")"

  if (( STATUS_JSON == 1 )); then
    printf '{"sshState":"%s","xuiState":"%s","fail2banState":"%s","backendPort":%s,"publicPort":%s,"panelPort":%s,"backendListener":%s,"publicListener":%s,"panelListener":%s}\n' \
      "$(json_escape "$ssh_state")" \
      "$(json_escape "$xui_state")" \
      "$(json_escape "$fail2ban_state")" \
      "$BACKEND_PORT" \
      "$PUBLIC_PORT" \
      "$PANEL_PORT" \
      "$tunnel_listener" \
      "$ingress_listener" \
      "$panel_listener"
    return
  fi

  cat <<EOF
Gateway status:
  sshd:      ${ssh_state}
  x-ui:      ${xui_state}
  fail2ban:  ${fail2ban_state}
  backend listener 127.0.0.1:${BACKEND_PORT}: ${tunnel_listener}
  public listener 0.0.0.0:${PUBLIC_PORT}: ${ingress_listener}
  panel listener 0.0.0.0:${PANEL_PORT}: ${panel_listener}
EOF
}

health_impl() {
  progress 5 "Running gateway health checks"
  local ssh_state xui_state fail2ban_state tunnel_listener ingress_listener panel_listener healthy

  ssh_state="$(systemctl is-active ssh 2>/dev/null || echo inactive)"
  xui_state="$(systemctl is-active x-ui 2>/dev/null || echo inactive)"
  fail2ban_state="$(systemctl is-active fail2ban 2>/dev/null || echo inactive)"

  progress 45 "Checking expected listeners"
  tunnel_listener="$(check_listener "$BACKEND_PORT")"
  ingress_listener="$(check_listener "$PUBLIC_PORT")"
  panel_listener="$(check_listener "$PANEL_PORT")"

  healthy=true
  [[ "$ssh_state" == "active" ]] || healthy=false
  [[ "$xui_state" == "active" ]] || healthy=false
  [[ "$fail2ban_state" == "active" ]] || healthy=false
  [[ "$ingress_listener" == "true" ]] || healthy=false
  [[ "$panel_listener" == "true" ]] || healthy=false

  progress 100 "Health checks completed"

  if (( HEALTH_JSON == 1 )); then
    printf '{"healthy":%s,"sshState":"%s","xuiState":"%s","fail2banState":"%s","backendPort":%s,"publicPort":%s,"panelPort":%s,"backendListener":%s,"publicListener":%s,"panelListener":%s}\n' \
      "$healthy" \
      "$(json_escape "$ssh_state")" \
      "$(json_escape "$xui_state")" \
      "$(json_escape "$fail2ban_state")" \
      "$BACKEND_PORT" \
      "$PUBLIC_PORT" \
      "$PANEL_PORT" \
      "$tunnel_listener" \
      "$ingress_listener" \
      "$panel_listener"
    return
  fi

  cat <<EOF
Gateway health: ${healthy}
  sshd:      ${ssh_state}
  x-ui:      ${xui_state}
  fail2ban:  ${fail2ban_state}
  backend listener 127.0.0.1:${BACKEND_PORT}: ${tunnel_listener}
  public listener 0.0.0.0:${PUBLIC_PORT}: ${ingress_listener}
  panel listener 0.0.0.0:${PANEL_PORT}: ${panel_listener}
EOF
}

start_impl() {
  require_root
  progress 12 "Starting sshd"
  systemctl start ssh
  progress 45 "Starting fail2ban"
  systemctl start fail2ban
  progress 75 "Starting x-ui"
  systemctl start x-ui
  progress 100 "Gateway services started"
}

stop_impl() {
  require_root
  progress 20 "Stopping x-ui"
  systemctl stop x-ui || true
  progress 100 "Gateway service stop completed"
}

uninstall_impl() {
  require_root
  progress 10 "Stopping x-ui"
  systemctl disable --now x-ui || true

  progress 40 "Removing x-ui files"
  rm -rf /usr/local/x-ui /etc/x-ui /var/log/x-ui
  rm -f /usr/bin/x-ui /etc/systemd/system/x-ui.service
  rm -f /usr/local/sbin/omnirelay-gatewayctl

  progress 70 "Cleaning managed sshd drop-in"
  rm -f /etc/ssh/sshd_config.d/99-estherlink.conf
  sshd -t || true
  systemctl restart ssh || true

  progress 100 "Gateway uninstall completed"
}

print_summary() {
  local endpoint panel_url
  endpoint="$VPS_IP"
  if [[ -z "$endpoint" ]]; then
    endpoint="$(detect_vps_ip || true)"
  fi
  if [[ -z "$endpoint" ]]; then
    endpoint="<VPS_IP_OR_HOSTNAME>"
  fi

  panel_url="https://${endpoint}:${PANEL_PORT}/${PANEL_BASE_PATH}/"

  cat <<EOF

============================================================
OmniRelay VPS 3x-ui offline install complete.

Services:
  - sshd:      $(systemctl is-active ssh || true)
  - x-ui:      $(systemctl is-active x-ui || true)
  - fail2ban:  $(systemctl is-active fail2ban || true)

Panel:
  URL:      ${panel_url}
  Username: ${PANEL_USER}
  Password: ${PANEL_PASSWORD}

Traffic model:
  Client -> VPS:${PUBLIC_PORT} -> x-ui/Xray -> http://127.0.0.1:${BACKEND_PORT}
  -> SSH reverse tunnel -> Windows proxy
============================================================
EOF
}

install_impl() {
  require_root
  require_bundle

  progress 3 "Validating platform"
  detect_arch >/dev/null
  local codename
  codename="$(detect_codename)"

  install_packages_offline "$codename"
  setup_tunnel_user
  configure_sshd
  disable_haproxy
  install_3xui_offline
  configure_panel_credentials
  configure_panel_tls
  apply_fail_closed_xray_template
  configure_fail2ban
  configure_firewall
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
      --bundle-dir)
        BUNDLE_DIR="${2:-}"
        shift 2
        ;;
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
      --panel-cert-file)
        PANEL_CERT_FILE="${2:-}"
        shift 2
        ;;
      --panel-key-file)
        PANEL_KEY_FILE="${2:-}"
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
  validate_port "$PUBLIC_PORT" "--public-port"
  validate_port "$PANEL_PORT" "--panel-port"
  validate_port "$BACKEND_PORT" "--backend-port"
  validate_port "$SSH_PORT" "--ssh-port"

  if [[ "$PUBLIC_PORT" == "$PANEL_PORT" ]]; then
    die "--public-port and --panel-port must be different."
  fi

  if [[ -n "$PUBKEY_FILE" ]]; then
    [[ -f "$PUBKEY_FILE" ]] || die "--pubkey-file not found: ${PUBKEY_FILE}"
    PUBKEY="$(tr -d '\r' < "$PUBKEY_FILE" | head -n1 | xargs)"
  fi

  if [[ -n "$PUBKEY" ]] && [[ ! "$PUBKEY" =~ ^ssh-(rsa|ed25519|ecdsa)\  ]]; then
    warn "Public key does not look like a standard SSH key. Continuing anyway."
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
    status)
      status_impl
      ;;
    health)
      health_impl
      ;;
    *)
      die "Unknown command: ${COMMAND}. Expected install|uninstall|start|stop|status|health"
      ;;
  esac
}

main "$@"
