//go:build linux

package host

import (
	"context"
	"fmt"
	"os"
	"os/exec"
	"strings"
)

type OSPackageManager struct{}

func (OSPackageManager) Ensure(ctx context.Context, packages []string) error {
	packages, err := ValidatePackageNames(packages)
	if err != nil {
		return err
	}
	if len(packages) == 0 {
		return nil
	}
	common := []string{"-o", "Acquire::Retries=4", "-o", "Acquire::http::Timeout=30", "-o", "Acquire::https::Timeout=30"}
	if err := runApt(ctx, append(common, "update")...); err != nil {
		return err
	}
	args := append(common, "install", "-y", "--no-install-recommends")
	args = append(args, packages...)
	return runApt(ctx, args...)
}

func runApt(ctx context.Context, args ...string) error {
	command := exec.CommandContext(ctx, "apt-get", args...)
	command.Env = append(os.Environ(), "DEBIAN_FRONTEND=noninteractive")
	output, err := command.CombinedOutput()
	if err != nil {
		return fmt.Errorf("apt-get %s: %w: %s", strings.Join(args, " "), err, strings.TrimSpace(string(output)))
	}
	return nil
}
