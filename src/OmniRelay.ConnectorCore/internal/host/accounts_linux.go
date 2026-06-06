//go:build linux

package host

import (
	"context"
	"fmt"
	"os/exec"
)

func (OSAccountManager) EnsureSystemAccount(ctx context.Context, name string, home string) error {
	if err := exec.CommandContext(ctx, "getent", "group", name).Run(); err != nil {
		if output, createErr := exec.CommandContext(ctx, "groupadd", "--system", name).CombinedOutput(); createErr != nil {
			return fmt.Errorf("create system group %s: %w: %s", name, createErr, output)
		}
	}
	if err := exec.CommandContext(ctx, "id", "-u", name).Run(); err == nil {
		return nil
	}
	output, err := exec.CommandContext(ctx, "useradd", "--system", "--gid", name, "--home-dir", home, "--shell", "/usr/sbin/nologin", name).CombinedOutput()
	if err != nil {
		return fmt.Errorf("create system user %s: %w: %s", name, err, output)
	}
	return nil
}
