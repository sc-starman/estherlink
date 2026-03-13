#!/usr/bin/env bash
set -Eeuo pipefail
IFS=$'\n\t'

log() {
  printf '[%s] %s\n' "$(date -u +'%Y-%m-%dT%H:%M:%SZ')" "$*"
}

die() {
  printf '[%s] ERROR: %s\n' "$(date -u +'%Y-%m-%dT%H:%M:%SZ')" "$*" >&2
  exit 1
}

run_root() {
  if (( EUID == 0 )); then
    "$@"
  else
    sudo "$@"
  fi
}

gatewayctl_exists() {
  if (( EUID == 0 )); then
    [[ -x /usr/local/sbin/omnirelay-gatewayctl ]]
  else
    sudo test -x /usr/local/sbin/omnirelay-gatewayctl
  fi
}

gatewayctl_get_protocol() {
  run_root /usr/local/sbin/omnirelay-gatewayctl get-protocol 2>/dev/null | tr -d '\r\n' | xargs
}

gatewayctl_status_json() {
  run_root /usr/local/sbin/omnirelay-gatewayctl status --json
}

assert_eq() {
  local expected="$1"
  local actual="$2"
  local context="$3"
  [[ "$actual" == "$expected" ]] || die "${context}: expected '${expected}', got '${actual}'"
}

assert_status_field() {
  local status_json="$1"
  local field="$2"
  local expected="$3"
  local actual
  actual="$(jq -r "${field}" <<<"$status_json")"
  assert_eq "$expected" "$actual" "status field ${field}"
}

script_name_for_protocol() {
  case "$1" in
    vless_reality_3xui) echo "setup_omnirelay_vps_3xui_vless_reality.sh" ;;
    vless_plain_3xui) echo "setup_omnirelay_vps_3xui_vless_plain.sh" ;;
    shadowsocks_3xui) echo "setup_omnirelay_vps_3xui_shadowsocks.sh" ;;
    shadowtls_v3_shadowsocks_singbox) echo "setup_omnirelay_vps_singbox_shadowtls.sh" ;;
    openvpn_tcp_relay) echo "setup_omnirelay_vps_openvpn.sh" ;;
    ipsec_l2tp_hwdsl2) echo "setup_omnirelay_vps_ipsec_l2tp.sh" ;;
    *) die "Unsupported protocol id: $1" ;;
  esac
}

resolve_script_path() {
  local script_name="$1"
  local candidates=(
    "${SCRIPT_DIR}/${script_name}"
    "./scripts/${script_name}"
    "./${script_name}"
  )
  local candidate
  for candidate in "${candidates[@]}"; do
    if [[ -f "$candidate" ]]; then
      echo "$candidate"
      return 0
    fi
  done
  return 1
}

verify_status_for_protocol() {
  local protocol="$1"
  local status_json="$2"
  assert_status_field "$status_json" '.activeProtocol' "$protocol"
  assert_status_field "$status_json" '.omniPanelState' "active"
  assert_status_field "$status_json" '.nginxState' "active"
  case "$protocol" in
    vless_reality_3xui|vless_plain_3xui|shadowsocks_3xui)
      assert_status_field "$status_json" '.xuiState' "active"
      assert_status_field "$status_json" '.singBoxState' "inactive"
      assert_status_field "$status_json" '.openVpnState' "inactive"
      ;;
    shadowtls_v3_shadowsocks_singbox)
      assert_status_field "$status_json" '.xuiState' "inactive"
      assert_status_field "$status_json" '.singBoxState' "active"
      assert_status_field "$status_json" '.openVpnState' "inactive"
      ;;
    openvpn_tcp_relay)
      assert_status_field "$status_json" '.xuiState' "inactive"
      assert_status_field "$status_json" '.singBoxState' "inactive"
      assert_status_field "$status_json" '.openVpnState' "active"
      ;;
    ipsec_l2tp_hwdsl2)
      assert_status_field "$status_json" '.xuiState' "inactive"
      assert_status_field "$status_json" '.singBoxState' "inactive"
      assert_status_field "$status_json" '.openVpnState' "inactive"
      assert_status_field "$status_json" '.ipsecState' "active"
      assert_status_field "$status_json" '.xl2tpdState' "active"
      ;;
  esac
}

