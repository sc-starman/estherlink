package host

import "testing"

func TestValidatePackageNamesSortsDeduplicatesAndRejectsInjection(t *testing.T) {
	result, err := ValidatePackageNames([]string{"openvpn", "dnsmasq", "openvpn"})
	if err != nil {
		t.Fatal(err)
	}
	if len(result) != 2 || result[0] != "dnsmasq" || result[1] != "openvpn" {
		t.Fatalf("unexpected packages: %+v", result)
	}
	if _, err := ValidatePackageNames([]string{"openvpn;rm"}); err == nil {
		t.Fatal("expected unsafe package name rejection")
	}
}
