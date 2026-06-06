package probe

import (
	"context"
	"crypto/tls"
	"fmt"
	"io"
	"net"
	"net/http"
	"strings"
	"time"

	"golang.org/x/net/proxy"
)

type BackendOptions struct {
	Host               string
	Port               int
	Targets            []string
	Timeout            time.Duration
	InsecureSkipVerify bool
}

type BackendResult struct {
	OK              bool            `json:"ok"`
	Healthy         bool            `json:"healthy"`
	ReasonCode      string          `json:"reasonCode"`
	Message         string          `json:"message"`
	BackendHost     string          `json:"backendHost"`
	BackendPort     int             `json:"backendPort"`
	BackendListener bool            `json:"backendListener"`
	BackendProtocol string          `json:"backendProtocol"`
	EgressReachable bool            `json:"egressReachable"`
	SuccessTarget   string          `json:"successTarget,omitempty"`
	DateHeader      string          `json:"dateHeader,omitempty"`
	Targets         []TargetOutcome `json:"targets"`
	CheckedAtUTC    string          `json:"checkedAtUtc"`
}

type TargetOutcome struct {
	URL        string `json:"url"`
	OK         bool   `json:"ok"`
	StatusCode int    `json:"statusCode,omitempty"`
	DateHeader string `json:"dateHeader,omitempty"`
	Error      string `json:"error,omitempty"`
}

func Backend(ctx context.Context, options BackendOptions) BackendResult {
	options = withDefaults(options)
	result := BackendResult{
		BackendHost: options.Host, BackendPort: options.Port,
		Targets:      make([]TargetOutcome, 0, len(options.Targets)),
		CheckedAtUTC: time.Now().UTC().Format(time.RFC3339),
	}
	protocol, listener := classifyBackend(ctx, options.Host, options.Port, options.Timeout)
	result.BackendListener = listener
	result.BackendProtocol = protocol
	switch protocol {
	case "socks5":
		result.Targets = SOCKSEgress(ctx, options)
		for _, outcome := range result.Targets {
			if outcome.OK {
				result.OK = true
				result.Healthy = true
				result.EgressReachable = true
				result.SuccessTarget = outcome.URL
				result.DateHeader = outcome.DateHeader
				result.ReasonCode = "ok"
				result.Message = "Tunnel path is healthy."
				return result
			}
		}
		result.ReasonCode = "egress_probe_failed"
		result.Message = "Backend responded but all egress probes failed."
	case "unreachable":
		result.ReasonCode = "backend_unreachable"
		result.Message = "Backend endpoint is unreachable."
	default:
		result.ReasonCode = "backend_protocol_not_socks5"
		result.Message = "Backend endpoint is reachable but is not SOCKS5."
	}
	return result
}

func SOCKSEgress(ctx context.Context, options BackendOptions) []TargetOutcome {
	options = withDefaults(options)
	address := net.JoinHostPort(options.Host, fmt.Sprint(options.Port))
	dialer, err := proxy.SOCKS5("tcp", address, nil, &net.Dialer{Timeout: options.Timeout})
	if err != nil {
		return []TargetOutcome{{Error: err.Error()}}
	}
	transport := &http.Transport{
		DialContext: func(ctx context.Context, network string, address string) (net.Conn, error) {
			type result struct {
				conn net.Conn
				err  error
			}
			done := make(chan result, 1)
			go func() {
				conn, dialErr := dialer.Dial(network, address)
				done <- result{conn: conn, err: dialErr}
			}()
			select {
			case <-ctx.Done():
				return nil, ctx.Err()
			case value := <-done:
				return value.conn, value.err
			}
		},
		TLSClientConfig: &tls.Config{InsecureSkipVerify: options.InsecureSkipVerify}, //nolint:gosec
	}
	defer transport.CloseIdleConnections()
	client := &http.Client{Transport: transport}
	outcomes := make([]TargetOutcome, 0, len(options.Targets))
	for _, target := range options.Targets {
		outcome := TargetOutcome{URL: target}
		requestCtx, cancel := context.WithTimeout(ctx, options.Timeout)
		request, err := http.NewRequestWithContext(requestCtx, http.MethodGet, target, nil)
		if err == nil {
			var response *http.Response
			response, err = client.Do(request)
			if response != nil {
				outcome.StatusCode = response.StatusCode
				outcome.DateHeader = response.Header.Get("Date")
				_, _ = io.Copy(io.Discard, io.LimitReader(response.Body, 4096))
				_ = response.Body.Close()
				outcome.OK = response.StatusCode < http.StatusBadRequest
			}
		}
		cancel()
		if err != nil {
			outcome.Error = normalizeError(err)
		} else if !outcome.OK {
			outcome.Error = fmt.Sprintf("HTTP status %d", outcome.StatusCode)
		}
		outcomes = append(outcomes, outcome)
	}
	return outcomes
}

func classifyBackend(ctx context.Context, host string, port int, timeout time.Duration) (string, bool) {
	dialer := net.Dialer{Timeout: timeout}
	conn, err := dialer.DialContext(ctx, "tcp", net.JoinHostPort(host, fmt.Sprint(port)))
	if err != nil {
		return "unreachable", false
	}
	defer conn.Close()
	deadline := time.Now().Add(timeout)
	_ = conn.SetDeadline(deadline)
	if _, err := conn.Write([]byte{0x05, 0x01, 0x00}); err != nil {
		return "unknown", true
	}
	response := make([]byte, 2)
	if _, err := io.ReadFull(conn, response); err != nil {
		return "unknown", true
	}
	if response[0] == 0x05 {
		return "socks5", true
	}
	return "unknown", true
}

func withDefaults(options BackendOptions) BackendOptions {
	if strings.TrimSpace(options.Host) == "" {
		options.Host = "127.0.0.1"
	}
	if options.Timeout <= 0 {
		options.Timeout = 20 * time.Second
	}
	if len(options.Targets) == 0 {
		options.Targets = []string{"https://8.8.8.8/", "https://dns.google/", "https://9.9.9.9/"}
	}
	return options
}

func normalizeError(err error) string {
	message := strings.Join(strings.Fields(err.Error()), " ")
	if len(message) > 300 {
		return message[:300]
	}
	return message
}
