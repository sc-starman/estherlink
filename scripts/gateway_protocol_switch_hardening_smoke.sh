#!/usr/bin/env bash
set -Eeuo pipefail
IFS=$'\n\t'

log(){ printf '[%s] %s\n' "$(date -u +'%Y-%m-%dT%H:%M:%SZ')" "$*"; }
die(){ printf '[%s] ERROR: %s\n' "$(date -u +'%Y-%m-%dT%H:%M:%SZ')" "$*" >&2; exit 1; }
run_root(){ (( EUID == 0 )) && "$@" || sudo "$@"; }
assert_eq(){ [[ "$1" == "$2" ]] || die "$3: expected '$1', got '$2'"; }

script_name_for_protocol(){
  case "$1" in
    vless_reality_singbox) echo "setup_omnirelay_vps_singbox_vless_reality.sh" ;;
    vless_plain_singbox) echo "setup_omnirelay_vps_singbox_vless_plain.sh" ;;
    shadowsocks_singbox) echo "setup_omnirelay_vps_singbox_shadowsocks.sh" ;;
    shadowtls_v3_shadowsocks_singbox) echo "setup_omnirelay_vps_singbox_shadowtls.sh" ;;
    openvpn_tcp_singbox) echo "setup_omnirelay_vps_openvpn_singbox.sh" ;;
    ipsec_l2tp_singbox) echo "setup_omnirelay_vps_ipsec_l2tp_singbox.sh" ;;
    *) die "Unsupported protocol id: $1" ;;
  esac
}

resolve_script_path(){
  local n="$1" c
  for c in "${SCRIPT_DIR}/${n}" "./src/OmniRelay.UI/scripts/${n}" "./scripts/${n}" "./${n}"; do
    [[ -f "$c" ]] && { echo "$c"; return 0; }
  done
  return 1
}

enforce_common_connector_purity(){
  local common
  common="$(resolve_script_path "setup_omnirelay_gateway_singbox_connector_common.sh")" || die "common connector script not found"
  if command -v rg >/dev/null 2>&1; then
    if rg -n -e 'VLESS|OPENVPN|IPSEC|SHADOW|--openvpn|--gateway-sni|omnirelay-openvpn|xl2tpd|strongswan' "$common" >/dev/null 2>&1; then
      die "common connector purity check failed: protocol-specific token found in ${common}"
    fi
    return 0
  fi
  if grep -En -- 'VLESS|OPENVPN|IPSEC|SHADOW|--openvpn|--gateway-sni|omnirelay-openvpn|xl2tpd|strongswan' "$common" >/dev/null 2>&1; then
    die "common connector purity check failed: protocol-specific token found in ${common}"
  fi
}

enforce_socks5_only_token_guard(){
  local -a targets=(
    "src/OmniRelay.Service/Runtime"
    "src/OmniRelay.Service/Workers"
    "src/OmniRelay.UI/Services/GatewayDeploymentService.cs"
    "src/OmniRelay.UI/scripts"
    "src/OmniRelay.Installer/payload/Release/ui/GatewayScripts"
    "README.md"
  )
  local pattern='h[t]{2}p-connect|HttpConnectProxyEngin[e]|Only HTTP CONNEC[T]|unsupported proxy protocol \(expected socks5 or h[t]{2}p-connect\)|--proxytunnel'

  if command -v rg >/dev/null 2>&1; then
    if rg -n -e "$pattern" "${targets[@]}" >/dev/null 2>&1; then
      rg -n -e "$pattern" "${targets[@]}" || true
      die "SOCKS5-only guard failed: forbidden legacy proxy token found."
    fi
    return 0
  fi

  if grep -ERn -- "$pattern" "${targets[@]}" >/dev/null 2>&1; then
    grep -ERn -- "$pattern" "${targets[@]}" || true
    die "SOCKS5-only guard failed: forbidden legacy proxy token found."
  fi
}

verify_status(){
  local protocol="$1" status
  status="$(run_root /usr/local/sbin/omnirelay-gatewayctl status --json)"
  assert_eq "$protocol" "$(jq -r '.activeProtocol' <<<"$status")" "activeProtocol"
  assert_eq "active" "$(jq -r '.singBoxState' <<<"$status")" "singBoxState"
  assert_eq "active" "$(jq -r '.omniPanelState' <<<"$status")" "omniPanelState"
  assert_eq "active" "$(jq -r '.nginxState' <<<"$status")" "nginxState"
}

install_protocol(){
  local protocol="$1" script_name script_path
  script_name="$(script_name_for_protocol "$protocol")"
  script_path="$(resolve_script_path "$script_name")" || die "script not found: $script_name"
  chmod +x "$script_path"

  if run_root test -x /usr/local/sbin/omnirelay-gatewayctl; then
    run_root /usr/local/sbin/omnirelay-gatewayctl uninstall \
      --public-port "$PUBLIC_PORT" --panel-port "$PANEL_PORT" --backend-port "$BACKEND_PORT" --ssh-port "$SSH_PORT" \
      --bootstrap-socks-port "$BOOTSTRAP_SOCKS_PORT" --dns-mode "$DNS_MODE" --doh-endpoints "$DOH_ENDPOINTS" --dns-udp-only "$DNS_UDP_ONLY" \
      --vps-ip "$VPS_IP" --tunnel-user "$TUNNEL_USER" --tunnel-auth "$TUNNEL_AUTH" || true
  fi

  local -a cmd=("$script_path" install
    --public-port "$PUBLIC_PORT" --panel-port "$PANEL_PORT" --backend-port "$BACKEND_PORT" --ssh-port "$SSH_PORT"
    --bootstrap-socks-port "$BOOTSTRAP_SOCKS_PORT" --dns-mode "$DNS_MODE" --doh-endpoints "$DOH_ENDPOINTS" --dns-udp-only "$DNS_UDP_ONLY"
    --vps-ip "$VPS_IP" --tunnel-user "$TUNNEL_USER" --tunnel-auth "$TUNNEL_AUTH")

  case "$protocol" in
    vless_reality_singbox) cmd+=(--gateway-sni "$VLESS_SNI" --gateway-target "$VLESS_TARGET") ;;
    shadowtls_v3_shadowsocks_singbox) cmd+=(--camouflage-server "$SHADOWTLS_CAMOUFLAGE") ;;
    openvpn_tcp_singbox) cmd+=(--openvpn-network "$OPENVPN_NETWORK" --openvpn-client-dns "$OPENVPN_CLIENT_DNS") ;;
  esac

  run_root "${cmd[@]}"
  assert_eq "$protocol" "$(run_root /usr/local/sbin/omnirelay-gatewayctl get-protocol | tr -d '\r\n' | xargs)" "get-protocol"
  verify_status "$protocol"
  log "Protocol verification passed: ${protocol}"
}

command -v jq >/dev/null 2>&1 || die "jq is required"
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

if (( $# > 0 )); then
  protocols=("$@")
else
  protocols=(
    "vless_reality_singbox"
    "vless_plain_singbox"
    "shadowsocks_singbox"
    "shadowtls_v3_shadowsocks_singbox"
    "openvpn_tcp_singbox"
    "ipsec_l2tp_singbox"
  )
fi

log "Protocol sequence: ${protocols[*]}"
enforce_common_connector_purity
enforce_socks5_only_token_guard
for protocol in "${protocols[@]}"; do
  install_protocol "$protocol"
done
log "Gateway protocol switch smoke test completed successfully."
