package probe

import (
	"context"
	"io"
	"net"
	"net/http"
	"net/http/httptest"
	"strconv"
	"testing"
	"time"
)

func TestBackendClassifiesReachableNonSOCKSListener(t *testing.T) {
	listener, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	defer listener.Close()
	go func() {
		conn, acceptErr := listener.Accept()
		if acceptErr == nil {
			_ = conn.Close()
		}
	}()
	host, port := listenerAddress(t, listener)
	result := Backend(context.Background(), BackendOptions{
		Host: host, Port: port, Targets: []string{"https://invalid.test/"}, Timeout: time.Second,
	})
	if result.BackendProtocol != "unknown" || result.ReasonCode != "backend_protocol_not_socks5" {
		t.Fatalf("unexpected result: %+v", result)
	}
}

func TestBackendPassesWhenAnySOCKSTargetSucceeds(t *testing.T) {
	target := httptest.NewServer(http.HandlerFunc(func(writer http.ResponseWriter, _ *http.Request) {
		writer.Header().Set("Date", "Thu, 04 Jun 2026 10:00:00 GMT")
		writer.WriteHeader(http.StatusNoContent)
	}))
	defer target.Close()
	socksAddress, stop := startSOCKSProxy(t)
	defer stop()
	host, portText, err := net.SplitHostPort(socksAddress)
	if err != nil {
		t.Fatal(err)
	}
	port, _ := strconv.Atoi(portText)
	result := Backend(context.Background(), BackendOptions{
		Host: host, Port: port, Targets: []string{"http://127.0.0.1:1/", target.URL}, Timeout: 2 * time.Second,
	})
	if !result.Healthy || result.SuccessTarget != target.URL || len(result.Targets) != 2 {
		t.Fatalf("unexpected result: %+v", result)
	}
}

func startSOCKSProxy(t *testing.T) (string, func()) {
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
			go serveSOCKS(conn)
		}
	}()
	return listener.Addr().String(), func() { _ = listener.Close() }
}

func serveSOCKS(client net.Conn) {
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
	switch header[3] {
	case 0x01:
		raw := make([]byte, 4)
		if _, err := io.ReadFull(client, raw); err != nil {
			return
		}
		host = net.IP(raw).String()
	case 0x03:
		length := make([]byte, 1)
		if _, err := io.ReadFull(client, length); err != nil {
			return
		}
		raw := make([]byte, int(length[0]))
		if _, err := io.ReadFull(client, raw); err != nil {
			return
		}
		host = string(raw)
	default:
		return
	}
	rawPort := make([]byte, 2)
	if _, err := io.ReadFull(client, rawPort); err != nil {
		return
	}
	port := int(rawPort[0])<<8 | int(rawPort[1])
	upstream, err := net.Dial("tcp", net.JoinHostPort(host, strconv.Itoa(port)))
	if err != nil {
		_, _ = client.Write([]byte{0x05, 0x05, 0x00, 0x01, 0, 0, 0, 0, 0, 0})
		return
	}
	defer upstream.Close()
	if _, err := client.Write([]byte{0x05, 0x00, 0x00, 0x01, 0, 0, 0, 0, 0, 0}); err != nil {
		return
	}
	go func() { _, _ = io.Copy(upstream, client) }()
	_, _ = io.Copy(client, upstream)
}

func listenerAddress(t *testing.T, listener net.Listener) (string, int) {
	t.Helper()
	host, portText, err := net.SplitHostPort(listener.Addr().String())
	if err != nil {
		t.Fatal(err)
	}
	port, err := strconv.Atoi(portText)
	if err != nil {
		t.Fatal(err)
	}
	return host, port
}
