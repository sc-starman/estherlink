#!/usr/bin/env bash
set -euo pipefail

TUNNELCTL_BIN="/usr/local/sbin/omnirelay-tunnelctl"
TUNNELCTL_CONFIG_DIR="/etc/omnirelay/tunnelctl"
TUNNELCTL_CONFIG_FILE="${TUNNELCTL_CONFIG_DIR}/env"

DEFAULT_BACKEND_HOST="127.0.0.1"
DEFAULT_BACKEND_PORT="15000"
DEFAULT_PROBE_URL="https://1.1.1.1/cdn-cgi/trace"
DEFAULT_TIMEOUT_SECONDS="12"

COMMAND="${1:-status}"
shift || true

BACKEND_HOST="${DEFAULT_BACKEND_HOST}"
BACKEND_PORT="${DEFAULT_BACKEND_PORT}"
PROBE_URL="${DEFAULT_PROBE_URL}"
PROBE_TIMEOUT_SECONDS="${DEFAULT_TIMEOUT_SECONDS}"
REMEDIATE_LEVEL="soft"
OUTPUT_JSON=false

usage() {
  cat <<'USAGE'
Usage:
  setup_omnirelay_gateway_tunnel_module.sh <command> [options]

Commands:
  install      Install /usr/local/sbin/omnirelay-tunnelctl and persist config
  uninstall    Remove omnirelay-tunnelctl and its config
  status       Show tunnel module status
  health       Alias of probe with healthy field
  probe        Run backend protocol + egress probe
  remediate    Run tunnel-path remediation only

Options:
  --backend-host <host>      Backend host (default: 127.0.0.1)
  --backend-port <port>      Backend port (default: 15000)
  --probe-url <url>          End-to-end probe URL
  --timeout <seconds>        Probe timeout seconds
  --level <soft|hard>        Remediation level for remediate
  --json                     JSON output for status/health/probe/remediate
USAGE
}

log() {
  printf '[%s] %s\n' "$(date -u +'%Y-%m-%dT%H:%M:%SZ')" "$*"
}

die() {
  log "ERROR: $*"
  exit 1
}

require_root() {
  (( EUID == 0 )) || die "This command requires root. Re-run with sudo."
}

validate_port() {
  local value="$1"
  [[ "$value" =~ ^[0-9]+$ ]] || die "--backend-port must be an integer."
  (( value >= 1 && value <= 65535 )) || die "--backend-port must be in range 1..65535."
}

json_escape() {
  local value="${1:-}"
  value="${value//\\/\\\\}"
  value="${value//\"/\\\"}"
  value="${value//$'\n'/\\n}"
  value="${value//$'\r'/}"
  printf '%s' "$value"
}

bool_json() {
  if [[ "$1" == "true" ]]; then
    printf 'true'
  else
    printf 'false'
  fi
}

load_config() {
  if [[ -f "$TUNNELCTL_CONFIG_FILE" ]]; then
    # shellcheck disable=SC1090
    source "$TUNNELCTL_CONFIG_FILE"
  fi

  BACKEND_HOST="${TUNNEL_BACKEND_HOST:-$BACKEND_HOST}"
  BACKEND_PORT="${TUNNEL_BACKEND_PORT:-$BACKEND_PORT}"
  PROBE_URL="${TUNNEL_PROBE_URL:-$PROBE_URL}"
  PROBE_TIMEOUT_SECONDS="${TUNNEL_PROBE_TIMEOUT_SECONDS:-$PROBE_TIMEOUT_SECONDS}"

  validate_port "$BACKEND_PORT"
  [[ "$PROBE_TIMEOUT_SECONDS" =~ ^[0-9]+$ ]] || PROBE_TIMEOUT_SECONDS="$DEFAULT_TIMEOUT_SECONDS"
  (( PROBE_TIMEOUT_SECONDS >= 3 && PROBE_TIMEOUT_SECONDS <= 120 )) || PROBE_TIMEOUT_SECONDS="$DEFAULT_TIMEOUT_SECONDS"
}

save_config() {
  mkdir -p "$TUNNELCTL_CONFIG_DIR"
  chmod 0755 "$TUNNELCTL_CONFIG_DIR"
  cat >"$TUNNELCTL_CONFIG_FILE" <<EOF
TUNNEL_BACKEND_HOST="${BACKEND_HOST}"
TUNNEL_BACKEND_PORT="${BACKEND_PORT}"
TUNNEL_PROBE_URL="${PROBE_URL}"
TUNNEL_PROBE_TIMEOUT_SECONDS="${PROBE_TIMEOUT_SECONDS}"
EOF
  chmod 0644 "$TUNNELCTL_CONFIG_FILE"
}

backend_listener_up() {
  ss -lnt "( sport = :${BACKEND_PORT} )" 2>/dev/null | awk 'NR>1 {print}' | grep -q .
}

backend_listener_holder_line() {
  ss -lntp "( sport = :${BACKEND_PORT} )" 2>/dev/null | awk 'NR>1 {print; exit}'
}

