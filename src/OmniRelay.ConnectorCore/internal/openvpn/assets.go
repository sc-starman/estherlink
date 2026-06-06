package openvpn

import (
	"bytes"
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"strings"
)

type SharedAssetSources struct {
	CACert      string
	ClientCert  string
	ClientKey   string
	TLSCryptKey string
}

type SharedAssets struct {
	CACert      []byte
	ClientCert  []byte
	ClientKey   []byte
	TLSCryptKey []byte
}

func LoadSharedAssets(sources SharedAssetSources, managedRoot string) (SharedAssets, bool, error) {
	sourcePaths := []string{sources.CACert, sources.ClientCert, sources.ClientKey, sources.TLSCryptKey}
	supplied := 0
	for _, path := range sourcePaths {
		if strings.TrimSpace(path) != "" {
			supplied++
		}
	}
	if supplied != 0 && supplied != len(sourcePaths) {
		return SharedAssets{}, false, fmt.Errorf("all OpenVPN shared asset sources are required when any source is set")
	}
	if supplied == len(sourcePaths) {
		missing := 0
		for _, path := range sourcePaths {
			if _, err := os.Stat(path); errors.Is(err, os.ErrNotExist) {
				missing++
			} else if err != nil {
				return SharedAssets{}, false, err
			}
		}
		if missing == len(sourcePaths) {
			return LoadSharedAssets(SharedAssetSources{}, managedRoot)
		}
		if missing != 0 {
			return SharedAssets{}, false, fmt.Errorf("OpenVPN shared asset sources are incomplete")
		}
	}

	paths := sourcePaths
	if supplied == 0 {
		paths = []string{
			filepath.Join(managedRoot, "ca.crt"),
			filepath.Join(managedRoot, "client-shared.crt"),
			filepath.Join(managedRoot, "client-shared.key"),
			filepath.Join(managedRoot, "ta.key"),
		}
	}
	content := make([][]byte, len(paths))
	missing := 0
	for index, path := range paths {
		value, err := os.ReadFile(path)
		if errors.Is(err, os.ErrNotExist) && supplied == 0 {
			missing++
			continue
		}
		if err != nil {
			return SharedAssets{}, false, fmt.Errorf("read OpenVPN shared asset %s: %w", path, err)
		}
		if len(bytes.TrimSpace(value)) == 0 {
			return SharedAssets{}, false, fmt.Errorf("OpenVPN shared asset %s is empty", path)
		}
		content[index] = value
	}
	if missing == len(paths) {
		return SharedAssets{}, false, nil
	}
	if missing > 0 {
		return SharedAssets{}, false, fmt.Errorf("managed OpenVPN shared assets are incomplete")
	}
	if !bytes.Contains(content[0], []byte("BEGIN CERTIFICATE")) {
		return SharedAssets{}, false, fmt.Errorf("OpenVPN shared CA asset is not a PEM certificate")
	}
	if !bytes.Contains(content[1], []byte("BEGIN CERTIFICATE")) {
		return SharedAssets{}, false, fmt.Errorf("OpenVPN shared client asset is not a PEM certificate")
	}
	if !bytes.Contains(content[2], []byte("PRIVATE KEY")) {
		return SharedAssets{}, false, fmt.Errorf("OpenVPN shared client key asset is not a PEM private key")
	}
	if !bytes.Contains(content[3], []byte("BEGIN OpenVPN Static key V1")) {
		return SharedAssets{}, false, fmt.Errorf("OpenVPN shared tls-crypt asset is not an OpenVPN static key")
	}
	return SharedAssets{
		CACert: content[0], ClientCert: content[1], ClientKey: content[2], TLSCryptKey: content[3],
	}, true, nil
}
