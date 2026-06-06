package accounting

import (
	"bufio"
	"database/sql"
	"encoding/csv"
	"errors"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"strconv"
	"strings"
	"time"
)

type CollectInput struct {
	Database          *sql.DB
	ProtocolID        string
	Source            string
	OpenVPNStatusPath string
	PPPSessionsPath   string
	SysClassNetRoot   string
	Now               time.Time
}

type CollectResult struct {
	UsageDeltas        map[string]int64 `json:"usageDeltas,omitempty"`
	ActiveConnections  map[string]int   `json:"activeConnections,omitempty"`
	ObservedSessions   int              `json:"observedSessions"`
	AttributedSessions int              `json:"attributedSessions"`
}

type clientIdentity struct {
	ClientID string
	Enabled  bool
}

func Collect(input CollectInput) (CollectResult, error) {
	result := CollectResult{
		UsageDeltas:       make(map[string]int64),
		ActiveConnections: make(map[string]int),
	}
	if input.Database == nil {
		return result, sql.ErrConnDone
	}
	if err := Migrate(input.Database); err != nil {
		return result, err
	}
	identities, enabled, err := loadClientIdentities(input.Database, input.ProtocolID)
	if err != nil {
		return result, err
	}
	now := input.Now.UTC()
	if now.IsZero() {
		now = time.Now().UTC()
	}
	switch input.Source {
	case "", "connector_tracker":
		return result, nil
	case "openvpn_status":
		samples, err := parseOpenVPNStatus(input.OpenVPNStatusPath)
		if err != nil {
			return result, err
		}
		totals := make(map[string]int64)
		for _, sample := range samples {
			result.ObservedSessions++
			identity, ok := identities[firstIdentityKey(sample.Username, sample.CommonName, identities)]
			if !ok {
				continue
			}
			result.AttributedSessions++
			result.ActiveConnections[identity.ClientID]++
			totals[identity.ClientID] += sample.TotalBytes
		}
		for clientID, total := range totals {
			delta, err := counterDelta(input.Database, "openvpn_user_total", clientID, total, now)
			if err != nil {
				return result, err
			}
			result.UsageDeltas[clientID] += delta
		}
	case "ipsec_ppp":
		root := input.SysClassNetRoot
		if root == "" {
			root = "/sys/class/net"
		}
		sessions, err := parsePPPSessions(input.PPPSessionsPath)
		if err != nil {
			return result, err
		}
		for _, session := range sessions {
			result.ObservedSessions++
			identity, ok := identities[session.Username]
			if !ok && (session.Username == "" || session.Username == "__unknown__") && len(enabled) == 1 {
				identity, ok = enabled[0], true
			}
			if !ok {
				continue
			}
			total, err := interfaceTotalBytes(filepath.Join(root, session.Interface))
			if err != nil {
				if errors.Is(err, os.ErrNotExist) {
					continue
				}
				return result, err
			}
			result.AttributedSessions++
			result.ActiveConnections[identity.ClientID]++
			delta, err := counterDelta(input.Database, "ipsec_iface_total", session.Interface, total, now)
			if err != nil {
				return result, err
			}
			result.UsageDeltas[identity.ClientID] += delta
		}
	default:
		return result, fmt.Errorf("unsupported accounting source %q", input.Source)
	}
	return result, nil
}

func loadClientIdentities(db *sql.DB, protocolID string) (map[string]clientIdentity, []clientIdentity, error) {
	rows, err := db.Query(`SELECT client_id, username, auth_username, enabled FROM clients WHERE protocol_id=?`, protocolID)
	if err != nil {
		return nil, nil, err
	}
	defer rows.Close()
	result := make(map[string]clientIdentity)
	enabled := make([]clientIdentity, 0)
	for rows.Next() {
		var identity clientIdentity
		var username, authUsername string
		var isEnabled int
		if err := rows.Scan(&identity.ClientID, &username, &authUsername, &isEnabled); err != nil {
			return nil, nil, err
		}
		identity.Enabled = isEnabled != 0
		for _, key := range []string{identity.ClientID, username, authUsername} {
			if key = strings.TrimSpace(key); key != "" {
				result[key] = identity
			}
		}
		if identity.Enabled {
			enabled = append(enabled, identity)
		}
	}
	return result, enabled, rows.Err()
}

type openVPNSample struct {
	CommonName string
	Username   string
	TotalBytes int64
}

