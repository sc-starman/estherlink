//go:build windows

package host

import "os"

func fileModeMatches(current os.FileMode, desired os.FileMode) bool {
	// Windows does not expose Unix permission bits faithfully. Treat equal
	// content as unchanged so reconciliation remains idempotent.
	return true
}
