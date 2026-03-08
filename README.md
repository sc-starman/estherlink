# OmniRelay

OmniRelay is a Windows HTTP CONNECT egress router behind a static-IP VPS ingress.

Traffic flow:
- External client connects to VPS public TCP port (for example `443`).
- VPS forwards TCP stream through reverse tunnel to Windows local proxy listener.
- Windows service parses CONNECT and chooses egress adapter:
  - Whitelisted destination -> IC1 (`VPS Network` adapter).
  - Non-whitelisted -> IC2 (`Outgoing Network` adapter).

## Projects

- `src/OmniRelay.Core`
  - Shared models: config, whitelist CIDR/IP parsing, status, adapter enumeration.
- `src/OmniRelay.Ipc`
  - Named pipe protocol + JSON client/server helpers.
- `src/OmniRelay.Service`
  - Windows Service host + CONNECT proxy engine + licensing + persistence.
- `src/OmniRelay.UI`
  - WPF control panel for configuration, whitelist, license verify, service control.
- `src/OmniRelay.Installer`
  - WiX-based MSI installer packaging UI + Service for Windows.
- `src/OmniRelay.Backend`
  - .NET 8 minimal API for licensing, whitelist updates, app releases.
- `src/OmniRelay.Backend.Contracts`
  - Backend request/response DTO contracts.
- `tests/OmniRelay.Backend.IntegrationTests`
  - Integration tests for backend license verify, whitelist diff, and app latest.

## Windows MVP (UI + Service)

Implemented:
- Windows service host (`UseWindowsService`) with named-pipe IPC commands:
  - `set_config`, `update_whitelist`, `get_status`, `start_proxy`, `stop_proxy`, `verify_license`
- HTTP CONNECT proxy listener on localhost (configurable port).
- Outbound bind-to-adapter logic:
  - Adapter selected by `IfIndex`
  - Service picks adapter primary IPv4
  - Outbound socket `Bind(localIPv4, 0)` before `Connect(target)`
- Destination-only whitelist routing (source mode removed).
- CIDR/IP whitelist parser with `# comment` support.
- License validation:
  - `POST /api/license/verify` with nonce and Ed25519 signature verification
  - `GET /api/license/public-keys` cache for offline signature validation
  - Encrypted DPAPI cache fallback if online check fails and cached signed license is still valid
- Config schema migration (`SchemaVersion=1`) for persisted config upgrades.
- Optional tunnel supervisor worker (`ssh -NT -R ...`) with reconnect/backoff and tunnel health status.
- Persistent config at `C:\ProgramData\OmniRelay\config.json` with encrypted license key.
- Service log at `C:\ProgramData\OmniRelay\logs\service.log`.

UI redesign (Tailwind-inspired WPF shell):
- MVVM + `Frame/Page` navigation with `CommunityToolkit.Mvvm`.
- Shell layout:
  - Left sidebar navigation
  - Top header
  - Page content frame
  - Footer status strip
- Pages:
  - Dashboard
  - Relay Management
  - Gateway Management
  - Whitelist Rules
  - License
  - Logs
  - Settings
- Runtime theme switching (Dark/Light) with persisted preference in:
  - `%AppData%\OmniRelay\ui.settings.json`
- Busy-state command gating to prevent duplicate action clicks.
- License page visual direction aligned to the provided Tailwind concept.

## Backend API

Capabilities:
- Licensing verify + activation tracking + Ed25519-signed cacheable responses
- Whitelist set publish/latest/diff APIs (CIDR/IP only)
- App release latest-version API
- Admin protection via `X-ADMIN-API-KEY` (hashed keys in DB)
- Admin audit trail for `/api/admin/*` writes
- Public rate limiting
- Swagger/OpenAPI
- Health checks: `/health/live`, `/health/ready`
- Metrics endpoint: `/metrics`
- EF Core migrations on PostgreSQL

## OmniRelay Web (Landing + Dashboard)

`src/OmniRelay.Backend` now hosts:
- Public landing page at `/` (OmniRelay marketing copy).
- Account auth pages:
  - `GET|POST /account/register`
  - `GET|POST /account/login`
  - `POST /account/logout`
- Protected dashboard pages:
  - `GET /dashboard`
  - `GET /dashboard/trial`
  - `GET /dashboard/licenses`
  - `GET /dashboard/billing`
  - `GET /app/downloads`
  - `GET /app/account`
- Internal dashboard APIs:
  - `POST /app/api/trial/request`
  - `POST /app/api/checkout/create-intent`
  - `GET /app/api/checkout/{orderId}/status?refresh=true|false`
  - `POST /webhooks/paykrypt`

Behavior:
- Trial: one per account, 2-day TTL, single-device.
- Paid: one-time purchase, perpetual license (`expires_at=null`) + 1 year update entitlement, single-device.
- Device transfer: explicit transfer required when moving to another device, up to 3 transfers per rolling 365 days.
- Payment flow: PayKrypt webhook + polling reconciliation, idempotent license issuance.

