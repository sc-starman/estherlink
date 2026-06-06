package openvpn

import (
	"bufio"
	"context"
	"database/sql"
	"encoding/csv"
	"fmt"
	"io"
	"net"
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

func Enforce(ctx context.Context, db *sql.DB, protocolID string, managementPort int) (EnforceResult, error) {
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
		command := "kill " + identity + "\nquit\n"
		if client.ClientID != "" {
			command = "client-kill " + client.ClientID + "\nquit\n"
		}
		if _, err := managementCommand(ctx, managementPort, command); err != nil {
			return result, fmt.Errorf("disconnect OpenVPN identity %q: %w", identity, err)
		}
		result.Disconnected = append(result.Disconnected, identity)
	}
	return result, nil
}

func eligibleIdentities(db *sql.DB, protocolID string) (map[string]struct{}, error) {
	if db == nil {
		return nil, sql.ErrConnDone
	}
	rows, err := db.Query(`
SELECT COALESCE(NULLIF(c.auth_username,''),c.username)
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
		var identity string
		if err := rows.Scan(&identity); err != nil {
			return nil, err
		}
		if identity = strings.TrimSpace(identity); identity != "" {
			result[identity] = struct{}{}
		}
	}
	return result, rows.Err()
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
	header := map[string]int{"Common Name": 1}
	result := make([]managementClient, 0)
	scanner := bufio.NewScanner(strings.NewReader(content))
	for scanner.Scan() {
		line := scanner.Text()
		if !strings.HasPrefix(line, "HEADER,CLIENT_LIST,") && !strings.HasPrefix(line, "CLIENT_LIST,") {
			continue
		}
		record, err := csv.NewReader(strings.NewReader(line)).Read()
		if err != nil {
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

func field(record []string, index int) string {
	if index < 0 || index >= len(record) {
		return ""
	}
	return strings.TrimSpace(record[index])
}