backend_listener_is_sshd() {
  local holder_line="${1:-}"
  [[ -n "$holder_line" ]] || return 1
  grep -q 'users:(("sshd"' <<<"$holder_line"
}

detect_backend_protocol() {
  if ! command -v python3 >/dev/null 2>&1; then
    echo "missing-python3"
    return 0
  fi

  python3 - "$BACKEND_HOST" "$BACKEND_PORT" <<'PY'
import socket
import sys

host = sys.argv[1]
port = int(sys.argv[2])

def probe_socks5():
    try:
        s = socket.create_connection((host, port), timeout=4)
        s.settimeout(4)
        s.sendall(b"\x05\x01\x00")
        data = s.recv(2)
        s.close()
        return len(data) >= 2 and data[0] == 0x05
    except Exception:
        return False

def probe_http_connect():
    try:
        s = socket.create_connection((host, port), timeout=4)
        s.settimeout(4)
        s.sendall(b"GET / HTTP/1.1\r\nHost: omnirelay-probe.local\r\n\r\n")
        data = s.recv(32)
        s.close()
        return data.startswith(b"HTTP/1.")
    except Exception:
        return False

# quick reachability test first
try:
    sock = socket.create_connection((host, port), timeout=3)
    sock.close()
except Exception:
    print("unreachable")
    sys.exit(0)

if probe_socks5():
    print("socks5")
elif probe_http_connect():
    print("http-connect")
else:
    print("unknown")
PY
}

probe_egress() {
  local protocol="$1"
  case "$protocol" in
    socks5)
      curl --silent --show-error --fail --max-time "$PROBE_TIMEOUT_SECONDS" --connect-timeout 6 --socks5-hostname "${BACKEND_HOST}:${BACKEND_PORT}" "$PROBE_URL" >/dev/null
      ;;
    http-connect)
      curl --silent --show-error --fail --max-time "$PROBE_TIMEOUT_SECONDS" --connect-timeout 6 --proxy "http://${BACKEND_HOST}:${BACKEND_PORT}" --proxytunnel "$PROBE_URL" >/dev/null
      ;;
    *)
      return 1
      ;;
  esac
}

run_probe() {
  local now protocol listener ok reason message egress_ok
  now="$(date -u +'%Y-%m-%dT%H:%M:%SZ')"
  listener=false
  if backend_listener_up; then
    listener=true
  fi

  protocol="$(detect_backend_protocol || echo unknown)"
  ok=false
  egress_ok=false
  reason=""
  message=""

  case "$protocol" in
    socks5|http-connect)
      if probe_egress "$protocol"; then
        ok=true
        egress_ok=true
        reason="ok"
        message="Tunnel path is healthy."
      else
        reason="egress_probe_failed"
        message="Backend responded but egress probe failed."
      fi
      ;;
    unreachable)
      reason="backend_unreachable"
      message="Backend endpoint is unreachable."
      ;;
    missing-python3)
      reason="dependency_missing_python3"
      message="python3 is required for backend protocol detection."
      ;;
    *)
      reason="backend_protocol_unknown"
      message="Backend endpoint responded with unknown protocol."
      ;;
  esac

  if [[ "$OUTPUT_JSON" == "true" ]]; then
    printf '{"ok":%s,"healthy":%s,"reasonCode":"%s","message":"%s","backendHost":"%s","backendPort":%s,"backendListener":%s,"backendProtocol":"%s","egressReachable":%s,"probeUrl":"%s","checkedAtUtc":"%s"}\n' \
      "$(bool_json "$ok")" \
      "$(bool_json "$ok")" \
      "$(json_escape "$reason")" \
      "$(json_escape "$message")" \
      "$(json_escape "$BACKEND_HOST")" \
      "$BACKEND_PORT" \
      "$(bool_json "$listener")" \
      "$(json_escape "$protocol")" \
      "$(bool_json "$egress_ok")" \
      "$(json_escape "$PROBE_URL")" \
      "$(json_escape "$now")"
  else
    printf 'ok=%s protocol=%s reason=%s message=%s\n' "$ok" "$protocol" "$reason" "$message"
  fi

  if [[ "$OUTPUT_JSON" == "true" ]]; then
    return 0
  fi

  [[ "$ok" == "true" ]]
}

status_cmd() {
  local now protocol listener
  now="$(date -u +'%Y-%m-%dT%H:%M:%SZ')"
  listener=false
  backend_listener_up && listener=true
  protocol="$(detect_backend_protocol || echo unknown)"

  if [[ "$OUTPUT_JSON" == "true" ]]; then
    printf '{"installed":true,"backendHost":"%s","backendPort":%s,"backendListener":%s,"backendProtocol":"%s","probeUrl":"%s","checkedAtUtc":"%s"}\n' \
      "$(json_escape "$BACKEND_HOST")" \
      "$BACKEND_PORT" \
      "$(bool_json "$listener")" \
      "$(json_escape "$protocol")" \
      "$(json_escape "$PROBE_URL")" \
      "$(json_escape "$now")"
  else
    printf 'installed=true backend=%s:%s listener=%s protocol=%s\n' "$BACKEND_HOST" "$BACKEND_PORT" "$listener" "$protocol"
  fi
}

