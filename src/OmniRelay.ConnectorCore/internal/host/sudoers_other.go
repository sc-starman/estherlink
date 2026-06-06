//go:build !linux

package host

import (
	"context"
	"fmt"
)

func (OSSudoersValidator) Validate(context.Context, string) error {
	return fmt.Errorf("sudoers validation is supported only on Linux")
}
