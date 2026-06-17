package openvpn

import "testing"

func TestParseManagementClientsUsesDynamicHeader(t *testing.T) {
	content := ">INFO:OpenVPN Management Interface\n" +
		"HEADER,CLIENT_LIST,Common Name,Real Address,Virtual Address,Username,Client ID\n" +
		"CLIENT_LIST,client-cn,192.0.2.1:1234,10.29.0.2,user-one,7\nEND\n"
	clients, err := parseManagementClients(content)
	if err != nil {
		t.Fatal(err)
	}
	if len(clients) != 1 || clients[0].Username != "user-one" || clients[0].ClientID != "7" {
		t.Fatalf("unexpected clients: %+v", clients)
	}
}

func TestParseManagementClientsUsesWhitespaceFallback(t *testing.T) {
	content := ">INFO:OpenVPN Management Interface\n" +
		"CLIENT_LIST client-cn 192.0.2.1:1234 10.29.0.2 255.255.255.0 100 200 2026-01-01 00:00:00 user-one 7\nEND\n"
	clients, err := parseManagementClients(content)
	if err != nil {
		t.Fatal(err)
	}
	if len(clients) != 1 || clients[0].CommonName != "client-cn" || clients[0].Username != "user-one" || clients[0].ClientID != "7" {
		t.Fatalf("unexpected clients: %+v", clients)
	}
}

func TestParseManagementClientsUsesTabStatusFileFormat(t *testing.T) {
	content := "TITLE\tOpenVPN 2.6.19\n" +
		"HEADER\tCLIENT_LIST\tCommon Name\tReal Address\tVirtual Address\tVirtual IPv6 Address\tBytes Received\tBytes Sent\tConnected Since\tConnected Since (time_t)\tUsername\tClient ID\tPeer ID\tData Channel Cipher\n" +
		"CLIENT_LIST\tovpn_asf\t82.180.219.62:7961\t10.29.0.3\t\t893808\t18589820\t2026-06-17 12:17:33\t1781686053\tovpn_asf\t3\t0\tAES-256-GCM\n" +
		"END\n"
	clients, err := parseManagementClients(content)
	if err != nil {
		t.Fatal(err)
	}
	if len(clients) != 1 || clients[0].CommonName != "ovpn_asf" || clients[0].Username != "ovpn_asf" || clients[0].ClientID != "3" {
		t.Fatalf("unexpected clients: %+v", clients)
	}
}

func TestLegacyClientCommonNameMatchesBashRendering(t *testing.T) {
	got := legacyClientCommonName("8f12ab4d-7fc9-41a1-a431-6f2ea8e49ca9")
	want := "ovpn-8f12ab4d7fc941a1a4316f2ea8e49ca9"
	if got != want {
		t.Fatalf("legacy common name = %q, want %q", got, want)
	}
}
