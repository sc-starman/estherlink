package reconcile

import (
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"sort"
)

type UninstallResult struct {
	RelayID       string   `json:"relayId"`
	TransactionID string   `json:"transactionId"`
	DeletedFiles  []string `json:"deletedFiles"`
}

func Uninstall(relayID string, options ApplyOptions) (UninstallResult, error) {
	options = defaultApplyOptions(options)
	now := options.Now().UTC()
	transactionID := fmt.Sprintf("%s-%s-uninstall", now.Format("20060102T150405.000000000Z"), relayID)
	relayRoot := filepath.Join(options.ConfigRoot, "relays", relayID, "gateway")
	manifestPath := filepath.Join(relayRoot, "managed-files.json")
	lockPath := filepath.Join(options.TransactionRoot, "locks", relayID+".lock")
	transactionDir := filepath.Join(options.TransactionRoot, transactionID)
	journalPath := filepath.Join(transactionDir, "journal.json")

	release, err := acquireLock(lockPath)
	if err != nil {
		return UninstallResult{}, err
	}
	defer release()

	paths, err := loadManagedManifest(manifestPath, relayID, options)
	if err != nil {
		return UninstallResult{}, err
	}
	if len(paths) == 0 {
		return UninstallResult{}, fmt.Errorf("managed-file manifest is missing or empty")
	}
	sort.Strings(paths)
	if err := os.MkdirAll(transactionDir, 0o700); err != nil {
		return UninstallResult{}, err
	}
	journal := transactionJournal{
		ID: transactionID, RelayID: relayID, StartedAtUTC: now,
		State: "uninstalling", SpecPath: filepath.Join(relayRoot, "spec.json"), Backups: make(map[string]string),
	}
	for index, path := range paths {
		if err := snapshotManagedFile(path, filepath.Join(transactionDir, "backups", fmt.Sprintf("%03d", index)), &journal); err != nil {
			return UninstallResult{}, err
		}
	}
	if err := writeJournal(journalPath, journal); err != nil {
		return UninstallResult{}, err
	}
	deleted := make([]string, 0, len(paths))
	for _, path := range paths {
		if err := os.Remove(path); err != nil {
			if errors.Is(err, os.ErrNotExist) {
				continue
			}
			return UninstallResult{}, failAndRollback(journalPath, &journal, err, options.AfterRollback)
		}
		deleted = append(deleted, path)
		journal.DeletedPaths = append(journal.DeletedPaths, path)
	}
	if options.AfterWrite != nil {
		if err := options.AfterWrite(); err != nil {
			return UninstallResult{}, failAndRollback(journalPath, &journal, err, options.AfterRollback)
		}
	}
	journal.State = "completed"
	journal.Changed = len(deleted) > 0
	journal.CompletedAtUTC = options.Now().UTC()
	if err := writeJournal(journalPath, journal); err != nil {
		return UninstallResult{}, err
	}
	return UninstallResult{RelayID: relayID, TransactionID: transactionID, DeletedFiles: deleted}, nil
}
