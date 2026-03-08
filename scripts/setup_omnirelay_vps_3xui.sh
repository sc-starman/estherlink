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
TUNNEL_USER="estherlink"
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
  status       Show gateway service status
  health       Run gateway operational checks
  dns-apply    Apply OmniRelay DNS-through-tunnel profile
  dns-status   Check DNS profile presence/readiness
  dns-repair   Restore/repair DNS profile

Options:
  --public-port <port>          Public client ingress port for 3x-ui/Xray inbound (default: ${PUBLIC_PORT})
  --panel-port <port>           3x-ui panel HTTP port (default: ${PANEL_PORT})
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
  --panel-base-path <path>      Panel base path (default: generated random path)
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

verify_bootstrap_socks() {
  progress 8 "Checking bootstrap SOCKS endpoint"
  local retries=24
  local wait_sec=5
  local listener_ok=0
  local curl_ok=0

  for ((i=1; i<=retries; i++)); do
    if ss -lnt "( sport = :${BOOTSTRAP_SOCKS_PORT} )" 2>/dev/null | awk 'NR>1 {print $0}' | grep -q .; then
      listener_ok=1
    else
      listener_ok=0
    fi

    if (( listener_ok == 1 )); then
      configure_online_proxy_env
      if curl --fail --silent --show-error --max-time 20 --socks5-hostname "127.0.0.1:${BOOTSTRAP_SOCKS_PORT}" "$PROXY_CHECK_URL" >/dev/null; then
        curl_ok=1
        break
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
    sqlite3
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
  # We disable that step and manage panel settings ourselves (HTTP port/base path/user/password).
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

configure_panel_credentials() {
  progress 68 "Configuring 3x-ui panel credentials"
  local canonical_base_path panel_probe_code

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
  canonical_base_path="${PANEL_BASE_PATH}"

  /usr/local/x-ui/x-ui setting \
    -username "$PANEL_USER" \
    -password "$PANEL_PASSWORD" \
    -port "$PANEL_PORT" \
    -webBasePath "$canonical_base_path" \
    -listenIP "0.0.0.0" >/dev/null

  # Best-effort local probe so operators can distinguish path mismatch from network/firewall issues.
  panel_probe_code="$(curl --silent --output /dev/null --write-out '%{http_code}' "http://127.0.0.1:${PANEL_PORT}${canonical_base_path}" || true)"
  if [[ "$panel_probe_code" == "404" || "$panel_probe_code" == "000" || -z "$panel_probe_code" ]]; then
    log "WARNING: Panel local probe returned HTTP ${panel_probe_code} at http://127.0.0.1:${PANEL_PORT}${canonical_base_path}"
  fi
}

disable_panel_tls() {
  progress 75 "Disabling 3x-ui panel TLS (HTTP mode)"

  local db_path="/etc/x-ui/x-ui.db"
  if [[ -f "$db_path" ]]; then
    python3 - "$db_path" <<'PY'
import sqlite3
import sys

db_path = sys.argv[1]
conn = sqlite3.connect(db_path)
cur = conn.cursor()
cur.execute("SELECT name FROM sqlite_master WHERE type='table' AND name='settings';")
if cur.fetchone() is not None:
    for key in ("webCertFile", "webKeyFile"):
        cur.execute("SELECT id FROM settings WHERE key = ?", (key,))
        row = cur.fetchone()
        if row:
            cur.execute("UPDATE settings SET value = '' WHERE id = ?", (row[0],))
conn.commit()
conn.close()
PY
  fi

  systemctl restart x-ui
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

status_impl() {
  ensure_ssh_service_resolved
  local ssh_state xui_state fail2ban_state tunnel_listener ingress_listener panel_listener socks_listener dns_json
  local dns_config_present dns_rule_active doh_reachable udp53_ready dns_path_healthy dns_mode dns_udp_only doh_endpoints
  ssh_state="$(systemctl is-active "$SSH_SERVICE" 2>/dev/null || echo inactive)"
  xui_state="$(systemctl is-active x-ui 2>/dev/null || echo inactive)"
  fail2ban_state="disabled"
  tunnel_listener="$(check_listener "$BACKEND_PORT")"
  ingress_listener="$(check_listener "$PUBLIC_PORT")"
  panel_listener="$(check_listener "$PANEL_PORT")"
  socks_listener="$(check_listener "$BOOTSTRAP_SOCKS_PORT")"
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
    printf '{"sshState":"%s","xuiState":"%s","fail2banState":"%s","backendPort":%s,"publicPort":%s,"panelPort":%s,"bootstrapSocksPort":%s,"backendListener":%s,"publicListener":%s,"panelListener":%s,"bootstrapSocksListener":%s,"dnsConfigPresent":%s,"dnsRuleActive":%s,"dohReachableViaTunnel":%s,"udp53PathReady":%s,"dnsPathHealthy":%s,"dnsMode":"%s","dnsUdpOnly":%s,"dohEndpoints":"%s"}\n' \
      "$(json_escape "$ssh_state")" \
      "$(json_escape "$xui_state")" \
      "$(json_escape "$fail2ban_state")" \
      "$BACKEND_PORT" \
      "$PUBLIC_PORT" \
      "$PANEL_PORT" \
      "$BOOTSTRAP_SOCKS_PORT" \
      "$tunnel_listener" \
      "$ingress_listener" \
      "$panel_listener" \
      "$socks_listener" \
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
  fail2ban:  ${fail2ban_state}
  backend listener 127.0.0.1:${BACKEND_PORT}: ${tunnel_listener}
  public listener 0.0.0.0:${PUBLIC_PORT}: ${ingress_listener}
  panel listener 0.0.0.0:${PANEL_PORT}: ${panel_listener}
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
  local ssh_state xui_state fail2ban_state tunnel_listener ingress_listener panel_listener socks_listener healthy dns_json
  local dns_config_present dns_rule_active doh_reachable udp53_ready dns_path_healthy dns_mode dns_udp_only doh_endpoints dns_last_error

  ssh_state="$(systemctl is-active "$SSH_SERVICE" 2>/dev/null || echo inactive)"
  xui_state="$(systemctl is-active x-ui 2>/dev/null || echo inactive)"
  fail2ban_state="disabled"

  progress 45 "Checking expected listeners"
  tunnel_listener="$(check_listener "$BACKEND_PORT")"
  ingress_listener="$(check_listener "$PUBLIC_PORT")"
  panel_listener="$(check_listener "$PANEL_PORT")"
  socks_listener="$(check_listener "$BOOTSTRAP_SOCKS_PORT")"
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
  [[ "$panel_listener" == "true" ]] || healthy=false
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
    printf '{"healthy":%s,"sshState":"%s","xuiState":"%s","fail2banState":"%s","backendPort":%s,"publicPort":%s,"panelPort":%s,"bootstrapSocksPort":%s,"backendListener":%s,"publicListener":%s,"panelListener":%s,"bootstrapSocksListener":%s,"dnsConfigPresent":%s,"dnsRuleActive":%s,"dohReachableViaTunnel":%s,"udp53PathReady":%s,"dnsPathHealthy":%s,"dnsMode":"%s","dnsUdpOnly":%s,"dohEndpoints":"%s","dnsLastError":"%s"}\n' \
      "$healthy" \
      "$(json_escape "$ssh_state")" \
      "$(json_escape "$xui_state")" \
      "$(json_escape "$fail2ban_state")" \
      "$BACKEND_PORT" \
      "$PUBLIC_PORT" \
      "$PANEL_PORT" \
      "$BOOTSTRAP_SOCKS_PORT" \
      "$tunnel_listener" \
      "$ingress_listener" \
      "$panel_listener" \
      "$socks_listener" \
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
  fail2ban:  ${fail2ban_state}
  backend listener 127.0.0.1:${BACKEND_PORT}: ${tunnel_listener}
  public listener 0.0.0.0:${PUBLIC_PORT}: ${ingress_listener}
  panel listener 0.0.0.0:${PANEL_PORT}: ${panel_listener}
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
  if [[ -n "$SSH_SERVICE" ]]; then
    systemctl restart "$SSH_SERVICE" || true
  fi

  progress 100 "Gateway uninstall completed"
}

print_summary() {
  ensure_ssh_service_resolved
  local endpoint panel_url
  endpoint="$VPS_IP"
  if [[ -z "$endpoint" ]]; then
    endpoint="$(detect_vps_ip || true)"
  fi
  if [[ -z "$endpoint" ]]; then
    endpoint="<VPS_IP_OR_HOSTNAME>"
  fi

  panel_url="http://${endpoint}:${PANEL_PORT}/${PANEL_BASE_PATH}/"

  cat <<EOF

============================================================
OmniRelay VPS 3x-ui online install complete.

Services:
  - sshd:      $(systemctl is-active "$SSH_SERVICE" || true)
  - x-ui:      $(systemctl is-active x-ui || true)
  - fail2ban:  disabled

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
  require_existing_sshd

  progress 3 "Validating platform"
  setup_tunnel_user
  configure_sshd
  progress 8 "Bootstrap SOCKS pre-check skipped; using live online install checks"
  install_packages_online
  disable_haproxy
  install_3xui_online
  configure_panel_credentials
  disable_panel_tls
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
    *)
      die "Unknown command: ${COMMAND}. Expected install|uninstall|start|stop|status|health|dns-apply|dns-status|dns-repair"
      ;;
  esac
}

main "$@"
