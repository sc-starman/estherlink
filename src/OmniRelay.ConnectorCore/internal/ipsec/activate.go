package ipsec

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"time"

	"github.com/omnirelay/connector-core/internal/host"
)

type ServiceManager interface {
	Restart(context.Context, ...string) error
	Stop(context.Context, ...string) error
}

type ActivationPaths struct {
	ConfigRoot string
	GlobalRoot string
	OwnerFile  string
	BackupRoot string
}

type ActivationResult struct {
	RelayID string `json:"relayId"`
	Active  bool   `json:"active"`
	Changed bool   `json:"changed"`
}

type ownerRecord struct {
	APIVersion string    `json:"apiVersion"`
	RelayID    string    `json:"relayId"`
	State      string    `json:"state"`
	UpdatedAt  time.Time `json:"updatedAtUtc"`
}

type backupRecord struct {
	APIVersion string                  `json:"apiVersion"`
	Files      map[string]originalFile `json:"files"`
}

type originalFile struct {
	Existed bool        `json:"existed"`
	Mode    os.FileMode `json:"mode,omitempty"`
	Content []byte      `json:"content,omitempty"`
}

var ipsecServices = []string{"strongswan-starter.service", "xl2tpd.service", "dnsmasq.service"}

func Activate(ctx context.Context, relayID string, paths ActivationPaths, services ServiceManager) (ActivationResult, error) {
	paths = defaultActivationPaths(paths)
	release, err := acquireOperationLock(paths.OwnerFile + ".lock")
	if err != nil {
		return ActivationResult{}, err
	}
	defer release()

	owner, ownerExists, err := loadOwner(paths.OwnerFile)
	if err != nil {
		return ActivationResult{}, err
	}
	if ownerExists && owner.RelayID != relayID {
		return ActivationResult{}, fmt.Errorf("IPsec/L2TP is already owned by relay %s", owner.RelayID)
	}
	var previousOwner *ownerRecord
	if ownerExists {
		copy := owner
		previousOwner = &copy
	}
	desired, err := desiredGlobalFiles(relayID, paths)
	if err != nil {
		return ActivationResult{}, err
	}
	current, err := snapshotFiles(desired)
	if err != nil {
		return ActivationResult{}, err
	}
	backupPath := filepath.Join(paths.BackupRoot, relayID, "original.json")
	if !ownerExists {
		if err := writeBackup(backupPath, current); err != nil {
			return ActivationResult{}, err
		}
	}
	if err := writeOwner(paths.OwnerFile, ownerRecord{
		APIVersion: "omnirelay.io/ipsec-owner/v1", RelayID: relayID, State: "activating", UpdatedAt: time.Now().UTC(),
	}); err != nil {
		return ActivationResult{}, err
	}
	if err := services.Stop(ctx, ipsecServices...); err != nil {
		return ActivationResult{}, failActivation(paths.OwnerFile, previousOwner, current, services, ctx, err)
	}
	changed := false
	for path, file := range desired {
		written, err := host.WriteFileAtomic(path, file.Content, file.Mode)
		if err != nil {
			return ActivationResult{}, failActivation(paths.OwnerFile, previousOwner, current, services, ctx, err)
		}
		changed = changed || written
	}
	if err := services.Restart(ctx, ipsecServices...); err != nil {
		return ActivationResult{}, failActivation(paths.OwnerFile, previousOwner, current, services, ctx, err)
	}
	if err := writeOwner(paths.OwnerFile, ownerRecord{
		APIVersion: "omnirelay.io/ipsec-owner/v1", RelayID: relayID, State: "active", UpdatedAt: time.Now().UTC(),
	}); err != nil {
		return ActivationResult{}, err
	}
	return ActivationResult{RelayID: relayID, Active: true, Changed: changed}, nil
}

