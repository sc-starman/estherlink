# EstherLink Production Runbook

## Scope
- Backend API (`src/EstherLink.Backend`) in single-region production.
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
4. Confirm remote port forwarding is accepted for `estherlink` user.

### Gateway Deployment Failures (VPS from UI)
1. In UI `Service Status`, review operation log and failing phase (`upload`, `gateway_install`, `gateway_health`).
2. Confirm session sudo password is set (clear/re-enter if auth fails).
3. Confirm tunnel user has shell + sudo permissions.
4. Validate bundled script exists on VPS:
   - `/usr/local/sbin/omnirelay-gatewayctl`
5. Validate gateway status/health directly:
   - `sudo /usr/local/sbin/omnirelay-gatewayctl status --json`
   - `sudo /usr/local/sbin/omnirelay-gatewayctl health --json`
6. If bundle integrity error appears, rebuild:
   - `bash scripts/build_omnirelay_vps_bundle.sh ...`

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