func parseOpenVPNStatus(path string) ([]openVPNSample, error) {
	file, err := os.Open(path)
	if errors.Is(err, os.ErrNotExist) {
		return nil, nil
	}
	if err != nil {
		return nil, err
	}
	defer file.Close()
	reader := csv.NewReader(file)
	reader.FieldsPerRecord = -1
	headers := map[string]int{}
	result := make([]openVPNSample, 0)
	for {
		row, err := reader.Read()
		if errors.Is(err, io.EOF) {
			return result, nil
		}
		if err != nil {
			return nil, err
		}
		if len(row) == 0 {
			continue
		}
		if row[0] == "HEADER" && len(row) > 2 && row[1] == "CLIENT_LIST" {
			headers = headerIndex(row[2:])
			continue
		}
		if row[0] != "CLIENT_LIST" || len(headers) == 0 {
			continue
		}
		values := row[1:]
		received := parseInt64(field(values, headers, "Bytes Received"))
		sent := parseInt64(field(values, headers, "Bytes Sent"))
		result = append(result, openVPNSample{
			CommonName: field(values, headers, "Common Name"),
			Username:   field(values, headers, "Username"),
			TotalBytes: max64(0, received) + max64(0, sent),
		})
	}
}

func headerIndex(values []string) map[string]int {
	result := make(map[string]int, len(values))
	for index, value := range values {
		result[strings.TrimSpace(value)] = index
	}
	return result
}

func field(values []string, headers map[string]int, key string) string {
	index, ok := headers[key]
	if !ok || index < 0 || index >= len(values) {
		return ""
	}
	return strings.TrimSpace(values[index])
}

func firstIdentityKey(username string, commonName string, identities map[string]clientIdentity) string {
	for _, value := range []string{strings.TrimSpace(username), strings.TrimSpace(commonName)} {
		if _, ok := identities[value]; ok {
			return value
		}
	}
	return ""
}

type pppSession struct {
	Interface string
	Username  string
}

func parsePPPSessions(path string) ([]pppSession, error) {
	file, err := os.Open(path)
	if errors.Is(err, os.ErrNotExist) {
		return nil, nil
	}
	if err != nil {
		return nil, err
	}
	defer file.Close()
	result := make([]pppSession, 0)
	scanner := bufio.NewScanner(file)
	for scanner.Scan() {
		parts := strings.SplitN(scanner.Text(), "\t", 2)
		if len(parts) != 2 || strings.TrimSpace(parts[0]) == "" {
			continue
		}
		result = append(result, pppSession{Interface: strings.TrimSpace(parts[0]), Username: strings.TrimSpace(parts[1])})
	}
	return result, scanner.Err()
}

func interfaceTotalBytes(path string) (int64, error) {
	rx, err := readInt64(filepath.Join(path, "statistics", "rx_bytes"))
	if err != nil {
		return 0, err
	}
	tx, err := readInt64(filepath.Join(path, "statistics", "tx_bytes"))
	if err != nil {
		return 0, err
	}
	return max64(0, rx) + max64(0, tx), nil
}

func readInt64(path string) (int64, error) {
	content, err := os.ReadFile(path)
	if err != nil {
		return 0, err
	}
	return strconv.ParseInt(strings.TrimSpace(string(content)), 10, 64)
}

func parseInt64(value string) int64 {
	parsed, _ := strconv.ParseInt(strings.TrimSpace(value), 10, 64)
	return parsed
}

func counterDelta(db *sql.DB, source string, key string, current int64, now time.Time) (int64, error) {
	current = max64(0, current)
	var previous int64
	err := db.QueryRow(`SELECT last_value FROM sampler_state WHERE source=? AND state_key=?`, source, key).Scan(&previous)
	first := errors.Is(err, sql.ErrNoRows)
	if err != nil && !first {
		return 0, err
	}
	delta := int64(0)
	if !first {
		if current >= previous {
			delta = current - previous
		} else {
			delta = current
		}
	}
	_, err = db.Exec(`INSERT INTO sampler_state(source,state_key,last_value,updated_at) VALUES(?,?,?,?)
ON CONFLICT(source,state_key) DO UPDATE SET last_value=excluded.last_value, updated_at=excluded.updated_at`,
		source, key, current, now.Unix())
	return delta, err
}

func max64(left int64, right int64) int64 {
	if left > right {
		return left
	}
	return right
}
