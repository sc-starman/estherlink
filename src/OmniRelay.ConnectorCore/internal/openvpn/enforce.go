package openvpn

import (
	"bufio"
	"context"
	"database/sql"
	"encoding/csv"
	"errors"
	"fmt"
	"io"
	"net"
	"os"
	"strconv"
	"strings"
	"time"
)

type EnforceResult struct {
	Connected    int      `json:"connected"`
	Disconnected []string `json:"disconnected,omitempty"`
}

type managementClient struct {
	CommonName string
	Username   string
	ClientID   string
}

func Enforce(ctx context.Context, db *sql.DB, protocolID string, managementPort int, statusPath string) (EnforceResult, error) {
	result := EnforceResult{}
	eligible, err := eligibleIdentities(db, protocolID)
	if err != nil {
		return result, err
	}
	response, err := managementCommand(ctx, managementPort, "status 3\nquit\n")
	if err != nil {
		return result, err
	}
	connected, err := parseManagementClients(response)
	if err != nil {
		return result, err
	}
	if len(connected) == 0 && strings.TrimSpace(statusPath) != "" {
		statusClients, statusErr := parseManagementClientsFromFile(statusPath)
		if statusErr != nil {
			return result, statusErr
		}
		if len(statusClients) > 0 {
			connected = statusClients
		}
	}
	result.Connected = len(connected)
	for _, client := range connected {
		identity := client.Username
		if identity == "" || identity == "UNDEF" {
			identity = client.CommonName
		}
		if identity == "" {
			continue
		}
		if _, ok := eligible[identity]; ok {
			continue
		}
		if err := disconnectManagementClient(ctx, managementPort, client); err != nil {
			return result, fmt.Errorf("disconnect OpenVPN identity %q: %w", identity, err)
		}
		result.Disconnected = append(result.Disconnected, identity)
	}
	return result, nil
}

func parseManagementClientsFromFile(path string) ([]managementClient, error) {
	content, err := os.ReadFile(path)
	if errors.Is(err, os.ErrNotExist) {
		return nil, nil
	}
	if err != nil {
		return nil, err
	}
	return parseManagementClients(string(content))
}

func eligibleIdentities(db *sql.DB, protocolID string) (map[string]struct{}, error) {
	if db == nil {
		return nil, sql.ErrConnDone
	}
	rows, err := db.Query(`
SELECT c.client_id, c.username, c.auth_username
FROM clients c
LEFT JOIN enforcement_state e ON e.client_id=c.client_id
WHERE c.protocol_id=? AND c.enabled=1
  AND COALESCE(e.disabled_reason,'') NOT IN ('quota_exceeded','expired','manual_disabled')`, protocolID)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	result := make(map[string]struct{})
	for rows.Next() {
		var clientID, username, authUsername string
		if err := rows.Scan(&clientID, &username, &authUsername); err != nil {
			return nil, err
		}
		for _, identity := range []string{clientID, username, authUsername, legacyClientCommonName(clientID)} {
			if identity = strings.TrimSpace(identity); identity != "" {
				result[identity] = struct{}{}
			}
		}
	}
	return result, rows.Err()
}

func legacyClientCommonName(clientID string) string {
	var builder strings.Builder
	for _, value := range clientID {
		switch {
		case value >= 'a' && value <= 'z',
			value >= 'A' && value <= 'Z',
			value >= '0' && value <= '9':
			builder.WriteRune(value)
		}
		if builder.Len() >= 40 {
			break
		}
	}
	if builder.Len() == 0 {
		return ""
	}
	return "ovpn-" + builder.String()
}

func disconnectManagementClient(ctx context.Context, port int, client managementClient) error {
	commands := make([]string, 0, 3)
	seen := map[string]struct{}{}
	add := func(command string) {
		command = strings.TrimSpace(command)
		if command == "" {
			return
		}
		if _, ok := seen[command]; ok {
			return
		}
		seen[command] = struct{}{}
		commands = append(commands, command)
	}
	if client.ClientID != "" {
		add("client-kill " + client.ClientID)
	}
	if client.CommonName != "" {
		add("kill " + client.CommonName)
	}
	if client.Username != "" && client.Username != "UNDEF" {
		add("kill " + client.Username)
	}
	if len(commands) == 0 {
		return nil
	}
	var lastErr error
	for attempt := 0; attempt < 3; attempt++ {
		for _, command := range commands {
			if _, err := managementCommand(ctx, port, command+"\nquit\n"); err != nil {
				lastErr = err
			}
		}
		if lastErr == nil {
			return nil
		}
		time.Sleep(200 * time.Millisecond)
	}
	return lastErr
}

func managementCommand(ctx context.Context, port int, command string) (string, error) {
	dialer := net.Dialer{Timeout: 3 * time.Second}
	connection, err := dialer.DialContext(ctx, "tcp", net.JoinHostPort("127.0.0.1", strconv.Itoa(port)))
	if err != nil {
		return "", err
	}
	defer connection.Close()
	_ = connection.SetDeadline(time.Now().Add(5 * time.Second))
	if _, err := io.WriteString(connection, command); err != nil {
		return "", err
	}
	content, err := io.ReadAll(connection)
	if err != nil {
		return "", err
	}
	return string(content), nil
}

func parseManagementClients(content string) ([]managementClient, error) {
	header := map[string]int{"Common Name": 1, "Username": 9, "Client ID": 10}
	result := make([]managementClient, 0)
	scanner := bufio.NewScanner(strings.NewReader(content))
	for scanner.Scan() {
		line := scanner.Text()
		if !strings.HasPrefix(line, "HEADER,CLIENT_LIST,") &&
			!strings.HasPrefix(line, "CLIENT_LIST,") &&
			!strings.HasPrefix(line, "CLIENT_LIST ") &&
			!strings.HasPrefix(line, "CLIENT_LIST\t") {
			continue
		}
		record, err := splitManagementRecord(line)
		if len(record) == 0 || err != nil {
			return nil, err
		}
		if len(record) < 2 {
			continue
		}
		if record[0] == "HEADER" {
			for index, name := range record[2:] {
				header[name] = index + 1
			}
			continue
		}
		result = append(result, managementClient{
			CommonName: field(record, header["Common Name"]),
			Username:   field(record, header["Username"]),
			ClientID:   field(record, header["Client ID"]),
		})
	}
	return result, scanner.Err()
}

func splitManagementRecord(line string) ([]string, error) {
	if strings.Contains(line, ",") {
		return csv.NewReader(strings.NewReader(line)).Read()
	}
	fields := strings.Fields(line)
	if len(fields) >= 11 && fields[0] == "CLIENT_LIST" {
		return []string{
			"CLIENT_LIST",
			fields[1],
			"",
			"",
			"",
			"",
			"",
			"",
			"",
			fields[9],
			fields[10],
		}, nil
	}
	return fields, nil
}

func field(record []string, index int) string {
	if index < 0 || index >= len(record) {
		return ""
	}
	return strings.TrimSpace(record[index])
}