func Deactivate(ctx context.Context, relayID string, paths ActivationPaths, services ServiceManager) (ActivationResult, error) {
	paths = defaultActivationPaths(paths)
	release, err := acquireOperationLock(paths.OwnerFile + ".lock")
	if err != nil {
		return ActivationResult{}, err
	}
	defer release()
	owner, exists, err := loadOwner(paths.OwnerFile)
	if err != nil {
		return ActivationResult{}, err
	}
	if !exists {
		return ActivationResult{RelayID: relayID}, nil
	}
	if owner.RelayID != relayID {
		return ActivationResult{}, fmt.Errorf("IPsec/L2TP is owned by relay %s", owner.RelayID)
	}
	backup, err := loadBackup(filepath.Join(paths.BackupRoot, relayID, "original.json"))
	if err != nil {
		return ActivationResult{}, fmt.Errorf("refusing IPsec/L2TP deactivation without restorable host snapshot: %w", err)
	}
	if err := services.Stop(ctx, ipsecServices...); err != nil {
		return ActivationResult{}, err
	}
	if err := restoreFiles(backup.Files); err != nil {
		return ActivationResult{}, err
	}
	if err := services.Restart(ctx, ipsecServices...); err != nil {
		return ActivationResult{}, err
	}
	if err := os.Remove(paths.OwnerFile); err != nil && !errors.Is(err, os.ErrNotExist) {
		return ActivationResult{}, err
	}
	return ActivationResult{RelayID: relayID, Changed: true}, nil
}

func SyncActiveChapSecrets(relayID string, paths ActivationPaths) (bool, error) {
	paths = defaultActivationPaths(paths)
	owner, exists, err := loadOwner(paths.OwnerFile)
	if err != nil {
		return false, err
	}
	if !exists {
		return false, nil
	}
	if owner.RelayID != relayID {
		return false, fmt.Errorf("IPsec/L2TP is owned by relay %s", owner.RelayID)
	}
	content, err := os.ReadFile(filepath.Join(paths.ConfigRoot, "relays", relayID, "gateway", "ipsec-l2tp", "chap-secrets"))
	if err != nil {
		return false, err
	}
	return host.WriteFileAtomic(filepath.Join(paths.GlobalRoot, "ppp", "chap-secrets"), content, 0o600)
}

func defaultActivationPaths(paths ActivationPaths) ActivationPaths {
	if paths.ConfigRoot == "" {
		paths.ConfigRoot = "/etc/omnirelay"
	}
	if paths.GlobalRoot == "" {
		paths.GlobalRoot = "/etc"
	}
	if paths.OwnerFile == "" {
		paths.OwnerFile = filepath.Join(paths.ConfigRoot, "ipsec-l2tp-owner.json")
	}
	if paths.BackupRoot == "" {
		paths.BackupRoot = filepath.Join(paths.ConfigRoot, "ipsec-l2tp-backups")
	}
	return paths
}

func desiredGlobalFiles(relayID string, paths ActivationPaths) (map[string]originalFile, error) {
	relayRoot := filepath.Join(paths.ConfigRoot, "relays", relayID, "gateway", "ipsec-l2tp")
	mapping := map[string]string{
		filepath.Join(paths.GlobalRoot, "ipsec.conf"):            filepath.Join(relayRoot, "ipsec.conf"),
		filepath.Join(paths.GlobalRoot, "ipsec.secrets"):         filepath.Join(relayRoot, "ipsec.secrets"),
		filepath.Join(paths.GlobalRoot, "xl2tpd", "xl2tpd.conf"): filepath.Join(relayRoot, "xl2tpd.conf"),
		filepath.Join(paths.GlobalRoot, "ppp", "options.xl2tpd"): filepath.Join(relayRoot, "ppp-options"),
		filepath.Join(paths.GlobalRoot, "ppp", "chap-secrets"):   filepath.Join(relayRoot, "chap-secrets"),
	}
	result := make(map[string]originalFile, len(mapping))
	for target, source := range mapping {
		content, err := os.ReadFile(source)
		if errors.Is(err, os.ErrNotExist) && filepath.Base(source) == "chap-secrets" {
			content = []byte{}
			err = nil
		}
		if err != nil {
			return nil, fmt.Errorf("read relay-owned IPsec/L2TP file %s: %w", source, err)
		}
		mode := os.FileMode(0o644)
		if filepath.Base(target) == "ipsec.secrets" || filepath.Base(target) == "chap-secrets" {
			mode = 0o600
		}
		result[target] = originalFile{Existed: true, Mode: mode, Content: content}
	}
	return result, nil
}

