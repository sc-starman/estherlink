# Connector-Core Gateway Runbook

## Scope

Connector-core is the gateway management authority for signed-cutover releases. It owns desired-state validation, migration, reconciliation, lifecycle, status, health, repair, accounting, client sync, DNS, firewall, FRP configuration, protocol configuration, and OmniPanel configuration.

External daemons remain separate systemd services. FRP owns transport reconnection; connector-core must not restart a healthy FRP process for transient session failures.

## Production Release

1. Upload signed Linux `amd64` and `arm64` connector-core artifacts with `scripts/upload_connector_core_release.ps1`.
2. Build the coordinated Release MSI.

The release scripts read the connector-core version from `src\OmniRelay.Installer\Product.wxs`. Signing keys are generated automatically under `certs\connector-core-private.pem` and `certs\connector-core-public.pem` when missing. Do not commit the `certs` directory.

```powershell
.\scripts\upload_connector_core_release.ps1 `
  -BaseUrl https://<backend-host> `
  -AdminApiKey <admin-api-key> `
  -Os linux `
  -Arch amd64

.\scripts\upload_connector_core_release.ps1 `
  -BaseUrl https://<backend-host> `
  -AdminApiKey <admin-api-key> `
  -Os linux `
  -Arch arm64
```

```powershell
.\scripts\build_windows_msi.ps1 `
  -Configuration Release `
  -Channel stable
```

Release MSI builds reject missing signing keys and reject legacy gateway-script packaging.

## Gateway Inspection

```bash
sudo connector-core gateway status --relay-id <relay-id> --json | jq
sudo connector-core gateway health --relay-id <relay-id> --json | jq
sudo connector-core probe backend --relay-id <relay-id> --json | jq
sudo connector-core probe egress --relay-id <relay-id> --json | jq
sudo connector-core dns status --relay-id <relay-id> --json | jq
sudo systemctl --no-pager -l status omnirelay-gateway-<relay-id>.target
sudo journalctl -u omnirelay-connector-<relay-id>.service -n 200 --no-pager
```

## Declarative Operations

```bash
sudo connector-core gateway validate --spec /path/to/spec.json --json | jq
sudo connector-core gateway plan --spec /path/to/spec.json --json | jq
sudo connector-core gateway apply --spec /path/to/spec.json --json | jq
sudo connector-core gateway repair --relay-id <relay-id> --level safe --json | jq
sudo connector-core gateway start --relay-id <relay-id> --json | jq
sudo connector-core gateway stop --relay-id <relay-id> --json | jq
sudo connector-core gateway uninstall --relay-id <relay-id> --json | jq
```

Client and accounting operations infer protocol from persisted `spec.json`:

```bash
sudo connector-core clients sync --relay-id <relay-id> --json | jq
sudo connector-core accounting migrate --relay-id <relay-id> --json | jq
sudo connector-core accounting sync --relay-id <relay-id> --json | jq
```

## Migration Lifecycle

The coordinated UI lifecycle is:

1. Signed bootstrap installs connector-core.
2. `gateway migrate` snapshots legacy state and applies desired state.
3. The UI starts the gateway, activates OmniPanel, and runs acceptance probes.
4. Successful acceptance runs `gateway finalize-migration`.
5. Failed acceptance runs `gateway rollback-migration`.

Manual rollback before finalization:

```bash
sudo connector-core gateway rollback-migration --relay-id <relay-id> --json | jq
```

Rollback restores the latest migration snapshot, the active nginx site, relay-specific legacy units, and affected shared-daemon configuration. It reloads/restarts only shared daemons affected by restored files.

Manual finalization after successful acceptance:

```bash
sudo connector-core gateway finalize-migration --relay-id <relay-id> --json | jq
```

Migration snapshots are retained for 14 days under:

```text
/var/lib/omnirelay/transactions/migration-backups/<relay-id>/
```

## Failure Recovery

Inspect apply journals:

```bash
sudo find /var/lib/omnirelay/transactions -maxdepth 3 -type f -name journal.json -print
sudo jq . /var/lib/omnirelay/transactions/<relay-id>/<transaction-id>/journal.json
```

If connector-core cannot execute:

1. Preserve `/etc/omnirelay`, `/var/lib/omnirelay/transactions`, relay systemd units, and protocol daemon configuration.
2. Copy broken state before manually restoring the latest migration snapshot.
3. Run `systemctl daemon-reload`.
4. Restart only daemons whose restored configuration changed.
5. Run status, health, backend, egress, and DNS probes.

Do not delete migration snapshots until the gateway remains healthy after cutover.

## Validation Gates

```powershell
cd src\OmniRelay.ConnectorCore
go test ./...
```

```bash
bash scripts/test_connector_core_linux_smoke.sh /path/to/connector-core
bash scripts/test_connector_core_migration_smoke.sh /path/to/connector-core
```

Privileged acceptance remains required on supported Ubuntu/Debian systems for:

- systemd lifecycle and rollback
- OpenVPN install, client changes, DNS, quota, speed limits, and uninstall
- IPsec/L2TP install, NAT clients, PSK activation, enforcement, and uninstall
- OmniPanel HTTP/TLS modes and nginx activation
- FRP reconnect without connector-core transport interference
