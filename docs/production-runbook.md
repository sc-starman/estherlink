# OmniRelay Production Runbook

## Scope
- Backend API (`src/OmniRelay.Backend`) in single-region production.
- Windows Service/UI data plane.
- Customer/self-hosted VPS ingress.

## Incident Levels
- `SEV-1`: Licensing verify unavailable or global admin auth failure.
- `SEV-2`: Partial API degradation, whitelist publish lag, tunnel instability.
- `SEV-3`: Non-critical metrics/logging/documentation issues.

## First 15 Minutes Checklist
1. Confirm `/health/live`, `/health/ready`, `/metrics`.
2. Check error rates and recent deploy/change window.
3. Check DB connectivity and migration history.
4. Validate signing key availability (`signing_keys` active record).
5. Validate admin key records (`admin_api_keys` non-revoked keys).

## Common Runbooks

### License Verify Failures
1. Check backend logs for `requestId` and `licenseKeyHash`.
2. Verify active signing key exists and is not revoked.
3. Validate `/api/license/public-keys` returns expected `keyId`.
4. If key mismatch is suspected, keep old key active during rotation grace.

### Admin Access Failures
1. Verify caller sends `X-ADMIN-API-KEY`.
2. Validate key hash exists in `admin_api_keys`.
3. Check `expires_at`/`revoked_at`.
4. Confirm `Admin:ApiKeyPepper` is correct for current environment.

### Tunnel Instability (Windows)
1. Inspect service status via UI `TunnelConnected/TunnelLastError`.
2. Validate SSH binary availability and key path.
3. Validate VPS `sshd` + `x-ui` listeners and firewall.
4. Confirm remote port forwarding is accepted for `OmniRelay` user.

### Gateway Deployment Failures (VPS from UI)
1. In UI `Gateway Management`, review operation log and failing phase (`gateway_bootstrap`, `gateway_install`, `gateway_health`).
2. Confirm session sudo password is set (clear/re-enter if auth fails).
3. Confirm tunnel user has shell + sudo permissions.
4. Validate bundled script exists on VPS:
   - `/usr/local/sbin/omnirelay-gatewayctl`
5. Validate gateway status/health directly:
   - `sudo /usr/local/sbin/omnirelay-gatewayctl status --json`
   - `sudo /usr/local/sbin/omnirelay-gatewayctl health --json`
6. If gateway install fails before service creation, rerun `Gateway Bootstrap Check` then retry install.

### DNS Through Tunnel (Hybrid UDP + DoH)
1. Validate DNS profile and path:
   - `sudo /usr/local/sbin/omnirelay-gatewayctl dns-status`
   - `sudo /usr/local/sbin/omnirelay-gatewayctl health --json`
2. If `dnsPathHealthy=false`, run repair:
   - `sudo /usr/local/sbin/omnirelay-gatewayctl dns-repair --dns-mode hybrid --doh-endpoints "https://1.1.1.1/dns-query,https://8.8.8.8/dns-query" --dns-udp-only true`
3. If apps work but browser fails (`DNS_PROBE_*`):
   - Confirm `dnsConfigPresent=true` and `dnsRuleActive=true`.
   - Confirm `dohReachableViaTunnel=true`.
   - Confirm `udp53PathReady=true` when running hybrid mode.
4. If DoH is blocked but UDP 53 works:
   - Temporarily switch to UDP mode for diagnosis:
   - `sudo /usr/local/sbin/omnirelay-gatewayctl dns-apply --dns-mode udp --dns-udp-only true`
5. If neither DoH nor UDP 53 is available:
   - Check tunnel state on relay side (`TunnelConnected`, `Bootstrap SOCKS Forward`).
   - Check VPS firewall/provider egress policy for DNS/HTTPS.

## Rollback
- Backend:
  1. Roll back container image to previous tag.
  2. Keep DB migration backward compatibility in mind before rollback.
  3. Confirm `/health/ready` and `license/public-keys`.
- Windows service:
  1. Stop service.
  2. Repoint binary to previous published package.
  3. Start service and verify IPC + proxy + tunnel status.

## Key Rotation
1. Insert new signing key (`signing_keys`) and keep previous key active.
2. Verify new responses use new `keyId`.
3. Keep previous key available for cache grace period.
4. Revoke old key after grace period.
