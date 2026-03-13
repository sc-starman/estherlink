# OpenVPN Gateway Integration Plan (OmniRelay Multi-Protocol)

## Summary
Add OpenVPN as a third gateway protocol in the current protocol-pluggable architecture (alongside `vless_reality_3xui` and `shadowtls_v3_shadowsocks_singbox`) with:

- One active protocol per VPS
- Auto-replace on protocol switch
- OmniPanel client CRUD + config export support
- Same gatewayctl command contract used by existing scripts

This plan targets `OpenVPN over TCP` and keeps behavior compatible with your current gateway model.

## Protocol Definition
- Protocol id: `openvpn_tcp_relay`
- Display name: `OpenVPN (TCP)`
- Script: `setup_omnirelay_vps_openvpn.sh`
- Default protocol port: `443` (user-editable)

## Architecture Fit
The new protocol will follow the same layers already in place:

1. Windows UI protocol selector + protocol-specific fields
2. `GatewayDeploymentService` script resolution and args builder
3. VPS script implementing full gatewayctl contract
4. OmniPanel provider handling OpenVPN client lifecycle and config export

## OpenVPN Data Model
Persist protocol metadata under `/etc/omnirelay/gateway/metadata.json`:

- `active_protocol: "openvpn_tcp_relay"`
- `public_port`
- `omnipanel_public_port`
- `omnipanel_internal_port`
- `openvpn` object:
  - `mode` (`tcp`)
  - `network` (e.g. `10.29.0.0/24`)
  - `clients_dir`
  - `pki_dir`
  - `server_conf`
  - `ccd_dir`

## OpenVPN Runtime Design
Use OpenVPN server with cert-based clients:

- `proto tcp-server`
- `dev tun`
- `topology subnet`
- TLS auth/hardening (`tls-crypt` preferred)
- Client certificates issued per OmniPanel client

### Traffic Behavior
For compatibility with your existing relay behavior:

- Client -> OpenVPN TCP 443 -> VPS OpenVPN
- Client traffic from `tun0` is transparently redirected to local SOCKS backend (`127.0.0.1:15000`) through `redsocks` (TCP only)
- Existing reverse SSH tunnel continues to carry egress to Windows proxy

Notes:
- UDP forwarding is blocked for this protocol profile
- DNS will remain managed via existing DNS profile flow (`dns-apply`, `dns-status`, `dns-repair`)

## VPS Script: `setup_omnirelay_vps_openvpn.sh`
Implement same command contract as other protocol scripts:

- `install|uninstall|start|stop|status|health|dns-apply|dns-status|dns-repair|sync-clients`

### Install Responsibilities
1. Validate platform and required inputs
2. Ensure bootstrap SOCKS and clock sync logic same as other scripts
3. Install packages:
   - `openvpn`, `easy-rsa`, `openssl`, `iptables`, `jq`, `curl`, `nginx`, `nodejs`
   - `redsocks` (for TCP detour to backend SOCKS)
4. Build PKI:
   - CA
   - server cert/key
   - tls-crypt key
5. Write OpenVPN server config under `/etc/openvpn/server/omnirelay.conf`
6. Write `redsocks` config and systemd unit
7. Configure iptables rules for tun0 TCP redirection to redsocks
8. Deploy OmniPanel artifact and nginx reverse proxy (same model as existing scripts)
9. Write metadata + DNS profile

### Sync-Clients Responsibilities
`sync-clients` reconciles client artifacts from OmniPanel-managed JSON:

- Create/revoke client certs
- Emit per-client `.ovpn` bundle files under clients directory
- Reload/restart OpenVPN safely
- Return non-zero only if final runtime health is bad

## OmniPanel Provider
Add provider:
- `src/OmniRelay.GatewayPanel/lib/providers/openvpn.ts`

Implement `GatewayProtocolProvider`:
- `getInbound`
- `addClient`
- `updateClient` (enable/disable)
- `deleteClient` (revoke)
- `buildClientConfig` (return `.ovpn` text + QR payload if needed)

Storage model similar to ShadowTLS:
- `/opt/omnirelay/omni-gateway/openvpn_clients.json`

After mutations:
- Run `SINGBOX_RELOAD_COMMAND` equivalent env var for OpenVPN:
  - `OPENVPN_SYNC_COMMAND=/usr/bin/sudo -n /usr/local/sbin/omnirelay-gatewayctl sync-clients`

## UI Changes
In Gateway Control -> `Client Protocol` tab:

- Add OpenVPN option in protocol selector
- Protocol-specific fields:
  - `OpenVPN Port`
  - `OpenVPN Tunnel Network` (default `10.29.0.0/24`)
  - `Client DNS` (optional, comma-separated)

Reuse common fields:
- OmniPanel Port

Hide VLESS/ShadowTLS-specific controls when OpenVPN is selected.

## Deployment Service Changes
In `GatewayDeploymentService`:

1. Script resolver:
   - map `openvpn_tcp_relay` to `setup_omnirelay_vps_openvpn.sh`
2. Install args builder:
   - append OpenVPN protocol args when selected
3. Health/status parsing:
   - include openvpn state fields from JSON output

## Installer Packaging
Add new script to UI payload:
- `src/OmniRelay.UI/OmniRelay.UI.csproj`
- `src/OmniRelay.Installer/Product.wxs`

Mirror existing `GatewayScripts` packaging entries.

## Security Baseline
- Use `sudo -n` for panel-triggered privileged sync commands
- Restrict sudoers to exact gatewayctl sync command
- Cert/key permissions `0600`, directories `0700`
- Firewall only required ports:
  - SSH
  - OpenVPN protocol port
  - OmniPanel public port

## Backward Compatibility
- Keep existing protocols untouched
- Protocol switch (`install` with another protocol selected) auto-replaces previous stack
- Old status DTO fields remain, with new protocol-aware fields added

## Validation and Test Plan
1. Install OpenVPN protocol from clean VPS
2. Verify:
   - `openvpn-server@omnirelay` active
   - `redsocks` active
   - OmniPanel active
   - nginx active
3. Add/disable/delete client from OmniPanel:
   - no generic command failure
   - certs/profiles update correctly
4. Import generated `.ovpn` in OpenVPN client and connect
5. Confirm egress path reaches backend tunnel and internet access works
6. Switch protocol from OpenVPN -> ShadowTLS and back
7. Run health and DNS commands across protocol switches

## Implementation Order
1. Add protocol constants/models + UI selector option
2. Add deployment script resolver + args
3. Implement `setup_omnirelay_vps_openvpn.sh`
4. Implement OmniPanel `openvpn.ts` provider
5. Wire protocol registry in `lib/protocol.ts`
6. Package script in UI/MSI
7. End-to-end smoke tests and fixups

