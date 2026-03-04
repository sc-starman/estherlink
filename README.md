# EstherLink

EstherLink is a Windows **HTTP CONNECT egress router** behind a static-IP VPS ingress.

- VPS accepts client TCP connections.
- VPS forwards those TCP streams to the Windows proxy listener through your tunnel.
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

## Implemented MVP

- Windows service host (`UseWindowsService`) with named-pipe IPC commands:
  - `set_config`, `update_whitelist`, `get_status`, `start_proxy`, `stop_proxy`, `verify_license`
- HTTP CONNECT proxy listener on localhost (configurable port).
- Outbound bind-to-adapter logic:
  - Adapter selected by IfIndex.
  - Service picks adapter primary IPv4.
  - Outbound socket `Bind(localIPv4, 0)` before `Connect(target)`.
- Whitelist modes:
  - Destination mode (works without source identity).
  - Source mode (requires PROXY protocol v2 enabled).
- CIDR/IP whitelist parser with `# comment` support.
- License validation:
  - POST to configurable endpoint.
  - Encrypted DPAPI cache fallback if online check fails and cached license is still valid.
- Persistent config at `C:\ProgramData\EstherLink\config.json` with encrypted license key.
- Service log file at `C:\ProgramData\EstherLink\logs\service.log`.

## Build

```powershell
dotnet restore EstherLink.sln
dotnet build EstherLink.sln -c Debug
```

## Run (Developer)

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
5. Install/Start service (or just start proxy when running service in console mode).

## Publish Service

```powershell
dotnet publish src/EstherLink.Service -c Release -r win-x64 --self-contained false -o .\out\service
```

Set UI `Service EXE Path` to:

```text
<repo>\out\service\EstherLink.Service.exe
```

Then click `Install/Start Service` in UI (UAC prompt appears).

## VPS Note

VPS must forward incoming client TCP streams to the Windows proxy listener endpoint over your tunnel.

Example concept:
- HAProxy on VPS listens on public port.
- Backend points to tunnel endpoint that reaches `127.0.0.1:<proxy-listen-port>` on Windows.

If whitelist-by-source is required, ensure your forwarding path provides client source identity to Windows (PROXY protocol v2 for this MVP).
