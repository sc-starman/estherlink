#!/usr/bin/env bash
set -Eeuo pipefail
IFS=$'\n\t'

BASE_URL=""
SPEC_PATH=""
CHANNEL="stable"
OPERATION="migrate"
BOOTSTRAP_MODE="direct"
SOCKS_PORT="16080"
PUBLIC_KEY_FILE=""

usage() {
  cat >&2 <<'EOF'
Usage: bootstrap_omnirelay_connector_core.sh --base-url URL --spec PATH [options]
  --channel stable|beta
  --operation migrate|apply
  --bootstrap-mode direct|tunnel
  --socks-port PORT
  --public-key-file PATH
EOF
}

fail() {
  printf 'connector-core bootstrap failed: %s\n' "$*" >&2
  exit 1
}

while (($#)); do
  case "$1" in
    --base-url) BASE_URL="${2:-}"; shift 2 ;;
    --spec) SPEC_PATH="${2:-}"; shift 2 ;;
    --channel) CHANNEL="${2:-}"; shift 2 ;;
    --operation) OPERATION="${2:-}"; shift 2 ;;
    --bootstrap-mode) BOOTSTRAP_MODE="${2:-}"; shift 2 ;;
    --socks-port) SOCKS_PORT="${2:-}"; shift 2 ;;
    --public-key-file) PUBLIC_KEY_FILE="${2:-}"; shift 2 ;;
    -h|--help) usage; exit 0 ;;
    *) usage; fail "unknown argument: $1" ;;
  esac
done

[[ -n "$BASE_URL" ]] || fail "--base-url is required"
[[ -f "$SPEC_PATH" ]] || fail "--spec must reference an existing file"
[[ "$CHANNEL" == "stable" || "$CHANNEL" == "beta" ]] || fail "--channel must be stable or beta"
[[ "$OPERATION" == "migrate" || "$OPERATION" == "apply" ]] || fail "--operation must be migrate or apply"
[[ "$BOOTSTRAP_MODE" == "direct" || "$BOOTSTRAP_MODE" == "tunnel" ]] || fail "--bootstrap-mode must be direct or tunnel"
[[ "$SOCKS_PORT" =~ ^[0-9]+$ ]] && ((SOCKS_PORT >= 1 && SOCKS_PORT <= 65535)) || fail "invalid SOCKS port"

[[ "$(id -u)" -eq 0 ]] || fail "bootstrap must run as root"
[[ -r /etc/os-release ]] || fail "/etc/os-release is missing"
# shellcheck disable=SC1091
. /etc/os-release
case "${ID:-}" in
  ubuntu|debian) ;;
  *) fail "unsupported distribution: ${ID:-unknown}" ;;
esac

case "$(uname -m)" in
  x86_64|amd64) ARCH="amd64" ;;
  aarch64|arm64) ARCH="arm64" ;;
  *) fail "unsupported architecture: $(uname -m)" ;;
esac
OS="linux"

BASE_URL="${BASE_URL%/}"
if [[ "$CHANNEL" == "beta" ]]; then
  RELEASE_URL="${BASE_URL}/download/connector-core/beta/${OS}/${ARCH}"
else
  RELEASE_URL="${BASE_URL}/download/connector-core/${OS}/${ARCH}"
fi

WORK_DIR="$(mktemp -d /tmp/omnirelay-connector-bootstrap.XXXXXX)"
APT_PROXY_FILE="/etc/apt/apt.conf.d/99-omnirelay-bootstrap-socks"
cleanup() {
  rm -rf "$WORK_DIR"
  rm -f "$APT_PROXY_FILE"
}
trap cleanup EXIT

if [[ "$BOOTSTRAP_MODE" == "tunnel" ]]; then
  cat >"$APT_PROXY_FILE" <<EOF
Acquire::http::Proxy "socks5h://127.0.0.1:${SOCKS_PORT}";
Acquire::https::Proxy "socks5h://127.0.0.1:${SOCKS_PORT}";
EOF
fi

export DEBIAN_FRONTEND=noninteractive
apt-get update -y
apt-get install -y --no-install-recommends ca-certificates curl openssl tar

CURL_ARGS=(--fail --silent --show-error --location --connect-timeout 15 --max-time 300)
if [[ "$BOOTSTRAP_MODE" == "tunnel" ]]; then
  CURL_ARGS+=(--socks5-hostname "127.0.0.1:${SOCKS_PORT}")
