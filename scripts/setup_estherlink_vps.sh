#!/usr/bin/env bash
set -Eeuo pipefail
IFS=$'\n\t'
umask 027

SCRIPT_NAME="$(basename "$0")"

PUBLIC_PORT=443
BACKEND_PORT=15000
SSH_PORT=22
TUNNEL_USER="estherlink"
VPS_IP=""
PUBKEY=""
PUBKEY_FILE=""

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
  --public-port <port>      Public ingress port for HAProxy (default: ${PUBLIC_PORT})
  --backend-port <port>     Loopback port on VPS for reverse tunnel (default: ${BACKEND_PORT})
  --ssh-port <port>         SSH port exposed on VPS/UFW (default: ${SSH_PORT})
  --vps-ip <ip-or-host>     VPS IP/hostname to print in final client command
  --pubkey '<ssh-pubkey>'   Add one SSH public key to ${TUNNEL_USER} authorized_keys
  --pubkey-file <path>      Read SSH public key from file and add it
  -h, --help                Show this help

Example:
  sudo ./${SCRIPT_NAME} --public-port 443 --backend-port 15000 --ssh-port 22 --pubkey-file /root/windows_tunnel.pub
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
    haproxy \
    openssh-server \
    fail2ban \
    ufw \
    ca-certificates
}

setup_tunnel_user() {
  log "Ensuring dedicated tunnel user '${TUNNEL_USER}' exists..."

  if ! id -u "$TUNNEL_USER" >/dev/null 2>&1; then
    useradd --create-home --home-dir "/home/${TUNNEL_USER}" --shell /usr/sbin/nologin "$TUNNEL_USER"
  fi

  passwd -l "$TUNNEL_USER" >/dev/null 2>&1 || true

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

  local sshd_dropin
  sshd_dropin="/etc/ssh/sshd_config.d/99-estherlink.conf"

  local content
  content="# Managed by ${SCRIPT_NAME}
AllowTcpForwarding yes
GatewayPorts clientspecified
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
    GatewayPorts clientspecified
    PermitTunnel no
    PermitListen 127.0.0.1:${BACKEND_PORT}
"

  write_file_if_changed "$sshd_dropin" 0644 root root "$content" || true

  sshd -t
  systemctl enable --now ssh
  systemctl restart ssh
}

configure_haproxy() {
  log "Configuring HAProxy TCP ingress on :${PUBLIC_PORT} -> 127.0.0.1:${BACKEND_PORT}..."

  local cfg
  cfg="/etc/haproxy/haproxy.cfg"

  local content
  content="# Managed by ${SCRIPT_NAME}
global
    log /dev/log local0
    log /dev/log local1 notice
    user haproxy
    group haproxy
    daemon
    maxconn 20000
    stats socket /run/haproxy/admin.sock mode 660 level admin expose-fd listeners

defaults
    log global
    mode tcp
    option tcplog
    option dontlognull
    option clitcpka
    option srvtcpka
    retries 3
    timeout connect 10s
    timeout client 1h
    timeout server 1h
    timeout tunnel 1h

frontend estherlink_ingress
    bind 0.0.0.0:${PUBLIC_PORT}
    default_backend windows_reverse_tunnel

backend windows_reverse_tunnel
    mode tcp
    option tcp-check
    server windows_proxy 127.0.0.1:${BACKEND_PORT} check inter 2s fall 3 rise 2
"

  write_file_if_changed "$cfg" 0644 root root "$content" || true

  haproxy -c -f "$cfg"
  systemctl enable --now haproxy
  systemctl restart haproxy
}

configure_fail2ban() {
  log "Configuring fail2ban (sshd jail)..."

  local jail_file
  jail_file="/etc/fail2ban/jail.d/estherlink-sshd.local"

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
  ufw allow "${PUBLIC_PORT}/tcp" comment "EstherLink ingress"

  ufw --force enable
}

detect_vps_ip() {
  ip -4 route get 1.1.1.1 2>/dev/null | awk '/src/ {for(i=1;i<=NF;i++) if($i=="src") {print $(i+1); exit}}'
}

print_summary() {
  local endpoint
  endpoint="${VPS_IP}"
  if [[ -z "$endpoint" ]]; then
    endpoint="$(detect_vps_ip || true)"
  fi
  if [[ -z "$endpoint" ]]; then
    endpoint="<VPS_IP_OR_HOSTNAME>"
  fi

  cat <<EOF

============================================================
EstherLink VPS ingress setup complete.

Services:
  - sshd:     $(systemctl is-active ssh || true)
  - haproxy:  $(systemctl is-active haproxy || true)
  - fail2ban: $(systemctl is-active fail2ban || true)

Firewall (UFW):
$(ufw status verbose || true)

Traffic Flow:
  Client -> VPS:${PUBLIC_PORT} -> HAProxy -> 127.0.0.1:${BACKEND_PORT} -> SSH reverse tunnel -> Windows 127.0.0.1:<WINDOWS_PROXY_PORT>

Windows reverse tunnel command (run on Windows):
  ssh -NT \
    -o ExitOnForwardFailure=yes \
    -o ServerAliveInterval=30 \
    -o ServerAliveCountMax=3 \
    -o TCPKeepAlive=yes \
    -R 127.0.0.1:${BACKEND_PORT}:127.0.0.1:<WINDOWS_PROXY_PORT> \
    ${TUNNEL_USER}@${endpoint} -p ${SSH_PORT}

Persistence suggestion on Windows:
  - Use NSSM wrapping the ssh command above
  - or use autossh for automatic restart

Useful logs/status:
  - HAProxy:   journalctl -u haproxy -f
  - SSH:       journalctl -u ssh -f
  - Fail2ban:  journalctl -u fail2ban -f
  - Bans:      fail2ban-client status sshd

Config files written:
  - /etc/haproxy/haproxy.cfg
  - /etc/ssh/sshd_config.d/99-estherlink.conf
  - /etc/fail2ban/jail.d/estherlink-sshd.local
  - /home/${TUNNEL_USER}/.ssh/authorized_keys
============================================================
EOF
}

parse_args() {
  while [[ $# -gt 0 ]]; do
    case "$1" in
      --public-port)
        PUBLIC_PORT="${2:-}"
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

main() {
  require_root
  parse_args "$@"

  validate_port "$PUBLIC_PORT" "--public-port"
  validate_port "$BACKEND_PORT" "--backend-port"
  validate_port "$SSH_PORT" "--ssh-port"

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
  configure_haproxy
  configure_fail2ban
  configure_firewall
  print_summary
}

main "$@"
