# Gateway Protocol Switch Smoke Test (Sing-box Unified)

This smoke test validates end-to-end protocol switching on a VPS with the new sing-box connector architecture:
- strict uninstall-before-install switching
- `gatewayctl get-protocol` consistency
- status/health contract (`singBoxState`, tunnel probe fields)
- OmniPanel CRUD and sync hooks
- no `x-ui`/`redsocks` residue

## Supported protocol IDs
- `vless_reality_singbox`
- `vless_plain_singbox`
- `shadowsocks_singbox`
- `shadowtls_v3_shadowsocks_singbox`
- `openvpn_tcp_singbox`
- `ipsec_l2tp_singbox`

## 1) Preconditions
- VPS reachable over SSH.
- Bootstrap SOCKS on VPS loopback is available (default `127.0.0.1:16080`).
- Upload scripts to VPS:
  - `/tmp/setup_omnirelay_vps_singbox_vless_reality.sh`
  - `/tmp/setup_omnirelay_vps_singbox_vless_plain.sh`
  - `/tmp/setup_omnirelay_vps_singbox_shadowsocks.sh`
  - `/tmp/setup_omnirelay_vps_singbox_shadowtls.sh`
  - `/tmp/setup_omnirelay_vps_openvpn_singbox.sh`
  - `/tmp/setup_omnirelay_vps_ipsec_l2tp_singbox.sh`
  - `/tmp/setup_omnirelay_gateway_singbox_connector_common.sh`
  - `/tmp/setup_omnirelay_gateway_bootstrap_common.sh`
  - `/tmp/setup_omnirelay_gateway_tunnel_module.sh`
  - `/tmp/setup_omnirelay_omnipanel_common.sh`

Optional upload from local repo root:

```powershell
scp src/OmniRelay.UI/scripts/setup_omnirelay_vps_singbox_vless_reality.sh root@<VPS_IP>:/tmp/
scp src/OmniRelay.UI/scripts/setup_omnirelay_vps_singbox_vless_plain.sh root@<VPS_IP>:/tmp/
scp src/OmniRelay.UI/scripts/setup_omnirelay_vps_singbox_shadowsocks.sh root@<VPS_IP>:/tmp/
scp src/OmniRelay.UI/scripts/setup_omnirelay_vps_singbox_shadowtls.sh root@<VPS_IP>:/tmp/
scp src/OmniRelay.UI/scripts/setup_omnirelay_vps_openvpn_singbox.sh root@<VPS_IP>:/tmp/
scp src/OmniRelay.UI/scripts/setup_omnirelay_vps_ipsec_l2tp_singbox.sh root@<VPS_IP>:/tmp/
scp src/OmniRelay.UI/scripts/setup_omnirelay_gateway_singbox_connector_common.sh root@<VPS_IP>:/tmp/
scp src/OmniRelay.UI/scripts/setup_omnirelay_gateway_bootstrap_common.sh root@<VPS_IP>:/tmp/
scp src/OmniRelay.UI/scripts/setup_omnirelay_gateway_tunnel_module.sh root@<VPS_IP>:/tmp/
scp src/OmniRelay.UI/scripts/setup_omnirelay_omnipanel_common.sh root@<VPS_IP>:/tmp/
scp scripts/gateway_protocol_switch_hardening_smoke.sh root@<VPS_IP>:/tmp/
```

## 2) Automated sequence smoke

```bash
chmod +x /tmp/gateway_protocol_switch_hardening_smoke.sh
sudo /tmp/gateway_protocol_switch_hardening_smoke.sh \
  vless_reality_singbox \
  vless_plain_singbox \
  shadowsocks_singbox \
  shadowtls_v3_shadowsocks_singbox \
  openvpn_tcp_singbox \
  ipsec_l2tp_singbox \
  vless_reality_singbox
```

Expected:
- each switch uninstalls previous protocol first
- each install finishes with healthy status
- no script fallback to old protocol ids

## 3) Manual validation variables

```bash
export VPS_IP="<VPS_IP>"
export SSH_PORT=22
export TUNNEL_USER="omnirelay"
export PUBLIC_PORT=443
export PANEL_PORT=4066
export BACKEND_PORT=15000
export BOOTSTRAP_SOCKS_PORT=16080
export DNS_MODE="hybrid"
export DOH_ENDPOINTS="https://1.1.1.1/dns-query,https://8.8.8.8/dns-query"
export DNS_UDP_ONLY="true"
```

Sanity checks:

