#!/usr/bin/env bash
set -Eeuo pipefail
IFS=$'\n\t'

log(){ printf '[%s] %s\n' "$(date -u +'%Y-%m-%dT%H:%M:%SZ')" "$*"; }
die(){ printf '[%s] ERROR: %s\n' "$(date -u +'%Y-%m-%dT%H:%M:%SZ')" "$*" >&2; exit 1; }
run_root(){ (( EUID == 0 )) && "$@" || sudo "$@"; }

SCRIPT_DIR="${SCRIPT_DIR:-$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)}"
REPO_ROOT="${REPO_ROOT:-$(cd "${SCRIPT_DIR}/.." && pwd)}"
SCRIPT_ROOT="${SCRIPT_ROOT:-${REPO_ROOT}/src/OmniRelay.UI/scripts}"

VPS_IP="${VPS_IP:-$(hostname -I 2>/dev/null | awk '{print $1}')}"
TUNNEL_USER="${TUNNEL_USER:-omnirelay}"
TUNNEL_AUTH="${TUNNEL_AUTH:-host_key}"
BOOTSTRAP_SOCKS_PORT="${BOOTSTRAP_SOCKS_PORT:-16080}"
DNS_MODE="${DNS_MODE:-hybrid}"
DOH_ENDPOINTS="${DOH_ENDPOINTS:-https://1.1.1.1/dns-query,https://8.8.8.8/dns-query}"
DNS_UDP_ONLY="${DNS_UDP_ONLY:-true}"
VLESS_SNI="${VLESS_SNI:-www.apple.com}"
VLESS_TARGET="${VLESS_TARGET:-www.apple.com:443}"

common_args(){
  local relay_id="$1" public_port="$2" panel_port="$3" backend_port="$4"
  printf '%s ' \
    --relay-id "$relay_id" \
    --public-port "$public_port" \
    --panel-port "$panel_port" \
    --backend-port "$backend_port" \
    --ssh-port "${SSH_PORT:-22}" \
    --bootstrap-socks-port "$BOOTSTRAP_SOCKS_PORT" \
    --dns-mode "$DNS_MODE" \
    --doh-endpoints "$DOH_ENDPOINTS" \
    --dns-udp-only "$DNS_UDP_ONLY" \
    --vps-ip "$VPS_IP" \
    --tunnel-user "$TUNNEL_USER" \
    --tunnel-auth "$TUNNEL_AUTH"
}

install_vless(){
  local script="${SCRIPT_ROOT}/setup_omnirelay_vps_singbox.sh"
  run_root bash "$script" install $(common_args relay_a 24443 24054 25000) --gateway-sni "$VLESS_SNI" --gateway-target "$VLESS_TARGET"
}

install_shadowsocks(){
  local script="${SCRIPT_ROOT}/setup_omnirelay_vps_singbox.sh"
  run_root bash "$script" install $(common_args relay_b 25443 25054 25001)
}

install_openvpn_pair(){
  local script="${SCRIPT_ROOT}/setup_omnirelay_vps_openvpn_singbox.sh"
  run_root bash "$script" install $(common_args relay_ovpn_a 26443 26054 25002) --openvpn-network 10.29.0.0/24
  run_root bash "$script" install $(common_args relay_ovpn_b 27443 27054 25003) --openvpn-network 10.30.0.0/24
  systemctl is-active --quiet omnirelay-openvpn-relay_ovpn_a || die "relay_ovpn_a OpenVPN service inactive"
  systemctl is-active --quiet omnirelay-openvpn-relay_ovpn_b || die "relay_ovpn_b OpenVPN service inactive"
}

verify_singbox_pair(){
  systemctl is-active --quiet omnirelay-singbox-relay_a || die "relay_a sing-box service inactive"
  systemctl is-active --quiet omnirelay-singbox-relay_b || die "relay_b sing-box service inactive"
  ss -lnt "( sport = :24054 )" 2>/dev/null | awk 'NR>1 {found=1} END{exit(found?0:1)}' || die "relay_a panel port is not listening"
  ss -lnt "( sport = :25054 )" 2>/dev/null | awk 'NR>1 {found=1} END{exit(found?0:1)}' || die "relay_b panel port is not listening"
  run_root /usr/local/sbin/omnirelay-gatewayctl-relay_a uninstall $(common_args relay_a 24443 24054 25000) || true
  systemctl is-active --quiet omnirelay-singbox-relay_b || die "relay_b stopped after relay_a uninstall"
}

verify_ipsec_guard(){
  local script="${SCRIPT_ROOT}/setup_omnirelay_vps_ipsec_l2tp_singbox.sh"
  run_root bash "$script" install $(common_args relay_ipsec_a 1701 28054 25004)
  if run_root bash "$script" install $(common_args relay_ipsec_b 1701 29054 25005); then
    die "second IPSec/L2TP relay install unexpectedly succeeded"
  fi
}

case "${1:-all}" in
  singbox) install_vless; install_shadowsocks; verify_singbox_pair ;;
  openvpn) install_openvpn_pair ;;
  ipsec-guard) verify_ipsec_guard ;;
  all) install_vless; install_shadowsocks; verify_singbox_pair; install_openvpn_pair; verify_ipsec_guard ;;
  *) die "usage: $0 [all|singbox|openvpn|ipsec-guard]" ;;
esac

log "Per-relay gateway smoke completed."
