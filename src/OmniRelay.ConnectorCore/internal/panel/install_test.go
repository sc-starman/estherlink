package panel

import (
	"archive/tar"
	"compress/gzip"
	"crypto/sha256"
	"encoding/hex"
	"os"
	"path/filepath"
	"runtime"
	"strings"
	"testing"
)

func TestInstallArtifactVerifiesExtractsAndActivatesIdempotently(t *testing.T) {
	if runtime.GOOS == "windows" {
		t.Skip("symlink activation requires Windows developer mode or elevated privileges")
	}
	root := t.TempDir()
	artifact := filepath.Join(root, "panel.tar.gz")
	writePanelArchive(t, artifact, "bundle/server.js", "console.log('ok')")
	content, _ := os.ReadFile(artifact)
	hash := sha256.Sum256(content)
	expected := hex.EncodeToString(hash[:])
	first, err := InstallArtifact(artifact, expected, filepath.Join(root, "app"))
	if err != nil || !first.Changed {
		t.Fatalf("first install failed: %+v err=%v", first, err)
	}
	if err := ValidateCurrent(filepath.Join(root, "app")); err != nil {
		t.Fatal(err)
	}
	second, err := InstallArtifact(artifact, expected, filepath.Join(root, "app"))
	if err != nil || second.Changed {
		t.Fatalf("second install should be unchanged: %+v err=%v", second, err)
	}
}

func TestInstallArtifactRejectsTraversalAndHashMismatch(t *testing.T) {
	root := t.TempDir()
	artifact := filepath.Join(root, "panel.tar.gz")
	writePanelArchive(t, artifact, "../server.js", "unsafe")
	if _, err := InstallArtifact(artifact, strings.Repeat("0", 64), filepath.Join(root, "app")); err == nil {
		t.Fatal("expected hash mismatch")
	}
	content, _ := os.ReadFile(artifact)
	hash := sha256.Sum256(content)
	if _, err := InstallArtifact(artifact, hex.EncodeToString(hash[:]), filepath.Join(root, "app")); err == nil {
		t.Fatal("expected traversal rejection")
	}
}

func writePanelArchive(t *testing.T, path string, name string, content string) {
	t.Helper()
	file, err := os.Create(path)
	if err != nil {
		t.Fatal(err)
	}
	gzipWriter := gzip.NewWriter(file)
	tarWriter := tar.NewWriter(gzipWriter)
	if err := tarWriter.WriteHeader(&tar.Header{Name: name, Mode: 0o644, Size: int64(len(content)), Typeflag: tar.TypeReg}); err != nil {
		t.Fatal(err)
	}
	if _, err := tarWriter.Write([]byte(content)); err != nil {
		t.Fatal(err)
	}
	if err := tarWriter.Close(); err != nil {
		t.Fatal(err)
	}
	if err := gzipWriter.Close(); err != nil {
		t.Fatal(err)
	}
	if err := file.Close(); err != nil {
		t.Fatal(err)
	}
}
