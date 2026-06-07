# VPS Self-Hosted Validation Checklist (Sing-box Gateway)

## Provisioning
1. Ubuntu 22.04+ with static public IP.
2. Install through the Windows UI so it uploads the signed connector-core bootstrap and gateway spec.
3. Confirm connector-core is installed on the VPS.
4. Tunnel user must have shell access and sudo permission.

## Required Services
1. `sshd` active.
2. `connector-core` installed at `/usr/local/bin/connector-core`.
3. `omnirelay-omnipanel` active.
4. `nginx` active.
5. `fail2ban` active.
6. UFW allows only required ports (default: `22`, protocol public port, OmniPanel public port).

## Connectivity Checks
1. `sshd -t`
2. `systemctl status omnirelay-singbox --no-pager`
3. `systemctl status omnirelay-omnipanel --no-pager`
4. `fail2ban-client status sshd`
5. `sudo /usr/local/bin/connector-core gateway status --relay-id <relay-id> --json | jq`
6. `sudo /usr/local/bin/connector-core dns status --relay-id <relay-id> --json | jq`

## Runtime Checks (All Protocol Families)
1. `protocol` is one of:
   - `vless_reality_singbox`
   - `vless_plain_singbox`
   - `shadowsocks_singbox`
   - `shadowtls_v3_shadowsocks_singbox`
   - `openvpn_tcp_singbox`
   - `ipsec_l2tp_singbox`
2. Gateway backend state is active.
3. Tunnel probe fields exist and are meaningful:
   - `tunnelHealthy`
   - `tunnelReason`
   - `tunnelBackendProtocol`
   - `tunnelEgressReachable`
4. No runtime dependency on legacy gateway setup scripts, `x-ui`, or `redsocks`.

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
