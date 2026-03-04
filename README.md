# EstherLink Backend (.NET 8 Minimal API)

Production-oriented backend for EstherLink desktop clients.

## Features

1. Licensing
- Online license verification (`POST /api/license/verify`)
- Device activation tracking and max-device enforcement
- Signed server response (HMAC-SHA256) for offline cache verification on client

2. Whitelist updates
- Country/category grouped whitelist sets
- Versioned snapshots with SHA-256
- Latest set fetch and CIDR diff endpoint

3. App update metadata
- Latest release check by channel + current version
- Semver-aware comparison

4. Security/ops
- Admin API key protection (`X-ADMIN-API-KEY`)
- Public endpoint rate limiting
- Swagger/OpenAPI
- Health probes (`/health/live`, `/health/ready`)
- EF Core migrations (PostgreSQL)
- Docker compose (backend + Postgres + Redis)

## Repository Structure

- `src/EstherLink.Backend`
  - .NET 8 Minimal API
  - EF Core (Npgsql)
  - Endpoints, services, auth filter, health checks, migrations
- `src/EstherLink.Backend.Contracts`
  - DTO contracts shared by backend endpoints
- `docker-compose.yml`
  - `postgres`, `redis`, `backend`

## Data Model

Implemented entities/tables:
- `licenses`
- `license_activations`
- `whitelist_sets` (includes `set_group_id` for stable logical set identity across versions)
- `whitelist_entries`
- `app_releases`

## Run with Docker Compose

```bash
docker compose up --build
```

Backend: `http://localhost:8080`
Swagger: `http://localhost:8080/swagger`

## Run Locally (without Docker)

1. Start PostgreSQL and create database/user matching `appsettings.json`.
2. Run backend:

```bash
dotnet run --project src/EstherLink.Backend
```

App auto-applies migrations on startup (`Database:ApplyMigrationsOnStartup=true`).

## EF Migrations

Local tool manifest includes `dotnet-ef`.

Create migration:

```bash
dotnet tool run dotnet-ef migrations add <Name> \
  --project src/EstherLink.Backend \
  --startup-project src/EstherLink.Backend
```

Apply migration:

```bash
dotnet tool run dotnet-ef database update \
  --project src/EstherLink.Backend \
  --startup-project src/EstherLink.Backend
```

## Configuration

`src/EstherLink.Backend/appsettings.json` keys:

- `ConnectionStrings:Postgres`
- `ConnectionStrings:Redis` (optional)
- `Admin:ApiKeys` (one or more keys)
- `Licensing:SigningSecret` (required for response signature)
- `Licensing:OfflineCacheTtlHours` (default 24)
- `Database:ApplyMigrationsOnStartup`

## API Summary

Public endpoints (rate limited):
- `POST /api/license/verify`
- `GET /api/whitelist/sets?country=IR&category=...`
- `GET /api/whitelist/{setId}/latest`
- `GET /api/whitelist/{setId}/diff?fromVersion=12`
- `GET /api/app/latest?channel=stable&current=1.2.3`

Admin endpoints (`X-ADMIN-API-KEY` required):
- `POST /api/admin/licenses`
- `POST /api/admin/licenses/{id}/revoke`
- `GET /api/admin/licenses/{id}`
- `POST /api/admin/whitelist/sets` (helper endpoint to create initial logical set)
- `POST /api/admin/whitelist/{setId}/publish`
- `POST /api/admin/app/releases`

## Example cURL

Verify license:

```bash
curl -X POST http://localhost:8080/api/license/verify \
  -H "Content-Type: application/json" \
  -d '{
    "licenseKey":"ABC-123",
    "fingerprint":{"machineGuid":"m1","nicMac":"aa-bb","osVersion":"win11"},
    "appVersion":"1.2.3",
    "nonce":"random-nonce-1"
  }'
```

Create license (admin):

```bash
curl -X POST http://localhost:8080/api/admin/licenses \
  -H "Content-Type: application/json" \
  -H "X-ADMIN-API-KEY: dev-admin-key" \
  -d '{
    "licenseKey":"ABC-123",
    "status":"active",
    "plan":"pro",
    "maxDevices":2
  }'
```

## Notes

- All timestamps are stored and returned in UTC (`DateTimeOffset.UtcNow`).
- License response signature includes: `valid, reason, plan, licenseExpiresAt, cacheExpiresAt, serverTime, nonce`.
- Redis is optional in this MVP; included for production caching extension.