fi

MANIFEST_PATH="${WORK_DIR}/manifest.json"
SIGNATURE_PATH="${WORK_DIR}/manifest.sig"
ARTIFACT_PATH="${WORK_DIR}/connector-core-${OS}-${ARCH}.tar.gz"
curl "${CURL_ARGS[@]}" "${RELEASE_URL}/manifest" -o "$MANIFEST_PATH"
curl "${CURL_ARGS[@]}" "${RELEASE_URL}/signature" -o "$SIGNATURE_PATH"
curl "${CURL_ARGS[@]}" "$RELEASE_URL" -o "$ARTIFACT_PATH"

if [[ -z "$PUBLIC_KEY_FILE" ]]; then
  PUBLIC_KEY_FILE="${WORK_DIR}/release-public-key.pem"
  cat >"$PUBLIC_KEY_FILE" <<'EOF'
__OMNIRELAY_CONNECTOR_CORE_RELEASE_PUBLIC_KEY_PEM__
EOF
fi
grep -q -- '-----BEGIN PUBLIC KEY-----' "$PUBLIC_KEY_FILE" ||
  fail "connector-core release public key was not supplied or injected"

openssl dgst -sha256 -verify "$PUBLIC_KEY_FILE" -signature "$SIGNATURE_PATH" "$MANIFEST_PATH" >/dev/null ||
  fail "release manifest signature verification failed"

manifest_string() {
  local key="$1"
  sed -n "s/.*\"${key}\":\"\\([^\"]*\\)\".*/\\1/p" "$MANIFEST_PATH" | head -n1
}

manifest_number() {
  local key="$1"
  sed -n "s/.*\"${key}\":\\([0-9][0-9]*\\).*/\\1/p" "$MANIFEST_PATH" | head -n1
}

MANIFEST_CHANNEL="$(manifest_string channel)"
MANIFEST_OS="$(manifest_string os)"
MANIFEST_ARCH="$(manifest_string arch)"
MANIFEST_ARTIFACT="$(manifest_string artifact)"
MANIFEST_SHA256="$(manifest_string sha256)"
MANIFEST_SIZE="$(manifest_number sizeBytes)"

[[ "$MANIFEST_CHANNEL" == "$CHANNEL" ]] || fail "manifest channel mismatch"
[[ "$MANIFEST_OS" == "$OS" ]] || fail "manifest OS mismatch"
[[ "$MANIFEST_ARCH" == "$ARCH" ]] || fail "manifest architecture mismatch"
[[ "$MANIFEST_ARTIFACT" == "connector-core-${OS}-${ARCH}.tar.gz" ]] || fail "manifest artifact name mismatch"
[[ "$MANIFEST_SHA256" =~ ^[a-fA-F0-9]{64}$ ]] || fail "manifest SHA256 is invalid"
[[ "$MANIFEST_SIZE" =~ ^[0-9]+$ ]] || fail "manifest artifact size is invalid"

ACTUAL_SHA256="$(sha256sum "$ARTIFACT_PATH" | awk '{print $1}')"
ACTUAL_SIZE="$(stat -c '%s' "$ARTIFACT_PATH")"
[[ "${ACTUAL_SHA256,,}" == "${MANIFEST_SHA256,,}" ]] || fail "artifact SHA256 verification failed"
[[ "$ACTUAL_SIZE" == "$MANIFEST_SIZE" ]] || fail "artifact size verification failed"

tar -xzf "$ARTIFACT_PATH" -C "$WORK_DIR"
[[ -f "${WORK_DIR}/connector-core" ]] || fail "archive does not contain connector-core"
chmod 0755 "${WORK_DIR}/connector-core"

INSTALL_DIR="/usr/local/bin"
INSTALL_PATH="${INSTALL_DIR}/connector-core"
mkdir -p "$INSTALL_DIR"
install -m 0755 "${WORK_DIR}/connector-core" "${INSTALL_PATH}.new"
mv -f "${INSTALL_PATH}.new" "$INSTALL_PATH"

"$INSTALL_PATH" version >/dev/null
"$INSTALL_PATH" gateway "$OPERATION" --spec "$SPEC_PATH" --json
