package host

import "context"

type Systemd interface {
	DaemonReload(context.Context) error
	EnableNow(context.Context, ...string) error
	Start(context.Context, ...string) error
	Restart(context.Context, ...string) error
	Stop(context.Context, ...string) error
	DisableNow(context.Context, ...string) error
	IsActive(context.Context, string) (string, error)
	Kill(ctx context.Context, signal string, unit string) error
}

func GatewayTarget(relayID string) string {
	return "omnirelay-gateway-" + relayID + ".target"
}
