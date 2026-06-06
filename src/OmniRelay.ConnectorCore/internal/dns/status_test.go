package dns

import (
	"encoding/binary"
	"testing"
)

func TestBuildQueryFramesDNSOverTCP(t *testing.T) {
	query := buildQuery(0x4f52, "example.com")
	if int(binary.BigEndian.Uint16(query[:2])) != len(query)-2 {
		t.Fatalf("invalid DNS TCP frame length")
	}
	if binary.BigEndian.Uint16(query[2:4]) != 0x4f52 || binary.BigEndian.Uint16(query[6:8]) != 1 {
		t.Fatalf("invalid DNS query header: %x", query)
	}
}
