package panel

import (
	"context"
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
)

type CommandRunner interface {
	Run(context.Context, string, ...string) error
}

type OSCommandRunner struct{}

func (OSCommandRunner) Run(ctx context.Context, name string, args ...string) error {
	output, err := exec.CommandContext(ctx, name, args...).CombinedOutput()
	if err != nil {
		return fmt.Errorf("%s: %w: %s", name, err, output)
	}
	return nil
}

func EnableNginxSite(ctx context.Context, availablePath string, enabledPath string, runner CommandRunner) (bool, error) {
	info, err := os.Stat(availablePath)
	if err != nil || !info.Mode().IsRegular() {
		return false, fmt.Errorf("managed nginx site is unavailable: %w", err)
	}
	previousTarget := ""
	previousExists := false
	if _, err := os.Lstat(enabledPath); err == nil {
		previousExists = true
		previousTarget, err = os.Readlink(enabledPath)
		if err != nil {
			return false, fmt.Errorf("refusing to replace non-symlink nginx site %s", enabledPath)
		}
	} else if !os.IsNotExist(err) {
		return false, err
	}
	if previousExists && sameLinkTarget(enabledPath, previousTarget, availablePath) {
		if err := runner.Run(ctx, "nginx", "-t"); err != nil {
			return false, err
		}
		return false, runner.Run(ctx, "systemctl", "reload", "nginx.service")
	}
	if err := os.MkdirAll(filepath.Dir(enabledPath), 0o755); err != nil {
		return false, err
	}
	temp := enabledPath + ".new"
	_ = os.Remove(temp)
	if err := os.Symlink(availablePath, temp); err != nil {
		return false, err
	}
	if err := os.Rename(temp, enabledPath); err != nil {
		_ = os.Remove(temp)
		return false, err
	}
	rollback := func() {
		_ = os.Remove(enabledPath)
		if previousExists {
			_ = os.Symlink(previousTarget, enabledPath)
		}
	}
	if err := runner.Run(ctx, "nginx", "-t"); err != nil {
		rollback()
		return false, err
	}
	if err := runner.Run(ctx, "systemctl", "reload", "nginx.service"); err != nil {
		rollback()
		_ = runner.Run(ctx, "systemctl", "reload", "nginx.service")
		return false, err
	}
	return true, nil
}

func sameLinkTarget(linkPath string, target string, expected string) bool {
	if !filepath.IsAbs(target) {
		target = filepath.Join(filepath.Dir(linkPath), target)
	}
	targetAbs, targetErr := filepath.Abs(target)
	expectedAbs, expectedErr := filepath.Abs(expected)
	return targetErr == nil && expectedErr == nil && targetAbs == expectedAbs
}
