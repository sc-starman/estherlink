//go:build !linux

package openvpn

import "os"

func repairExportProfileAccess(path string) error {
	return os.Chmod(path, exportProfileMode)
}
