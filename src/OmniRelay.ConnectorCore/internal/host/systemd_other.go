//go:build !linux

package host

import (
	"context"
	"fmt"
)

type OSSystemd struct{}

func unsupportedSystemd() error {
	return fmt.Errorf("systemd gateway lifecycle is supported only on Linux")
}

func (OSSystemd) DaemonReload(context.Context) error          { return unsupportedSystemd() }
func (OSSystemd) EnableNow(context.Context, ...string) error  { return unsupportedSystemd() }
func (OSSystemd) Start(context.Context, ...string) error      { return unsupportedSystemd() }
func (OSSystemd) Restart(context.Context, ...string) error    { return unsupportedSystemd() }
func (OSSystemd) Stop(context.Context, ...string) error       { return unsupportedSystemd() }
func (OSSystemd) DisableNow(context.Context, ...string) error { return unsupportedSystemd() }
func (OSSystemd) IsActive(context.Context, string) (string, error) {
	return "unsupported", unsupportedSystemd()
}
func (OSSystemd) Kill(context.Context, string, string) error { return unsupportedSystemd() }
