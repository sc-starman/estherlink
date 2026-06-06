package clock

import (
	"context"
	"io"
	"net"
	"net/http"
	"net/http/httptest"
	"strconv"
	"testing"
	"time"

	"github.com/omnirelay/connector-core/internal/probe"
)

func TestSyncAppliesRemoteDateWhenSkewExceedsThreshold(t *testing.T) {
	remote := time.Date(2026, 6, 4, 10, 0, 0, 0, time.UTC)
	target := httptest.NewServer(http.HandlerFunc(func(writer http.ResponseWriter, _ *http.Request) {
		writer.Header().Set("Date", remote.Format(http.TimeFormat))
		writer.WriteHeader(http.StatusNoContent)
	}))
	defer target.Close()
	socksAddress, stop := clockSOCKSProxy(t)
	defer stop()
	host, portText, _ := net.SplitHostPort(socksAddress)
	port, _ := strconv.Atoi(portText)
	current := remote.Add(-time.Hour)
	result := Sync(context.Background(), Options{
		Probe: probeOptions(host, port, target.URL),
		Now:   func() time.Time { return current },
		Set:   func(value time.Time) error { current = value; return nil },
	})
	if !result.OK || !result.Applied || result.SkewSecAfter != 0 {
		t.Fatalf("unexpected result: %+v", result)
	}
}

func TestSyncDoesNotFailWhenClockAlreadyWithinThreshold(t *testing.T) {
	remote := time.Date(2026, 6, 4, 10, 0, 0, 0, time.UTC)
	target := httptest.NewServer(http.HandlerFunc(func(writer http.ResponseWriter, _ *http.Request) {
		writer.Header().Set("Date", remote.Format(http.TimeFormat))
		writer.WriteHeader(http.StatusNoContent)
	}))
	defer target.Close()
	socksAddress, stop := clockSOCKSProxy(t)
	defer stop()
	host, portText, _ := net.SplitHostPort(socksAddress)
	port, _ := strconv.Atoi(portText)
	result := Sync(context.Background(), Options{
		Probe: probeOptions(host, port, target.URL),
		Now:   func() time.Time { return remote.Add(2 * time.Second) },
	})
	if !result.OK || result.Applied {
		t.Fatalf("unexpected result: %+v", result)
	}
}

func probeOptions(host string, port int, target string) probe.BackendOptions {
	return probe.BackendOptions{Host: host, Port: port, Targets: []string{target}, Timeout: time.Second}
}

func clockSOCKSProxy(t *testing.T) (string, func()) {
	t.Helper()
	listener, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	go func() {
		for {
			conn, acceptErr := listener.Accept()
			if acceptErr != nil {
				return
			}
			go clockServeSOCKS(conn)
		}
	}()
	return listener.Addr().String(), func() { _ = listener.Close() }
}

func clockServeSOCKS(client net.Conn) {
	defer client.Close()
	greeting := make([]byte, 3)
	if _, err := io.ReadFull(client, greeting); err != nil {
		return
	}
	if _, err := client.Write([]byte{0x05, 0x00}); err != nil {
		return
	}
	header := make([]byte, 4)
	if _, err := io.ReadFull(client, header); err != nil {
		return
	}
	var host string
	if header[3] == 0x01 {
		raw := make([]byte, 4)
		if _, err := io.ReadFull(client, raw); err != nil {
			return
		}
		host = net.IP(raw).String()
	} else {
		length := make([]byte, 1)
		if _, err := io.ReadFull(client, length); err != nil {
			return
		}
		raw := make([]byte, int(length[0]))
		if _, err := io.ReadFull(client, raw); err != nil {
			return
		}
		host = string(raw)
	}
	rawPort := make([]byte, 2)
	if _, err := io.ReadFull(client, rawPort); err != nil {
		return
	}
	port := int(rawPort[0])<<8 | int(rawPort[1])
	upstream, err := net.Dial("tcp", net.JoinHostPort(host, strconv.Itoa(port)))
	if err != nil {
		return
	}
	defer upstream.Close()
	_, _ = client.Write([]byte{0x05, 0x00, 0x00, 0x01, 0, 0, 0, 0, 0, 0})
	go func() { _, _ = io.Copy(upstream, client) }()
	_, _ = io.Copy(client, upstream)
}
