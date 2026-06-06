#!/usr/bin/env bash
set -Eeuo pipefail

CONNECTOR_CORE="${1:-./connector-core}"
[[ -x "$CONNECTOR_CORE" ]] || { echo "connector-core binary is not executable: $CONNECTOR_CORE" >&2; exit 2; }

ROOT="$(mktemp -d /tmp/omnirelay-connector-smoke.XXXXXX)"
trap 'rm -rf "$ROOT"' EXIT

SPEC="$ROOT/spec.json"
cat >"$SPEC" <<'JSON'
{
  "apiVersion": "omnirelay.io/v1alpha1",
  "kind": "Gateway",
  "relayId": "e4ccc282a1004b62ad2cda5770d6e32d",
  "release": {
    "channel": "stable",
    "connectorCoreVersion": "smoke"
  },
  "gateway": {
    "type": "local",
    "protocol": "vless_plain_singbox",
    "publicPort": 8443,
    "connectorMode": "full_tunnel"
  },
  "tunnel": {
    "backendHost": "127.0.0.1",
    "backendPort": 15001,
    "probeUrls": ["https://8.8.8.8/"],
    "timeoutSeconds": 10
  },
  "dns": {
    "dohEndpoints": ["https://8.8.8.8/dns-query"]
  },
  "panel": {
    "port": 0
  }
}
JSON

COMMON_ARGS=(
  --config-root "$ROOT/etc/omnirelay"
  --transaction-root "$ROOT/transactions"
  --systemd-root "$ROOT/systemd"
  --nginx-root "$ROOT/nginx"
  --dnsmasq-root "$ROOT/dnsmasq"
  --install-packages=false
  --reload-systemd=false
  --json
)

"$CONNECTOR_CORE" gateway validate --spec "$SPEC" --json
"$CONNECTOR_CORE" gateway plan --spec "$SPEC" --json
"$CONNECTOR_CORE" gateway apply --spec "$SPEC" "${COMMON_ARGS[@]}" >"$ROOT/first.json"
"$CONNECTOR_CORE" gateway apply --spec "$SPEC" "${COMMON_ARGS[@]}" >"$ROOT/second.json"

grep -q '"changed":true' "$ROOT/first.json"
grep -q '"changed":false' "$ROOT/second.json"
test -s "$ROOT/etc/omnirelay/relays/e4ccc282a1004b62ad2cda5770d6e32d/gateway/spec.json"
test -s "$ROOT/systemd/omnirelay-gateway-e4ccc282a1004b62ad2cda5770d6e32d.target"

echo "connector-core Linux smoke test passed"
