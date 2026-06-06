//go:build !linux

package host

import (
	"context"
	"fmt"
)

type OSPackageManager struct{}

func (OSPackageManager) Ensure(context.Context, []string) error {
	return fmt.Errorf("package reconciliation is supported only on Ubuntu/Debian Linux")
}
