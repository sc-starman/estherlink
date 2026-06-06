package clock

import (
	"context"
	"fmt"
	"math"
	"net/http"
	"time"

	"github.com/omnirelay/connector-core/internal/probe"
)

type Options struct {
	Probe          probe.BackendOptions
	Now            func() time.Time
	Set            func(time.Time) error
	ApplyThreshold time.Duration
	MaxSkew        time.Duration
}

type Result struct {
	OK              bool   `json:"ok"`
	ReasonCode      string `json:"reasonCode"`
	Source          string `json:"source,omitempty"`
	RemoteUTC       string `json:"remoteUtc,omitempty"`
	LocalUTCBefore  string `json:"localUtcBefore"`
	LocalUTCAfter   string `json:"localUtcAfter"`
	SkewSecBefore   int64  `json:"skewSecBefore"`
	SkewSecAfter    int64  `json:"skewSecAfter"`
	Applied         bool   `json:"applied"`
	CheckedAtUTC    string `json:"checkedAtUtc"`
	ProbeReasonCode string `json:"probeReasonCode,omitempty"`
}

func Sync(ctx context.Context, options Options) Result {
	options = defaults(options)
	localBefore := options.Now().UTC()
	result := Result{
		LocalUTCBefore: localBefore.Format(time.RFC3339),
		LocalUTCAfter:  localBefore.Format(time.RFC3339),
		CheckedAtUTC:   localBefore.Format(time.RFC3339),
	}
	probeResult := probe.Backend(ctx, options.Probe)
	result.ProbeReasonCode = probeResult.ReasonCode
	result.Source = probeResult.SuccessTarget
	if probeResult.DateHeader == "" {
		result.ReasonCode = "date_header_unavailable"
		return result
	}
	remote, err := http.ParseTime(probeResult.DateHeader)
	if err != nil {
		result.ReasonCode = "invalid_date_header"
		return result
	}
	remote = remote.UTC()
	result.RemoteUTC = remote.Format(time.RFC3339)
	result.SkewSecBefore = int64(localBefore.Sub(remote).Seconds())
	if durationAbs(localBefore.Sub(remote)) > options.ApplyThreshold {
		if options.Set == nil {
			result.ReasonCode = "clock_set_unavailable"
			return result
		}
		if err := options.Set(remote); err != nil {
			result.ReasonCode = "clock_set_failed"
			return result
		}
		result.Applied = true
	}
	localAfter := options.Now().UTC()
	result.LocalUTCAfter = localAfter.Format(time.RFC3339)
	result.SkewSecAfter = int64(localAfter.Sub(remote).Seconds())
	if durationAbs(localAfter.Sub(remote)) > options.MaxSkew {
		result.ReasonCode = "skew_exceeded"
		return result
	}
	result.OK = true
	result.ReasonCode = "ok"
	return result
}

func defaults(options Options) Options {
	if options.Now == nil {
		options.Now = time.Now
	}
	if options.ApplyThreshold <= 0 {
		options.ApplyThreshold = 5 * time.Second
	}
	if options.MaxSkew <= 0 {
		options.MaxSkew = 120 * time.Second
	}
	options.Probe.InsecureSkipVerify = true
	return options
}

func durationAbs(value time.Duration) time.Duration {
	return time.Duration(math.Abs(float64(value)))
}

func Failure(result Result) error {
	if result.OK {
		return nil
	}
	return fmt.Errorf("clock synchronization failed: %s", result.ReasonCode)
}
