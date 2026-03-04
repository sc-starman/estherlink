# VPS Self-Hosted Validation Checklist (3x-ui Ingress)

## Provisioning
1. Ubuntu 22.04+ with static public IP.
2. Run `scripts/setup_omnirelay_vps_3xui.sh` as root.
3. Add Windows tunnel SSH public key for `estherlink` user (`--pubkey` or `--pubkey-file`).
4. Keep old script `scripts/setup_estherlink_vps.sh` only for rollback scenarios.

## Required Services
1. `sshd` active.
2. `x-ui` active.
3. `fail2ban` active.
4. UFW allows only required ports (default: `22`, `443`, `8443`).
5. HAProxy disabled/not used for ingress path.

## Connectivity Checks
1. `sshd -t`
2. `systemctl status x-ui --no-pager`
3. `fail2ban-client status sshd`
4. `ss -lnt | grep -E ':22\\b|:443\\b|:8443\\b|:15000\\b'`
5. `journalctl -u x-ui -n 100 --no-pager`

## 3x-ui Runtime Checks
1. Log in to panel on `https://<VPS_IP>:8443/<RANDOM_PATH>/`.
2. Confirm inbound is `VLESS + TCP + REALITY` bound to `0.0.0.0:443`.
3. Confirm client profiles are TCP-only (UDP disabled for launch).
4. Confirm Xray template contains outbound `to_windows_tunnel_http` to `127.0.0.1:15000`.
5. Confirm routing includes:
   - `network=udp -> blocked`
   - final default rule -> `to_windows_tunnel_http`

## Tunnel Validation
1. Start Windows reverse SSH tunnel:
   - `ssh -NT -R 127.0.0.1:15000:127.0.0.1:<WINDOWS_PROXY_PORT> estherlink@<VPS_IP> -p 22`
2. On VPS:
   - `timeout 2 bash -c 'cat < /dev/null > /dev/tcp/127.0.0.1/15000' && echo OPEN || echo CLOSED`
3. Confirm client sessions in 3x-ui can reach internet only when tunnel is up.
4. Confirm client traffic reaches Windows proxy logs.

## Fail-Closed Validation
1. Stop reverse tunnel process on Windows.
2. Keep 3x-ui inbound up on VPS.
3. Confirm client connections fail (no fallback via VPS direct egress).
4. Restore tunnel and confirm traffic resumes.

## Security Controls
1. Password login disabled for tunnel user.
2. `PermitListen` constrained to `127.0.0.1:15000`.
3. No public listener allowed on port `15000`.
4. Fail2ban jail configured for `sshd`.
5. Firewall defaults: deny incoming, allow outgoing.
