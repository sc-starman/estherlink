package openvpn

import (
	"strings"
	"testing"
)

func TestAssignAddressesPreservesValidExistingAndAllocatesDeterministically(t *testing.T) {
	result, err := AssignAddresses("10.29.0.0/29", []string{"beta", "alpha"}, map[string]string{"beta": "10.29.0.5"})
	if err != nil {
		t.Fatal(err)
	}
	if result["beta"] != "10.29.0.5" || result["alpha"] != "10.29.0.2" {
		t.Fatalf("unexpected assignments: %+v", result)
	}
}

func TestRenderServerConfigContainsManagedRuntimePaths(t *testing.T) {
	content, err := RenderServerConfig(ServerOptions{
		RelayID: "e4ccc282a1004b62ad2cda5770d6e32d", PublicPort: 443, Network: "10.29.0.0/24",
		Interface: "omniovpn", CAFile: "/ca", CertFile: "/cert", KeyFile: "/key",
		TLSCryptFile: "/tls", AuthVerifyCommand: "/usr/local/bin/connector-core openvpn authenticate --relay-id e4cc", CCDDir: "/ccd", PoolFile: "/pool", StatusFile: "/status",
		ClientDNS: "10.29.0.1",
	})
	if err != nil {
		t.Fatal(err)
	}
	for _, expected := range []string{
		"server 10.29.0.0 255.255.255.0",
		`push "dhcp-option DNS 10.29.0.1"`,
		`auth-user-pass-verify "/usr/local/bin/connector-core openvpn authenticate --relay-id e4cc" via-file`,
		"management 127.0.0.1 8077",
	} {
		if !strings.Contains(string(content), expected) {
			t.Fatalf("config missing %q: %s", expected, content)
		}
	}
}

func TestInterfaceNameIsRelayScopedAndLinuxSafe(t *testing.T) {
	name := InterfaceName("e4ccc282a1004b62ad2cda5770d6e32d")
	if name != "omnie4ccc28" || len(name) > 15 {
		t.Fatalf("unexpected interface name: %q", name)
	}
}

func TestRenderAuthEntriesExcludesDisabledClients(t *testing.T) {
	content := RenderAuthEntries([]Client{
		{Identity: "enabled", Secret: "secret", Enabled: true},
		{Identity: "disabled", Secret: "secret", Enabled: false},
	})
	if !strings.Contains(string(content), "enabled:") || strings.Contains(string(content), "disabled:") {
		t.Fatalf("unexpected auth entries: %s", content)
	}
}
