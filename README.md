# EstherLink Backend (.NET 8 Minimal API)

Production-ready backend for EstherLink desktop clients.

## What It Provides

1. Licensing
- `POST /api/license/verify`
- Device activations (`max_devices` enforcement)
- HMAC-SHA256 signed verify response for offline cache validation

2. Whitelist updates (CIDR/IP)
- Latest grouped whitelist sets by country/category
- Versioned whitelist snapshots
- Diff endpoint (`added` / `removed`)

3. App updates
- Latest release metadata by channel
- Semver-aware update check

4. Security and operations
- Admin endpoint protection via `X-ADMIN-API-KEY`
- Public endpoint rate limiting
- Swagger/OpenAPI
- Health checks (`/health/live`, `/health/ready`)
- EF Core migrations (PostgreSQL)
- Docker Compose stack (Postgres + Redis + backend)

## Repository Layout

- `src/EstherLink.Backend`
  - Minimal API, EF Core models/migrations, services, security filters
- `src/EstherLink.Backend.Contracts`
  - Request/response DTOs
- `tests/EstherLink.Backend.IntegrationTests`
  - Integration tests for license verify, whitelist diff, and app latest
- `docker-compose.yml`
  - Local stack orchestration
- `.github/workflows/ci.yml`
  - Build/test/validation CI pipeline

## Run with Docker

```bash
docker compose up --build
```

- API: `http://localhost:8080`
- Swagger: `http://localhost:8080/swagger`

## Run Locally

```bash
dotnet tool restore
dotnet restore EstherLink.sln
dotnet build EstherLink.sln -c Debug
dotnet run --project src/EstherLink.Backend
```

## Seed Sample Data (Step 1)

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

## Run Integration Tests (Step 2)

```bash
dotnet test tests/EstherLink.Backend.IntegrationTests/EstherLink.Backend.IntegrationTests.csproj -c Debug
```

Test coverage includes:
- license verify + device limit behavior
- whitelist diff behavior
- app latest update behavior

## CI Pipeline (Step 3)

Workflow: `.github/workflows/ci.yml`

CI runs:
- `dotnet tool restore`
- `dotnet restore`
- `dotnet build`
- `dotnet test`
- `dotnet-ef migrations script --idempotent` validation
- `docker compose config` validation

## Configuration

`src/EstherLink.Backend/appsettings.json`:

- `ConnectionStrings:Postgres`
- `ConnectionStrings:Redis` (optional)
- `Admin:ApiKeys` (admin keys)
- `Licensing:SigningSecret` (required)
- `Licensing:OfflineCacheTtlHours` (default 24)
- `Database:ApplyMigrationsOnStartup`

## Public API Summary

- `POST /api/license/verify`
- `GET /api/whitelist/sets?country=IR&category=core`
- `GET /api/whitelist/{setId}/latest`
- `GET /api/whitelist/{setId}/diff?fromVersion=1`
- `GET /api/app/latest?channel=stable&current=1.2.3`

## Admin API Summary

All admin endpoints require `X-ADMIN-API-KEY`.

- `POST /api/admin/licenses`
- `POST /api/admin/licenses/{id}/revoke`
- `GET /api/admin/licenses/{id}`
- `POST /api/admin/whitelist/sets`
- `POST /api/admin/whitelist/{setId}/publish`
- `POST /api/admin/app/releases`
- `POST /api/admin/seed/sample`

## Notes

- UTC timestamps are used end-to-end (`DateTimeOffset.UtcNow`).
- License response signature includes: `valid, reason, plan, licenseExpiresAt, cacheExpiresAt, serverTime, nonce`.
