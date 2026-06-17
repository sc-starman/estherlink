//go:build !linux

package reconcile

import "os"

func repairPanelReadableFile(path string, mode os.FileMode) error {
	return os.Chmod(path, mode)
}
