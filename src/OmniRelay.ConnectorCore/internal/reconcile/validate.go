package reconcile

import (
	"fmt"
	"os"
	"path/filepath"

	"github.com/omnirelay/connector-core/internal/spec"
)

func ValidateRuntimeAssets(gatewaySpec spec.GatewaySpec, configRoot string) error {
	required := make([]string, 0)
	if gatewaySpec.SingBox.TLS.Enabled {
		required = append(required, gatewaySpec.SingBox.TLS.CertFile, gatewaySpec.SingBox.TLS.KeyFile)
	}
	if gatewaySpec.Gateway.Protocol == "openvpn_tcp_singbox" {
		root := filepath.Join(configRoot, "relays", gatewaySpec.RelayID, "gateway", "openvpn")
		required = append(required,
			filepath.Join(root, "ca.crt"),
			filepath.Join(root, "client-shared.crt"),
			filepath.Join(root, "client-shared.key"),
			filepath.Join(root, "ta.key"),
		)
	}
	for _, path := range required {
		if path == "" {
			return fmt.Errorf("required runtime asset path is empty")
		}
		info, err := os.Stat(path)
		if err != nil {
			return fmt.Errorf("required runtime asset %s: %w", path, err)
		}
		if !info.Mode().IsRegular() || info.Size() == 0 {
			return fmt.Errorf("required runtime asset %s is not a non-empty regular file", path)
		}
	}
	return nil
}
