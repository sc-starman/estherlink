package host

import "context"

type SudoersValidator interface {
	Validate(context.Context, string) error
}

type OSSudoersValidator struct{}