func snapshotFiles(files map[string]originalFile) (map[string]originalFile, error) {
	result := make(map[string]originalFile, len(files))
	for path := range files {
		content, err := os.ReadFile(path)
		if errors.Is(err, os.ErrNotExist) {
			result[path] = originalFile{}
			continue
		}
		if err != nil {
			return nil, err
		}
		info, err := os.Stat(path)
		if err != nil {
			return nil, err
		}
		result[path] = originalFile{Existed: true, Mode: info.Mode().Perm(), Content: content}
	}
	return result, nil
}

func restoreFiles(files map[string]originalFile) error {
	for path, file := range files {
		if !file.Existed {
			if err := os.Remove(path); err != nil && !errors.Is(err, os.ErrNotExist) {
				return err
			}
			continue
		}
		if _, err := host.WriteFileAtomic(path, file.Content, file.Mode); err != nil {
			return err
		}
	}
	return nil
}

func failActivation(ownerFile string, previousOwner *ownerRecord, current map[string]originalFile, services ServiceManager, ctx context.Context, cause error) error {
	rollbackErr := restoreFiles(current)
	restartErr := services.Restart(ctx, ipsecServices...)
	if previousOwner == nil {
		_ = os.Remove(ownerFile)
	} else {
		_ = writeOwner(ownerFile, *previousOwner)
	}
	if rollbackErr != nil || restartErr != nil {
		return fmt.Errorf("%w; rollback=%v; service_restore=%v", cause, rollbackErr, restartErr)
	}
	return cause
}

func loadOwner(path string) (ownerRecord, bool, error) {
	content, err := os.ReadFile(path)
	if errors.Is(err, os.ErrNotExist) {
		return ownerRecord{}, false, nil
	}
	if err != nil {
		return ownerRecord{}, false, err
	}
	var owner ownerRecord
	if err := json.Unmarshal(content, &owner); err != nil {
		return ownerRecord{}, false, err
	}
	if owner.RelayID == "" {
		return ownerRecord{}, false, fmt.Errorf("IPsec/L2TP owner record is invalid")
	}
	return owner, true, nil
}

func writeOwner(path string, owner ownerRecord) error {
	content, err := json.MarshalIndent(owner, "", "  ")
	if err != nil {
		return err
	}
	_, err = host.WriteFileAtomic(path, append(content, '\n'), 0o600)
	return err
}

func writeBackup(path string, files map[string]originalFile) error {
	content, err := json.MarshalIndent(backupRecord{APIVersion: "omnirelay.io/ipsec-backup/v1", Files: files}, "", "  ")
	if err != nil {
		return err
	}
	_, err = host.WriteFileAtomic(path, append(content, '\n'), 0o600)
	return err
}

func loadBackup(path string) (backupRecord, error) {
	content, err := os.ReadFile(path)
	if err != nil {
		return backupRecord{}, err
	}
	var backup backupRecord
	if err := json.Unmarshal(content, &backup); err != nil {
		return backupRecord{}, err
	}
	if backup.APIVersion != "omnirelay.io/ipsec-backup/v1" {
		return backupRecord{}, fmt.Errorf("unsupported IPsec/L2TP backup version")
	}
	return backup, nil
}

func acquireOperationLock(path string) (func(), error) {
	if err := os.MkdirAll(filepath.Dir(path), 0o700); err != nil {
		return nil, err
	}
	file, err := os.OpenFile(path, os.O_CREATE|os.O_EXCL|os.O_WRONLY, 0o600)
	if err != nil {
		if errors.Is(err, os.ErrExist) {
			return nil, fmt.Errorf("another IPsec/L2TP operation is active")
		}
		return nil, err
	}
	_ = file.Close()
	return func() { _ = os.Remove(path) }, nil
}
