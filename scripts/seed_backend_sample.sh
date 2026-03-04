#!/usr/bin/env bash
set -euo pipefail

BASE_URL="${BASE_URL:-http://localhost:8080}"
ADMIN_API_KEY="${ADMIN_API_KEY:-dev-admin-key}"

curl -sS -X POST "${BASE_URL}/api/admin/seed/sample" \
  -H "X-ADMIN-API-KEY: ${ADMIN_API_KEY}" \
  -H "Content-Type: application/json" \
  | sed 's/{/{\n  /; s/,/\n  ,/g'

echo
echo "Seed request completed."
