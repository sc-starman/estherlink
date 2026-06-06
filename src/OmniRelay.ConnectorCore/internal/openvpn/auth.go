package openvpn

import (
	"crypto/subtle"
	"database/sql"
	"errors"
	"fmt"
	"os"
	"strings"
)

type AuthResult struct {
	Allowed    bool   `json:"allowed"`
	ClientID   string `json:"clientId,omitempty"`
	ReasonCode string `json:"reasonCode"`
}

func Authenticate(db *sql.DB, protocolID string, credentialsFile string) (AuthResult, error) {
	if db == nil {
		return AuthResult{}, sql.ErrConnDone
	}
	content, err := os.ReadFile(credentialsFile)
	if err != nil {
		return AuthResult{}, err
	}
	lines := strings.Split(strings.ReplaceAll(string(content), "\r\n", "\n"), "\n")
	if len(lines) < 2 {
		return AuthResult{ReasonCode: "invalid_credentials_file"}, nil
	}
	username := strings.Trim(strings.TrimSpace(lines[0]), `"`)
	password := strings.TrimSpace(lines[1])
	if username == "" || password == "" {
		return AuthResult{ReasonCode: "empty_credentials"}, nil
	}

	var clientID, storedSecret, disabledReason string
	var enabled int
	err = db.QueryRow(`
SELECT c.client_id, c.enabled, c.auth_secret, COALESCE(e.disabled_reason, '')
FROM clients c
LEFT JOIN enforcement_state e ON e.client_id=c.client_id
WHERE c.protocol_id=? AND c.auth_username=?
LIMIT 1`, protocolID, username).Scan(&clientID, &enabled, &storedSecret, &disabledReason)
	if errors.Is(err, sql.ErrNoRows) {
		return AuthResult{ReasonCode: "unknown_client"}, nil
	}
	if err != nil {
		return AuthResult{}, fmt.Errorf("query OpenVPN client: %w", err)
	}
	if enabled == 0 {
		return AuthResult{ClientID: clientID, ReasonCode: "client_disabled"}, nil
	}
	if disabledReason == "quota_exceeded" || disabledReason == "expired" || disabledReason == "manual_disabled" {
		return AuthResult{ClientID: clientID, ReasonCode: disabledReason}, nil
	}
	if subtle.ConstantTimeCompare([]byte(storedSecret), []byte(password)) != 1 {
		return AuthResult{ClientID: clientID, ReasonCode: "invalid_secret"}, nil
	}
	return AuthResult{Allowed: true, ClientID: clientID, ReasonCode: "ok"}, nil
}
