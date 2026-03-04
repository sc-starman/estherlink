# EstherLink

EstherLink is a Windows HTTP CONNECT egress router behind a static-IP VPS ingress.

Traffic flow:
- External client connects to VPS public TCP port (for example `443`).
- VPS forwards TCP stream through reverse tunnel to Windows local proxy listener.
- Windows service parses CONNECT and chooses egress adapter:
  - Whitelisted destination/source -> IC1 (`VPS Network` adapter).
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
- Whitelist modes:
  - Destination mode (works without source identity)
  - Source mode (requires PROXY protocol v2 enabled)
- CIDR/IP whitelist parser with `# comment` support.
- License validation:
  - POST to configurable endpoint
  - Encrypted DPAPI cache fallback if online check fails and cached license is still valid
- Persistent config at `C:\ProgramData\EstherLink\config.json` with encrypted license key.
- Service log at `C:\ProgramData\EstherLink\logs\service.log`.

## Backend API

Capabilities:
- Licensing verify + activation tracking + signed cacheable responses
- Whitelist set publish/latest/diff APIs (CIDR/IP only)
- App release latest-version API
- Admin protection via `X-ADMIN-API-KEY`
- Public rate limiting
- Swagger/OpenAPI
- Health checks: `/health/live`, `/health/ready`
- EF Core migrations on PostgreSQL (Redis optional)

Public endpoints:
- `POST /api/license/verify`
- `GET /api/whitelist/sets?country=IR&category=core`
- `GET /api/whitelist/{setId}/latest`
- `GET /api/whitelist/{setId}/diff?fromVersion=1`
- `GET /api/app/latest?channel=stable&current=1.2.3`

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
2. Enter VPS host/port, proxy listen port, license endpoint/key.
3. Update whitelist.
4. Verify license.
5. Install/Start service (or start proxy when service runs in console mode).

## Publish Windows Service

```powershell
dotnet publish src/EstherLink.Service -c Release -r win-x64 --self-contained false -o .\out\service
```

Set UI `Service EXE Path` to:

```text
<repo>\out\service\EstherLink.Service.exe
```

Then click `Install/Start Service` in UI (UAC prompt appears).

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

## CI

Workflow: `.github/workflows/ci.yml`

Runs:
- `dotnet tool restore`
- `dotnet restore`
- `dotnet build`
- `dotnet test`
- `dotnet-ef migrations script --idempotent` validation
- `docker compose config` validation

## VPS Note

VPS must forward incoming client TCP streams to the Windows proxy listener endpoint over the reverse tunnel.

Example concept:
- HAProxy on VPS listens on public port.
- Backend points to tunnel endpoint that reaches `127.0.0.1:<proxy-listen-port>` on Windows.

If whitelist-by-source is required, ensure forwarding path provides client source identity to Windows (PROXY protocol v2 for this MVP).

Helper setup script:
- `scripts/setup_estherlink_vps.sh`

## Notes

- UTC timestamps are used end-to-end (`DateTimeOffset.UtcNow`).
- License verify response signature input includes:
  - `valid`, `reason`, `plan`, `licenseExpiresAt`, `cacheExpiresAt`, `serverTime`, `nonce`
