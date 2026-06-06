package openvpn

import (
	"database/sql"
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"regexp"
	"sort"

	"github.com/omnirelay/connector-core/internal/host"
)

var safeIdentity = regexp.MustCompile(`^[A-Za-z0-9_.@-]{1,128}$`)

type ClientSyncResult struct {
	Changed     bool `json:"changed"`
	ActiveUsers int  `json:"activeUsers"`
}

func SyncClients(db *sql.DB, protocolID string, network string, openVPNRoot string) (ClientSyncResult, error) {
	if db == nil {
		return ClientSyncResult{}, sql.ErrConnDone
	}
	identities, err := loadActiveIdentities(db, protocolID)
	if err != nil {
		return ClientSyncResult{}, err
	}
	for _, identity := range identities {
		if !safeIdentity.MatchString(identity) {
			return ClientSyncResult{}, fmt.Errorf("OpenVPN client identity %q is unsafe for CCD use", identity)
		}
	}
	mapPath := filepath.Join(openVPNRoot, "client-ip-map.json")
	existing, err := loadAddressMap(mapPath)
	if err != nil {
		return ClientSyncResult{}, err
	}
	assignments, err := AssignAddresses(network, identities, existing)
	if err != nil {
		return ClientSyncResult{}, err
	}
	ccd, err := RenderCCD(network, assignments)
	if err != nil {
		return ClientSyncResult{}, err
	}
	changed := false
	ccdRoot := filepath.Join(openVPNRoot, "ccd")
	for identity, content := range ccd {
		written, err := host.WriteFileAtomic(filepath.Join(ccdRoot, identity), content, 0o600)
		if err != nil {
			return ClientSyncResult{}, err
		}
		changed = changed || written
	}
	for identity := range existing {
		if _, keep := assignments[identity]; keep {
			continue
		}
		if !safeIdentity.MatchString(identity) {
			return ClientSyncResult{}, fmt.Errorf("existing OpenVPN client identity %q is unsafe for CCD cleanup", identity)
		}
		if err := os.Remove(filepath.Join(ccdRoot, identity)); err != nil && !os.IsNotExist(err) {
			return ClientSyncResult{}, err
		}
		changed = true
	}
	content, err := json.MarshalIndent(assignments, "", "  ")
	if err != nil {
		return ClientSyncResult{}, err
	}
	written, err := host.WriteFileAtomic(mapPath, append(content, '\n'), 0o600)
	if err != nil {
		return ClientSyncResult{}, err
	}
	return ClientSyncResult{Changed: changed || written, ActiveUsers: len(identities)}, nil
}

func loadActiveIdentities(db *sql.DB, protocolID string) ([]string, error) {
	rows, err := db.Query(`
SELECT COALESCE(NULLIF(auth_username,''),username)
FROM clients
WHERE protocol_id=? AND enabled=1
ORDER BY email COLLATE NOCASE, client_id`, protocolID)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	result := make([]string, 0)
	for rows.Next() {
		var identity string
		if err := rows.Scan(&identity); err != nil {
			return nil, err
		}
		result = append(result, identity)
	}
	sort.Strings(result)
	return result, rows.Err()
}

func loadAddressMap(path string) (map[string]string, error) {
	content, err := os.ReadFile(path)
	if os.IsNotExist(err) {
		return map[string]string{}, nil
	}
	if err != nil {
		return nil, err
	}
	result := make(map[string]string)
	if err := json.Unmarshal(content, &result); err != nil {
		return nil, fmt.Errorf("parse OpenVPN client IP map: %w", err)
	}
	return result, nil
}
