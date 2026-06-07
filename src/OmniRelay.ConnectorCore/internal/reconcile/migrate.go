package reconcile

import (
	"errors"
	"fmt"
	"io/fs"
	"os"
	"path/filepath"
	"strings"
	"time"

	"github.com/omnirelay/connector-core/internal/host"
	"github.com/omnirelay/connector-core/internal/spec"
)

type MigrateOptions struct {
	ApplyOptions
	LegacyBinaryRoot string
	GlobalRoot       string
	PanelAppRoot     string
	NginxEnabledRoot string
}

type MigrateResult struct {
	RelayID            string      `json:"relayId"`
	LegacySnapshotPath string      `json:"legacySnapshotPath"`
	SnapshotFiles      int         `json:"snapshotFiles"`
	Apply              ApplyResult `json:"apply"`
}

func Migrate(gatewaySpec spec.GatewaySpec, options MigrateOptions) (MigrateResult, error) {
	gatewaySpec.ApplyDefaults()
	if err := gatewaySpec.Validate(); err != nil {
		return MigrateResult{}, err
	}
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
	snapshotRoot := filepath.Join(options.TransactionRoot, "migration-backups", gatewaySpec.RelayID, options.Now().UTC().Format("20060102T150405.000000000Z"))
	count, err := snapshotLegacyState(gatewaySpec, options, snapshotRoot)
	if err != nil {
		return MigrateResult{}, err
	}
	result, err := Apply(gatewaySpec, options.ApplyOptions)
	if err != nil {
		return MigrateResult{}, err
	}
	_ = pruneMigrationBackups(gatewaySpec.RelayID, options.TransactionRoot, options.Now().UTC(), 14*24*time.Hour)
	return MigrateResult{
		RelayID: gatewaySpec.RelayID, LegacySnapshotPath: snapshotRoot, SnapshotFiles: count, Apply: result,
	}, nil
}

func pruneMigrationBackups(relayID string, transactionRoot string, now time.Time, retention time.Duration) error {
	root := filepath.Join(transactionRoot, "migration-backups", relayID)
	entries, err := os.ReadDir(root)
	if errors.Is(err, os.ErrNotExist) {
		return nil
	}
	if err != nil {
		return err
	}
	cutoff := now.Add(-retention)
	for _, entry := range entries {
		if !entry.IsDir() {
			continue
		}
		created, err := time.Parse("20060102T150405.000000000Z", entry.Name())
		if err != nil || !created.Before(cutoff) {
			continue
		}
		if err := os.RemoveAll(filepath.Join(root, entry.Name())); err != nil {
			return err
		}
	}
	return nil
}

func snapshotLegacyState(gatewaySpec spec.GatewaySpec, options MigrateOptions, snapshotRoot string) (int, error) {
	relayID := gatewaySpec.RelayID
	if err := os.MkdirAll(snapshotRoot, 0o700); err != nil {
		return 0, err
	}
	sources := []string{
		filepath.Join(options.ConfigRoot, "relays", relayID),
		filepath.Join(options.LegacyBinaryRoot, "omnirelay-gatewayctl-"+relayID),
		filepath.Join(options.LegacyBinaryRoot, "omnirelay-tunnelctl-"+relayID),
		filepath.Join(options.LegacyBinaryRoot, "omnirelay-clock-sync-"+relayID),
		filepath.Join(options.LegacyBinaryRoot, "omnirelay-accounting-sync-"+relayID),
		filepath.Join(options.NginxRoot, "omnirelay-omnipanel-"+relayID+".conf"),
		filepath.Join(options.PanelAppRoot, relayID, "omnipanel"),
	}
	switch gatewaySpec.Gateway.Protocol {
	case "openvpn_tcp_singbox":
		sources = append(sources, filepath.Join(options.DNSMasqRoot, "omnirelay-openvpn-"+relayID+".conf"))
	case "ipsec_l2tp_singbox":
		sources = append(sources,
			filepath.Join(options.DNSMasqRoot, "omnirelay-ipsec-l2tp-"+relayID+".conf"),
			filepath.Join(options.GlobalRoot, "ipsec.conf"),
			filepath.Join(options.GlobalRoot, "ipsec.secrets"),
			filepath.Join(options.GlobalRoot, "xl2tpd", "xl2tpd.conf"),
			filepath.Join(options.GlobalRoot, "ppp", "options.xl2tpd"),
			filepath.Join(options.GlobalRoot, "ppp", "chap-secrets"),
		)
	}
	entries, err := os.ReadDir(options.SystemdRoot)
	if err == nil {
		for _, entry := range entries {
			if !entry.IsDir() && strings.HasPrefix(entry.Name(), "omnirelay-") && strings.Contains(entry.Name(), relayID) {
				sources = append(sources, filepath.Join(options.SystemdRoot, entry.Name()))
			}
		}
	} else if !os.IsNotExist(err) {
		return 0, err
	}
	count := 0
	for index, source := range sources {
		if _, err := os.Lstat(source); os.IsNotExist(err) {
			continue
		} else if err != nil {
			return count, err
		}
		destination := filepath.Join(snapshotRoot, fmt.Sprintf("%03d-%s", index, filepath.Base(source)))
		copied, err := copyLegacyPath(source, destination)
		if err != nil {
			return count, err
		}
		count += copied
	}
	enabledSite := filepath.Join(options.NginxEnabledRoot, "omnirelay-omnipanel-"+relayID+".conf")
	if _, err := os.Lstat(enabledSite); err == nil {
		destination := filepath.Join(snapshotRoot, fmt.Sprintf("%03d-omnirelay-omnipanel-%s.enabled.conf", len(sources), relayID))
		copied, copyErr := copyLegacyPath(enabledSite, destination)
		if copyErr != nil {
			return count, copyErr
		}
		count += copied
	} else if !os.IsNotExist(err) {
		return count, err
	}
	return count, nil
}

func copyLegacyPath(source string, destination string) (int, error) {
	info, err := os.Lstat(source)
	if err != nil {
		return 0, err
	}
	if info.Mode()&os.ModeSymlink != 0 {
		target, err := os.Readlink(source)
		if err != nil {
			return 0, err
		}
		if err := os.MkdirAll(filepath.Dir(destination), 0o700); err != nil {
			return 0, err
		}
		return 1, os.Symlink(target, destination)
	}
	if !info.IsDir() {
		content, err := os.ReadFile(source)
		if err != nil {
			return 0, err
		}
		_, err = host.WriteFileAtomic(destination, content, info.Mode().Perm())
		return 1, err
	}
	count := 0
	err = filepath.WalkDir(source, func(path string, entry fs.DirEntry, walkErr error) error {
		if walkErr != nil {
			return walkErr
		}
		relative, err := filepath.Rel(source, path)
		if err != nil {
			return err
		}
		target := filepath.Join(destination, relative)
		info, err := entry.Info()
		if err != nil {
			return err
		}
		if entry.IsDir() {
			return os.MkdirAll(target, info.Mode().Perm())
		}
		if info.Mode()&os.ModeSymlink != 0 {
			link, err := os.Readlink(path)
			if err != nil {
				return err
			}
			count++
			return os.Symlink(link, target)
		}
		content, err := os.ReadFile(path)
		if err != nil {
			return err
		}
		if _, err := host.WriteFileAtomic(target, content, info.Mode().Perm()); err != nil {
			return err
		}
		count++
		return nil
	})
	return count, err
}
