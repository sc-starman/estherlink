package panel

import (
	"context"
	"crypto/sha256"
	"encoding/hex"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"testing"
)

func TestDownloadArtifactVerifiesAndCaches(t *testing.T) {
	payload := []byte("panel-archive")
	hash := sha256.Sum256(payload)
	expected := hex.EncodeToString(hash[:])
	requests := 0
	server := httptest.NewServer(http.HandlerFunc(func(writer http.ResponseWriter, request *http.Request) {
		requests++
		_, _ = writer.Write(payload)
	}))
	defer server.Close()
	options := DownloadOptions{URL: server.URL, SHA256: expected, CacheRoot: filepath.Join(t.TempDir(), "cache")}
	first, err := DownloadArtifact(context.Background(), options)
	if err != nil || !first.Changed {
		t.Fatalf("first download failed: %+v err=%v", first, err)
	}
	second, err := DownloadArtifact(context.Background(), options)
	if err != nil || second.Changed || requests != 1 {
		t.Fatalf("cached download failed: %+v requests=%d err=%v", second, requests, err)
	}
	content, err := os.ReadFile(second.Path)
	if err != nil || string(content) != string(payload) {
		t.Fatalf("unexpected cached artifact: %q err=%v", content, err)
	}
}

func TestDownloadArtifactRejectsHashMismatch(t *testing.T) {
	server := httptest.NewServer(http.HandlerFunc(func(writer http.ResponseWriter, request *http.Request) {
		_, _ = writer.Write([]byte("wrong"))
	}))
	defer server.Close()
	if _, err := DownloadArtifact(context.Background(), DownloadOptions{
		URL: server.URL, SHA256: string(make([]byte, 64)), CacheRoot: t.TempDir(),
	}); err == nil {
		t.Fatal("expected hash mismatch")
	}
}
