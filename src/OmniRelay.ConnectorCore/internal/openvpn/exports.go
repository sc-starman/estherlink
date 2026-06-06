package openvpn

import (
	"database/sql"
	"fmt"
	"os"
	"path/filepath"
	"sort"
	"strings"

	"github.com/omnirelay/connector-core/internal/host"
)

type ExportSyncResult struct {
	Changed     bool `json:"changed"`
	ActiveUsers int  `json:"activeUsers"`
}

type exportClient struct {
	ID       string
	Identity string
	Secret   string
}

func SyncExports(db *sql.DB, protocolID string, publicHost string, publicPort int, openVPNRoot string) (ExportSyncResult, error) {
	if db == nil {
		return ExportSyncResult{}, sql.ErrConnDone
	}
	publicHost = strings.TrimSpace(publicHost)
	if publicHost == "" || strings.ContainsAny(publicHost, " \t\r\n") {
		return ExportSyncResult{}, fmt.Errorf("OpenVPN public host is required and must not contain whitespace")
	}
	if publicPort < 1 || publicPort > 65535 {
		return ExportSyncResult{}, fmt.Errorf("OpenVPN public port must be between 1 and 65535")
	}
	assets, available, err := LoadSharedAssets(SharedAssetSources{}, openVPNRoot)
	if err != nil {
		return ExportSyncResult{}, err
	}
	if !available {
		return ExportSyncResult{}, fmt.Errorf("managed OpenVPN shared assets are missing")
	}
	clients, err := loadExportClients(db, protocolID)
	if err != nil {
		return ExportSyncResult{}, err
	}
	exportRoot := filepath.Join(openVPNRoot, "exports")
	desired := make(map[string]struct{}, len(clients))
	changed := false
	for _, client := range clients {
		if !safeIdentity.MatchString(client.ID) {
			return ExportSyncResult{}, fmt.Errorf("OpenVPN client id %q is unsafe for export filename use", client.ID)
		}
		path := filepath.Join(exportRoot, client.ID+".ovpn")
		desired[path] = struct{}{}
		written, err := host.WriteFileAtomic(path, RenderClientProfile(publicHost, publicPort, client.Identity, client.Secret, assets), 0o600)
		if err != nil {
			return ExportSyncResult{}, err
		}
		changed = changed || written
	}
	entries, err := os.ReadDir(exportRoot)
	if err != nil && !os.IsNotExist(err) {
		return ExportSyncResult{}, err
	}
	for _, entry := range entries {
		if entry.IsDir() || filepath.Ext(entry.Name()) != ".ovpn" {
			continue
		}
		path := filepath.Join(exportRoot, entry.Name())
		if _, keep := desired[path]; keep {
			continue
		}
		if err := os.Remove(path); err != nil && !os.IsNotExist(err) {
			return ExportSyncResult{}, err
		}
		changed = true
	}
	return ExportSyncResult{Changed: changed, ActiveUsers: len(clients)}, nil
}

func RenderClientProfile(publicHost string, publicPort int, identity string, secret string, assets SharedAssets) []byte {
	return []byte(fmt.Sprintf(`client
dev tun
proto tcp
remote %s %d
resolv-retry infinite
nobind
persist-key
persist-tun
auth-user-pass
auth-nocache
remote-cert-tls server
cipher AES-256-GCM
auth SHA256
verb 3
# OmniRelay Username: %s
# OmniRelay Password: %s
<ca>
%s</ca>
<cert>
%s</cert>
<key>
%s</key>
<tls-crypt>
%s</tls-crypt>
`, publicHost, publicPort, identity, secret,
		ensureTrailingNewline(assets.CACert), ensureTrailingNewline(assets.ClientCert),
		ensureTrailingNewline(assets.ClientKey), ensureTrailingNewline(assets.TLSCryptKey)))
}

func loadExportClients(db *sql.DB, protocolID string) ([]exportClient, error) {
	rows, err := db.Query(`
SELECT client_id,COALESCE(NULLIF(auth_username,''),username),auth_secret
FROM clients
WHERE protocol_id=? AND enabled=1
ORDER BY client_id`, protocolID)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	result := make([]exportClient, 0)
	for rows.Next() {
		var client exportClient
		if err := rows.Scan(&client.ID, &client.Identity, &client.Secret); err != nil {
			return nil, err
		}
		if strings.TrimSpace(client.Identity) == "" || client.Secret == "" {
			return nil, fmt.Errorf("enabled OpenVPN client %q is missing credentials", client.ID)
		}
		result = append(result, client)
	}
	sort.Slice(result, func(i, j int) bool { return result[i].ID < result[j].ID })
	return result, rows.Err()
}

func ensureTrailingNewline(value []byte) string {
	result := string(value)
	if !strings.HasSuffix(result, "\n") {
		result += "\n"
	}
	return result
}