remediate_cmd() {
  require_root

  local now action reason ok holder_line destructive_allowed
  now="$(date -u +'%Y-%m-%dT%H:%M:%SZ')"
  ok=true
  reason="ok"
  action="none"
  destructive_allowed=true
  holder_line="$(backend_listener_holder_line || true)"

  # Port 15000 is expected to be owned by reverse-tunnel sshd children.
  # Killing sshd here causes listener flapping and self-inflicted outages.
  if backend_listener_is_sshd "$holder_line"; then
    destructive_allowed=false
    reason="skipped_sshd_holder"
    action="sshd_holder_detected_no_kill"
  fi

  if [[ "$destructive_allowed" == "true" ]]; then
    if command -v fuser >/dev/null 2>&1; then
      if fuser -k "${BACKEND_PORT}/tcp" >/dev/null 2>&1; then
        action="killed_port_holder"
      else
        action="no_port_holder"
      fi
    else
      action="fuser_unavailable"
    fi
  fi

  if [[ "$REMEDIATE_LEVEL" == "hard" ]]; then
    # Hard mode also clears TIME_WAIT/ESTAB sessions tied to backend if supported.
    if [[ "$destructive_allowed" == "true" ]] && command -v ss >/dev/null 2>&1; then
      ss -K dport = "$BACKEND_PORT" >/dev/null 2>&1 || true
    elif [[ "$destructive_allowed" != "true" ]]; then
      action="${action},hard_socket_reset_skipped"
    fi
    if [[ "$destructive_allowed" == "true" ]]; then
      action="${action},hard_socket_reset"
    fi
  fi

  if [[ "$OUTPUT_JSON" == "true" ]]; then
    printf '{"ok":%s,"reasonCode":"%s","action":"%s","level":"%s","backendPort":%s,"runAtUtc":"%s"}\n' \
      "$(bool_json "$ok")" \
      "$(json_escape "$reason")" \
      "$(json_escape "$action")" \
      "$(json_escape "$REMEDIATE_LEVEL")" \
      "$BACKEND_PORT" \
      "$(json_escape "$now")"
  else
    printf 'ok=%s reason=%s action=%s level=%s\n' "$ok" "$reason" "$action" "$REMEDIATE_LEVEL"
  fi
}

install_cmd() {
  require_root
  validate_port "$BACKEND_PORT"
  save_config

  local self_path
  self_path="$(readlink -f "$0" 2>/dev/null || printf '%s' "$0")"
  if [[ "$self_path" != "$TUNNELCTL_BIN" ]]; then
    install -m 0755 "$self_path" "$TUNNELCTL_BIN"
  else
    chmod 0755 "$TUNNELCTL_BIN"
  fi
  sed -i 's/\r$//' "$TUNNELCTL_BIN" 2>/dev/null || true

  if [[ "$OUTPUT_JSON" == "true" ]]; then
    printf '{"ok":true,"installedPath":"%s","configPath":"%s"}\n' "$(json_escape "$TUNNELCTL_BIN")" "$(json_escape "$TUNNELCTL_CONFIG_FILE")"
  else
    log "Tunnel module installed at ${TUNNELCTL_BIN}"
  fi
}

uninstall_cmd() {
  require_root
  rm -f "$TUNNELCTL_BIN"
  rm -f "$TUNNELCTL_CONFIG_FILE"
  rmdir "$TUNNELCTL_CONFIG_DIR" 2>/dev/null || true

  if [[ "$OUTPUT_JSON" == "true" ]]; then
    printf '{"ok":true,"removed":true}\n'
  else
    log "Tunnel module removed."
  fi
}

parse_args() {
  while [[ $# -gt 0 ]]; do
    case "$1" in
      --backend-host)
        BACKEND_HOST="${2:-}"
        shift 2
        ;;
      --backend-port)
        BACKEND_PORT="${2:-}"
        shift 2
        ;;
      --probe-url)
        PROBE_URL="${2:-}"
        shift 2
        ;;
      --timeout)
        PROBE_TIMEOUT_SECONDS="${2:-}"
        shift 2
        ;;
      --level)
        REMEDIATE_LEVEL="${2:-soft}"
        shift 2
        ;;
      --json)
        OUTPUT_JSON=true
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

main() {
  parse_args "$@"

  if [[ "$COMMAND" != "install" ]]; then
    load_config
  fi

  case "$COMMAND" in
    install)
      install_cmd
      ;;
    uninstall)
      uninstall_cmd
      ;;
    status)
      status_cmd
      ;;
    health)
      run_probe
      ;;
    probe)
      run_probe
      ;;
    remediate)
      case "$REMEDIATE_LEVEL" in
        soft|hard) ;;
        *) die "--level must be soft or hard." ;;
      esac
      remediate_cmd
      ;;
    *)
      usage
      die "Unknown command: ${COMMAND}."
      ;;
  esac
}

main "$@"
