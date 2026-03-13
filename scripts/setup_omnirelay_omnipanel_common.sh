#!/usr/bin/env bash
set -Eeuo pipefail
IFS=$'\n\t'

omnipanel_bool() {
  local value
  value="$(printf '%s' "${1:-false}" | tr '[:upper:]' '[:lower:]' | xargs)"
  case "$value" in
    true|1|yes|y) printf 'true' ;;
    *) printf 'false' ;;
  esac
}

omnipanel_ssl_mode() {
  local mode
  mode="$(printf '%s' "${1:-}" | tr '[:upper:]' '[:lower:]' | xargs)"
  if [[ "$mode" == "uploaded" ]]; then
    printf 'uploaded'
    return
  fi
  printf 'letsencrypt'
}

omnipanel_generate_self_signed_cert() {
  local cert_path="$1"
  local key_path="$2"
  local server_name="$3"
  local detected_ip="$4"
  local tmp_cfg san_index

  tmp_cfg="$(mktemp)"
  san_index=2
  cat > "$tmp_cfg" <<EOF
[req]
default_bits = 2048
prompt = no
default_md = sha256
distinguished_name = dn
x509_extensions = v3_req

[dn]
CN = ${server_name}

[v3_req]
keyUsage = critical, digitalSignature, keyEncipherment
extendedKeyUsage = serverAuth
subjectAltName = @alt_names

[alt_names]
DNS.1 = localhost
IP.1 = 127.0.0.1
EOF

  if [[ "$server_name" =~ ^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$ ]]; then
    printf 'IP.%s = %s\n' "$san_index" "$server_name" >> "$tmp_cfg"
    ((san_index++))
  else
    printf 'DNS.%s = %s\n' "$san_index" "$server_name" >> "$tmp_cfg"
    ((san_index++))
  fi

  if [[ -n "$detected_ip" && "$detected_ip" != "$server_name" ]]; then
    printf 'IP.%s = %s\n' "$san_index" "$detected_ip" >> "$tmp_cfg"
  fi

  openssl req -x509 -nodes -newkey rsa:2048 -days 825 \
    -keyout "$key_path" \
    -out "$cert_path" \
    -config "$tmp_cfg" >/dev/null 2>&1
  rm -f "$tmp_cfg"
}

omnipanel_prepare_tls_assets() {
  local metadata_dir="$1"
  local ssl_enabled="$2"
  local ssl_mode="$3"
  local panel_domain="$4"
  local panel_cert_file="$5"
  local panel_key_file="$6"
  local detected_ip="$7"
  local vps_ip="$8"
  local cert_path key_path cert_dir server_name

  cert_dir="${metadata_dir}/certs"
  cert_path="${cert_dir}/omnipanel.crt"
  key_path="${cert_dir}/omnipanel.key"
  mkdir -p "$cert_dir"
  chmod 0700 "$cert_dir"

  if [[ "$ssl_enabled" != "true" ]]; then
    printf '%s;%s' "" ""
    return 0
  fi

  if [[ "$ssl_mode" == "uploaded" ]]; then
    [[ -n "$panel_cert_file" && -f "$panel_cert_file" ]] || die "Uploaded SSL mode selected but --panel-cert-file not found."
    [[ -n "$panel_key_file" && -f "$panel_key_file" ]] || die "Uploaded SSL mode selected but --panel-key-file not found."
    install -m 0644 -o root -g root "$panel_cert_file" "$cert_path"
    install -m 0600 -o root -g root "$panel_key_file" "$key_path"
    printf '%s;%s' "$cert_path" "$key_path"
    return 0
  fi

  if [[ -z "$panel_domain" ]]; then
    server_name="$vps_ip"
    [[ -n "$server_name" ]] || server_name="$detected_ip"
    [[ -n "$server_name" ]] || server_name="localhost"
    omnipanel_generate_self_signed_cert "$cert_path" "$key_path" "$server_name" "$detected_ip"
    chmod 0600 "$key_path"
    chmod 0644 "$cert_path"
    chown root:root "$key_path" "$cert_path"
    printf '%s;%s' "$cert_path" "$key_path"
    return 0
  fi

  # Let's Encrypt via certbot HTTP-01 (requires domain -> VPS and TCP/80 reachable).
  export DEBIAN_FRONTEND=noninteractive
  if ! command -v certbot >/dev/null 2>&1; then
    apt-get update -y >/dev/null 2>&1 || true
    apt-get install -y certbot >/dev/null 2>&1 || die "Unable to install certbot for OmniPanel SSL."
  fi

  systemctl stop nginx >/dev/null 2>&1 || true
  certbot certonly --non-interactive --agree-tos --register-unsafely-without-email \
    --keep-until-expiring --standalone -d "$panel_domain" >/dev/null 2>&1 \
    || die "Let's Encrypt certificate issuance failed for domain '${panel_domain}'. Ensure DNS points to VPS and TCP/80 is reachable."

  cert_path="/etc/letsencrypt/live/${panel_domain}/fullchain.pem"
  key_path="/etc/letsencrypt/live/${panel_domain}/privkey.pem"
  [[ -f "$cert_path" && -f "$key_path" ]] || die "Let's Encrypt certificate files not found after issuance."
  printf '%s;%s' "$cert_path" "$key_path"
}

