# Gateway Protocol Switch Smoke Test (VLESS Reality <-> VLESS Plain <-> 3x-ui SS <-> ShadowTLS <-> IPSec/L2TP)

This is a focused runtime checklist to validate:
- protocol switching (`vless_reality_3xui` -> `vless_plain_3xui` -> `shadowsocks_3xui` -> `shadowtls_v3_shadowsocks_singbox` -> `ipsec_l2tp_hwdsl2` -> `vless_reality_3xui`)
- gateway status/health JSON contract
- OmniPanel login + client CRUD for all protocol variants
- universal protocol discovery (`gatewayctl get-protocol`) before/after each switch

## 1) Preconditions
- VPS reachable over SSH.
- Bootstrap SOCKS is already running on VPS loopback (default `127.0.0.1:16080`).
- Both scripts are available on VPS (or uploaded before each install):
  - `/tmp/setup_omnirelay_vps_3xui_vless_reality.sh`
  - `/tmp/setup_omnirelay_vps_3xui_vless_plain.sh`
  - `/tmp/setup_omnirelay_vps_3xui_shadowsocks.sh`
  - `/tmp/setup_omnirelay_vps_singbox_shadowtls.sh`
  - `/tmp/setup_omnirelay_vps_ipsec_l2tp.sh`

Optional upload from local repo root:

```powershell
scp scripts/setup_omnirelay_vps_3xui_vless_reality.sh root@<VPS_IP>:/tmp/
scp scripts/setup_omnirelay_vps_3xui_vless_plain.sh root@<VPS_IP>:/tmp/
scp scripts/setup_omnirelay_vps_3xui_shadowsocks.sh root@<VPS_IP>:/tmp/
scp scripts/setup_omnirelay_vps_singbox_shadowtls.sh root@<VPS_IP>:/tmp/
scp scripts/setup_omnirelay_vps_ipsec_l2tp.sh root@<VPS_IP>:/tmp/
scp scripts/gateway_protocol_switch_hardening_smoke.sh root@<VPS_IP>:/tmp/
```

## 1.1) Automated Shortcut (Strict Switch Guard Smoke)

If you want a single command to verify:
- `gatewayctl get-protocol`
- strict uninstall-before-install on protocol change
- post-install service-state sanity per protocol

run this on VPS:

```bash
chmod +x /tmp/gateway_protocol_switch_hardening_smoke.sh
sudo /tmp/gateway_protocol_switch_hardening_smoke.sh \
  vless_reality_3xui \
  shadowtls_v3_shadowsocks_singbox \
  vless_plain_3xui
```

Notes:
- Default `SCRIPT_DIR` is `/tmp`; override with `SCRIPT_DIR=/path`.
- You can pass any sequence of supported protocol ids.
- If uninstall fails during a switch, the script exits immediately.

## 2) Common test variables (on VPS)

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

Sanity check bootstrap SOCKS:

```bash
ss -lnt '( sport = :16080 )'
curl -fsS --socks5-hostname 127.0.0.1:16080 https://deb.debian.org/ >/dev/null && echo "SOCKS OK"
```

Check current installed protocol (if any):

```bash
if [ -x /usr/local/sbin/omnirelay-gatewayctl ]; then
  sudo /usr/local/sbin/omnirelay-gatewayctl get-protocol || true
fi
```

## 3) Install VLESS protocol and validate

```bash
chmod +x /tmp/setup_omnirelay_vps_3xui_vless_reality.sh
sudo /tmp/setup_omnirelay_vps_3xui_vless_reality.sh install \
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

Check status/health contract:

```bash
sudo /usr/local/sbin/omnirelay-gatewayctl get-protocol
sudo /usr/local/sbin/omnirelay-gatewayctl status --json | jq
sudo /usr/local/sbin/omnirelay-gatewayctl health --json | jq
```

Expected:
- `.activeProtocol == "vless_reality_3xui"`
- `.xuiState == "active"`
- `.singBoxState == "inactive"`

## 4) OmniPanel API smoke (works for current active protocol)

```bash
panel_user="$(jq -r '.username' /opt/omnirelay/omni-gateway/panel-auth.json)"
panel_pass="$(jq -r '.password' /opt/omnirelay/omni-gateway/panel-auth.json)"
panel_port="$(jq -r '.omnipanel_public_port // 4066' /etc/omnirelay/gateway/metadata.json)"

curl -sk -c /tmp/omni.cookies \
  -H 'Content-Type: application/json' \
  -d "{\"username\":\"${panel_user}\",\"password\":\"${panel_pass}\"}" \
  "https://127.0.0.1:${panel_port}/api/auth/login"

curl -sk -b /tmp/omni.cookies "https://127.0.0.1:${panel_port}/api/inbound" | jq

add_json="$(curl -sk -b /tmp/omni.cookies \
  -H 'Content-Type: application/json' \
  -d '{"email":"smoke-vless@local"}' \
  "https://127.0.0.1:${panel_port}/api/client/add")"
