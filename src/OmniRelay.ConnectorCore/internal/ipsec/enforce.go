package ipsec

import (
	"database/sql"
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"strconv"
	"strings"
)

type ProcessKiller interface {
	Kill(pid int) error
}

type OSProcessKiller struct{}

func (OSProcessKiller) Kill(pid int) error {
	process, err := os.FindProcess(pid)
	if err != nil {
		return err
	}
	return process.Kill()
}

type EnforceOptions struct {
	Database     *sql.DB
	ProtocolID   string
	SessionsPath string
	ProcRoot     string
	Killer       ProcessKiller
}

type EnforceResult struct {
	ObservedSessions int `json:"observedSessions"`
	Disconnected     int `json:"disconnected"`
}

func EnforceSessions(options EnforceOptions) (EnforceResult, error) {
	result := EnforceResult{}
	if options.Database == nil {
		return result, sql.ErrConnDone
	}
	if options.Killer == nil {
		options.Killer = OSProcessKiller{}
	}
	if options.ProcRoot == "" {
		options.ProcRoot = "/proc"
	}
	disabled, enabledCount, err := loadEligibility(options.Database, options.ProtocolID)
	if err != nil {
		return result, err
	}
	sessions, err := loadSessions(options.SessionsPath)
	if err != nil {
		return result, err
	}
	for interfaceName, username := range sessions {
		result.ObservedSessions++
		_, explicitlyDisabled := disabled[username]
		if !explicitlyDisabled && !(username == "__unknown__" && enabledCount == 0) {
			continue
		}
		pids, err := findPPPProcesses(options.ProcRoot, interfaceName)
		if err != nil {
			return result, err
		}
		for _, pid := range pids {
			if err := options.Killer.Kill(pid); err != nil && !errors.Is(err, os.ErrProcessDone) {
				return result, fmt.Errorf("disconnect PPP process %d: %w", pid, err)
			}
			result.Disconnected++
		}
	}
	return result, nil
}

func loadEligibility(db *sql.DB, protocolID string) (map[string]struct{}, int, error) {
	rows, err := db.Query(`SELECT client_id,username,auth_username,enabled FROM clients WHERE protocol_id=?`, protocolID)
	if err != nil {
		return nil, 0, err
	}
	defer rows.Close()
	disabled := make(map[string]struct{})
	enabledCount := 0
	for rows.Next() {
		var clientID, username, authUsername string
		var enabled int
		if err := rows.Scan(&clientID, &username, &authUsername, &enabled); err != nil {
			return nil, 0, err
		}
		if enabled != 0 {
			enabledCount++
			continue
		}
		for _, identity := range []string{clientID, username, authUsername} {
			if identity = strings.TrimSpace(identity); identity != "" {
				disabled[identity] = struct{}{}
			}
		}
	}
	return disabled, enabledCount, rows.Err()
}

func findPPPProcesses(procRoot string, interfaceName string) ([]int, error) {
	entries, err := os.ReadDir(procRoot)
	if err != nil {
		return nil, err
	}
	result := make([]int, 0)
	for _, entry := range entries {
		pid, err := strconv.Atoi(entry.Name())
		if err != nil || !entry.IsDir() {
			continue
		}
		content, err := os.ReadFile(filepath.Join(procRoot, entry.Name(), "cmdline"))
		if errors.Is(err, os.ErrNotExist) || errors.Is(err, os.ErrPermission) {
			continue
		}
		if err != nil {
			return nil, err
		}
		args := strings.Split(strings.TrimRight(string(content), "\x00"), "\x00")
		if len(args) == 0 || !strings.Contains(filepath.Base(args[0]), "pppd") {
			continue
		}
		for _, arg := range args[1:] {
			if arg == interfaceName {
				result = append(result, pid)
				break
			}
		}
	}
	return result, nil
}
