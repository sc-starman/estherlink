package dns

import (
	"context"
	"encoding/binary"
	"fmt"
	"io"
	"net"
	"strconv"
	"time"
)

type StatusResult struct {
	Healthy       bool   `json:"healthy"`
	ReasonCode    string `json:"reasonCode"`
	ListenAddress string `json:"listenAddress"`
	ListenPort    int    `json:"listenPort"`
	AnswerCount   int    `json:"answerCount"`
}

func Status(ctx context.Context, address string, port int, timeout time.Duration) StatusResult {
	result := StatusResult{ReasonCode: "dns_query_failed", ListenAddress: address, ListenPort: port}
	dialer := net.Dialer{Timeout: timeout}
	connection, err := dialer.DialContext(ctx, "tcp", net.JoinHostPort(address, strconv.Itoa(port)))
	if err != nil {
		result.ReasonCode = "dns_listener_unreachable"
		return result
	}
	defer connection.Close()
	_ = connection.SetDeadline(time.Now().Add(timeout))
	query := buildQuery(0x4f52, "example.com")
	if _, err := connection.Write(query); err != nil {
		return result
	}
	var length [2]byte
	if _, err := io.ReadFull(connection, length[:]); err != nil {
		return result
	}
	response := make([]byte, int(binary.BigEndian.Uint16(length[:])))
	if _, err := io.ReadFull(connection, response); err != nil {
		return result
	}
	if len(response) < 12 || binary.BigEndian.Uint16(response[0:2]) != 0x4f52 {
		result.ReasonCode = "dns_invalid_response"
		return result
	}
	if response[3]&0x0f != 0 {
		result.ReasonCode = fmt.Sprintf("dns_rcode_%d", response[3]&0x0f)
		return result
	}
	result.AnswerCount = int(binary.BigEndian.Uint16(response[6:8]))
	result.Healthy = true
	result.ReasonCode = "ok"
	return result
}

func buildQuery(id uint16, name string) []byte {
	payload := make([]byte, 12)
	binary.BigEndian.PutUint16(payload[0:2], id)
	binary.BigEndian.PutUint16(payload[2:4], 0x0100)
	binary.BigEndian.PutUint16(payload[4:6], 1)
	for _, label := range splitLabels(name) {
		payload = append(payload, byte(len(label)))
		payload = append(payload, label...)
	}
	payload = append(payload, 0, 0, 1, 0, 1)
	result := make([]byte, 2, len(payload)+2)
	binary.BigEndian.PutUint16(result, uint16(len(payload)))
	return append(result, payload...)
}

func splitLabels(name string) []string {
	result := make([]string, 0, 3)
	start := 0
	for index := 0; index <= len(name); index++ {
		if index == len(name) || name[index] == '.' {
			if index > start {
				result = append(result, name[start:index])
			}
			start = index + 1
		}
	}
	return result
}
