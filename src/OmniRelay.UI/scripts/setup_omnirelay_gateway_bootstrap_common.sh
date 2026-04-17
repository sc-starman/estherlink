#!/usr/bin/env bash
set -Eeuo pipefail
IFS=$'\n\t'

omnirelay_bootstrap_normalize_mode() {
  local value
  value="$(printf '%s' "${1:-tunnel}" | tr '[:upper:]' '[:lower:]' | xargs)"
  case "$value" in
    tunnel|direct) printf '%s' "$value" ;;
    *) return 1 ;;
  esac
}

omnirelay_bootstrap_disable_proxy_env() {
  unset ALL_PROXY HTTPS_PROXY HTTP_PROXY
  unset all_proxy https_proxy http_proxy
  export NO_PROXY="127.0.0.1,localhost"
  export no_proxy="127.0.0.1,localhost"
}

omnirelay_bootstrap_configure_proxy_env() {
  local mode="${1:-tunnel}"
  local socks_port="${2:-16080}"
  omnirelay_bootstrap_disable_proxy_env
  if [[ "$mode" == "tunnel" ]]; then
    local proxy_url
    proxy_url="socks5h://127.0.0.1:${socks_port}"
    export ALL_PROXY="$proxy_url"
    export HTTPS_PROXY="$proxy_url"
    export HTTP_PROXY="$proxy_url"
  fi
}

omnirelay_bootstrap_configure_apt_proxy() {
  local mode="${1:-tunnel}"
  local socks_port="${2:-16080}"
  local apt_proxy_file="${3:-/etc/apt/apt.conf.d/99-omnirelay-socks}"

  if [[ "$mode" == "tunnel" ]]; then
    cat > "$apt_proxy_file" <<EOF
Acquire::http::Proxy "socks5h://127.0.0.1:${socks_port}";
Acquire::https::Proxy "socks5h://127.0.0.1:${socks_port}";
EOF
  else
    rm -f "$apt_proxy_file"
  fi
}

omnirelay_bootstrap_sync_clock() {
  local mode="${1:-tunnel}"
  local socks_port="${2:-16080}"
  local probe_url="${3:-https://deb.debian.org/}"
  local date_header remote_epoch now_epoch delta abs_delta was_ntp curl_cmd

  was_ntp=""
  omnirelay_bootstrap_configure_proxy_env "$mode" "$socks_port"
  if [[ "$mode" == "tunnel" ]]; then
    curl_cmd=(curl --silent --show-error --insecure --max-time 20 --connect-timeout 10 --retry 0 --socks5-hostname "127.0.0.1:${socks_port}" -I "$probe_url")
  else
    curl_cmd=(curl --silent --show-error --insecure --max-time 20 --connect-timeout 10 --retry 0 -I "$probe_url")
  fi

  date_header="$("${curl_cmd[@]}" 2>/dev/null | tr -d '\r' | awk 'tolower($1)=="date:"{$1="";sub(/^ /,"");print;exit}')"
  [[ -n "$date_header" ]] || return 1

  remote_epoch="$(date -u -d "$date_header" +%s 2>/dev/null || true)"
  [[ -n "$remote_epoch" ]] || return 1

  now_epoch="$(date -u +%s 2>/dev/null || echo 0)"
  delta=$(( remote_epoch - now_epoch ))
  abs_delta=$delta
  if (( abs_delta < 0 )); then
    abs_delta=$(( -abs_delta ))
  fi

  if (( abs_delta <= 5 )); then
    return 0
  fi

  if command -v timedatectl >/dev/null 2>&1; then
    was_ntp="$(timedatectl show -p NTP --value 2>/dev/null || true)"
    [[ "$was_ntp" == "yes" ]] && timedatectl set-ntp false >/dev/null 2>&1 || true
  fi

  if ! date -u -s "@${remote_epoch}" >/dev/null 2>&1; then
    [[ "$was_ntp" == "yes" ]] && timedatectl set-ntp true >/dev/null 2>&1 || true
    return 1
  fi

  command -v hwclock >/dev/null 2>&1 && hwclock --systohc >/dev/null 2>&1 || true
  [[ "$was_ntp" == "yes" ]] && timedatectl set-ntp true >/dev/null 2>&1 || true
  return 0
}

omnirelay_bootstrap_verify_egress() {
  local mode="${1:-tunnel}"
  local socks_port="${2:-16080}"
  local probe_url="${3:-https://deb.debian.org/}"
  local retries="${4:-24}"
  local wait_sec="${5:-5}"
  local listener_ok=0
  local egress_ok=0
  local sync_attempted=0
  local curl_err=""

  for ((i=1; i<=retries; i++)); do
    if [[ "$mode" == "tunnel" ]]; then
      if ss -lnt "( sport = :${socks_port} )" 2>/dev/null | awk 'NR>1 {print $0}' | grep -q .; then
        listener_ok=1
      else
        listener_ok=0
      fi
      if (( listener_ok == 0 )); then
        sleep "$wait_sec"
        continue
      fi
      omnirelay_bootstrap_configure_proxy_env "$mode" "$socks_port"
      if curl --fail --silent --show-error --max-time 20 --connect-timeout 10 --retry 0 --socks5-hostname "127.0.0.1:${socks_port}" "$probe_url" >/dev/null 2>/tmp/omnirelay-bootstrap-curl.err; then
        rm -f /tmp/omnirelay-bootstrap-curl.err
        egress_ok=1
        break
      fi
    else
      omnirelay_bootstrap_configure_proxy_env "$mode" "$socks_port"
      if curl --fail --silent --show-error --max-time 20 --connect-timeout 10 --retry 0 "$probe_url" >/dev/null 2>/tmp/omnirelay-bootstrap-curl.err; then
        rm -f /tmp/omnirelay-bootstrap-curl.err
        egress_ok=1
        break
      fi
    fi

    curl_err="$(tr -d '\r' </tmp/omnirelay-bootstrap-curl.err 2>/dev/null || true)"
    rm -f /tmp/omnirelay-bootstrap-curl.err
    if (( sync_attempted == 0 )) && printf '%s' "$curl_err" | grep -qi "certificate is not yet valid"; then
      omnirelay_bootstrap_sync_clock "$mode" "$socks_port" "$probe_url" || true
      sync_attempted=1
      continue
    fi
    sleep "$wait_sec"
  done

  if [[ "$mode" == "tunnel" ]]; then
    (( listener_ok == 1 )) || return 41
  fi
  (( egress_ok == 1 )) || return 42
  return 0
}
