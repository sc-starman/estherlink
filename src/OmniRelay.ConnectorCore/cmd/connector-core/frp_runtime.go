package main

import (
	"context"
	"encoding/json"
	"errors"
	"flag"
	"fmt"
	"os"
	"os/signal"
	"path/filepath"
	"strings"
	"syscall"
	"time"

	"github.com/fatedier/frp/client"
	"github.com/fatedier/frp/pkg/config"
	"github.com/fatedier/frp/pkg/config/source"
	"github.com/fatedier/frp/pkg/config/v1/validation"
	"github.com/fatedier/frp/pkg/policy/featuregate"
	"github.com/fatedier/frp/pkg/policy/security"
	"github.com/fatedier/frp/pkg/util/log"
	"github.com/fatedier/frp/server"
)

type frpTunnelState struct {
	OK                 bool              `json:"ok"`
	LastCheckedUTC     string            `json:"lastCheckedUtc"`
	LastError          string            `json:"lastError"`
	SessionEstablished bool              `json:"sessionEstablished"`
	SessionReason      string            `json:"sessionReason"`
	ProxyPhases        map[string]string `json:"proxyPhases"`
}

type frpsState struct {
	OK             bool   `json:"ok"`
	LastCheckedUTC string `json:"lastCheckedUtc"`
	LastError      string `json:"lastError"`
	Running        bool   `json:"running"`
}

func defaultFrpTunnelStatePath() string {
	return filepath.Join(filepath.Dir(defaultStatePath), "frp_tunnel_state.json")
}

func defaultFrpsStatePath() string {
	return filepath.Join(filepath.Dir(defaultStatePath), "frps_state.json")
}

func runFrpTunnelCommand(args []string) error {
	flags := flag.NewFlagSet("tunnel run", flag.ContinueOnError)
	configPath := flags.String("config", "./frpc.toml", "frpc config path")
	statePath := flags.String("state-file", defaultFrpTunnelStatePath(), "state json path")
	strictConfig := flags.Bool("strict-config", true, "strict config parsing mode")
	if err := flags.Parse(args); err != nil {
		return err
	}

	signals := make(chan os.Signal, 4)
	signal.Notify(signals, syscall.SIGINT, syscall.SIGTERM, syscall.SIGHUP)
	defer signal.Stop(signals)

	for {
		runtime, err := buildFrpTunnelRuntime(*configPath, *strictConfig)
		if err != nil {
			_ = writeFrpTunnelState(*statePath, frpTunnelState{
				OK:                 false,
				LastCheckedUTC:     time.Now().UTC().Format(time.RFC3339),
				LastError:          normalizeErr(err),
				SessionEstablished: false,
				SessionReason:      "startup_failed",
				ProxyPhases:        map[string]string{},
			})
			return err
		}

		runCtx, runCancel := context.WithCancel(context.Background())
		runErrCh := make(chan error, 1)
		go func() {
			runErrCh <- runtime.service.Run(runCtx)
		}()

		ticker := time.NewTicker(2 * time.Second)
		_ = writeFrpTunnelState(*statePath, frpTunnelState{
			OK:                 false,
			LastCheckedUTC:     time.Now().UTC().Format(time.RFC3339),
			LastError:          "",
			SessionEstablished: false,
			SessionReason:      "starting",
			ProxyPhases:        map[string]string{},
		})

		reloadRequested := false
		terminateRequested := false
		var runErr error

		running := true
		for running {
			select {
			case sig := <-signals:
				switch sig {
				case syscall.SIGHUP:
					reloadRequested = true
					runCancel()
					runtime.service.GracefulClose(500 * time.Millisecond)
				default:
					terminateRequested = true
					runCancel()
					runtime.service.GracefulClose(500 * time.Millisecond)
				}
			case err := <-runErrCh:
				runErr = err
				running = false
			case <-ticker.C:
				established, reason, phases := evaluateFrpTunnelStatus(runtime.service.StatusExporter(), runtime.proxyNames)
				state := frpTunnelState{
					OK:                 established,
					LastCheckedUTC:     time.Now().UTC().Format(time.RFC3339),
					LastError:          "",
					SessionEstablished: established,
					SessionReason:      reason,
					ProxyPhases:        phases,
				}
				if !established && reason == "service_not_running" {
					state.LastError = "frp_service_not_running"
				}
				_ = writeFrpTunnelState(*statePath, state)
			}
		}

		ticker.Stop()
		runCancel()

		if terminateRequested {
			_ = writeFrpTunnelState(*statePath, frpTunnelState{
				OK:                 false,
				LastCheckedUTC:     time.Now().UTC().Format(time.RFC3339),
				LastError:          "terminated",
				SessionEstablished: false,
				SessionReason:      "terminated",
				ProxyPhases:        map[string]string{},
			})
			return nil
		}

		if runErr != nil && !errors.Is(runErr, context.Canceled) {
			state := frpTunnelState{
				OK:                 false,
				LastCheckedUTC:     time.Now().UTC().Format(time.RFC3339),
				LastError:          normalizeErr(runErr),
				SessionEstablished: false,
				SessionReason:      "run_failed",
				ProxyPhases:        map[string]string{},
			}
			_ = writeFrpTunnelState(*statePath, state)
			if !reloadRequested {
				return runErr
			}
		}

		if !reloadRequested {
			return nil
		}
	}
}

