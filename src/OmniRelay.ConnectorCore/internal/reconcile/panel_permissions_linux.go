//go:build linux

package reconcile

import (
	"errors"
	"os"
	"os/user"
	"strconv"
)

const panelReadableGroup = "omnigateway"

func repairPanelReadableFile(path string, mode os.FileMode) error {
	if os.Geteuid() == 0 {
		group, err := user.LookupGroup(panelReadableGroup)
		if err != nil {
			var unknown user.UnknownGroupError
			if !errors.As(err, &unknown) {
				return err
			}
		} else {
			gid, err := strconv.Atoi(group.Gid)
			if err != nil {
				return err
			}
			if err := os.Chown(path, -1, gid); err != nil {
				return err
			}
		}
	}
	return os.Chmod(path, mode)
}
