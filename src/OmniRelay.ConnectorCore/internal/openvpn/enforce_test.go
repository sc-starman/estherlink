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
