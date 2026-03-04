#!/usr/bin/env bash
set -Eeuo pipefail
IFS=$'\n\t'

SCRIPT_NAME="$(basename "$0")"

OUTPUT_DIR="src/EstherLink.UI/Assets/GatewayBundle"
WORK_DIR=".artifacts/omnirelay-vps-bundle"
XUI_VERSION="v2.6.5"
BUNDLE_NAME="omnirelay-vps-bundle-x64.tar.gz"

PACKAGES=(
  openssh-server
  fail2ban
  ufw
  curl
  ca-certificates
  tar
  gzip
  jq
  openssl
  python3
)

usage() {
  cat <<EOF
Usage: ./${SCRIPT_NAME} [options]

Options:
  --output-dir <path>    Output folder for bundle tar + checksum (default: ${OUTPUT_DIR})
  --work-dir <path>      Temporary build workspace (default: ${WORK_DIR})
  --xui-version <tag>    3x-ui release tag to embed (default: ${XUI_VERSION})
  -h, --help             Show help

Requirements:
  - docker
  - curl
  - sha256sum

Produces:
  <output-dir>/${BUNDLE_NAME}
  <output-dir>/${BUNDLE_NAME}.sha256
EOF
}

log() {
  printf '[%s] %s\n' "$(date -u +'%Y-%m-%dT%H:%M:%SZ')" "$*"
}

die() {
  printf '[%s] ERROR: %s\n' "$(date -u +'%Y-%m-%dT%H:%M:%SZ')" "$*" >&2
  exit 1
}

require_cmd() {
  command -v "$1" >/dev/null 2>&1 || die "Missing required command: $1"
}

parse_args() {
  while [[ $# -gt 0 ]]; do
    case "$1" in
      --output-dir)
        OUTPUT_DIR="${2:-}"
        shift 2
        ;;
      --work-dir)
        WORK_DIR="${2:-}"
        shift 2
        ;;
      --xui-version)
        XUI_VERSION="${2:-}"
        shift 2
        ;;
      -h|--help)
        usage
        exit 0
        ;;
      *)
        die "Unknown option: $1"
        ;;
    esac
  done
}

build_codename_repo() {
  local codename="$1"
  local image_tag="$2"
  local out_dir="$3"

  mkdir -p "$out_dir"

  local package_csv
  package_csv="$(IFS=,; echo "${PACKAGES[*]}")"

  log "Building offline apt repo for ${codename} using docker image ${image_tag}..."
  docker run --rm \
    -e DEBIAN_FRONTEND=noninteractive \
    -e PACKAGE_LIST="$package_csv" \
    -v "${out_dir}:/out" \
    "${image_tag}" \
    bash -lc '
      set -Eeuo pipefail
      rm -f /etc/apt/apt.conf.d/docker-clean || true
      cat > /etc/apt/apt.conf.d/99-keep-cache <<CFG
Binary::apt::APT::Keep-Downloaded-Packages "true";
APT::Keep-Downloaded-Packages "true";
CFG
      apt-get update
      apt-get install -y --no-install-recommends dpkg-dev
      IFS="," read -r -a PKGS <<< "$PACKAGE_LIST"
      apt-get install -y --no-install-recommends "${PKGS[@]}"
      if compgen -G "/var/cache/apt/archives/*.deb" > /dev/null; then
        cp -v /var/cache/apt/archives/*.deb /out/
      else
        echo "APT cache is empty, switching to explicit package download fallback..."
        apt-get install -y --no-install-recommends apt-rdepends
        mapfile -t RESOLVED < <(apt-rdepends "${PKGS[@]}" 2>/dev/null | awk "/^[a-zA-Z0-9][a-zA-Z0-9.+-]*$/" | sort -u)
        workdir="$(mktemp -d)"
        cd "$workdir"
        for pkg in "${RESOLVED[@]}"; do
          apt-get download "$pkg" || true
        done
        if compgen -G "*.deb" > /dev/null; then
          cp -v ./*.deb /out/
        fi
      fi
      if ! compgen -G "/out/*.deb" > /dev/null; then
        echo "No .deb packages were exported to /out." >&2
        exit 21
      fi
      cd /out
      dpkg-scanpackages . /dev/null > Packages
      gzip -9c Packages > Packages.gz
    '
}

main() {
  parse_args "$@"

  require_cmd docker
  require_cmd curl
  require_cmd sha256sum

  local work_root bundle_root apt_root xui_root scripts_root
  work_root="$(pwd)/${WORK_DIR}"
  bundle_root="${work_root}/bundle"
  apt_root="${bundle_root}/apt"
  xui_root="${bundle_root}/xui"
  scripts_root="${bundle_root}/scripts"

  rm -rf "$work_root"
  mkdir -p "$apt_root/jammy" "$apt_root/noble" "$xui_root" "$scripts_root"

  build_codename_repo "jammy" "ubuntu:22.04" "$apt_root/jammy"
  build_codename_repo "noble" "ubuntu:24.04" "$apt_root/noble"

  log "Downloading 3x-ui release ${XUI_VERSION}..."
  curl -fL "https://github.com/MHSanaei/3x-ui/releases/download/${XUI_VERSION}/x-ui-linux-amd64.tar.gz" \
    -o "${xui_root}/x-ui-linux-amd64.tar.gz"

  log "Copying gateway setup script into bundle..."
  cp "scripts/setup_omnirelay_vps_3xui.sh" "${scripts_root}/setup_omnirelay_vps_3xui.sh"

  local xui_sha script_sha
  xui_sha="$(sha256sum "${xui_root}/x-ui-linux-amd64.tar.gz" | awk '{print $1}')"
  script_sha="$(sha256sum "${scripts_root}/setup_omnirelay_vps_3xui.sh" | awk '{print $1}')"

  cat > "${bundle_root}/manifest.json" <<EOF
{
  "bundleVersion": "1.0.0",
  "createdAtUtc": "$(date -u +'%Y-%m-%dT%H:%M:%SZ')",
  "architecture": "amd64",
  "supportedUbuntuCodenames": ["jammy", "noble"],
  "xuiVersion": "${XUI_VERSION}",
  "checksums": {
    "xuiArchiveSha256": "${xui_sha}",
    "setupScriptSha256": "${script_sha}"
  }
}
EOF

  mkdir -p "${OUTPUT_DIR}"
  local output_tar output_sha
  output_tar="${OUTPUT_DIR}/${BUNDLE_NAME}"
  output_sha="${OUTPUT_DIR}/${BUNDLE_NAME}.sha256"

  log "Packing offline bundle tarball..."
  tar -C "$work_root" -czf "$output_tar" bundle

  sha256sum "$output_tar" > "$output_sha"

  log "Bundle created: $output_tar"
  log "Checksum file: $output_sha"
}

main "$@"
