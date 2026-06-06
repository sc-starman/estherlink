package panel

import (
	"context"
	"fmt"
	"io"
	"net"
	"net/http"
	"os"
	"path/filepath"
	"strings"
	"time"

	"golang.org/x/net/proxy"
)

type DownloadOptions struct {
	URL          string
	SHA256       string
	CacheRoot    string
	SOCKSAddress string
	Timeout      time.Duration
	MaxBytes     int64
}

type DownloadResult struct {
	Path      string `json:"path"`
	SHA256    string `json:"sha256"`
	Changed   bool   `json:"changed"`
	UsedSOCKS bool   `json:"usedSocks"`
}

func DownloadArtifact(ctx context.Context, options DownloadOptions) (DownloadResult, error) {
	options.URL = strings.TrimSpace(options.URL)
	options.SHA256 = strings.ToLower(strings.TrimSpace(options.SHA256))
	if options.URL == "" || options.SHA256 == "" {
		return DownloadResult{}, fmt.Errorf("OmniPanel artifact URL and SHA256 are required")
	}
	if options.CacheRoot == "" {
		options.CacheRoot = "/var/cache/omnirelay/omnipanel"
	}
	if options.Timeout <= 0 {
		options.Timeout = 10 * time.Minute
	}
	if options.MaxBytes <= 0 {
		options.MaxBytes = 512 * 1024 * 1024
	}
	if err := os.MkdirAll(options.CacheRoot, 0o700); err != nil {
		return DownloadResult{}, err
	}
	cachePath := filepath.Join(options.CacheRoot, options.SHA256+".tar.gz")
	if actual, err := fileSHA256(cachePath); err == nil && actual == options.SHA256 {
		return DownloadResult{Path: cachePath, SHA256: actual, UsedSOCKS: options.SOCKSAddress != ""}, nil
	}

	client, closeIdle, err := downloadClient(options)
	if err != nil {
		return DownloadResult{}, err
	}
	defer closeIdle()
	requestCtx, cancel := context.WithTimeout(ctx, options.Timeout)
	defer cancel()
	request, err := http.NewRequestWithContext(requestCtx, http.MethodGet, options.URL, nil)
	if err != nil {
		return DownloadResult{}, err
	}
	response, err := client.Do(request)
	if err != nil {
		return DownloadResult{}, err
	}
	defer response.Body.Close()
	if response.StatusCode < 200 || response.StatusCode >= 300 {
		return DownloadResult{}, fmt.Errorf("download OmniPanel artifact: HTTP status %d", response.StatusCode)
	}
	if response.ContentLength > options.MaxBytes {
		return DownloadResult{}, fmt.Errorf("OmniPanel artifact exceeds maximum size")
	}
	temp, err := os.CreateTemp(options.CacheRoot, ".download-*.tmp")
	if err != nil {
		return DownloadResult{}, err
	}
	tempPath := temp.Name()
	defer os.Remove(tempPath)
	written, copyErr := io.Copy(temp, io.LimitReader(response.Body, options.MaxBytes+1))
	closeErr := temp.Close()
	if copyErr != nil {
		return DownloadResult{}, copyErr
	}
	if closeErr != nil {
		return DownloadResult{}, closeErr
	}
	if written > options.MaxBytes {
		return DownloadResult{}, fmt.Errorf("OmniPanel artifact exceeds maximum size")
	}
	actual, err := fileSHA256(tempPath)
	if err != nil {
		return DownloadResult{}, err
	}
	if actual != options.SHA256 {
		return DownloadResult{}, fmt.Errorf("OmniPanel artifact SHA256 mismatch")
	}
	if err := os.Chmod(tempPath, 0o600); err != nil {
		return DownloadResult{}, err
	}
	if err := os.Rename(tempPath, cachePath); err != nil {
		return DownloadResult{}, err
	}
	return DownloadResult{Path: cachePath, SHA256: actual, Changed: true, UsedSOCKS: options.SOCKSAddress != ""}, nil
}

func downloadClient(options DownloadOptions) (*http.Client, func(), error) {
	transport := &http.Transport{}
	if options.SOCKSAddress != "" {
		dialer, err := proxy.SOCKS5("tcp", options.SOCKSAddress, nil, &net.Dialer{Timeout: 20 * time.Second})
		if err != nil {
			return nil, nil, err
		}
		transport.DialContext = func(ctx context.Context, network string, address string) (net.Conn, error) {
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
		}
	}
	return &http.Client{Transport: transport}, transport.CloseIdleConnections, nil
}
