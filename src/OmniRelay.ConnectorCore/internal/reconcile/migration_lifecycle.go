package reconcile

import (
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"sort"
	"strings"
)

type MigrationLifecycleResult struct {
	RelayID      string   `json:"relayId"`
	SnapshotPath string   `json:"snapshotPath,omitempty"`
	Paths        []string `json:"paths,omitempty"`
	RestartUnits []string `json:"restartUnits,omitempty"`
}

func FinalizeMigration(relayID string, options MigrateOptions) (MigrationLifecycleResult, error) {
	options.ApplyOptions = defaultApplyOptions(options.ApplyOptions)
	if options.LegacyBinaryRoot == "" {
		options.LegacyBinaryRoot = "/usr/local/sbin"
	}
	if options.GlobalRoot == "" {
		options.GlobalRoot = "/etc"
	}
	if options.PanelAppRoot == "" {
		options.PanelAppRoot = "/opt/omnirelay/relays"
	}
	if options.NginxEnabledRoot == "" {
		options.NginxEnabledRoot = "/etc/nginx/sites-enabled"
	}
	paths := []string{
		filepath.Join(options.LegacyBinaryRoot, "omnirelay-gatewayctl-"+relayID),
		filepath.Join(options.LegacyBinaryRoot, "omnirelay-tunnelctl-"+relayID),
		filepath.Join(options.LegacyBinaryRoot, "omnirelay-clock-sync-"+relayID),
		filepath.Join(options.LegacyBinaryRoot, "omnirelay-accounting-sync-"+relayID),
		filepath.Join(options.SystemdRoot, "omnirelay-singbox-"+relayID+".service"),
		filepath.Join(options.SystemdRoot, "omnirelay-accounting-sync-"+relayID+".service"),
		filepath.Join(options.SystemdRoot, "omnirelay-accounting-sync-"+relayID+".timer"),
	}
	deleted := make([]string, 0, len(paths))
	for _, path := range paths {
		if err := os.Remove(path); err != nil {
			if errors.Is(err, os.ErrNotExist) {
				continue
			}
			return MigrationLifecycleResult{}, err
		}
		deleted = append(deleted, path)
	}
	return MigrationLifecycleResult{RelayID: relayID, Paths: deleted}, nil
}

