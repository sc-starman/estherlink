package openvpn

import (
	"os"
	"path/filepath"
	"testing"
)

func TestLoadSharedAssetsCopiesSourcesAndFallsBackToManagedAssets(t *testing.T) {
	root := t.TempDir()
	sourceRoot := filepath.Join(root, "upload")
	managedRoot := filepath.Join(root, "managed")
	writeAsset := func(path string, content string) {
		t.Helper()
		if err := os.MkdirAll(filepath.Dir(path), 0o700); err != nil {
			t.Fatal(err)
		}
		if err := os.WriteFile(path, []byte(content), 0o600); err != nil {
			t.Fatal(err)
		}
	}
	sources := SharedAssetSources{
		CACert: filepath.Join(sourceRoot, "ca.crt"), ClientCert: filepath.Join(sourceRoot, "client.crt"),
		ClientKey: filepath.Join(sourceRoot, "client.key"), TLSCryptKey: filepath.Join(sourceRoot, "ta.key"),
	}
	writeAsset(sources.CACert, "-----BEGIN CERTIFICATE-----\nca\n")
	writeAsset(sources.ClientCert, "-----BEGIN CERTIFICATE-----\nclient\n")
	writeAsset(sources.ClientKey, "-----BEGIN PRIVATE KEY-----\nkey\n")
	writeAsset(sources.TLSCryptKey, "-----BEGIN OpenVPN Static key V1-----\nkey\n")

	assets, available, err := LoadSharedAssets(sources, managedRoot)
	if err != nil || !available || len(assets.ClientKey) == 0 {
		t.Fatalf("unexpected source load: available=%v err=%v", available, err)
	}
	writeAsset(filepath.Join(managedRoot, "ca.crt"), string(assets.CACert))
	writeAsset(filepath.Join(managedRoot, "client-shared.crt"), string(assets.ClientCert))
	writeAsset(filepath.Join(managedRoot, "client-shared.key"), string(assets.ClientKey))
	writeAsset(filepath.Join(managedRoot, "ta.key"), string(assets.TLSCryptKey))
	if _, available, err := LoadSharedAssets(SharedAssetSources{}, managedRoot); err != nil || !available {
		t.Fatalf("unexpected managed fallback: available=%v err=%v", available, err)
	}
}

func TestLoadSharedAssetsRejectsPartialManagedState(t *testing.T) {
	root := t.TempDir()
	if err := os.WriteFile(filepath.Join(root, "ca.crt"), []byte("-----BEGIN CERTIFICATE-----\n"), 0o600); err != nil {
		t.Fatal(err)
	}
	if _, _, err := LoadSharedAssets(SharedAssetSources{}, root); err == nil {
		t.Fatal("expected incomplete managed asset error")
	}
}
