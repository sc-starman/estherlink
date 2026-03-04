#!/usr/bin/env bash
set -Eeuo pipefail
IFS=$'\n\t'
umask 027

SCRIPT_NAME="$(basename "$0")"

PUBLIC_PORT=443
PANEL_PORT=8443
BACKEND_PORT=15000
SSH_PORT=22
TUNNEL_USER="estherlink"

VPS_IP=""
PUBKEY=""
PUBKEY_FILE=""
XUI_VERSION=""

PANEL_USER=""
PANEL_PASSWORD=""
PANEL_BASE_PATH=""
PANEL_CERT_FILE=""
PANEL_KEY_FILE=""

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

usage() {
  cat <<EOF
Usage: sudo ./${SCRIPT_NAME} [options]

Options:
  --public-port <port>       Public client ingress port for 3x-ui/Xray inbound (default: ${PUBLIC_PORT})
  --panel-port <port>        3x-ui panel HTTPS port (default: ${PANEL_PORT})
  --backend-port <port>      Loopback port for Windows reverse tunnel endpoint (default: ${BACKEND_PORT})
  --ssh-port <port>          SSH port exposed on VPS/UFW (default: ${SSH_PORT})
  --vps-ip <ip-or-host>      VPS IP/hostname for printed access commands
  --pubkey '<ssh-pubkey>'    Add one SSH public key to ${TUNNEL_USER} authorized_keys
  --pubkey-file <path>       Read SSH public key from file and add it
  --xui-version <tag>        Pin 3x-ui version (example: v2.6.5). Default: latest
  --panel-user <name>        Panel username (default: generated)
  --panel-password <value>   Panel password (default: generated)
  --panel-base-path <path>   Panel base path (default: generated random path)
  --panel-cert-file <path>   Existing panel TLS cert path (PEM). Requires --panel-key-file
  --panel-key-file <path>    Existing panel TLS key path (PEM). Requires --panel-cert-file
  -h, --help                 Show this help

Example:
  sudo ./${SCRIPT_NAME} --public-port 443 --panel-port 8443 --backend-port 15000 \\
    --ssh-port 22 --pubkey-file /root/windows_tunnel.pub
EOF
}

validate_port() {
  local value="$1"
  local name="$2"
  [[ "$value" =~ ^[0-9]+$ ]] || die "${name} must be an integer."
  (( value >= 1 && value <= 65535 )) || die "${name} must be between 1 and 65535."
}

require_root() {
  (( EUID == 0 )) || die "Run as root (use sudo)."
}

random_string() {
  local len="$1"
  LC_ALL=C tr -dc 'a-zA-Z0-9' </dev/urandom | head -c "$len"
}

detect_arch() {
  case "$(uname -m)" in
    x86_64|amd64) echo "amd64" ;;
    aarch64|arm64) echo "arm64" ;;
    armv7l|armv7) echo "armv7" ;;
    armv6l|armv6) echo "armv6" ;;
    i386|i686) echo "386" ;;
    s390x) echo "s390x" ;;
    *) die "Unsupported architecture: $(uname -m)" ;;
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

install_packages() {
  log "Installing required packages..."
  export DEBIAN_FRONTEND=noninteractive
  apt-get update -y
  apt-get install -y --no-install-recommends \
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
  log "Ensuring dedicated tunnel user '${TUNNEL_USER}' exists..."

  if ! id -u "$TUNNEL_USER" >/dev/null 2>&1; then
    useradd --create-home --home-dir "/home/${TUNNEL_USER}" --shell /usr/sbin/nologin "$TUNNEL_USER"
  fi

  passwd -l "$TUNNEL_USER" >/dev/null 2>&1 || true
  usermod -s /usr/sbin/nologin "$TUNNEL_USER" || true

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
    else
      log "Provided SSH public key already present for ${TUNNEL_USER}."
    fi
  else
    warn "No --pubkey/--pubkey-file provided. Add a key to /home/${TUNNEL_USER}/.ssh/authorized_keys before connecting from Windows."
  fi
}

