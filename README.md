# EstherLink

EstherLink is a Windows HTTP CONNECT egress router behind a static-IP VPS ingress.

Traffic flow:
- External client connects to VPS public TCP port (for example `443`).
- VPS forwards TCP stream through reverse tunnel to Windows local proxy listener.
- Windows service parses CONNECT and chooses egress adapter:
  - Whitelisted destination -> IC1 (`VPS Network` adapter).
  - Non-whitelisted -> IC2 (`Outgoing Network` adapter).

## Projects

- `src/EstherLink.Core`
  - Shared models: config, whitelist CIDR/IP parsing, status, adapter enumeration.
- `src/EstherLink.Ipc`
  - Named pipe protocol + JSON client/server helpers.
- `src/EstherLink.Service`
  - Windows Service host + CONNECT proxy engine + licensing + persistence.
- `src/EstherLink.UI`
  - WPF control panel for configuration, whitelist, license verify, service control.
- `src/EstherLink.Installer`
  - WiX-based MSI installer packaging UI + Service for Windows.
- `src/EstherLink.Backend`
  - .NET 8 minimal API for licensing, whitelist updates, app releases.
- `src/EstherLink.Backend.Contracts`
  - Backend request/response DTO contracts.
- `tests/EstherLink.Backend.IntegrationTests`
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
- Persistent config at `C:\ProgramData\EstherLink\config.json` with encrypted license key.
- Service log at `C:\ProgramData\EstherLink\logs\service.log`.

UI redesign (Tailwind-inspired WPF shell):
- MVVM + `Frame/Page` navigation with `CommunityToolkit.Mvvm`.
- Shell layout:
  - Left sidebar navigation
  - Top header
  - Page content frame
  - Footer status strip
- Pages:
  - Dashboard
  - Network Configuration
  - Whitelist Rules
  - Service Status
  - License
  - Logs
  - Settings
- Runtime theme switching (Dark/Light) with persisted preference in:
  - `%AppData%\EstherLink\ui.settings.json`
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
- EF Core migrations on PostgreSQL (Redis optional)

## OmniRelay Web (Landing + Dashboard)

`src/EstherLink.Backend` now hosts:
- Public landing page at `/` (OmniRelay marketing copy).
- Account auth pages:
  - `GET|POST /account/register`
  - `GET|POST /account/login`
  - `POST /account/logout`
- Protected dashboard pages:
  - `GET /app/dashboard`
  - `GET /app/licenses`
  - `GET /app/billing`
  - `GET /app/downloads`
  - `GET /app/account`
- Internal dashboard APIs:
  - `POST /app/api/trial/request`
  - `POST /app/api/checkout/create-intent`
  - `GET /app/api/checkout/{orderId}/status?refresh=true|false`
  - `POST /webhooks/paykrypt`

Behavior:
- Trial: one per account, 2-day TTL, `max_devices=1`.
- Paid: one-time purchase, perpetual license (`expires_at=null`) + 1 year update entitlement.
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
dotnet restore EstherLink.sln
dotnet build EstherLink.sln -c Debug
```

## Run Windows Components (Developer)

Terminal 1:

```powershell
dotnet run --project src/EstherLink.Service
```

Terminal 2:

```powershell
dotnet run --project src/EstherLink.UI
```

In UI:
1. Select `VPS Network (IC1)` and `Outgoing Network (IC2)` adapters.
2. Enter tunnel host/user/auth settings and proxy listen port.
3. Configure gateway public/panel ports in Network Configuration.
4. Update whitelist and verify license.
5. Open `Service Status` and use:
   - Relay controls (Install/Start/Stop/Uninstall)
   - Gateway controls (Install/Start/Stop/Uninstall/Health Check)
   - `Install/Start All` for end-to-end deployment.

## Publish Windows Service

```powershell
dotnet publish src/EstherLink.Service -c Release -r win-x64 --self-contained false -o .\out\service
```

## Build Windows MSI (UI + Service)

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build_windows_msi.ps1 -Configuration Release
```

Release MSI requires offline gateway bundle files:
- `src/EstherLink.UI/Assets/GatewayBundle/omnirelay-vps-bundle-x64.tar.gz`
- `src/EstherLink.UI/Assets/GatewayBundle/omnirelay-vps-bundle-x64.tar.gz.sha256`

Build the bundle first (Linux/macOS host with Docker):

```bash
bash scripts/build_omnirelay_vps_bundle.sh --output-dir src/EstherLink.UI/Assets/GatewayBundle --xui-version v2.6.5
```

Output:

```text
<repo>\src\EstherLink.Installer\bin\Release\OmniRelay.msi
```

MSI behavior:
1. Installs UI binaries under `Program Files\OmniRelay\UI`.
2. Installs Service binaries under `Program Files\OmniRelay\Service`.
3. Registers and starts `EstherLink.Service` (DisplayName: `OmniRelay Service`).
4. Creates Start Menu shortcut for OmniRelay UI.

## Run Backend with Docker

```bash
docker compose up --build
```

- Backend API: `http://localhost:8080`
- Swagger: `http://localhost:8080/swagger`

## Run Backend Locally (without Docker)

```powershell
dotnet run --project src/EstherLink.Backend
```

For Tailwind CSS during web development:

```powershell
cd src/EstherLink.Backend
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

Seed includes:
- demo license (`DEMO-KEY-001`)
- sample whitelist logical set (`IR Core`, versions 1 and 2)
- sample app releases (`stable` 1.2.0 and 1.3.0)

## Backend Integration Tests

```powershell
dotnet test tests/EstherLink.Backend.IntegrationTests/EstherLink.Backend.IntegrationTests.csproj -c Debug
```

## UI ViewModel Tests

```powershell
dotnet test tests/EstherLink.UI.Tests/EstherLink.UI.Tests.csproj -c Debug
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
- Offline bundle builder: `scripts/build_omnirelay_vps_bundle.sh`
- Rollback/legacy: `scripts/setup_estherlink_vps.sh`

Example (manual offline install, if not using UI deploy):

```bash
sudo bash scripts/setup_omnirelay_vps_3xui.sh install --bundle-dir /opt/omnirelay/bundle --public-port 443 --panel-port 8443 --backend-port 15000 --ssh-port 22 --tunnel-user estherlink --tunnel-auth host_key
```

## Notes

- UTC timestamps are used end-to-end (`DateTimeOffset.UtcNow`).
- License verify response signature input includes:
  - `valid`, `reason`, `plan`, `licenseExpiresAt`, `cacheExpiresAt`, `serverTime`, `requestId`, `signatureAlg`, `keyId`, `nonce`
- Web config sections used by dashboard:
  - `PayKrypt`
  - `Commerce`
  - `Web`
