# Postgres Backup and Recovery

## Policy
- Daily full backup.
- Continuous WAL archiving for PITR.
- Retention:
  - Full backups: 14 days.
  - WAL: 7 days minimum.

## Backup Command (Example)
```bash
pg_dump -Fc -h <HOST> -U <USER> -d omnirelay -f omnirelay_$(date -u +%Y%m%dT%H%M%SZ).dump
```

## WAL Archiving (Example Parameters)
- `archive_mode = on`
- `archive_command = 'test ! -f /archive/%f && cp %p /archive/%f'`
- `wal_level = replica`

## Restore Drill (Monthly)
1. Provision clean Postgres instance.
2. Restore latest full backup:
   - `pg_restore -h <HOST> -U <USER> -d omnirelay --clean --if-exists <backup.dump>`
3. Apply WAL up to target point-in-time if needed.
4. Run application smoke tests:
   - `GET /health/ready`
   - `POST /api/license/verify`
   - `GET /api/license/public-keys`
5. Record RTO/RPO and corrective actions.

## Recovery Targets
- RTO: 60 minutes.
- RPO: 15 minutes.