```bash
ss -lnt '( sport = :16080 )'
curl -fsS --socks5-hostname 127.0.0.1:16080 https://deb.debian.org/ >/dev/null && echo "SOCKS OK"
if [ -x /usr/local/sbin/omnirelay-gatewayctl ]; then
  sudo /usr/local/sbin/omnirelay-gatewayctl get-protocol || true
fi
```

## 4) Per-protocol install probes

Example for VLESS Reality:

```bash
chmod +x /tmp/setup_omnirelay_vps_singbox_vless_reality.sh
sudo /tmp/setup_omnirelay_vps_singbox_vless_reality.sh install \
  --public-port "$PUBLIC_PORT" \
  --panel-port "$PANEL_PORT" \
  --backend-port "$BACKEND_PORT" \
  --ssh-port "$SSH_PORT" \
  --tunnel-user "$TUNNEL_USER" \
  --tunnel-auth host_key \
  --bootstrap-socks-port "$BOOTSTRAP_SOCKS_PORT" \
  --dns-mode "$DNS_MODE" \
  --doh-endpoints "$DOH_ENDPOINTS" \
  --dns-udp-only "$DNS_UDP_ONLY" \
  --vps-ip "$VPS_IP" \
  --gateway-sni "www.apple.com" \
  --gateway-target "www.apple.com:443"
```

Run this after every install:

```bash
sudo /usr/local/sbin/omnirelay-gatewayctl get-protocol
sudo /usr/local/sbin/omnirelay-gatewayctl status --json | jq
sudo /usr/local/sbin/omnirelay-gatewayctl health --json | jq
```

Assertions for all protocols:
- `.singBoxState == "active"`
- `.tunnelHealthy` exists
- `.tunnelReason` exists
- `.tunnelBackendProtocol` exists
- `.tunnelEgressReachable` exists

Additional assertions:
- OpenVPN: `.activeProtocol == "openvpn_tcp_singbox"` and `.openVpnState == "active"`
- IPSec/L2TP: `.activeProtocol == "ipsec_l2tp_singbox"` and `.ipsecState == "active"` and `.xl2tpdState == "active"`
- VLESS/SS/ShadowTLS: protocol id matches expected `*_singbox` id

## 5) OmniPanel CRUD + policy smoke

```bash
panel_user="$(jq -r '.username' /opt/omnirelay/omni-gateway/panel-auth.json)"
panel_pass="$(jq -r '.password' /opt/omnirelay/omni-gateway/panel-auth.json)"
panel_port="$(jq -r '.omnipanel_public_port // 4066' /etc/omnirelay/gateway/metadata.json)"

curl -sk -c /tmp/omni.cookies \
  -H 'Content-Type: application/json' \
  -d "{\"username\":\"${panel_user}\",\"password\":\"${panel_pass}\"}" \
  "https://127.0.0.1:${panel_port}/api/auth/login"

add_json="$(curl -sk -b /tmp/omni.cookies \
  -H 'Content-Type: application/json' \
  -d '{"email":"smoke-client@local","limitIp":0,"totalGB":1,"expiryTime":0}' \
  "https://127.0.0.1:${panel_port}/api/client/add")"
echo "$add_json" | jq
cid="$(echo "$add_json" | jq -r '.client.id')"

curl -sk -b /tmp/omni.cookies \
  "https://127.0.0.1:${panel_port}/api/client/config?uuid=${cid}" | jq

curl -sk -b /tmp/omni.cookies \
  -H 'Content-Type: application/json' \
  -d "{\"uuid\":\"${cid}\",\"enable\":false}" \
  "https://127.0.0.1:${panel_port}/api/client/update" | jq

curl -sk -b /tmp/omni.cookies \
  -H 'Content-Type: application/json' \
  -d "{\"uuid\":\"${cid}\"}" \
  "https://127.0.0.1:${panel_port}/api/client/delete" | jq
```

Quota/expiry checks:
- create a low-traffic-cap user (`totalGB` small), generate traffic, verify auto-disable
- create near-expiry user (`expiryTime` close), verify disable after expiry
- restart sing-box and verify usage counters persist

## 6) Negative checks (must stay removed)

```bash
systemctl is-active x-ui || true
systemctl is-active omnirelay-redsocks || true
test -f /etc/systemd/system/x-ui.service && echo "unexpected x-ui service file"
test -f /etc/systemd/system/omnirelay-redsocks.service && echo "unexpected redsocks service file"
```

Expected: no active/service files for x-ui or redsocks.