New DB objects:
- Identity tables (`app_users`, `app_roles`, etc.)
- `user_licenses`
- `commerce_orders`
- `paykrypt_intents`
- `paykrypt_webhook_events`

Migration:
- `20260304131959_AddWebDashboardAndCommerce`

Public endpoints:
- `POST /api/license/verify`
- `GET /api/license/public-keys`
- `GET /api/whitelist/sets?country=IR&category=core`
- `GET /api/whitelist/{setId}/latest`
- `GET /api/whitelist/{setId}/diff?fromVersion=1`
- `GET /api/app/latest?channel=stable&current=1.2.3`
- `GET /metrics`

Admin endpoints (`X-ADMIN-API-KEY` required):
- `POST /api/admin/licenses`
- `POST /api/admin/licenses/{id}/revoke`
- `GET /api/admin/licenses/{id}`
- `POST /api/admin/whitelist/sets`
- `POST /api/admin/whitelist/{setId}/publish`
- `POST /api/admin/app/releases`
- `POST /api/admin/seed/sample`

## Build (Solution)

```powershell
dotnet tool restore
dotnet restore OmniRelay.sln
dotnet build OmniRelay.sln -c Debug
```

## Run Windows Components (Developer)

Terminal 1:

```powershell
dotnet run --project src/OmniRelay.Service
```

Terminal 2:

```powershell
dotnet run --project src/OmniRelay.UI
```

In UI:
1. Select `VPS Network (IC1)` and `Outgoing Network (IC2)` adapters.
2. Configure Relay page (proxy port + IC1/IC2 adapters) and apply relay config.
3. Configure Gateway page (tunnel/auth + bootstrap socks + gateway ports) and apply gateway config.
4. Update whitelist and verify license.
5. Use:
   - `Relay Management` for relay install/start/stop/uninstall and relay monitoring.
   - `Gateway Management` for bootstrap check, install/start/stop/uninstall/health and gateway monitoring.

## Publish Windows Service

```powershell
dotnet publish src/OmniRelay.Service -c Release -r win-x64 --self-contained false -o .\out\service
```

## Build Windows MSI (UI + Service)

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build_windows_msi.ps1 -Configuration Release
```

MSI includes the VPS gateway setup script used by the UI for online gateway install over SOCKS bootstrap tunnel.

Output:

```text
<repo>\src\OmniRelay.Installer\bin\Release\OmniRelay.msi
```

MSI behavior:
1. Installs UI binaries under `Program Files\OmniRelay\UI`.
2. Installs Service binaries under `Program Files\OmniRelay\Service`.
3. Registers and starts `OmniRelay.Service` (DisplayName: `OmniRelay Service`).
4. Creates Start Menu shortcut for OmniRelay UI.

Installer packaging note:
- MSI payload is published as `win-x64` self-contained for both UI and Service, so a separate .NET runtime installation is not required on target Windows machines.

## Run Backend with Docker

```bash
docker compose up --build
```

- Backend API: `http://localhost:8080`
- Swagger: `http://localhost:8080/swagger`

### HTTPS Reverse Proxy (Nginx + Let's Encrypt on :443)

`docker-compose.yml` now includes:
- `nginx-proxy` on ports `80/443`
- `acme-companion` for automatic Let's Encrypt certificates
- backend wired via `VIRTUAL_HOST` / `LETSENCRYPT_HOST`

Before starting in server mode, set DNS + environment:
1. Point your domain A record to the VPS public IP.
2. Create `.env` at repo root (you can copy from `.env.example`):

```env
OMNIRELAY_DOMAIN=api.your-domain.com
LETSENCRYPT_EMAIL=ops@your-domain.com
```

Then run:

```bash
docker compose up -d --build
```

Public endpoint:
- `https://<OMNIRELAY_DOMAIN>` (TLS auto-managed)

### Email Delivery Mode (Registration + Contact)

Both account verification emails and contact-form emails use the same delivery provider.

Set provider in `.env`:

```env
EMAIL_DELIVERY_PROVIDER=smtp
```

`smtp` mode (default):

```env
SMTP_HOST=smtp.example.com
SMTP_PORT=587
SMTP_USE_SSL=true
SMTP_REQUIRE_AUTH=true
SMTP_CONNECT_TIMEOUT_SECONDS=10
SMTP_SEND_TIMEOUT_SECONDS=45
SMTP_RETRY_COUNT=1
SMTP_USERNAME=mailer@example.com
SMTP_PASSWORD=change-me
SMTP_FROM_EMAIL=noreply@example.com
SMTP_FROM_NAME=OmniRelay Contact
```

`mail_service` mode:

