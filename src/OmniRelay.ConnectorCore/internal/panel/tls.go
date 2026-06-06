package panel

import (
	"bytes"
	"context"
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"strings"

	"github.com/omnirelay/connector-core/internal/host"
	"github.com/omnirelay/connector-core/internal/spec"
)

type TLSAssets struct {
	Certificate []byte
	PrivateKey  []byte
}

func ManagedTLSPaths(panelRoot string) (string, string) {
	return filepath.Join(panelRoot, "tls", "cert.pem"), filepath.Join(panelRoot, "tls", "key.pem")
}

func LoadUploadedTLSAssets(certSource string, keySource string, panelRoot string) (TLSAssets, bool, error) {
	certSource = strings.TrimSpace(certSource)
	keySource = strings.TrimSpace(keySource)
	if (certSource == "") != (keySource == "") {
		return TLSAssets{}, false, fmt.Errorf("panel TLS certificate and key sources must be supplied together")
	}
	managedCert, managedKey := ManagedTLSPaths(panelRoot)
	if certSource == "" {
		certSource, keySource = managedCert, managedKey
	}
	cert, certErr := os.ReadFile(certSource)
	key, keyErr := os.ReadFile(keySource)
	if errors.Is(certErr, os.ErrNotExist) && errors.Is(keyErr, os.ErrNotExist) &&
		certSource != managedCert && keySource != managedKey {
		return LoadUploadedTLSAssets("", "", panelRoot)
	}
	if errors.Is(certErr, os.ErrNotExist) && errors.Is(keyErr, os.ErrNotExist) {
		return TLSAssets{}, false, nil
	}
	if certErr != nil || keyErr != nil {
		return TLSAssets{}, false, fmt.Errorf("panel TLS assets are incomplete: cert=%v key=%v", certErr, keyErr)
	}
	if !bytes.Contains(cert, []byte("BEGIN CERTIFICATE")) {
		return TLSAssets{}, false, fmt.Errorf("panel TLS certificate is not PEM encoded")
	}
	if !bytes.Contains(key, []byte("PRIVATE KEY")) {
		return TLSAssets{}, false, fmt.Errorf("panel TLS private key is not PEM encoded")
	}
	return TLSAssets{Certificate: cert, PrivateKey: key}, true, nil
}

func EffectiveTLSPaths(gatewaySpec spec.GatewaySpec, panelRoot string) (string, string) {
	switch gatewaySpec.Panel.TLSMode {
	case "certbot":
		root := filepath.Join("/etc/letsencrypt/live", gatewaySpec.Panel.Domain)
		return filepath.Join(root, "fullchain.pem"), filepath.Join(root, "privkey.pem")
	default:
		return ManagedTLSPaths(panelRoot)
	}
}

func EnsureTLS(ctx context.Context, gatewaySpec spec.GatewaySpec, panelRoot string, runner CommandRunner) (bool, error) {
	if !gatewaySpec.Panel.TLSEnabled {
		return false, nil
	}
	certPath, keyPath := EffectiveTLSPaths(gatewaySpec, panelRoot)
	if filesExist(certPath, keyPath) {
		return false, nil
	}
	switch gatewaySpec.Panel.TLSMode {
	case "uploaded":
		return false, fmt.Errorf("managed uploaded panel TLS assets are missing")
	case "self_signed":
		if err := os.MkdirAll(filepath.Dir(certPath), 0o700); err != nil {
			return false, err
		}
		tempCert, tempKey := certPath+".new", keyPath+".new"
		_ = os.Remove(tempCert)
		_ = os.Remove(tempKey)
		if err := runner.Run(ctx, "openssl", "req", "-x509", "-newkey", "rsa:3072", "-sha256", "-nodes",
			"-days", "365", "-subj", "/CN="+gatewaySpec.Panel.PublicHost, "-keyout", tempKey, "-out", tempCert); err != nil {
			_ = os.Remove(tempCert)
			_ = os.Remove(tempKey)
			return false, err
		}
		cert, err := os.ReadFile(tempCert)
		if err != nil {
			return false, err
		}
		key, err := os.ReadFile(tempKey)
		if err != nil {
			return false, err
		}
		if _, err := host.WriteFileAtomic(certPath, cert, 0o644); err != nil {
			return false, err
		}
		if _, err := host.WriteFileAtomic(keyPath, key, 0o600); err != nil {
			return false, err
		}
		_ = os.Remove(tempCert)
		_ = os.Remove(tempKey)
		return true, nil
	case "certbot":
		if strings.TrimSpace(gatewaySpec.Panel.Domain) == "" {
			return false, fmt.Errorf("certbot panel TLS requires a domain")
		}
		if err := runner.Run(ctx, "certbot", "certonly", "--standalone", "--non-interactive", "--agree-tos",
			"--register-unsafely-without-email", "-d", gatewaySpec.Panel.Domain); err != nil {
			return false, err
		}
		if !filesExist(certPath, keyPath) {
			return false, fmt.Errorf("certbot completed without expected certificate assets")
		}
		return true, nil
	default:
		return false, fmt.Errorf("unsupported panel TLS mode %q", gatewaySpec.Panel.TLSMode)
	}
}

func filesExist(paths ...string) bool {
	for _, path := range paths {
		info, err := os.Stat(path)
		if err != nil || !info.Mode().IsRegular() || info.Size() == 0 {
			return false
		}
	}
	return true
}