configure_sshd() {
  log "Configuring sshd for reverse tunnel support..."
  local sshd_dropin="/etc/ssh/sshd_config.d/99-estherlink.conf"
  local content
  content="# Managed by ${SCRIPT_NAME}
AllowTcpForwarding yes
GatewayPorts no
PermitTunnel no
ClientAliveInterval 30
ClientAliveCountMax 3

Match User ${TUNNEL_USER}
    PasswordAuthentication no
    KbdInteractiveAuthentication no
    AuthenticationMethods publickey
    PubkeyAuthentication yes
    PermitTTY no
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

configure_fail2ban() {
  log "Configuring fail2ban (sshd jail)..."
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
  log "Configuring UFW firewall..."
  ufw --force default deny incoming
  ufw --force default allow outgoing

  ufw allow "${SSH_PORT}/tcp" comment "SSH"
  ufw allow "${PUBLIC_PORT}/tcp" comment "OmniRelay client ingress (3x-ui/Xray)"
  ufw allow "${PANEL_PORT}/tcp" comment "3x-ui panel"

  ufw --force enable
}

disable_haproxy() {
  if systemctl list-unit-files | grep -q '^haproxy\.service'; then
    log "Disabling HAProxy to free ingress ownership for 3x-ui..."
    systemctl disable --now haproxy || true
  fi
}

fetch_latest_xui_version() {
  local latest
  latest="$(curl -fsSL "https://api.github.com/repos/MHSanaei/3x-ui/releases/latest" | jq -r '.tag_name // empty')"
  [[ -n "$latest" ]] || die "Failed to fetch latest 3x-ui version tag from GitHub API."
  printf '%s' "$latest"
}

install_3xui() {
  local arch version download_url tmp
  arch="$(detect_arch)"
  version="${XUI_VERSION}"
  if [[ -z "$version" ]]; then
    version="$(fetch_latest_xui_version)"
  fi

  log "Installing 3x-ui (${version}) for architecture ${arch}..."
  tmp="$(mktemp -d)"
  download_url="https://github.com/MHSanaei/3x-ui/releases/download/${version}/x-ui-linux-${arch}.tar.gz"

  curl -fL "$download_url" -o "${tmp}/x-ui.tar.gz"
  tar -xzf "${tmp}/x-ui.tar.gz" -C "$tmp"
  [[ -d "${tmp}/x-ui" ]] || die "Extracted archive does not contain x-ui directory."

  systemctl disable --now x-ui >/dev/null 2>&1 || true
  rm -rf /usr/local/x-ui
  mkdir -p /usr/local
  mv "${tmp}/x-ui" /usr/local/x-ui

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

  log "Configuring panel credentials and endpoint..."
  /usr/local/x-ui/x-ui setting \
    -username "$PANEL_USER" \
    -password "$PANEL_PASSWORD" \
    -port "$PANEL_PORT" \
    -webBasePath "$PANEL_BASE_PATH" \
    -listenIP "0.0.0.0" >/dev/null
}

generate_panel_self_signed_cert() {
  local endpoint cn san

  endpoint="${VPS_IP}"
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

  log "Generating self-signed TLS certificate for 3x-ui panel (${cn})..."
  openssl req -x509 -nodes -newkey rsa:3072 -sha256 -days 365 \
    -keyout "$PANEL_KEY_FILE" \
    -out "$PANEL_CERT_FILE" \
    -subj "/CN=${cn}" \
    -addext "subjectAltName=${san}" >/dev/null 2>&1

  chmod 0600 "$PANEL_KEY_FILE"
  chmod 0644 "$PANEL_CERT_FILE"
}

configure_panel_tls() {
  if [[ -n "$PANEL_CERT_FILE" || -n "$PANEL_KEY_FILE" ]]; then
    [[ -n "$PANEL_CERT_FILE" && -n "$PANEL_KEY_FILE" ]] || die "Both --panel-cert-file and --panel-key-file are required together."
    [[ -f "$PANEL_CERT_FILE" ]] || die "--panel-cert-file not found: ${PANEL_CERT_FILE}"
    [[ -f "$PANEL_KEY_FILE" ]] || die "--panel-key-file not found: ${PANEL_KEY_FILE}"
  else
    generate_panel_self_signed_cert
  fi

  log "Applying panel TLS certificate..."
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
      "tag": "direct",
      "protocol": "freedom",
      "settings": {
        "domainStrategy": "AsIs",
        "redirect": "",
        "noises": []
      }
    },
    {
      "tag": "blocked",
      "protocol": "blackhole",
      "settings": {}
    }
  ],
  "policy": {
    "levels": {
      "0": {
        "statsUserDownlink": true,
        "statsUserUplink": true
      }
    },
    "system": {
      "statsInboundDownlink": true,
      "statsInboundUplink": true,
      "statsOutboundDownlink": false,
      "statsOutboundUplink": false
    }
  },
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
        "outboundTag": "blocked",
        "protocol": [
          "bittorrent"
        ]
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
  "stats": {},
  "metrics": {
    "tag": "metrics_out",
    "listen": "127.0.0.1:11111"
  }
}
EOF
}

