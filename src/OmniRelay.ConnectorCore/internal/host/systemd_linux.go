//go:build linux

package host

import (
	"context"
	"fmt"
	"os/exec"
	"strings"
)

type OSSystemd struct{}

func (OSSystemd) DaemonReload(ctx context.Context) error {
	return runSystemctl(ctx, "daemon-reload")
}

func (OSSystemd) EnableNow(ctx context.Context, units ...string) error {
	return runSystemctl(ctx, append([]string{"enable", "--now"}, units...)...)
}

func (OSSystemd) Start(ctx context.Context, units ...string) error {
	return runSystemctl(ctx, append([]string{"start"}, units...)...)
}

func (OSSystemd) Restart(ctx context.Context, units ...string) error {
	return runSystemctl(ctx, append([]string{"restart"}, units...)...)
}

func (OSSystemd) Stop(ctx context.Context, units ...string) error {
	return runSystemctl(ctx, append([]string{"stop"}, units...)...)
}

func (OSSystemd) DisableNow(ctx context.Context, units ...string) error {
	return runSystemctl(ctx, append([]string{"disable", "--now"}, units...)...)
}

func (OSSystemd) Kill(ctx context.Context, signal string, unit string) error {
	return runSystemctl(ctx, "kill", "-s", signal, unit)
}

func (OSSystemd) IsActive(ctx context.Context, unit string) (string, error) {
	command := exec.CommandContext(ctx, "systemctl", "is-active", unit)
	output, err := command.CombinedOutput()
	state := strings.TrimSpace(string(output))
	if state == "" {
		state = "unknown"
	}
	if err != nil && state != "inactive" && state != "failed" && state != "unknown" {
		return state, fmt.Errorf("systemctl is-active %s: %w: %s", unit, err, state)
	}
	return state, nil
}

func runSystemctl(ctx context.Context, args ...string) error {
	command := exec.CommandContext(ctx, "systemctl", args...)
	output, err := command.CombinedOutput()
	if err != nil {
		return fmt.Errorf("systemctl %s: %w: %s", strings.Join(args, " "), err, strings.TrimSpace(string(output)))
	}
	return nil
}
