package host

import (
	"os"
	"path/filepath"
	"runtime"
	"testing"
)

func TestWriteFileAtomicCorrectsModeWithoutContentChange(t *testing.T) {
	path := filepath.Join(t.TempDir(), "secret")
	if err := os.WriteFile(path, []byte("secret"), 0o644); err != nil {
		t.Fatal(err)
	}
	changed, err := WriteFileAtomic(path, []byte("secret"), 0o600)
	if err != nil {
		t.Fatal(err)
	}
	if runtime.GOOS != "windows" {
		if !changed {
			t.Fatal("permission correction should report a change")
		}
		info, err := os.Stat(path)
		if err != nil {
			t.Fatal(err)
		}
		if info.Mode().Perm() != 0o600 {
			t.Fatalf("mode was not corrected: %o", info.Mode().Perm())
		}
	}
}
