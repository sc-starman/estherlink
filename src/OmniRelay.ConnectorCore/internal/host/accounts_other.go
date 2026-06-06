//go:build !linux

package host

import (
	"context"
	"fmt"
)

func (OSAccountManager) EnsureSystemAccount(context.Context, string, string) error {
	return fmt.Errorf("system account reconciliation is supported only on Linux")
}
