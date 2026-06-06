package ipsec

import (
	"database/sql"
	"fmt"

	"github.com/omnirelay/connector-core/internal/host"
)

type ClientSyncResult struct {
	Changed     bool `json:"changed"`
	ActiveUsers int  `json:"activeUsers"`
}

func SyncClients(db *sql.DB, protocolID string, outputPath string) (ClientSyncResult, error) {
	if db == nil {
		return ClientSyncResult{}, sql.ErrConnDone
	}
	rows, err := db.Query(`
SELECT COALESCE(NULLIF(auth_username,''),username), auth_secret
FROM clients
WHERE protocol_id=? AND enabled=1
ORDER BY email COLLATE NOCASE, client_id`, protocolID)
	if err != nil {
		return ClientSyncResult{}, err
	}
	defer rows.Close()
	clients := make([]Client, 0)
	for rows.Next() {
		var client Client
		if err := rows.Scan(&client.Username, &client.Secret); err != nil {
			return ClientSyncResult{}, err
		}
		if client.Username == "" || client.Secret == "" {
			return ClientSyncResult{}, fmt.Errorf("enabled IPsec/L2TP client has missing credentials")
		}
		client.Enabled = true
		clients = append(clients, client)
	}
	if err := rows.Err(); err != nil {
		return ClientSyncResult{}, err
	}
	written, err := host.WriteFileAtomic(outputPath, RenderChapSecrets(clients), 0o600)
	if err != nil {
		return ClientSyncResult{}, err
	}
	return ClientSyncResult{Changed: written, ActiveUsers: len(clients)}, nil
}
