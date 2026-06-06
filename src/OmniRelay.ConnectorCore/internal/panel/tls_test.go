package panel

import (
	"context"
	"os"
	"path/filepath"
	"testing"

	"github.com/omnirelay/connector-core/internal/spec"
)

type tlsRunner struct {
	called bool
}

func (r *tlsRunner) Run(_ context.Context, name string, args ...string) error {
	r.called = true
	if name == "openssl" {
		for index, arg := range args {
			if arg == "-keyout" {
				_ = os.WriteFile(args[index+1], []byte("-----BEGIN PRIVATE KEY-----\nkey\n"), 0o600)
			}
			if arg == "-out" {
				_ = os.WriteFile(args[index+1], []byte("-----BEGIN CERTIFICATE-----\ncert\n"), 0o644)
			}
		}
	}
	return nil
}

func TestLoadUploadedTLSAssetsFallsBackToManagedState(t *testing.T) {
	root := t.TempDir()
	cert, key := ManagedTLSPaths(root)
	if err := os.MkdirAll(filepath.Dir(cert), 0o700); err != nil {
		t.Fatal(err)
	}
	_ = os.WriteFile(cert, []byte("-----BEGIN CERTIFICATE-----\ncert\n"), 0o644)
	_ = os.WriteFile(key, []byte("-----BEGIN PRIVATE KEY-----\nkey\n"), 0o600)
	assets, available, err := LoadUploadedTLSAssets(filepath.Join(root, "missing.crt"), filepath.Join(root, "missing.key"), root)
	if err != nil || !available || len(assets.PrivateKey) == 0 {
		t.Fatalf("unexpected managed fallback: available=%v err=%v", available, err)
	}
}

func TestEnsureTLSSelfSignedCreatesManagedAssets(t *testing.T) {
	root := t.TempDir()
	gatewaySpec := spec.GatewaySpec{Panel: spec.PanelSpec{TLSEnabled: true, TLSMode: "self_signed", PublicHost: "panel.example.test"}}
	runner := &tlsRunner{}
	changed, err := EnsureTLS(context.Background(), gatewaySpec, root, runner)
	if err != nil || !changed || !runner.called {
		t.Fatalf("unexpected self-signed result: changed=%v called=%v err=%v", changed, runner.called, err)
	}
	cert, key := ManagedTLSPaths(root)
	if !filesExist(cert, key) {
		t.Fatal("managed TLS assets were not created")
	}
}
