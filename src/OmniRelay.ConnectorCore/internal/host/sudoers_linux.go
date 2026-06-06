//go:build linux

package host

import (
	"context"
	"fmt"
	"os/exec"
)

func (OSSudoersValidator) Validate(ctx context.Context, path string) error {
	output, err := exec.CommandContext(ctx, "visudo", "-cf", path).CombinedOutput()
	if err != nil {
		return fmt.Errorf("validate sudoers file %s: %w: %s", path, err, output)
	}
	return nil
}
