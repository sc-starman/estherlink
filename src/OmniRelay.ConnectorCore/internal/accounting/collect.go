package accounting

import (
	"bufio"
	"database/sql"
	"encoding/csv"
	"errors"
	"fmt"
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
	content, err := os.ReadFile(path)
	if errors.Is(err, os.ErrNotExist) {
		return nil, nil
	}
	if err != nil {
		return nil, err
	}
	result := make([]openVPNSample, 0)
	inClientTable := false
	tableHeaders := []string{}
	for _, rawLine := range strings.Split(strings.ReplaceAll(string(content), "\r\n", "\n"), "\n") {
		line := strings.TrimSpace(rawLine)
		if line == "" {
			continue
		}
		upperLine := strings.ToUpper(line)

		if strings.HasPrefix(upperLine, "CLIENT_LIST") {
			fields := splitOpenVPNStatusFields(line)
			if len(fields) == 0 || strings.ToUpper(fields[0]) != "CLIENT_LIST" {
				continue
			}
			payload := fields[1:]
			if len(payload) == 0 {
				continue
			}
			commonName := payload[0]
			username := firstNonEmpty(payloadValue(payload, 8), payloadValue(payload, 9), payloadValue(payload, 7))
			rxRaw, txRaw := openVPNCounterPair(payload)
			sample := buildOpenVPNSample(commonName, username, rxRaw, txRaw)
			if sample.TotalBytes > 0 {
				result = append(result, sample)
			}
			continue
		}

		if strings.HasPrefix(upperLine, "OPENVPN CLIENT LIST") {
			inClientTable = true
			tableHeaders = nil
			continue
		}

		if strings.HasPrefix(upperLine, "ROUTING TABLE") ||
			strings.HasPrefix(upperLine, "ROUTING_TABLE") ||
			strings.HasPrefix(upperLine, "GLOBAL STATS") ||
			strings.HasPrefix(upperLine, "GLOBAL_STATS") ||
			upperLine == "END" {
			inClientTable = false
			tableHeaders = nil
			continue
		}

		fields := splitOpenVPNStatusFields(line)
		if len(fields) < 2 {
			continue
		}

		if strings.EqualFold(fields[0], "HEADER") {
			headerKind := strings.ToUpper(strings.TrimSpace(fields[1]))
			if headerKind == "CLIENT_LIST" {
				inClientTable = true
				tableHeaders = lowerOpenVPNHeaders(fields[2:])
				continue
			}
			if headerKind == "ROUTING_TABLE" || headerKind == "GLOBAL_STATS" {
				inClientTable = false
				tableHeaders = nil
				continue
			}
		}

		normalized := lowerOpenVPNHeaders(fields)
		if containsOpenVPNHeader(normalized, "common name") &&
			containsOpenVPNHeader(normalized, "bytes received") &&
			containsOpenVPNHeader(normalized, "bytes sent") {
			inClientTable = true
			tableHeaders = normalized
			continue
		}

		if !inClientTable {
			continue
		}

		row := make(map[string]string, len(tableHeaders))
		for index := 0; index < len(tableHeaders) && index < len(fields); index++ {
			row[tableHeaders[index]] = fields[index]
		}
		commonName := firstNonEmpty(strings.TrimSpace(row["common name"]), payloadValue(fields, 0))
		username := strings.TrimSpace(row["username"])
		rxRaw := strings.TrimSpace(row["bytes received"])
		txRaw := strings.TrimSpace(row["bytes sent"])
		if rxRaw == "" && txRaw == "" {
			if len(fields) > 5 {
				rxRaw = payloadValue(fields, 4)
				txRaw = payloadValue(fields, 5)
			} else {
				rxRaw = payloadValue(fields, 2)
				txRaw = payloadValue(fields, 3)
			}
		}
		sample := buildOpenVPNSample(commonName, username, rxRaw, txRaw)
		if sample.TotalBytes > 0 {
			result = append(result, sample)
		}
	}
	return result, nil
}

func splitOpenVPNStatusFields(line string) []string {
	if strings.Contains(line, ",") {
		reader := csv.NewReader(strings.NewReader(line))
		reader.FieldsPerRecord = -1
		fields, err := reader.Read()
		if err == nil {
			for index := range fields {
				fields[index] = strings.TrimSpace(fields[index])
			}
			return fields
		}
	}
	if strings.Contains(line, "\t") {
		parts := strings.Split(line, "\t")
		for index := range parts {
			parts[index] = strings.TrimSpace(parts[index])
		}
		return parts
	}
	parts := strings.Split(strings.TrimSpace(line), "  ")
	result := make([]string, 0, len(parts))
	for _, part := range parts {
		if trimmed := strings.TrimSpace(part); trimmed != "" {
			result = append(result, trimmed)
		}
	}
	return result
}

func lowerOpenVPNHeaders(values []string) []string {
	result := make([]string, 0, len(values))
	for _, value := range values {
		result = append(result, strings.ToLower(strings.TrimSpace(value)))
	}
	return result
}

func containsOpenVPNHeader(values []string, expected string) bool {
	for _, value := range values {
		if value == expected {
			return true
		}
	}
	return false
}

func openVPNCounterPair(payload []string) (string, string) {
	for _, pair := range [][2]int{{4, 5}, {2, 3}, {5, 6}, {3, 4}} {
		if len(payload) <= pair[1] {
			continue
		}
		rxRaw := payloadValue(payload, pair[0])
		txRaw := payloadValue(payload, pair[1])
		if isUnsignedCounter(rxRaw) && isUnsignedCounter(txRaw) {
			return rxRaw, txRaw
		}
	}
	return "0", "0"
}

func isUnsignedCounter(value string) bool {
	value = strings.TrimSpace(value)
	if value == "" {
		return false
	}
	for _, char := range value {
		if char < '0' || char > '9' {
			return false
		}
	}
	return true
}

func payloadValue(values []string, index int) string {
	if index < 0 || index >= len(values) {
		return ""
	}
	return strings.TrimSpace(values[index])
}

func firstNonEmpty(values ...string) string {
	for _, value := range values {
		if trimmed := strings.TrimSpace(value); trimmed != "" && trimmed != "UNDEF" {
			return trimmed
		}
	}
	return ""
}

func buildOpenVPNSample(commonName string, username string, rxRaw string, txRaw string) openVPNSample {
	identity := firstNonEmpty(username, commonName)
	return openVPNSample{
		CommonName: strings.TrimSpace(commonName),
		Username:   identity,
		TotalBytes: max64(0, parseInt64(rxRaw)) + max64(0, parseInt64(txRaw)),
	}
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