func RollbackMigration(relayID string, options MigrateOptions) (MigrationLifecycleResult, error) {
	options.ApplyOptions = defaultApplyOptions(options.ApplyOptions)
	if options.LegacyBinaryRoot == "" {
		options.LegacyBinaryRoot = "/usr/local/sbin"
	}
	if options.GlobalRoot == "" {
		options.GlobalRoot = "/etc"
	}
	if options.PanelAppRoot == "" {
		options.PanelAppRoot = "/opt/omnirelay/relays"
	}
	if options.NginxEnabledRoot == "" {
		options.NginxEnabledRoot = "/etc/nginx/sites-enabled"
	}
	snapshotRoot, err := latestMigrationSnapshot(relayID, options.TransactionRoot)
	if err != nil {
		return MigrationLifecycleResult{}, err
	}
	manifestPath := filepath.Join(options.ConfigRoot, "relays", relayID, "gateway", "managed-files.json")
	managedPaths, err := loadManagedManifest(manifestPath, relayID, options.ApplyOptions)
	if err != nil {
		return MigrationLifecycleResult{}, err
	}
	sort.Sort(sort.Reverse(sort.StringSlice(managedPaths)))
	for _, path := range managedPaths {
		if err := os.Remove(path); err != nil && !errors.Is(err, os.ErrNotExist) {
			return MigrationLifecycleResult{}, err
		}
	}
	relayRoot := filepath.Join(options.ConfigRoot, "relays", relayID)
	if err := os.RemoveAll(relayRoot); err != nil {
		return MigrationLifecycleResult{}, err
	}
	enabledSite := filepath.Join(options.NginxEnabledRoot, "omnirelay-omnipanel-"+relayID+".conf")
	if err := os.Remove(enabledSite); err != nil && !errors.Is(err, os.ErrNotExist) {
		return MigrationLifecycleResult{}, err
	}

	entries, err := os.ReadDir(snapshotRoot)
	if err != nil {
		return MigrationLifecycleResult{}, err
	}
	restored := make([]string, 0, len(entries))
	for _, entry := range entries {
		name := entry.Name()
		separator := strings.IndexByte(name, '-')
		if separator < 0 || separator == len(name)-1 {
			return MigrationLifecycleResult{}, fmt.Errorf("invalid migration snapshot entry %q", name)
		}
		baseName := name[separator+1:]
		var destination string
		switch {
		case baseName == relayID:
			destination = relayRoot
		case strings.HasSuffix(baseName, ".service"), strings.HasSuffix(baseName, ".timer"), strings.HasSuffix(baseName, ".target"):
			destination = filepath.Join(options.SystemdRoot, baseName)
		case strings.HasPrefix(baseName, "omnirelay-gatewayctl-"),
			strings.HasPrefix(baseName, "omnirelay-tunnelctl-"),
			strings.HasPrefix(baseName, "omnirelay-clock-sync-"),
			strings.HasPrefix(baseName, "omnirelay-accounting-sync-"):
			destination = filepath.Join(options.LegacyBinaryRoot, baseName)
		case baseName == "ipsec.conf", baseName == "ipsec.secrets":
			destination = filepath.Join(options.GlobalRoot, baseName)
		case baseName == "xl2tpd.conf":
			destination = filepath.Join(options.GlobalRoot, "xl2tpd", baseName)
		case baseName == "options.xl2tpd", baseName == "chap-secrets":
			destination = filepath.Join(options.GlobalRoot, "ppp", baseName)
		case strings.HasPrefix(baseName, "omnirelay-openvpn-"), strings.HasPrefix(baseName, "omnirelay-ipsec-l2tp-"):
			destination = filepath.Join(options.DNSMasqRoot, baseName)
		case strings.HasPrefix(baseName, "omnirelay-omnipanel-") && strings.HasSuffix(baseName, ".enabled.conf"):
			destination = enabledSite
		case strings.HasPrefix(baseName, "omnirelay-omnipanel-") && strings.HasSuffix(baseName, ".conf"):
			destination = filepath.Join(options.NginxRoot, baseName)
		case baseName == "omnipanel":
			destination = filepath.Join(options.PanelAppRoot, relayID, baseName)
		default:
			return MigrationLifecycleResult{}, fmt.Errorf("unsupported migration snapshot entry %q", name)
		}
		if err := os.RemoveAll(destination); err != nil {
			return MigrationLifecycleResult{}, err
		}
		if _, err := copyLegacyPath(filepath.Join(snapshotRoot, name), destination); err != nil {
			return MigrationLifecycleResult{}, err
		}
		restored = append(restored, destination)
	}
	return MigrationLifecycleResult{
		RelayID: relayID, SnapshotPath: snapshotRoot, Paths: restored,
		RestartUnits: rollbackRestartUnits(restored, options),
	}, nil
}

func rollbackRestartUnits(restored []string, options MigrateOptions) []string {
	units := map[string]struct{}{}
	for _, path := range restored {
		switch {
		case path == filepath.Join(options.NginxEnabledRoot, filepath.Base(path)),
			pathWithinRoot(options.NginxRoot, path),
			pathWithinRoot(options.PanelAppRoot, path):
			units["nginx.service"] = struct{}{}
		case pathWithinRoot(options.DNSMasqRoot, path):
			units["dnsmasq.service"] = struct{}{}
		case path == filepath.Join(options.GlobalRoot, "ipsec.conf"),
			path == filepath.Join(options.GlobalRoot, "ipsec.secrets"):
			units["strongswan-starter.service"] = struct{}{}
		case pathWithinRoot(filepath.Join(options.GlobalRoot, "xl2tpd"), path),
			pathWithinRoot(filepath.Join(options.GlobalRoot, "ppp"), path):
			units["xl2tpd.service"] = struct{}{}
		}
	}
	result := make([]string, 0, len(units))
	for unit := range units {
		result = append(result, unit)
	}
	sort.Strings(result)
	return result
}

func pathWithinRoot(root string, path string) bool {
	relative, err := filepath.Rel(filepath.Clean(root), filepath.Clean(path))
	return err == nil && relative != ".." && !strings.HasPrefix(relative, ".."+string(filepath.Separator))
}

func latestMigrationSnapshot(relayID string, transactionRoot string) (string, error) {
	root := filepath.Join(transactionRoot, "migration-backups", relayID)
	entries, err := os.ReadDir(root)
	if err != nil {
		return "", fmt.Errorf("read migration snapshots: %w", err)
	}
	names := make([]string, 0, len(entries))
	for _, entry := range entries {
		if entry.IsDir() {
			names = append(names, entry.Name())
		}
	}
	if len(names) == 0 {
		return "", fmt.Errorf("no migration snapshot exists for relay %s", relayID)
	}
	sort.Strings(names)
	return filepath.Join(root, names[len(names)-1]), nil
}
