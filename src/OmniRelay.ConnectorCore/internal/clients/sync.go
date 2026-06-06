package clients

import (
	"database/sql"
	"encoding/json"
	"fmt"
	"os"

	"github.com/omnirelay/connector-core/internal/host"
	"github.com/omnirelay/connector-core/internal/protocol"
)

type SyncOptions struct {
	ConfigPath string
	Database   *sql.DB
	ProtocolID string
	Validate   func([]byte) error
}

type SyncResult struct {
	Changed     bool `json:"changed"`
	ActiveUsers int  `json:"activeUsers"`
}

type client struct {
	ID     string
	Secret string
}

func Sync(options SyncOptions) (SyncResult, error) {
	definition, ok := protocol.Lookup(options.ProtocolID)
	if !ok {
		return SyncResult{}, fmt.Errorf("unsupported protocol %q", options.ProtocolID)
	}
	if !definition.PerClient {
		return SyncResult{}, nil
	}
	if options.Database == nil {
		return SyncResult{}, fmt.Errorf("accounting database is required")
	}
	activeClients, err := loadActiveClients(options.Database, options.ProtocolID)
	if err != nil {
		return SyncResult{}, err
	}
	raw, err := os.ReadFile(options.ConfigPath)
	if err != nil {
		return SyncResult{}, err
	}
	var config map[string]any
	if err := json.Unmarshal(raw, &config); err != nil {
		return SyncResult{}, fmt.Errorf("parse connector config: %w", err)
	}
	changed, err := replaceUsers(config, options.ProtocolID, activeClients)
	if err != nil {
		return SyncResult{}, err
	}
	result := SyncResult{Changed: changed, ActiveUsers: len(activeClients)}
	if !changed {
		return result, nil
	}
	candidate, err := json.MarshalIndent(config, "", "  ")
	if err != nil {
		return SyncResult{}, err
	}
	candidate = append(candidate, '\n')
	if options.Validate != nil {
		if err := options.Validate(candidate); err != nil {
			return SyncResult{}, fmt.Errorf("validate reconciled connector config: %w", err)
		}
	}
	written, err := host.WriteFileAtomic(options.ConfigPath, candidate, 0o600)
	if err != nil {
		return SyncResult{}, err
	}
	result.Changed = written
	return result, nil
}

func loadActiveClients(db *sql.DB, protocolID string) ([]client, error) {
	rows, err := db.Query(`
SELECT client_id, COALESCE(auth_secret,'')
FROM clients
WHERE protocol_id=? AND enabled=1
ORDER BY email COLLATE NOCASE, client_id`, protocolID)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	result := make([]client, 0)
	for rows.Next() {
		var item client
		if err := rows.Scan(&item.ID, &item.Secret); err != nil {
			return nil, err
		}
		result = append(result, item)
	}
	return result, rows.Err()
}

func replaceUsers(config map[string]any, protocolID string, activeClients []client) (bool, error) {
	rawInbounds, ok := config["inbounds"].([]any)
	if !ok {
		return false, fmt.Errorf("connector config has no inbounds array")
	}
	replaced := false
	for _, rawInbound := range rawInbounds {
		inbound, ok := rawInbound.(map[string]any)
		if !ok || !shouldReplaceInbound(protocolID, stringValue(inbound["type"])) {
			continue
		}
		users, err := buildUsers(protocolID, activeClients, existingFlow(inbound))
		if err != nil {
			return false, err
		}
		if equalJSON(inbound["users"], users) {
			continue
		}
		inbound["users"] = users
		replaced = true
	}
	if !hasManagedInbound(rawInbounds, protocolID) {
		return false, fmt.Errorf("connector config has no managed inbound for protocol %q", protocolID)
	}
	return replaced, nil
}

func shouldReplaceInbound(protocolID string, inboundType string) bool {
	switch protocolID {
	case "vless_plain_singbox", "vless_tls_singbox":
		return inboundType == "vless"
	case "trojan_singbox":
		return inboundType == "trojan"
	case "shadowsocks_singbox":
		return inboundType == "shadowsocks"
	case "shadowtls_v3_shadowsocks_singbox":
		return inboundType == "shadowtls" || inboundType == "shadowsocks"
	default:
		return false
	}
}

func hasManagedInbound(inbounds []any, protocolID string) bool {
	for _, rawInbound := range inbounds {
		if inbound, ok := rawInbound.(map[string]any); ok && shouldReplaceInbound(protocolID, stringValue(inbound["type"])) {
			return true
		}
	}
	return false
}

func buildUsers(protocolID string, activeClients []client, flow string) ([]any, error) {
	users := make([]any, 0, len(activeClients))
	for _, item := range activeClients {
		switch protocolID {
		case "vless_plain_singbox", "vless_tls_singbox":
			user := map[string]any{"name": item.ID, "uuid": item.ID}
			if flow != "" {
				user["flow"] = flow
			}
			users = append(users, user)
		default:
			if item.Secret == "" {
				return nil, fmt.Errorf("enabled client %q has no authentication secret", item.ID)
			}
			users = append(users, map[string]any{"name": item.ID, "password": item.Secret})
		}
	}
	return users, nil
}

func existingFlow(inbound map[string]any) string {
	users, _ := inbound["users"].([]any)
	for _, rawUser := range users {
		if user, ok := rawUser.(map[string]any); ok {
			if flow := stringValue(user["flow"]); flow != "" {
				return flow
			}
		}
	}
	return ""
}

func equalJSON(left any, right any) bool {
	leftJSON, leftErr := json.Marshal(left)
	rightJSON, rightErr := json.Marshal(right)
	return leftErr == nil && rightErr == nil && string(leftJSON) == string(rightJSON)
}

func stringValue(value any) string {
	text, _ := value.(string)
	return text
}
