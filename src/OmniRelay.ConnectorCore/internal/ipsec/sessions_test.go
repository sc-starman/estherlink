package ipsec

import (
	"os"
	"path/filepath"
	"strings"
	"testing"
)

func TestUpdateSessionAddsAndRemovesDeterministically(t *testing.T) {
	path := filepath.Join(t.TempDir(), "sessions.tsv")
	if changed, err := UpdateSession(path, "up", Session{Interface: "ppp1", Username: "bob"}); err != nil || !changed {
		t.Fatalf("add bob: changed=%v err=%v", changed, err)
	}
	if changed, err := UpdateSession(path, "up", Session{Interface: "ppp0", Username: "alice"}); err != nil || !changed {
		t.Fatalf("add alice: changed=%v err=%v", changed, err)
	}
	content, err := os.ReadFile(path)
	if err != nil {
		t.Fatal(err)
	}
	if string(content) != "ppp0\talice\nppp1\tbob\n" {
		t.Fatalf("unexpected sessions: %q", content)
	}
	if changed, err := UpdateSession(path, "down", Session{Interface: "ppp0"}); err != nil || !changed {
		t.Fatalf("remove alice: changed=%v err=%v", changed, err)
	}
}

func TestRenderSessionHookCallsConnectorCore(t *testing.T) {
	content, err := RenderSessionHook("abc123", "up", "/usr/local/bin/connector-core")
	if err != nil {
		t.Fatal(err)
	}
	if !strings.Contains(string(content), "connector-core ipsec session --relay-id abc123 --action up") {
		t.Fatalf("unexpected hook: %s", content)
	}
}
