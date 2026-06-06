package host

import "context"

type AccountManager interface {
	EnsureSystemAccount(context.Context, string, string) error
}

type OSAccountManager struct{}
