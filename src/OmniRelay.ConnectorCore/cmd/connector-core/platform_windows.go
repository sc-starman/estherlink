//go:build windows

package main

import (
	"fmt"
	"os"
	"path/filepath"
	"time"
)

const (
	defaultConfigPath    = "C:\\ProgramData\\OmniRelay\\local-gateway\\singbox.config.json"
	defaultMetadataPath  = "C:\\ProgramData\\OmniRelay\\local-gateway\\connector.metadata.json"
	defaultAccountingDB  = "C:\\ProgramData\\OmniRelay\\local-gateway\\accounting.db"
	defaultStatePath     = "C:\\ProgramData\\OmniRelay\\local-gateway\\connector_core_state.json"
	defaultLockPath      = "C:\\ProgramData\\OmniRelay\\local-gateway\\connector_core.lock"
	defaultPanelGroup    = "omnigateway"
	defaultCycleInterval = 30
)

func withFileLock(_ string, fn func() error) error {
	return fn()
}

func repairAccountingPermissions(dbPath string, _ string) error {
	return os.MkdirAll(filepath.Dir(dbPath), 0o755)
}

func repairPanelAppPermissions(appRoot string, _ string, _ string) error {
	return os.MkdirAll(appRoot, 0o755)
}

func setSystemClock(time.Time) error {
	return fmt.Errorf("setting the system clock is supported only on Linux gateways")
}