apply_fail_closed_xray_template() {
  local db_path="/etc/x-ui/x-ui.db"
  local template_path="/etc/x-ui/omnirelay-xray-template.json"

  log "Applying fail-closed Xray outbound template (all client traffic via 127.0.0.1:${BACKEND_PORT})..."
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

parse_args() {
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
      --ssh-port)
        SSH_PORT="${2:-}"
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
      --xui-version)
        XUI_VERSION="${2:-}"
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

print_summary() {
  local endpoint panel_url
  endpoint="${VPS_IP}"
  if [[ -z "$endpoint" ]]; then
    endpoint="$(detect_vps_ip || true)"
  fi
  if [[ -z "$endpoint" ]]; then
    endpoint="<VPS_IP_OR_HOSTNAME>"
  fi

  panel_url="https://${endpoint}:${PANEL_PORT}/${PANEL_BASE_PATH}/"

  cat <<EOF

============================================================
OmniRelay VPS 3x-ui ingress setup complete.

Services:
  - sshd:      $(systemctl is-active ssh || true)
  - x-ui:      $(systemctl is-active x-ui || true)
  - fail2ban:  $(systemctl is-active fail2ban || true)
  - haproxy:   $(systemctl is-active haproxy 2>/dev/null || echo "inactive/not-installed")

Firewall (UFW):
$(ufw status verbose || true)

Traffic model:
  Client -> VPS:${PUBLIC_PORT} (3x-ui/Xray inbound) -> outbound http://127.0.0.1:${BACKEND_PORT}
  -> SSH reverse tunnel -> Windows 127.0.0.1:<WINDOWS_PROXY_PORT>

Fail-closed behavior:
  - Xray default outbound is forced to 127.0.0.1:${BACKEND_PORT}
  - UDP is blocked by routing rule
  - If tunnel/proxy is down, client traffic fails (no direct VPS fallback)

3x-ui panel:
  URL:      ${panel_url}
  Username: ${PANEL_USER}
  Password: ${PANEL_PASSWORD}
  TLS cert: ${PANEL_CERT_FILE}
  TLS key:  ${PANEL_KEY_FILE}

Windows reverse tunnel command (run on Windows):
  ssh -NT \\
    -o ExitOnForwardFailure=yes \\
    -o ServerAliveInterval=30 \\
    -o ServerAliveCountMax=3 \\
    -o TCPKeepAlive=yes \\
    -R 127.0.0.1:${BACKEND_PORT}:127.0.0.1:<WINDOWS_PROXY_PORT> \\
    ${TUNNEL_USER}@${endpoint} -p ${SSH_PORT}

3x-ui post-install (panel):
  1) Create inbound: VLESS + TCP + REALITY
  2) Bind inbound to 0.0.0.0:${PUBLIC_PORT}
  3) Keep client profiles TCP-only (disable UDP for launch)
  4) Manage all client auth/profiles from 3x-ui panel

Validation commands:
  - sshd config:           sshd -t
  - x-ui status:           systemctl status x-ui --no-pager
  - x-ui logs:             journalctl -u x-ui -f
  - ssh logs:              journalctl -u ssh -f
  - fail2ban status:       fail2ban-client status sshd
  - listeners check:       ss -lnt | grep -E ':${SSH_PORT}\\b|:${PANEL_PORT}\\b|:${PUBLIC_PORT}\\b|:${BACKEND_PORT}\\b'
  - tunnel endpoint probe: timeout 2 bash -c 'cat < /dev/null > /dev/tcp/127.0.0.1/${BACKEND_PORT}' && echo OPEN || echo CLOSED

Security checks:
  - Reverse tunnel port is loopback-only by policy:
    PermitListen 127.0.0.1:${BACKEND_PORT}
  - No intended public listener on :${BACKEND_PORT}
  - SSH tunnel user has no shell and key-only auth

Rollback path:
  - Old HAProxy script remains available:
    scripts/setup_estherlink_vps.sh
============================================================
EOF
}

main() {
  require_root
  parse_args "$@"

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

  install_packages
  setup_tunnel_user
  configure_sshd
  disable_haproxy
  install_3xui
  configure_panel_credentials
  configure_panel_tls
  apply_fail_closed_xray_template
  configure_fail2ban
  configure_firewall
  print_summary
}

main "$@"

