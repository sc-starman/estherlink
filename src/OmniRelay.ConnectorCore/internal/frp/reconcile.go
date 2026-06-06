package frp

import (
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"sort"
	"strings"

	"github.com/omnirelay/connector-core/internal/host"
	"github.com/omnirelay/connector-core/internal/spec"
)

const ServiceName = "omnirelay-frps.service"

type ReconcileOptions struct {
	ConfigRoot      string
	SystemdRoot     string
	ConnectorBinary string
	StateRoot       string
}

type ReconcileResult struct {
	Active       bool     `json:"active"`
	Changed      bool     `json:"changed"`
	RelayIDs     []string `json:"relayIds,omitempty"`
	ConfigPath   string   `json:"configPath,omitempty"`
	ServicePath  string   `json:"servicePath,omitempty"`
	RemovedPaths []string `json:"removedPaths,omitempty"`
}

func Reconcile(options ReconcileOptions) (ReconcileResult, error) {
	options = defaults(options)
	specs, err := loadRemoteSpecs(options.ConfigRoot)
	if err != nil {
		return ReconcileResult{}, err
	}
	configPath := filepath.Join(options.ConfigRoot, "frps", "frps.toml")
	servicePath := filepath.Join(options.SystemdRoot, ServiceName)
	if len(specs) == 0 {
		removed := make([]string, 0, 2)
		for _, path := range []string{configPath, servicePath} {
			if err := os.Remove(path); err == nil {
				removed = append(removed, path)
			} else if !errors.Is(err, os.ErrNotExist) {
				return ReconcileResult{}, err
			}
		}
		return ReconcileResult{Changed: len(removed) > 0, RemovedPaths: removed}, nil
	}

	first := specs[0]
	relayIDs := make([]string, 0, len(specs))
	for _, gatewaySpec := range specs {
		relayIDs = append(relayIDs, gatewaySpec.RelayID)
		if gatewaySpec.Tunnel.FRPServerPort != first.Tunnel.FRPServerPort ||
			gatewaySpec.Tunnel.FRPAuthToken != first.Tunnel.FRPAuthToken {
			return ReconcileResult{}, fmt.Errorf(
				"remote relay %s uses an FRPS profile that conflicts with relay %s",
				gatewaySpec.RelayID, first.RelayID,
			)
		}
	}
	sort.Strings(relayIDs)
	config := renderConfig(first.Tunnel.FRPServerPort, first.Tunnel.FRPAuthToken)
	service := renderService(options.ConnectorBinary, configPath, filepath.Join(options.StateRoot, "frps_state.json"))
	configChanged, err := host.WriteFileAtomic(configPath, config, 0o600)
	if err != nil {
		return ReconcileResult{}, err
	}
	serviceChanged, err := host.WriteFileAtomic(servicePath, service, 0o644)
	if err != nil {
		return ReconcileResult{}, err
	}
	return ReconcileResult{
		Active: true, Changed: configChanged || serviceChanged, RelayIDs: relayIDs,
		ConfigPath: configPath, ServicePath: servicePath,
	}, nil
}

func loadRemoteSpecs(configRoot string) ([]spec.GatewaySpec, error) {
	pattern := filepath.Join(configRoot, "relays", "*", "gateway", "spec.json")
	paths, err := filepath.Glob(pattern)
	if err != nil {
		return nil, err
	}
	sort.Strings(paths)
	result := make([]spec.GatewaySpec, 0, len(paths))
	for _, path := range paths {
		gatewaySpec, err := spec.LoadFile(path)
		if err != nil {
			return nil, fmt.Errorf("load persisted gateway spec %s: %w", path, err)
		}
		if gatewaySpec.Gateway.Type == "remote" {
			result = append(result, gatewaySpec)
		}
	}
	return result, nil
}

func renderConfig(port int, token string) []byte {
	return []byte(fmt.Sprintf(`bindPort = %d
auth.method = "token"
auth.token = "%s"
transport.maxPoolCount = 20
log.to = "console"
log.level = "warn"
`, port, token))
}

func renderService(binary string, configPath string, statePath string) []byte {
	return []byte(fmt.Sprintf(`[Unit]
Description=OmniRelay embedded FRPS server
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
ExecStart=%s frps run --config %s --state-file %s --strict-config=true
Restart=always
RestartSec=3
NoNewPrivileges=true
UMask=0077

[Install]
WantedBy=multi-user.target
`, binary, configPath, statePath))
}

func defaults(options ReconcileOptions) ReconcileOptions {
	if strings.TrimSpace(options.ConfigRoot) == "" {
		options.ConfigRoot = "/etc/omnirelay"
	}
	if strings.TrimSpace(options.SystemdRoot) == "" {
		options.SystemdRoot = "/etc/systemd/system"
	}
	if strings.TrimSpace(options.ConnectorBinary) == "" {
		options.ConnectorBinary = "/usr/local/bin/connector-core"
	}
	if strings.TrimSpace(options.StateRoot) == "" {
		options.StateRoot = "/var/lib/omnirelay/frps"
	}
	return options
}
