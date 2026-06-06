#!/usr/bin/env bash
set -Eeuo pipefail

CONNECTOR_CORE="${1:-./connector-core}"
[[ -x "$CONNECTOR_CORE" ]] || { echo "connector-core binary is not executable: $CONNECTOR_CORE" >&2; exit 2; }

RELAY_ID="e4ccc282a1004b62ad2cda5770d6e32d"
ROOT="$(mktemp -d /tmp/omnirelay-migration-smoke.XXXXXX)"
trap 'rm -rf "$ROOT"' EXIT

CONFIG_ROOT="$ROOT/etc/omnirelay"
TRANSACTION_ROOT="$ROOT/transactions"
SYSTEMD_ROOT="$ROOT/systemd"
NGINX_ROOT="$ROOT/nginx/sites-available"
NGINX_ENABLED_ROOT="$ROOT/nginx/sites-enabled"
DNSMASQ_ROOT="$ROOT/dnsmasq"
LEGACY_BINARY_ROOT="$ROOT/sbin"
GLOBAL_ROOT="$ROOT/global"
PANEL_APP_ROOT="$ROOT/apps"
SPEC="$ROOT/spec.json"

mkdir -p \
  "$CONFIG_ROOT/relays/$RELAY_ID/gateway" \
  "$SYSTEMD_ROOT" \
  "$LEGACY_BINARY_ROOT"
printf 'legacy-state\n' >"$CONFIG_ROOT/relays/$RELAY_ID/gateway/legacy-state.txt"
printf 'legacy-gatewayctl\n' >"$LEGACY_BINARY_ROOT/omnirelay-gatewayctl-$RELAY_ID"
printf 'legacy-unit\n' >"$SYSTEMD_ROOT/omnirelay-singbox-$RELAY_ID.service"

cat >"$SPEC" <<JSON
{
  "apiVersion": "omnirelay.io/v1alpha1",
  "kind": "Gateway",
  "relayId": "$RELAY_ID",
  "release": {"channel": "stable", "connectorCoreVersion": "smoke"},
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
  "dns": {"dohEndpoints": ["https://8.8.8.8/dns-query"]},
  "panel": {"port": 0}
}
JSON

COMMON_ROOT_ARGS=(
  --config-root "$CONFIG_ROOT"
  --transaction-root "$TRANSACTION_ROOT"
  --systemd-root "$SYSTEMD_ROOT"
  --nginx-root "$NGINX_ROOT"
  --nginx-enabled-root "$NGINX_ENABLED_ROOT"
  --dnsmasq-root "$DNSMASQ_ROOT"
  --legacy-binary-root "$LEGACY_BINARY_ROOT"
  --global-root "$GLOBAL_ROOT"
  --panel-app-root "$PANEL_APP_ROOT"
  --reload-systemd=false
  --json
)

"$CONNECTOR_CORE" gateway migrate --spec "$SPEC" --install-packages=false "${COMMON_ROOT_ARGS[@]}" >"$ROOT/migrate.json"
grep -q '"ok":true' "$ROOT/migrate.json"
test -s "$CONFIG_ROOT/relays/$RELAY_ID/gateway/spec.json"
test -s "$LEGACY_BINARY_ROOT/omnirelay-gatewayctl-$RELAY_ID"

printf 'mutated\n' >"$LEGACY_BINARY_ROOT/omnirelay-gatewayctl-$RELAY_ID"
"$CONNECTOR_CORE" gateway rollback-migration --relay-id "$RELAY_ID" "${COMMON_ROOT_ARGS[@]}" >"$ROOT/rollback.json"
grep -q '"ok":true' "$ROOT/rollback.json"
grep -q '^legacy-gatewayctl$' "$LEGACY_BINARY_ROOT/omnirelay-gatewayctl-$RELAY_ID"
grep -q '^legacy-state$' "$CONFIG_ROOT/relays/$RELAY_ID/gateway/legacy-state.txt"
test ! -e "$CONFIG_ROOT/relays/$RELAY_ID/gateway/spec.json"

"$CONNECTOR_CORE" gateway migrate --spec "$SPEC" --install-packages=false "${COMMON_ROOT_ARGS[@]}" >"$ROOT/remigrate.json"
"$CONNECTOR_CORE" gateway finalize-migration --relay-id "$RELAY_ID" "${COMMON_ROOT_ARGS[@]}" >"$ROOT/finalize.json"
grep -q '"ok":true' "$ROOT/finalize.json"
test ! -e "$LEGACY_BINARY_ROOT/omnirelay-gatewayctl-$RELAY_ID"
test ! -e "$SYSTEMD_ROOT/omnirelay-singbox-$RELAY_ID.service"
test -s "$SYSTEMD_ROOT/omnirelay-gateway-$RELAY_ID.target"

echo "connector-core migration lifecycle smoke test passed"