omnipanel_render_nginx() {
  local nginx_conf="$1"
  local panel_port="$2"
  local internal_port="$3"
  local panel_domain="$4"
  local domain_only="$5"
  local ssl_enabled="$6"
  local cert_path="$7"
  local key_path="$8"

  if [[ "$ssl_enabled" == "true" ]]; then
    if [[ "$domain_only" == "true" && -n "$panel_domain" ]]; then
      cat > "$nginx_conf" <<EOF
server {
    listen ${panel_port} ssl default_server;
    server_name _;
    ssl_certificate ${cert_path};
    ssl_certificate_key ${key_path};
    return 444;
}

server {
    listen ${panel_port} ssl;
    server_name ${panel_domain};
    ssl_certificate ${cert_path};
    ssl_certificate_key ${key_path};
    ssl_session_cache shared:SSL:10m;
    ssl_session_timeout 1d;
    ssl_protocols TLSv1.2 TLSv1.3;

    location / {
        proxy_http_version 1.1;
        proxy_set_header Host \$host;
        proxy_set_header X-Forwarded-For \$proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto \$scheme;
        proxy_set_header Upgrade \$http_upgrade;
        proxy_set_header Connection "upgrade";
        proxy_pass http://127.0.0.1:${internal_port};
    }
}
EOF
      return 0
    fi

    cat > "$nginx_conf" <<EOF
server {
    listen ${panel_port} ssl;
    server_name ${panel_domain:-_};
    ssl_certificate ${cert_path};
    ssl_certificate_key ${key_path};
    ssl_session_cache shared:SSL:10m;
    ssl_session_timeout 1d;
    ssl_protocols TLSv1.2 TLSv1.3;

    location / {
        proxy_http_version 1.1;
        proxy_set_header Host \$host;
        proxy_set_header X-Forwarded-For \$proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto \$scheme;
        proxy_set_header Upgrade \$http_upgrade;
        proxy_set_header Connection "upgrade";
        proxy_pass http://127.0.0.1:${internal_port};
    }
}
EOF
    return 0
  fi

  cat > "$nginx_conf" <<EOF
server {
    listen ${panel_port};
    server_name _;
EOF

  if [[ "$domain_only" == "true" && -n "$panel_domain" ]]; then
    cat >> "$nginx_conf" <<EOF
    if (\$host != "${panel_domain}") {
        return 444;
    }
EOF
  fi

  cat >> "$nginx_conf" <<EOF
    location / {
        proxy_http_version 1.1;
        proxy_set_header Host \$host;
        proxy_set_header X-Forwarded-For \$proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto \$scheme;
        proxy_set_header Upgrade \$http_upgrade;
        proxy_set_header Connection "upgrade";
        proxy_pass http://127.0.0.1:${internal_port};
    }
}
EOF
}

omnipanel_configure_nginx_proxy() {
  local panel_port="$1"
  local internal_port="$2"
  local metadata_dir="$3"
  local panel_domain="$4"
  local panel_domain_only="$5"
  local panel_ssl_enabled="$6"
  local panel_ssl_mode="$7"
  local panel_cert_file="$8"
  local panel_key_file="$9"
  local detected_ip="${10:-}"
  local vps_ip="${11:-}"
  local nginx_conf cert_key_pair cert_path key_path panel_ready

  panel_domain="$(printf '%s' "$panel_domain" | xargs)"
  panel_domain_only="$(omnipanel_bool "$panel_domain_only")"
  panel_ssl_enabled="$(omnipanel_bool "$panel_ssl_enabled")"
  panel_ssl_mode="$(omnipanel_ssl_mode "$panel_ssl_mode")"
  nginx_conf="/etc/nginx/sites-available/omnirelay-omnipanel.conf"
  panel_ready=false

  if [[ "$panel_domain_only" == "true" && -z "$panel_domain" ]]; then
    die "Domain-only access requires --panel-domain."
  fi

  if [[ "$panel_ssl_enabled" == "true" && -z "$panel_domain" && "$panel_ssl_mode" == "letsencrypt" ]]; then
    die "Let's Encrypt SSL mode requires --panel-domain."
  fi

  cert_key_pair="$(omnipanel_prepare_tls_assets "$metadata_dir" "$panel_ssl_enabled" "$panel_ssl_mode" "$panel_domain" "$panel_cert_file" "$panel_key_file" "$detected_ip" "$vps_ip")"
  cert_path="${cert_key_pair%%;*}"
  key_path="${cert_key_pair##*;}"

  omnipanel_render_nginx "$nginx_conf" "$panel_port" "$internal_port" "$panel_domain" "$panel_domain_only" "$panel_ssl_enabled" "$cert_path" "$key_path"
  ln -sfn "$nginx_conf" /etc/nginx/sites-enabled/omnirelay-omnipanel.conf
  rm -f /etc/nginx/sites-enabled/default || true
  nginx -t
  systemctl enable --now nginx
  systemctl restart nginx

  for ((i=1; i<=15; i++)); do
    if ss -lnt "( sport = :${panel_port} )" 2>/dev/null | awk 'NR>1 {print $0}' | grep -q .; then
      panel_ready=true
      break
    fi
    sleep 1
  done

  [[ "$panel_ready" == "true" ]] || die "nginx did not open panel port ${panel_port}."
}