type frpTunnelRuntime struct {
	service    *client.Service
	proxyNames []string
}

func buildFrpTunnelRuntime(configPath string, strict bool) (*frpTunnelRuntime, error) {
	result, err := config.LoadClientConfigResult(configPath, strict)
	if err != nil {
		return nil, err
	}

	if len(result.Common.FeatureGates) > 0 {
		if err := featuregate.SetFromMap(result.Common.FeatureGates); err != nil {
			return nil, err
		}
	}

	configSource := source.NewConfigSource()
	if err := configSource.ReplaceAll(result.Proxies, result.Visitors); err != nil {
		return nil, fmt.Errorf("failed to set config source: %w", err)
	}

	var storeSource *source.StoreSource
	if result.Common.Store.IsEnabled() {
		storePath := result.Common.Store.Path
		if storePath != "" && !filepath.IsAbs(storePath) {
			storePath = filepath.Join(filepath.Dir(configPath), storePath)
		}

		s, err := source.NewStoreSource(source.StoreSourceConfig{Path: storePath})
		if err != nil {
			return nil, fmt.Errorf("failed to create store source: %w", err)
		}
		storeSource = s
	}

	aggregator := source.NewAggregator(configSource)
	if storeSource != nil {
		aggregator.SetStoreSource(storeSource)
	}

	proxyCfgs, visitorCfgs, err := aggregator.Load()
	if err != nil {
		return nil, fmt.Errorf("failed to load config from sources: %w", err)
	}

	proxyCfgs, visitorCfgs = config.FilterClientConfigurers(result.Common, proxyCfgs, visitorCfgs)
	proxyCfgs = config.CompleteProxyConfigurers(proxyCfgs)
	visitorCfgs = config.CompleteVisitorConfigurers(visitorCfgs)

	unsafeFeatures := security.NewUnsafeFeatures([]string{})
	warning, err := validation.ValidateAllClientConfig(result.Common, proxyCfgs, visitorCfgs, unsafeFeatures)
	if warning != nil {
		fmt.Printf("WARNING: %v\n", warning)
	}
	if err != nil {
		return nil, err
	}

	log.InitLogger(result.Common.Log.To, result.Common.Log.Level, int(result.Common.Log.MaxDays), result.Common.Log.DisablePrintColor)

	svc, err := client.NewService(client.ServiceOptions{
		Common:                 result.Common,
		ConfigSourceAggregator: aggregator,
		UnsafeFeatures:         unsafeFeatures,
		ConfigFilePath:         configPath,
	})
	if err != nil {
		return nil, err
	}

	names := make([]string, 0, len(proxyCfgs))
	for _, proxyCfg := range proxyCfgs {
		if proxyCfg == nil || proxyCfg.GetBaseConfig() == nil {
			continue
		}
		name := strings.TrimSpace(proxyCfg.GetBaseConfig().Name)
		if name != "" {
			names = append(names, name)
		}
	}

	return &frpTunnelRuntime{service: svc, proxyNames: names}, nil
}

func evaluateFrpTunnelStatus(exporter client.StatusExporter, proxyNames []string) (bool, string, map[string]string) {
	phases := make(map[string]string, len(proxyNames))
	if len(proxyNames) == 0 {
		return false, "no_proxy_configured", phases
	}

	allRunning := true
	for _, name := range proxyNames {
		status, ok := exporter.GetProxyStatus(name)
		if !ok || status == nil {
			allRunning = false
			phases[name] = "missing"
			continue
		}

		phase := strings.TrimSpace(strings.ToLower(status.Phase))
		if phase == "" {
			phase = "unknown"
		}
		phases[name] = phase

		if phase != "running" {
			allRunning = false
		}
	}

	if allRunning {
		return true, "ok", phases
	}

	for _, name := range proxyNames {
		if phase, ok := phases[name]; ok && phase != "running" {
			return false, "proxy_not_running:" + name + ":" + phase, phases
		}
	}

	return false, "service_not_running", phases
}

func checkFrpTunnelCommand(args []string) error {
	flags := flag.NewFlagSet("tunnel check", flag.ContinueOnError)
	configPath := flags.String("config", "./frpc.toml", "frpc config path")
	strictConfig := flags.Bool("strict-config", true, "strict config parsing mode")
	if err := flags.Parse(args); err != nil {
		return err
	}

	result, err := config.LoadClientConfigResult(*configPath, *strictConfig)
	if err != nil {
		return err
	}

	proxyCfgs := config.CompleteProxyConfigurers(result.Proxies)
	visitorCfgs := config.CompleteVisitorConfigurers(result.Visitors)

	unsafeFeatures := security.NewUnsafeFeatures([]string{})
	warning, err := validation.ValidateAllClientConfig(result.Common, proxyCfgs, visitorCfgs, unsafeFeatures)
	if warning != nil {
		fmt.Printf("WARNING: %v\n", warning)
	}
	return err
}

