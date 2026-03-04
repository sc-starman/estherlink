# VPS Self-Hosted Validation Checklist

## Provisioning
1. Ubuntu 22.04+ with static public IP.
2. Run `scripts/setup_estherlink_vps.sh` as root.
3. Add Windows tunnel SSH public key for `estherlink` user.

## Required Services
1. `sshd` active.
2. `haproxy` active.
3. `fail2ban` active.
4. UFW allows only required ports (default 22 and 443).

## Connectivity Checks
1. `haproxy -c -f /etc/haproxy/haproxy.cfg`
2. `sshd -t`
3. `fail2ban-client status sshd`
4. `ss -lnt | grep -E ':22\\b|:443\\b|:15000\\b'`

## Tunnel Validation
1. Start Windows reverse SSH tunnel:
   - `ssh -NT -R 127.0.0.1:15000:127.0.0.1:<WINDOWS_PROXY_PORT> estherlink@<VPS_IP> -p 22`
2. On VPS:
   - `timeout 2 bash -c 'cat < /dev/null > /dev/tcp/127.0.0.1/15000'`
3. Confirm client traffic reaches Windows proxy logs.

## Security Controls
1. Password login disabled for tunnel user.
2. `PermitListen` constrained to expected loopback port.
3. Fail2ban jail configured for `sshd`.
4. Firewall defaults: deny incoming, allow outgoing.