echo "$add_json" | jq
cid="$(echo "$add_json" | jq -r '.client.id')"

curl -sk -b /tmp/omni.cookies \
  "https://127.0.0.1:${panel_port}/api/client/config?uuid=${cid}" | jq

curl -sk -b /tmp/omni.cookies \
  -H 'Content-Type: application/json' \
  -d "{\"uuid\":\"${cid}\"}" \
  "https://127.0.0.1:${panel_port}/api/client/delete" | jq
```

## 5) Switch to ShadowTLS + Shadowsocks and validate

```bash
chmod +x /tmp/setup_omnirelay_vps_singbox_shadowtls.sh
sudo /tmp/setup_omnirelay_vps_singbox_shadowtls.sh install \
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
  --camouflage-server "www.apple.com:443"
```

Check status/health:

```bash
sudo /usr/local/sbin/omnirelay-gatewayctl get-protocol
sudo /usr/local/sbin/omnirelay-gatewayctl status --json | jq
sudo /usr/local/sbin/omnirelay-gatewayctl health --json | jq
```

Expected:
- `.activeProtocol == "shadowtls_v3_shadowsocks_singbox"`
- `.singBoxState == "active"`
- `.xuiState == "inactive"`

Run OmniPanel CRUD smoke again (same block as section 4), e.g. with `smoke-shadowtls@local`.

## 6) Switch to 3x-ui VLESS plain (no TLS / no Reality) and validate

```bash
chmod +x /tmp/setup_omnirelay_vps_3xui_vless_plain.sh
sudo /tmp/setup_omnirelay_vps_3xui_vless_plain.sh install \
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
  --vps-ip "$VPS_IP"
```

Check status/health:

```bash
sudo /usr/local/sbin/omnirelay-gatewayctl get-protocol
sudo /usr/local/sbin/omnirelay-gatewayctl status --json | jq
sudo /usr/local/sbin/omnirelay-gatewayctl health --json | jq
```

Expected:
- `.activeProtocol == "vless_plain_3xui"`
- `.xuiState == "active"`
- `.singBoxState == "inactive"`

Run OmniPanel CRUD smoke again (same block as section 4), e.g. with `smoke-vless-plain@local`.

## 7) Switch to 3x-ui Shadowsocks and validate

```bash
chmod +x /tmp/setup_omnirelay_vps_3xui_shadowsocks.sh
sudo /tmp/setup_omnirelay_vps_3xui_shadowsocks.sh install \
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
  --vps-ip "$VPS_IP"
```

Check status/health:

```bash
sudo /usr/local/sbin/omnirelay-gatewayctl get-protocol
sudo /usr/local/sbin/omnirelay-gatewayctl status --json | jq
sudo /usr/local/sbin/omnirelay-gatewayctl health --json | jq
```

Expected:
- `.activeProtocol == "shadowsocks_3xui"`
- `.xuiState == "active"`
- `.singBoxState == "inactive"`

Run OmniPanel CRUD smoke again (same block as section 4), e.g. with `smoke-3xui-ss@local`.

## 8) Switch to IPSec/L2TP (hwdsl2) and validate

```bash
chmod +x /tmp/setup_omnirelay_vps_ipsec_l2tp.sh
sudo /tmp/setup_omnirelay_vps_ipsec_l2tp.sh install \
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
  --vps-ip "$VPS_IP"
```

Check status/health:

```bash
sudo /usr/local/sbin/omnirelay-gatewayctl get-protocol
sudo /usr/local/sbin/omnirelay-gatewayctl status --json | jq
sudo /usr/local/sbin/omnirelay-gatewayctl health --json | jq
```

Expected:
- `.activeProtocol == "ipsec_l2tp_hwdsl2"`
- `.ipsecState == "active"`
- `.xl2tpdState == "active"`
- `.publicListener == true`

Run OmniPanel CRUD smoke again (same block as section 4), e.g. with `smoke-ipsec@local`.

## 9) Switch back to VLESS Reality and re-validate

Run the VLESS install command again (section 3), then:

```bash
sudo /usr/local/sbin/omnirelay-gatewayctl get-protocol
sudo /usr/local/sbin/omnirelay-gatewayctl status --json | jq
sudo /usr/local/sbin/omnirelay-gatewayctl health --json | jq
```

Expected:
- `.activeProtocol == "vless_reality_3xui"`
- `.xuiState == "active"`
- `.singBoxState == "inactive"`

## 10) Quick failure triage

```bash
sudo systemctl --no-pager --full status omnirelay-omnipanel
sudo journalctl -u omnirelay-omnipanel -n 120 --no-pager
sudo systemctl --no-pager --full status x-ui || true
sudo systemctl --no-pager --full status omnirelay-singbox || true
sudo systemctl --no-pager --full status ipsec || sudo systemctl --no-pager --full status strongswan-starter || true
sudo systemctl --no-pager --full status xl2tpd || true
sudo /usr/local/sbin/omnirelay-gatewayctl health --json | jq
```