func runFrpsCommand(args []string) error {
	flags := flag.NewFlagSet("frps run", flag.ContinueOnError)
	configPath := flags.String("config", "./frps.toml", "frps config path")
	statePath := flags.String("state-file", defaultFrpsStatePath(), "state json path")
	strictConfig := flags.Bool("strict-config", true, "strict config parsing mode")
	if err := flags.Parse(args); err != nil {
		return err
	}

	signals := make(chan os.Signal, 4)
	signal.Notify(signals, syscall.SIGINT, syscall.SIGTERM, syscall.SIGHUP)
	defer signal.Stop(signals)

	for {
		cfg, _, err := config.LoadServerConfig(*configPath, *strictConfig)
		if err != nil {
			_ = writeFrpsState(*statePath, frpsState{OK: false, LastCheckedUTC: time.Now().UTC().Format(time.RFC3339), LastError: normalizeErr(err), Running: false})
			return err
		}

		validator := validation.NewConfigValidator(security.NewUnsafeFeatures([]string{}))
		warning, err := validator.ValidateServerConfig(cfg)
		if warning != nil {
			fmt.Printf("WARNING: %v\n", warning)
		}
		if err != nil {
			_ = writeFrpsState(*statePath, frpsState{OK: false, LastCheckedUTC: time.Now().UTC().Format(time.RFC3339), LastError: normalizeErr(err), Running: false})
			return err
		}

		log.InitLogger(cfg.Log.To, cfg.Log.Level, int(cfg.Log.MaxDays), cfg.Log.DisablePrintColor)

		svc, err := server.NewService(cfg)
		if err != nil {
			_ = writeFrpsState(*statePath, frpsState{OK: false, LastCheckedUTC: time.Now().UTC().Format(time.RFC3339), LastError: normalizeErr(err), Running: false})
			return err
		}

		runCtx, runCancel := context.WithCancel(context.Background())
		runDone := make(chan struct{})
		go func() {
			defer close(runDone)
			svc.Run(runCtx)
		}()

		_ = writeFrpsState(*statePath, frpsState{OK: true, LastCheckedUTC: time.Now().UTC().Format(time.RFC3339), LastError: "", Running: true})

		reloadRequested := false
		terminateRequested := false

		wait := true
		for wait {
			sig := <-signals
			switch sig {
			case syscall.SIGHUP:
				reloadRequested = true
				runCancel()
				_ = svc.Close()
				wait = false
			default:
				terminateRequested = true
				runCancel()
				_ = svc.Close()
				wait = false
			}
		}

		<-runDone

		if terminateRequested {
			_ = writeFrpsState(*statePath, frpsState{OK: false, LastCheckedUTC: time.Now().UTC().Format(time.RFC3339), LastError: "terminated", Running: false})
			return nil
		}

		if !reloadRequested {
			return nil
		}
	}
}

func checkFrpsCommand(args []string) error {
	flags := flag.NewFlagSet("frps check", flag.ContinueOnError)
	configPath := flags.String("config", "./frps.toml", "frps config path")
	strictConfig := flags.Bool("strict-config", true, "strict config parsing mode")
	if err := flags.Parse(args); err != nil {
		return err
	}

	cfg, _, err := config.LoadServerConfig(*configPath, *strictConfig)
	if err != nil {
		return err
	}

	validator := validation.NewConfigValidator(security.NewUnsafeFeatures([]string{}))
	warning, err := validator.ValidateServerConfig(cfg)
	if warning != nil {
		fmt.Printf("WARNING: %v\n", warning)
	}
	return err
}

func writeFrpTunnelState(path string, state frpTunnelState) error {
	if err := os.MkdirAll(filepath.Dir(path), 0o755); err != nil {
		return err
	}

	payload, err := json.Marshal(state)
	if err != nil {
		return err
	}
	payload = append(payload, '\n')

	tmp := path + ".tmp"
	if err := os.WriteFile(tmp, payload, 0o644); err != nil {
		return err
	}
	return os.Rename(tmp, path)
}

func writeFrpsState(path string, state frpsState) error {
	if err := os.MkdirAll(filepath.Dir(path), 0o755); err != nil {
		return err
	}

	payload, err := json.Marshal(state)
	if err != nil {
		return err
	}
	payload = append(payload, '\n')

	tmp := path + ".tmp"
	if err := os.WriteFile(tmp, payload, 0o644); err != nil {
		return err
	}
	return os.Rename(tmp, path)
}

func normalizeErr(err error) string {
	if err == nil {
		return ""
	}
	text := strings.TrimSpace(err.Error())
	if text == "" {
		return "unknown_error"
	}
	return text
}
