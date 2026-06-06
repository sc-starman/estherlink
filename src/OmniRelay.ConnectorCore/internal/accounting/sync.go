package accounting

import (
	"context"
	"database/sql"
	"time"
)

type SyncInput struct {
	Database          *sql.DB
	ProtocolID        string
	Now               time.Time
	UsageDeltas       map[string]int64
	ActiveConnections map[string]int
}

type SyncResult struct {
	UpdatedClients int             `json:"updatedClients"`
	EnableChanges  map[string]bool `json:"enableChanges,omitempty"`
	DisableClients []string        `json:"disableClients,omitempty"`
}

func Sync(input SyncInput) (SyncResult, error) {
	result := SyncResult{EnableChanges: make(map[string]bool)}
	now := input.Now.UTC()
	if input.Database == nil {
		return result, sql.ErrConnDone
	}
	if err := Migrate(input.Database); err != nil {
		return result, err
	}
	tx, err := input.Database.BeginTx(context.Background(), nil)
	if err != nil {
		return result, err
	}
	defer func() { _ = tx.Rollback() }()

	for clientID, bytes := range input.UsageDeltas {
		if bytes <= 0 {
			continue
		}
		if _, err := tx.Exec(
			`INSERT INTO usage_totals(client_id,used_bytes,updated_at)
			 VALUES(?,?,?)
			 ON CONFLICT(client_id) DO UPDATE SET
			   used_bytes=usage_totals.used_bytes + excluded.used_bytes,
			   updated_at=excluded.updated_at`,
			clientID, bytes, now.Unix(),
		); err != nil {
			return result, err
		}
	}
	if err := updateConnections(tx, input.ProtocolID, input.ActiveConnections, now); err != nil {
		return result, err
	}
	rows, err := tx.Query(
		`SELECT c.client_id, c.enabled, c.total_bytes_limit, c.expiry_unix_ms,
		        COALESCE(u.used_bytes, 0), COALESCE(e.disabled_reason, '')
		   FROM clients c
		   LEFT JOIN usage_totals u ON u.client_id = c.client_id
		   LEFT JOIN enforcement_state e ON e.client_id = c.client_id
		  WHERE c.protocol_id = ?`,
		input.ProtocolID,
	)
	if err != nil {
		return result, err
	}
	defer rows.Close()

	nowMS := now.UnixMilli()
	for rows.Next() {
		var clientID, disabledReason string
		var enabled int
		var totalBytes, expiryUnixMS, usedBytes int64
		if err := rows.Scan(&clientID, &enabled, &totalBytes, &expiryUnixMS, &usedBytes, &disabledReason); err != nil {
			return result, err
		}
		reason := eligibilityReason(nowMS, totalBytes, expiryUnixMS, usedBytes)
		if reason != "" {
			shouldDisable := enabled != 0
			if shouldDisable {
				if _, err := tx.Exec(`UPDATE clients SET enabled=0, updated_at=? WHERE client_id=?`, now.Unix(), clientID); err != nil {
					return result, err
				}
				result.EnableChanges[clientID] = false
				result.UpdatedClients++
			}
			if shouldDisable || disabledReason != reason {
				result.DisableClients = append(result.DisableClients, clientID)
			}
			if _, err := tx.Exec(
				`INSERT OR REPLACE INTO enforcement_state(client_id,disabled_reason,disabled_at,updated_at)
				 VALUES(?,?,?,?)`, clientID, reason, now.Unix(), now.Unix(),
			); err != nil {
				return result, err
			}
			continue
		}
		if enabled == 0 && isManagedDisabledReason(disabledReason) {
			if _, err := tx.Exec(`UPDATE clients SET enabled=1, updated_at=? WHERE client_id=?`, now.Unix(), clientID); err != nil {
				return result, err
			}
			result.EnableChanges[clientID] = true
			result.UpdatedClients++
		}
		if _, err := tx.Exec(
			`INSERT OR REPLACE INTO enforcement_state(client_id,disabled_reason,disabled_at,updated_at)
			 VALUES(?,?,?,?)`, clientID, "", 0, now.Unix(),
		); err != nil {
			return result, err
		}
	}
	if err := rows.Err(); err != nil {
		return result, err
	}
	if err := tx.Commit(); err != nil {
		return result, err
	}
	return result, nil
}

func updateConnections(tx *sql.Tx, protocolID string, active map[string]int, now time.Time) error {
	rows, err := tx.Query(`SELECT client_id FROM clients WHERE protocol_id=?`, protocolID)
	if err != nil {
		return err
	}
	defer rows.Close()
	for rows.Next() {
		var clientID string
		if err := rows.Scan(&clientID); err != nil {
			return err
		}
		if _, err := tx.Exec(
			`INSERT OR REPLACE INTO connection_counters(client_id,active_connections,last_seen_at)
			 VALUES(?,?,?)`, clientID, active[clientID], now.Unix(),
		); err != nil {
			return err
		}
	}
	return rows.Err()
}

func eligibilityReason(nowMS int64, totalBytes int64, expiryUnixMS int64, usedBytes int64) string {
	if expiryUnixMS > 0 && nowMS >= expiryUnixMS {
		return "expired"
	}
	if totalBytes > 0 && usedBytes >= totalBytes {
		return "quota_exceeded"
	}
	return ""
}

func isManagedDisabledReason(reason string) bool {
	return reason == "expired" || reason == "quota_exceeded"
}
