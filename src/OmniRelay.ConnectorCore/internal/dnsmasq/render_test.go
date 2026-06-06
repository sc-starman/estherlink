package dnsmasq

import (
	"strings"
	"testing"
)

func TestRenderOmitsGlobalSingletonDirectives(t *testing.T) {
	content, err := Render("ppp+", "10.39.0.1", "127.0.0.1", 38845)
	if err != nil {
		t.Fatal(err)
	}
	for _, forbidden := range []string{"bind-dynamic", "no-resolv", "cache-size"} {
		if strings.Contains(string(content), forbidden) {
			t.Fatalf("per-relay dnsmasq config contains global directive %q", forbidden)
		}
	}
}
