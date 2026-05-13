//go:build !linux && !windows

package main

import (
	"fmt"
	"os"
)

func main() {
	fmt.Fprintln(os.Stderr, "connector-core is only supported on Linux")
	os.Exit(1)
}
