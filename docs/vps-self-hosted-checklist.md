# VPS Self-Hosted Validation Checklist (Sing-box Gateway)

## Provisioning
1. Ubuntu 22.04+ with static public IP.
2. Bundle or upload gateway scripts to VPS.
3. Run command-mode installer with new script names, for example:
   - `sudo bash scripts/setup_omnirelay_vps_singbox_vless_reality.sh install --bundle-dir <bundle-dir> ...`
4. Tunnel user must have shell access and sudo permission.

## Required Services
1. `sshd` active.
2. `omnirelay-singbox` active.
3. `omnirelay-omnipanel` active.
4. `nginx` active.
5. `fail2ban` active.
6. UFW allows only required ports (default: `22`, protocol public port, OmniPanel public port).

## Connectivity Checks
1. `sshd -t`
2. `systemctl status omnirelay-singbox --no-pager`
3. `systemctl status omnirelay-omnipanel --no-pager`
4. `fail2ban-client status sshd`
5. `sudo /usr/local/sbin/omnirelay-gatewayctl get-protocol`
6. `sudo /usr/local/sbin/omnirelay-gatewayctl status --json | jq`
7. `sudo /usr/local/sbin/omnirelay-gatewayctl health --json | jq`

## Runtime Checks (All Protocol Families)
1. `activeProtocol` is one of:
   - `vless_reality_singbox`
   - `vless_plain_singbox`
   - `shadowsocks_singbox`
   - `shadowtls_v3_shadowsocks_singbox`
   - `openvpn_tcp_singbox`
   - `ipsec_l2tp_singbox`
2. `singBoxState == "active"`.
3. Tunnel probe fields exist and are meaningful:
   - `tunnelHealthy`
   - `tunnelReason`
   - `tunnelBackendProtocol`
   - `tunnelEgressReachable`
4. No runtime dependency on `x-ui` or `redsocks`.

## Tunnel Validation
1. Start Windows reverse SSH tunnel:
   - `ssh -NT -R 127.0.0.1:15000:127.0.0.1:<WINDOWS_PROXY_PORT> omnirelay@<VPS_IP> -p 22`
2. On VPS:
   - `timeout 2 bash -c 'cat < /dev/null > /dev/tcp/127.0.0.1/15000' && echo OPEN || echo CLOSED`
3. Confirm traffic egress works while tunnel is healthy.
4. Stop tunnel and confirm fail-closed behavior.

## Fail-Closed Validation
1. Stop reverse tunnel process on Windows.
2. Keep ingress runtime active.
3. Confirm client traffic fails without tunnel.
4. Restore tunnel and confirm traffic resumes.

## Security Controls
1. Tunnel auth mode matches deployment (`host_key` or `password`).
2. `PermitListen` constrained to loopback backend (`127.0.0.1:15000`).
3. No public listener is exposed on backend loopback port.
4. Fail2ban jail configured for `sshd`.
5. Firewall defaults: deny incoming, allow outgoing.

## Removed-Stack Verification
1. `systemctl is-active x-ui` returns inactive/not-found.
2. `systemctl is-active omnirelay-redsocks` returns inactive/not-found.
3. `/etc/systemd/system/x-ui.service` does not exist.
4. `/etc/systemd/system/omnirelay-redsocks.service` does not exist.
