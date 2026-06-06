package ipsec

import (
	"strings"
	"testing"
)

func TestRenderIPSecL2TPDesiredState(t *testing.T) {
	config, err := RenderIPSecConfig(ConnectionName("e4ccc282a1004b62ad2cda5770d6e32d"))
	if err != nil {
		t.Fatal(err)
	}
	secrets, err := RenderSecrets(`secret-with-"quote"-value`)
	if err != nil {
		t.Fatal(err)
	}
	xl2tpd, err := RenderXL2TPD("10.39.0.0/24", Paths{PPPOptions: "/etc/omnirelay/relay/ppp/options"})
	if err != nil {
		t.Fatal(err)
	}
	for _, pair := range []struct {
		content []byte
		value   string
	}{
		{config, "conn L2TP-PSK-e4ccc282"},
		{secrets, `PSK "secret-with-\"quote\"-value"`},
		{xl2tpd, "ip range = 10.39.0.2-10.39.0.254"},
		{xl2tpd, "local ip = 10.39.0.1"},
	} {
		if !strings.Contains(string(pair.content), pair.value) {
			t.Fatalf("rendered content missing %q: %s", pair.value, pair.content)
		}
	}
}

func TestRenderChapSecretsExcludesDisabledAndQuotesValues(t *testing.T) {
	content := RenderChapSecrets([]Client{
		{Username: "enabled user", Secret: `sec"ret`, Enabled: true},
		{Username: "disabled", Secret: "secret", Enabled: false},
	})
	if !strings.Contains(string(content), `"enabled user" l2tpd "sec\"ret" *`) || strings.Contains(string(content), "disabled") {
		t.Fatalf("unexpected chap secrets: %s", content)
	}
}
