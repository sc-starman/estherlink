package ipsec

import (
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"sort"
	"strings"
	"time"

	"github.com/omnirelay/connector-core/internal/host"
)

type Session struct {
	Interface string
	Username  string
}

func RenderSessionHook(relayID string, action string, connectorBinary string) ([]byte, error) {
	if action != "up" && action != "down" {
		return nil, fmt.Errorf("invalid PPP session hook action %q", action)
	}
	if strings.TrimSpace(relayID) == "" || strings.ContainsAny(relayID, " \t\r\n") {
		return nil, fmt.Errorf("invalid relay identifier")
	}
	if strings.TrimSpace(connectorBinary) == "" || strings.ContainsAny(connectorBinary, " \t\r\n") {
		return nil, fmt.Errorf("invalid connector-core path")
	}
	username := `"${PEERNAME:-${PPPLOGNAME:-__unknown__}}"`
	if action == "down" {
		username = `__unknown__`
	}
	return []byte(fmt.Sprintf(`#!/bin/sh
exec %s ipsec session --relay-id %s --action %s --interface "${IFNAME:-${1:-}}" --username %s --json
`, connectorBinary, relayID, action, username)), nil
}

func UpdateSession(path string, action string, session Session) (bool, error) {
	session.Interface = strings.TrimSpace(session.Interface)
	session.Username = strings.TrimSpace(session.Username)
	if session.Interface == "" || strings.ContainsAny(session.Interface, " \t\r\n/") {
		return false, fmt.Errorf("invalid PPP interface")
	}
	if action != "up" && action != "down" {
		return false, fmt.Errorf("invalid PPP session action")
	}
	release, err := acquireSessionLock(path + ".lock")
	if err != nil {
		return false, err
	}
	defer release()
	sessions, err := loadSessions(path)
	if err != nil {
		return false, err
	}
	before := renderSessions(sessions)
	delete(sessions, session.Interface)
	if action == "up" {
		if session.Username == "" {
			session.Username = "__unknown__"
		}
		sessions[session.Interface] = session.Username
	}
	after := renderSessions(sessions)
	if string(before) == string(after) {
		return false, nil
	}
	_, err = host.WriteFileAtomic(path, after, 0o600)
	return err == nil, err
}

func loadSessions(path string) (map[string]string, error) {
	content, err := os.ReadFile(path)
	if errors.Is(err, os.ErrNotExist) {
		return map[string]string{}, nil
	}
	if err != nil {
		return nil, err
	}
	result := make(map[string]string)
	for _, line := range strings.Split(string(content), "\n") {
		parts := strings.SplitN(line, "\t", 2)
		if len(parts) == 2 && strings.TrimSpace(parts[0]) != "" {
			result[strings.TrimSpace(parts[0])] = strings.TrimSpace(parts[1])
		}
	}
	return result, nil
}

func renderSessions(sessions map[string]string) []byte {
	keys := make([]string, 0, len(sessions))
	for key := range sessions {
		keys = append(keys, key)
	}
	sort.Strings(keys)
	var builder strings.Builder
	for _, key := range keys {
		fmt.Fprintf(&builder, "%s\t%s\n", key, sessions[key])
	}
	return []byte(builder.String())
}

func acquireSessionLock(path string) (func(), error) {
	if err := os.MkdirAll(filepath.Dir(path), 0o755); err != nil {
		return nil, err
	}
	for attempt := 0; attempt < 20; attempt++ {
		file, err := os.OpenFile(path, os.O_CREATE|os.O_EXCL|os.O_WRONLY, 0o600)
		if err == nil {
			_ = file.Close()
			return func() { _ = os.Remove(path) }, nil
		}
		if !errors.Is(err, os.ErrExist) {
			return nil, err
		}
		time.Sleep(50 * time.Millisecond)
	}
	return nil, fmt.Errorf("PPP session state is busy")
}
