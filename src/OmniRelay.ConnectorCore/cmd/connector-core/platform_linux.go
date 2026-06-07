//go:build linux

package main

import (
	"os"
	"os/user"
	"path/filepath"
	"strconv"
	"syscall"
	"time"
)

const (
	defaultConfigPath    = "/etc/omnirelay/gateway/connector/config.json"
	defaultMetadataPath  = "/etc/omnirelay/gateway/metadata.json"
	defaultAccountingDB  = "/etc/omnirelay/gateway/connector/accounting.db"
	defaultStatePath     = "/etc/omnirelay/gateway/connector/connector_core_state.json"
	defaultLockPath      = "/run/omnirelay-accounting-sync.lock"
	defaultPanelGroup    = "omnigateway"
	defaultCycleInterval = 30
)

func withFileLock(lockPath string, fn func() error) error {
	if err := os.MkdirAll(filepath.Dir(lockPath), 0o755); err != nil {
		return err
	}
	handle, err := os.OpenFile(lockPath, os.O_CREATE|os.O_RDWR, 0o640)
	if err != nil {
		return err
	}
	defer handle.Close()
	if err := syscall.Flock(int(handle.Fd()), syscall.LOCK_EX); err != nil {
		return err
	}
	defer func() { _ = syscall.Flock(int(handle.Fd()), syscall.LOCK_UN) }()
	return fn()
}

func repairAccountingPermissions(dbPath string, panelGroup string) error {
	info, err := user.LookupGroup(panelGroup)
	if err != nil {
		return nil
	}
	gid, err := strconv.Atoi(info.Gid)
	if err != nil {
		return err
	}
	dbDir := filepath.Dir(dbPath)
	if err := os.MkdirAll(dbDir, 0o2770); err != nil {
		return err
	}
	if err := os.Chown(dbDir, 0, gid); err != nil {
		return err
	}
	if err := os.Chmod(dbDir, 0o2770); err != nil {
		return err
	}
	for _, target := range []string{dbPath, dbPath + "-wal", dbPath + "-shm"} {
		if _, err := os.Stat(target); err != nil {
			continue
		}
		if err := os.Chown(target, 0, gid); err != nil {
			return err
		}
		if err := os.Chmod(target, 0o660); err != nil {
			return err
		}
	}
	return nil
}

func repairPanelAppPermissions(appRoot string, panelUser string, panelGroup string) error {
	userInfo, err := user.Lookup(panelUser)
	if err != nil {
		return nil
	}
	groupInfo, err := user.LookupGroup(panelGroup)
	if err != nil {
		return nil
	}
	uid, err := strconv.Atoi(userInfo.Uid)
	if err != nil {
		return err
	}
	gid, err := strconv.Atoi(groupInfo.Gid)
	if err != nil {
		return err
	}
	for _, dir := range panelAncestorDirs(appRoot) {
		if err := os.MkdirAll(dir, 0o755); err != nil {
			return err
		}
		if err := os.Chmod(dir, 0o755); err != nil {
			return err
		}
	}
	return filepath.WalkDir(appRoot, func(path string, entry os.DirEntry, walkErr error) error {
		if walkErr != nil {
			return walkErr
		}
		if err := os.Lchown(path, uid, gid); err != nil {
			return err
		}
		if entry.Type()&os.ModeSymlink != 0 {
			return nil
		}
		info, err := entry.Info()
		if err != nil {
			return err
		}
		if entry.IsDir() {
			return os.Chmod(path, 0o755)
		}
		mode := os.FileMode(0o644)
		if info.Mode().Perm()&0o111 != 0 {
			mode = 0o755
		}
		return os.Chmod(path, mode)
	})
}

func panelAncestorDirs(appRoot string) []string {
	clean := filepath.Clean(appRoot)
	return []string{
		filepath.Dir(filepath.Dir(filepath.Dir(clean))),
		filepath.Dir(filepath.Dir(clean)),
		filepath.Dir(clean),
		clean,
	}
}

func setSystemClock(value time.Time) error {
	timeval := syscall.NsecToTimeval(value.UnixNano())
	return syscall.Settimeofday(&timeval)
}
