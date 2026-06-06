//go:build !linux

package host

import "fmt"

func VerifySupportedPlatform() error {
	return fmt.Errorf("gateway reconciliation is supported only on Ubuntu/Debian Linux")
}
