package ipsec

import (
	"context"
	"errors"
	"os"
	"path/filepath"
	"testing"
)

type fakeServiceManager struct {
	restartErr error
	stopCalls  int
	restarts   int
}

func (manager *fakeServiceManager) Restart(context.Context, ...string) error {
	manager.restarts++
	return manager.restartErr
}

func (manager *fakeServiceManager) Stop(context.Context, ...string) error {
	manager.stopCalls++
	return nil
}

func TestActivateAndDeactivateRestoresOriginalGlobalFiles(t *testing.T) {
	root := t.TempDir()
	configRoot := filepath.Join(root, "omnirelay")
	globalRoot := filepath.Join(root, "etc")
	relayID := "e4ccc282a1004b62ad2cda5770d6e32d"
	writeRelayIPSecFiles(t, configRoot, relayID)
	originalPath := filepath.Join(globalRoot, "ipsec.conf")
	writeActivationFile(t, originalPath, "administrator-config")
	manager := &fakeServiceManager{}
	paths := ActivationPaths{ConfigRoot: configRoot, GlobalRoot: globalRoot}
	result, err := Activate(context.Background(), relayID, paths, manager)
	if err != nil || !result.Active || !result.Changed {
		t.Fatalf("activate failed: %+v err=%v", result, err)
	}
	content, _ := os.ReadFile(originalPath)
	if string(content) != "managed-ipsec" {
		t.Fatalf("global config was not activated: %s", content)
	}
	if _, err := Activate(context.Background(), "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", paths, manager); err == nil {
		t.Fatal("expected single-owner conflict")
	}
	result, err = Deactivate(context.Background(), relayID, paths, manager)
	if err != nil || result.Active {
		t.Fatalf("deactivate failed: %+v err=%v", result, err)
	}
	content, _ = os.ReadFile(originalPath)
	if string(content) != "administrator-config" {
		t.Fatalf("administrator config was not restored: %s", content)
	}
}

func TestActivateRollsBackFilesWhenServiceRestartFails(t *testing.T) {
	root := t.TempDir()
	configRoot := filepath.Join(root, "omnirelay")
	globalRoot := filepath.Join(root, "etc")
	relayID := "e4ccc282a1004b62ad2cda5770d6e32d"
	writeRelayIPSecFiles(t, configRoot, relayID)
	originalPath := filepath.Join(globalRoot, "ipsec.conf")
	writeActivationFile(t, originalPath, "administrator-config")
	manager := &fakeServiceManager{restartErr: errors.New("restart failed")}
	_, err := Activate(context.Background(), relayID, ActivationPaths{ConfigRoot: configRoot, GlobalRoot: globalRoot}, manager)
	if err == nil {
		t.Fatal("expected activation failure")
	}
	content, _ := os.ReadFile(originalPath)
	if string(content) != "administrator-config" {
		t.Fatalf("failed activation did not roll back: %s", content)
	}
}

func TestSyncActiveChapSecretsUpdatesOnlyOwningRelay(t *testing.T) {
	root := t.TempDir()
	configRoot := filepath.Join(root, "omnirelay")
	globalRoot := filepath.Join(root, "etc")
	relayID := "e4ccc282a1004b62ad2cda5770d6e32d"
	writeRelayIPSecFiles(t, configRoot, relayID)
	manager := &fakeServiceManager{}
	paths := ActivationPaths{ConfigRoot: configRoot, GlobalRoot: globalRoot}
	if _, err := Activate(context.Background(), relayID, paths, manager); err != nil {
		t.Fatal(err)
	}
	relayChap := filepath.Join(configRoot, "relays", relayID, "gateway", "ipsec-l2tp", "chap-secrets")
	writeActivationFile(t, relayChap, "new-client")
	changed, err := SyncActiveChapSecrets(relayID, paths)
	if err != nil || !changed {
		t.Fatalf("active CHAP sync failed: changed=%v err=%v", changed, err)
	}
	content, _ := os.ReadFile(filepath.Join(globalRoot, "ppp", "chap-secrets"))
	if string(content) != "new-client" {
		t.Fatalf("global CHAP secrets were not updated: %s", content)
	}
	if _, err := SyncActiveChapSecrets("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", paths); err == nil {
		t.Fatal("expected non-owner CHAP sync rejection")
	}
}

func writeRelayIPSecFiles(t *testing.T, configRoot string, relayID string) {
	t.Helper()
	root := filepath.Join(configRoot, "relays", relayID, "gateway", "ipsec-l2tp")
	for name, content := range map[string]string{
		"ipsec.conf": "managed-ipsec", "ipsec.secrets": "managed-secret",
		"xl2tpd.conf": "managed-xl2tpd", "ppp-options": "managed-ppp", "chap-secrets": "managed-chap",
	} {
		writeActivationFile(t, filepath.Join(root, name), content)
	}
}

func writeActivationFile(t *testing.T, path string, content string) {
	t.Helper()
	if err := os.MkdirAll(filepath.Dir(path), 0o700); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(path, []byte(content), 0o600); err != nil {
		t.Fatal(err)
	}
}