install_protocol() {
  local protocol="$1"
  local script_name script_path current switched status_json installed_protocol
  script_name="$(script_name_for_protocol "$protocol")"
  script_path="$(resolve_script_path "$script_name")" || die "Script not found for ${protocol}: ${script_name}"
  chmod +x "$script_path"

  switched=0
  current=""
  if gatewayctl_exists; then
    current="$(gatewayctl_get_protocol || true)"
    if [[ -n "$current" && "$current" != "$protocol" ]]; then
      switched=1
      log "Protocol switch required: ${current} -> ${protocol}. Running strict uninstall first."
      run_root /usr/local/sbin/omnirelay-gatewayctl uninstall \
        --public-port "$PUBLIC_PORT" \
        --panel-port "$PANEL_PORT" \
        --backend-port "$BACKEND_PORT" \
        --ssh-port "$SSH_PORT" \
        --bootstrap-socks-port "$BOOTSTRAP_SOCKS_PORT" \
        --dns-mode "$DNS_MODE" \
        --doh-endpoints "$DOH_ENDPOINTS" \
        --dns-udp-only "$DNS_UDP_ONLY" \
        --vps-ip "$VPS_IP" \
        --tunnel-user "$TUNNEL_USER" \
        --tunnel-auth "$TUNNEL_AUTH"
      log "Strict uninstall completed."
    else
      log "No strict uninstall needed before ${protocol} install (current='${current:-none}')."
    fi
  else
    log "No existing gatewayctl detected. Installing ${protocol} directly."
  fi

  log "Installing protocol ${protocol} using ${script_path}."
  local -a cmd=(
    "$script_path" install
    --public-port "$PUBLIC_PORT"
    --panel-port "$PANEL_PORT"
    --backend-port "$BACKEND_PORT"
    --ssh-port "$SSH_PORT"
    --bootstrap-socks-port "$BOOTSTRAP_SOCKS_PORT"
    --dns-mode "$DNS_MODE"
    --doh-endpoints "$DOH_ENDPOINTS"
    --dns-udp-only "$DNS_UDP_ONLY"
    --vps-ip "$VPS_IP"
    --tunnel-user "$TUNNEL_USER"
    --tunnel-auth "$TUNNEL_AUTH"
  )
  case "$protocol" in
    vless_reality_3xui)
      cmd+=(--gateway-sni "$VLESS_SNI" --gateway-target "$VLESS_TARGET")
      ;;
    shadowtls_v3_shadowsocks_singbox)
      cmd+=(--camouflage-server "$SHADOWTLS_CAMOUFLAGE")
      ;;
    openvpn_tcp_relay)
      cmd+=(--openvpn-network "$OPENVPN_NETWORK" --openvpn-client-dns "$OPENVPN_CLIENT_DNS")
      ;;
  esac
  run_root "${cmd[@]}"

  gatewayctl_exists || die "gatewayctl missing after ${protocol} install."
  installed_protocol="$(gatewayctl_get_protocol || true)"
  assert_eq "$protocol" "$installed_protocol" "get-protocol after install"

  status_json="$(gatewayctl_status_json)"
  verify_status_for_protocol "$protocol" "$status_json"

  if (( switched == 1 )); then
    log "Switch verification passed for ${protocol}: uninstall happened before install."
  fi
  log "Post-install status verification passed for ${protocol}."
}

if ! command -v jq >/dev/null 2>&1; then
  die "jq is required for this smoke test."
fi

SCRIPT_DIR="${SCRIPT_DIR:-/tmp}"
PUBLIC_PORT="${PUBLIC_PORT:-443}"
PANEL_PORT="${PANEL_PORT:-4066}"
BACKEND_PORT="${BACKEND_PORT:-15000}"
SSH_PORT="${SSH_PORT:-22}"
BOOTSTRAP_SOCKS_PORT="${BOOTSTRAP_SOCKS_PORT:-16080}"
DNS_MODE="${DNS_MODE:-hybrid}"
DOH_ENDPOINTS="${DOH_ENDPOINTS:-https://1.1.1.1/dns-query,https://8.8.8.8/dns-query}"
DNS_UDP_ONLY="${DNS_UDP_ONLY:-true}"
TUNNEL_USER="${TUNNEL_USER:-omnirelay}"
TUNNEL_AUTH="${TUNNEL_AUTH:-host_key}"
VPS_IP="${VPS_IP:-$(hostname -I 2>/dev/null | awk '{print $1}')}"
VLESS_SNI="${VLESS_SNI:-www.apple.com}"
VLESS_TARGET="${VLESS_TARGET:-www.apple.com:443}"
SHADOWTLS_CAMOUFLAGE="${SHADOWTLS_CAMOUFLAGE:-www.apple.com:443}"
OPENVPN_NETWORK="${OPENVPN_NETWORK:-10.29.0.0/24}"
OPENVPN_CLIENT_DNS="${OPENVPN_CLIENT_DNS:-1.1.1.1,8.8.8.8}"

[[ -n "${VPS_IP:-}" ]] || die "Unable to determine VPS_IP. Set VPS_IP explicitly."

declare -a protocols=()
if (( $# > 0 )); then
  protocols=("$@")
elif [[ -n "${PROTOCOL_SEQUENCE:-}" ]]; then
  # shellcheck disable=SC2206
  protocols=(${PROTOCOL_SEQUENCE})
else
  protocols=(
    "vless_reality_3xui"
    "vless_reality_3xui"
    "shadowtls_v3_shadowsocks_singbox"
    "vless_plain_3xui"
  )
fi

log "Starting gateway protocol switch hardening smoke test."
log "Protocol sequence: ${protocols[*]}"
for protocol in "${protocols[@]}"; do
  install_protocol "$protocol"
done

log "Smoke test completed successfully."
