# OpenVPN Integration Plan (Sing-box Connector Mode)

## Summary
OpenVPN is integrated as `openvpn_tcp_singbox` in the unified sing-box architecture.

Key decisions:
- OpenVPN remains protocol-native ingress (`tun0`).
- sing-box is the mandatory egress connector to reverse-tunnel SOCKS (`127.0.0.1:$BACKEND_PORT`).
- `redsocks` is removed.
- Gateway status/health contract uses `singBoxState` + tunnel probe fields.

## Protocol Definition
- Protocol ID: `openvpn_tcp_singbox`
- Display name: `OpenVPN (TCP, sing-box connector)`
- Deployment path: connector-core gateway spec and reconciler.

## Data Plane
1. Client connects to OpenVPN TCP listener (public port).
2. Traffic enters `tun0`.
3. Connector rules redirect eligible traffic to local sing-box connector inbound.
4. sing-box routes outbound to local SOCKS backend (`127.0.0.1:$BACKEND_PORT`).
5. Reverse SSH tunnel carries egress to Windows proxy.

## Runtime Contract
connector-core exposes standard gateway and DNS commands for the relay.

Health/status expectations:
- `.activeProtocol == "openvpn_tcp_singbox"`
- `.openVpnState == "active"`
- `.singBoxState == "active"`
- tunnel probe fields included in output

## Deployment Wiring
- UI maps `openvpn_tcp_singbox` into the connector-core gateway spec.
- connector-core renders and reconciles the OpenVPN and sing-box runtime.
- OmniPanel provider remains OpenVPN-native for client/auth lifecycle.

## Removed Dependencies
- No `x-ui` runtime path.
- No `redsocks` packages, service units, or health/remediation logic.

## Validation
1. Fresh install with `openvpn_tcp_singbox`.
2. Confirm active services:
   - `openvpn-server@omnirelay` (or equivalent OpenVPN service)
   - `omnirelay-singbox`
   - `omnirelay-omnipanel`
3. Confirm `gatewayctl status --json` and `health --json` reflect connector path and tunnel probes.
4. Confirm OpenVPN traffic egresses only when tunnel/SOCKS backend is healthy.
5. Verify protocol switch to/from OpenVPN cleans connector redirect rules idempotently.