```env
EMAIL_DELIVERY_PROVIDER=mail_service
MAIL_SERVICE_BASE_URL=https://mail-sender.example.com:8443
MAIL_SERVICE_SEND_PATH=/send
MAIL_SERVICE_API_KEY=replace-with-mail-service-api-key
MAIL_SERVICE_API_KEY_HEADER=x-api-key
MAIL_SERVICE_TIMEOUT_SECONDS=45
MAIL_SERVICE_RETRY_COUNT=1
```

## Run Backend Locally (without Docker)

```powershell
dotnet run --project src/OmniRelay.Backend
```

For Tailwind CSS during web development:

```powershell
cd src/OmniRelay.Backend
npm install
npm run watch:css
```

## Backend Seed Data

Admin endpoint:
- `POST /api/admin/seed/sample`

Helper script:
- `scripts/seed_backend_sample.sh`

Example:

```bash
BASE_URL=http://localhost:8080 ADMIN_API_KEY=dev-admin-key bash scripts/seed_backend_sample.sh
```

Windows MSI upload tip (when behind Cloudflare):
- `scripts/upload_windows_installer_release.ps1` accepts:
  - `-BaseUrl` for the public download URL base.
  - `-UploadBaseUrl` (optional) for the admin upload endpoint base.
- If Cloudflare returns `524` during upload, point `-UploadBaseUrl` to a non-proxied origin host (DNS-only) and keep `-BaseUrl` as your public site domain.

Seed includes:
- demo license (`DEMO-KEY-001`)
- sample whitelist logical set (`IR Core`, versions 1 and 2)
- sample app releases (`stable` 1.2.0 and 1.3.0)

## Backend Integration Tests

```powershell
dotnet test tests/OmniRelay.Backend.IntegrationTests/OmniRelay.Backend.IntegrationTests.csproj -c Debug
```

## UI ViewModel Tests

```powershell
dotnet test tests/OmniRelay.UI.Tests/OmniRelay.UI.Tests.csproj -c Debug
```

## CI

Workflow: `.github/workflows/ci.yml`

Runs:
- `dotnet tool restore`
- `dotnet restore`
- `dotnet build`
- `dotnet test`
- `dotnet list package --vulnerable --include-transitive`
- `dotnet-ef migrations script --idempotent` validation
- `docker compose config` validation
- Trivy filesystem scan (`HIGH,CRITICAL`)

## Operations Docs

- Production runbook: `docs/production-runbook.md`
- VPS checklist: `docs/vps-self-hosted-checklist.md`
- Backup/recovery: `docs/backup-recovery.md`

## VPS Note

VPS must forward incoming client TCP streams to the Windows proxy listener endpoint over the reverse tunnel.

Primary ingress path (current):
- 3x-ui/Xray on VPS listens on public port `443` (client auth/profile management).
- Xray outbound is forced to `127.0.0.1:15000` (loopback tunnel endpoint).
- Windows reverse SSH tunnel maps VPS `127.0.0.1:15000` to Windows `127.0.0.1:<proxy-listen-port>`.
- Fail mode is fail-closed for client traffic (no direct VPS fallback).

Helper setup scripts:
- Primary control script (command-mode): `scripts/setup_omnirelay_vps_3xui.sh`
- Rollback/legacy: `scripts/setup_OmniRelay_vps.sh`

Example (manual online install using SOCKS bootstrap):

```bash
sudo bash scripts/setup_omnirelay_vps_3xui.sh install --public-port 443 --panel-port 2054 --backend-port 15000 --ssh-port 22 --tunnel-user OmniRelay --tunnel-auth host_key --bootstrap-socks-port 16080 --dns-mode hybrid --doh-endpoints "https://1.1.1.1/dns-query,https://8.8.8.8/dns-query" --dns-udp-only true
```

DNS-through-tunnel commands:

```bash
sudo /usr/local/sbin/omnirelay-gatewayctl dns-status
sudo /usr/local/sbin/omnirelay-gatewayctl dns-apply --dns-mode hybrid --doh-endpoints "https://1.1.1.1/dns-query,https://8.8.8.8/dns-query" --dns-udp-only true
sudo /usr/local/sbin/omnirelay-gatewayctl dns-repair --dns-mode hybrid --doh-endpoints "https://1.1.1.1/dns-query,https://8.8.8.8/dns-query" --dns-udp-only true
sudo /usr/local/sbin/omnirelay-gatewayctl health --json
```

Gateway Management page exposes the same DNS controls:
- DNS mode: `hybrid|doh|udp`
- DoH endpoints list
- `Allow DNS UDP only` policy toggle

## Notes

- UTC timestamps are used end-to-end (`DateTimeOffset.UtcNow`).
- License verify response signature input includes:
  - `valid`, `reason`, `plan`, `licenseExpiresAt`, `cacheExpiresAt`, `serverTime`, `requestId`, `signatureAlg`, `keyId`, `nonce`
- Web config sections used by dashboard:
  - `PayKrypt`
  - `Commerce`
  - `Web`
