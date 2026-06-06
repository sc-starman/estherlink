//go:build !windows

package host

import "os"

func fileModeMatches(current os.FileMode, desired os.FileMode) bool {
	return current.Perm() == desired.Perm()
}
