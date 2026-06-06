package openvpn

import (
	"context"
	"crypto/sha256"
	"database/sql"
	"encoding/hex"
	"fmt"
	"os/exec"
	"sort"
	"strconv"
)

type LimitRunner interface {
	Run(context.Context, string, ...string) error
}

type OSLimitRunner struct{}

func (OSLimitRunner) Run(ctx context.Context, name string, args ...string) error {
	output, err := exec.CommandContext(ctx, name, args...).CombinedOutput()
	if err != nil {
		return fmt.Errorf("%s: %w: %s", name, err, output)
	}
	return nil
}

type SpeedLimitResult struct {
	AppliedClients int    `json:"appliedClients"`
	Interface      string `json:"interface"`
	IFBInterface   string `json:"ifbInterface"`
}

type limitedClient struct {
	ID       string
	Identity string
	Address  string
	Kbps     int
}

func ApplySpeedLimits(ctx context.Context, db *sql.DB, protocolID string, relayID string, openVPNRoot string, runner LimitRunner) (SpeedLimitResult, error) {
	if db == nil {
		return SpeedLimitResult{}, sql.ErrConnDone
	}
	interfaceName := InterfaceName(relayID)
	ifbName := IFBInterfaceName(relayID)
	result := SpeedLimitResult{Interface: interfaceName, IFBInterface: ifbName}
	addresses, err := loadAddressMap(openVPNRoot + "/client-ip-map.json")
	if err != nil {
		return result, err
	}
	clients, err := loadLimitedClients(db, protocolID, addresses)
	if err != nil {
		return result, err
	}
	CleanupSpeedLimits(ctx, relayID, runner)
	if len(clients) == 0 {
		return result, nil
	}
	if err := runner.Run(ctx, "ip", "link", "show", "dev", interfaceName); err != nil {
		return result, fmt.Errorf("OpenVPN interface %s is not available: %w", interfaceName, err)
	}
	for _, command := range [][]string{
		{"ip", "link", "add", ifbName, "type", "ifb"},
		{"ip", "link", "set", "dev", ifbName, "up"},
		{"tc", "qdisc", "add", "dev", interfaceName, "root", "handle", "1:", "htb", "default", "9999"},
		{"tc", "class", "add", "dev", interfaceName, "parent", "1:", "classid", "1:1", "htb", "rate", "10000mbit", "ceil", "10000mbit"},
		{"tc", "class", "add", "dev", interfaceName, "parent", "1:1", "classid", "1:9999", "htb", "rate", "10000mbit", "ceil", "10000mbit"},
		{"tc", "qdisc", "add", "dev", ifbName, "root", "handle", "1:", "htb", "default", "9999"},
		{"tc", "class", "add", "dev", ifbName, "parent", "1:", "classid", "1:1", "htb", "rate", "10000mbit", "ceil", "10000mbit"},
		{"tc", "class", "add", "dev", ifbName, "parent", "1:1", "classid", "1:9999", "htb", "rate", "10000mbit", "ceil", "10000mbit"},
		{"tc", "qdisc", "add", "dev", interfaceName, "handle", "ffff:", "ingress"},
		{"tc", "filter", "add", "dev", interfaceName, "parent", "ffff:", "protocol", "all", "prio", "1", "matchall", "action", "mirred", "egress", "redirect", "dev", ifbName},
	} {
		if err := runner.Run(ctx, command[0], command[1:]...); err != nil {
			CleanupSpeedLimits(ctx, relayID, runner)
			return result, err
		}
	}
	for index, client := range clients {
		classID := "1:" + strconv.Itoa(index+10)
		rate := strconv.Itoa(client.Kbps) + "kbit"
		for _, command := range [][]string{
			{"tc", "class", "add", "dev", interfaceName, "parent", "1:1", "classid", classID, "htb", "rate", rate, "ceil", rate, "burst", "32k", "cburst", "32k"},
			{"tc", "filter", "add", "dev", interfaceName, "parent", "1:", "protocol", "ip", "prio", "10", "u32", "match", "ip", "dst", client.Address + "/32", "flowid", classID},
			{"tc", "class", "add", "dev", ifbName, "parent", "1:1", "classid", classID, "htb", "rate", rate, "ceil", rate, "burst", "32k", "cburst", "32k"},
			{"tc", "filter", "add", "dev", ifbName, "parent", "1:", "protocol", "ip", "prio", "10", "u32", "match", "ip", "src", client.Address + "/32", "flowid", classID},
		} {
			if err := runner.Run(ctx, command[0], command[1:]...); err != nil {
				CleanupSpeedLimits(ctx, relayID, runner)
				return result, fmt.Errorf("apply speed limit for client %s: %w", client.ID, err)
			}
		}
		result.AppliedClients++
	}
	return result, nil
}

func CleanupSpeedLimits(ctx context.Context, relayID string, runner LimitRunner) {
	interfaceName := InterfaceName(relayID)
	ifbName := IFBInterfaceName(relayID)
	for _, command := range [][]string{
		{"tc", "qdisc", "del", "dev", interfaceName, "ingress"},
		{"tc", "qdisc", "del", "dev", interfaceName, "root"},
		{"tc", "qdisc", "del", "dev", ifbName, "root"},
		{"ip", "link", "set", ifbName, "down"},
		{"ip", "link", "delete", ifbName, "type", "ifb"},
	} {
		_ = runner.Run(ctx, command[0], command[1:]...)
	}
}

func IFBInterfaceName(relayID string) string {
	hash := sha256.Sum256([]byte(relayID))
	return "ifb" + hex.EncodeToString(hash[:4])
}

func loadLimitedClients(db *sql.DB, protocolID string, addresses map[string]string) ([]limitedClient, error) {
	rows, err := db.Query(`
SELECT client_id,COALESCE(NULLIF(auth_username,''),username),speed_limit_kbps
FROM clients
WHERE protocol_id=? AND enabled=1 AND speed_limit_kbps>0
ORDER BY client_id`, protocolID)
	if err != nil {
		return nil, err
	}
	defer rows.Close()
	result := make([]limitedClient, 0)
	for rows.Next() {
		var client limitedClient
		if err := rows.Scan(&client.ID, &client.Identity, &client.Kbps); err != nil {
			return nil, err
		}
		client.Address = addresses[client.Identity]
		if client.Address == "" {
			return nil, fmt.Errorf("OpenVPN speed-limited client %s has no assigned address", client.ID)
		}
		result = append(result, client)
	}
	sort.Slice(result, func(i, j int) bool { return result[i].ID < result[j].ID })
	return result, rows.Err()
}
