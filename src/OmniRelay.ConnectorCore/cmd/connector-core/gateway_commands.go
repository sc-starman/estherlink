package main

import (
	"context"
	"crypto/rand"
	"database/sql"
	"encoding/base64"
	"flag"
	"fmt"
	"net"
	"os"
	"path/filepath"
	"strings"
	"time"

	"github.com/omnirelay/connector-core/internal/accounting"
	"github.com/omnirelay/connector-core/internal/clients"
	clockapi "github.com/omnirelay/connector-core/internal/clock"
	commandapi "github.com/omnirelay/connector-core/internal/command"
	dnsapi "github.com/omnirelay/connector-core/internal/dns"
	firewallapi "github.com/omnirelay/connector-core/internal/firewall"
	frpapi "github.com/omnirelay/connector-core/internal/frp"
	"github.com/omnirelay/connector-core/internal/host"
	ipsecapi "github.com/omnirelay/connector-core/internal/ipsec"
	openvpnapi "github.com/omnirelay/connector-core/internal/openvpn"
	panelapi "github.com/omnirelay/connector-core/internal/panel"
	"github.com/omnirelay/connector-core/internal/probe"
	"github.com/omnirelay/connector-core/internal/protocol"
	"github.com/omnirelay/connector-core/internal/reconcile"
	singboxconfig "github.com/omnirelay/connector-core/internal/singbox"
	"github.com/omnirelay/connector-core/internal/spec"
	statusapi "github.com/omnirelay/connector-core/internal/status"
)

var version = "dev"

func runModernCommand(args []string) (bool, error) {
	if len(args) == 0 {
		return false, nil
	}
	switch args[0] {
	case "version":
		return true, commandapi.WriteJSON(os.Stdout, commandapi.Result{
			OK: true, Command: "version", Data: map[string]string{"version": version},
		})
	case "gateway":
		if len(args) < 2 {
			return true, &commandapi.Error{Code: "usage", Message: "gateway subcommand is required", ExitCode: commandapi.ExitUsage}
		}
		switch args[1] {
		case "validate":
			return true, runGatewayValidate(args[2:])
		case "plan":
			return true, runGatewayPlan(args[2:])
		case "apply":
			return true, runGatewayApply(args[2:])
		case "migrate":
			return true, runGatewayMigrate(args[2:])
		case "finalize-migration", "rollback-migration":
			return true, runGatewayMigrationLifecycle(args[1], args[2:])
		case "start", "stop":
			return true, runGatewayLifecycle(args[1], args[2:])
		case "uninstall":
			return true, runGatewayUninstall(args[2:])
		case "repair":
			return true, runGatewayRepair(args[2:])
		case "status", "health":
			return true, runGatewayStatus(args[1], args[2:])
		default:
			return true, &commandapi.Error{Code: "usage", Message: "unsupported gateway subcommand: " + args[1], ExitCode: commandapi.ExitUsage}
		}
	case "accounting":
		if len(args) < 2 {
			return true, &commandapi.Error{Code: "usage", Message: "accounting subcommand is required", ExitCode: commandapi.ExitUsage}
		}
		switch args[1] {
		case "migrate":
			return true, runAccountingMigrate(args[2:])
		case "sync":
			return true, runAccountingSync(args[2:])
		default:
			return true, &commandapi.Error{Code: "usage", Message: "unsupported accounting subcommand: " + args[1], ExitCode: commandapi.ExitUsage}
		}
	case "clients":
		if len(args) < 2 {
			return true, &commandapi.Error{Code: "usage", Message: "clients subcommand is required", ExitCode: commandapi.ExitUsage}
		}
		switch args[1] {
		case "sync":
			return true, runClientsSync(args[2:])
		default:
			return true, &commandapi.Error{Code: "usage", Message: "unsupported clients subcommand: " + args[1], ExitCode: commandapi.ExitUsage}
		}
	case "probe":
		if len(args) < 2 {
			return true, &commandapi.Error{Code: "usage", Message: "probe subcommand is required", ExitCode: commandapi.ExitUsage}
		}
		switch args[1] {
		case "backend", "egress":
			return true, runProbe(args[1], args[2:])
		default:
			return true, &commandapi.Error{Code: "usage", Message: "unsupported probe subcommand: " + args[1], ExitCode: commandapi.ExitUsage}
		}
	case "clock":
		if len(args) < 2 {
			return true, &commandapi.Error{Code: "usage", Message: "clock subcommand is required", ExitCode: commandapi.ExitUsage}
		}
		switch args[1] {
		case "sync":
			return true, runClockSync(args[2:])
		default:
			return true, &commandapi.Error{Code: "usage", Message: "unsupported clock subcommand: " + args[1], ExitCode: commandapi.ExitUsage}
		}
	case "openvpn":
		if len(args) < 2 {
			return true, &commandapi.Error{Code: "usage", Message: "openvpn subcommand is required", ExitCode: commandapi.ExitUsage}
		}
		switch args[1] {
		case "authenticate":
			return true, runOpenVPNAuthenticate(args[2:])
		case "enforce":
			return true, runOpenVPNEnforce(args[2:])
		case "limits":
			return true, runOpenVPNLimits(args[2:])
		default:
			return true, &commandapi.Error{Code: "usage", Message: "unsupported openvpn subcommand: " + args[1], ExitCode: commandapi.ExitUsage}
		}
	case "ipsec":
		if len(args) < 2 {
			return true, &commandapi.Error{Code: "usage", Message: "ipsec subcommand is required", ExitCode: commandapi.ExitUsage}
		}
		switch args[1] {
		case "activate", "deactivate":
			return true, runIPSecActivation(args[1], args[2:])
		case "session":
			return true, runIPSecSession(args[2:])
		case "enforce":
			return true, runIPSecEnforce(args[2:])
		default:
			return true, &commandapi.Error{Code: "usage", Message: "unsupported ipsec subcommand: " + args[1], ExitCode: commandapi.ExitUsage}
		}
	case "panel":
		if len(args) < 2 {
			return true, &commandapi.Error{Code: "usage", Message: "panel subcommand is required", ExitCode: commandapi.ExitUsage}
		}
		switch args[1] {
		case "activate":
			return true, runPanelActivate(args[2:])
		default:
			return true, &commandapi.Error{Code: "usage", Message: "unsupported panel subcommand: " + args[1], ExitCode: commandapi.ExitUsage}
		}
	case "dns":
		if len(args) < 2 {
			return true, &commandapi.Error{Code: "usage", Message: "dns subcommand is required", ExitCode: commandapi.ExitUsage}
		}
		switch args[1] {
		case "apply":
			return true, runDNSApply(args[2:])
		case "status":
			return true, runDNSStatus(args[2:])
		default:
			return true, &commandapi.Error{Code: "usage", Message: "unsupported dns subcommand: " + args[1], ExitCode: commandapi.ExitUsage}
		}
	case "firewall":
		if len(args) < 2 {
			return true, &commandapi.Error{Code: "usage", Message: "firewall subcommand is required", ExitCode: commandapi.ExitUsage}
		}
		switch args[1] {
		case "apply", "cleanup":
			return true, runFirewall(args[1], args[2:])
		default:
			return true, &commandapi.Error{Code: "usage", Message: "unsupported firewall subcommand: " + args[1], ExitCode: commandapi.ExitUsage}
		}
	case "frps":
		if len(args) >= 2 && args[1] == "reconcile" {
			return true, runFRPSReconcile(args[2:])
		}
		return false, nil
	}
	return false, nil
}

func runFRPSReconcile(args []string) error {
	flags := flag.NewFlagSet("frps reconcile", flag.ContinueOnError)
	flags.SetOutput(os.Stderr)
	configRoot := flags.String("config-root", "/etc/omnirelay", "managed configuration root")
	systemdRoot := flags.String("systemd-root", "/etc/systemd/system", "systemd unit root")
	stateRoot := flags.String("state-root", "/var/lib/omnirelay/frps", "FRPS state root")
	_ = flags.Bool("json", false, "emit JSON result")
	if err := flags.Parse(args); err != nil {
		return &commandapi.Error{Code: "usage", Message: err.Error(), ExitCode: commandapi.ExitUsage}
	}
	if err := host.VerifySupportedPlatform(); err != nil {
		return &commandapi.Error{Code: "unsupported_platform", Message: err.Error(), ExitCode: commandapi.ExitValidation}
	}
	result, err := frpapi.Reconcile(frpapi.ReconcileOptions{
		ConfigRoot: *configRoot, SystemdRoot: *systemdRoot, StateRoot: *stateRoot,
	})
	if err != nil {
		return &commandapi.Error{Code: "frps_reconcile_failed", Message: err.Error(), ExitCode: commandapi.ExitApply}
	}
	ctx, cancel := context.WithTimeout(context.Background(), 45*time.Second)
	defer cancel()
	manager := host.OSSystemd{}
	if result.Active {
		if err := manager.DaemonReload(ctx); err == nil {
			err = manager.EnableNow(ctx, frpapi.ServiceName)
		}
	} else {
		_ = manager.DisableNow(ctx, frpapi.ServiceName)
		err = manager.DaemonReload(ctx)
	}
	if err != nil {
		return &commandapi.Error{Code: "frps_lifecycle_failed", Message: err.Error(), ExitCode: commandapi.ExitApply}
	}
	return commandapi.WriteJSON(os.Stdout, commandapi.Result{OK: true, Command: "frps reconcile", Data: result})
}

func runGatewayMigrate(args []string) error {
	flags := flag.NewFlagSet("gateway migrate", flag.ContinueOnError)
	flags.SetOutput(os.Stderr)
	specPath := flags.String("spec", "", "gateway specification path")
	configRoot := flags.String("config-root", "/etc/omnirelay", "managed configuration root")
	transactionRoot := flags.String("transaction-root", "/var/lib/omnirelay/transactions", "transaction journal root")
	systemdRoot := flags.String("systemd-root", "/etc/systemd/system", "systemd unit root")
	nginxRoot := flags.String("nginx-root", "/etc/nginx/sites-available", "nginx sites-available root")
	nginxEnabledRoot := flags.String("nginx-enabled-root", "/etc/nginx/sites-enabled", "nginx sites-enabled root")
	dnsmasqRoot := flags.String("dnsmasq-root", "/etc/dnsmasq.d", "dnsmasq configuration root")
	legacyBinaryRoot := flags.String("legacy-binary-root", "/usr/local/sbin", "legacy binary root")
	globalRoot := flags.String("global-root", "/etc", "external daemon configuration root")
	panelAppRoot := flags.String("panel-app-root", "/opt/omnirelay/relays", "OmniPanel application root")
	installPackages := flags.Bool("install-packages", true, "ensure required apt packages before migration")
	reloadSystemd := flags.Bool("reload-systemd", true, "validate generated units with systemd daemon-reload")
	_ = flags.Bool("json", false, "emit JSON result")
	if err := flags.Parse(args); err != nil {
		return &commandapi.Error{Code: "usage", Message: err.Error(), ExitCode: commandapi.ExitUsage}
	}
	if *specPath == "" {
		return &commandapi.Error{Code: "usage", Message: "--spec is required", ExitCode: commandapi.ExitUsage}
	}
	gatewaySpec, err := spec.LoadFile(*specPath)
	if err != nil {
		return commandapi.ValidationError(err)
	}
	if err := host.VerifySupportedPlatform(); err != nil {
		return &commandapi.Error{Code: "unsupported_platform", Message: err.Error(), ExitCode: commandapi.ExitValidation}
	}
	if *installPackages {
		ctx, cancel := context.WithTimeout(context.Background(), 15*time.Minute)
		defer cancel()
		if err := (host.OSPackageManager{}).Ensure(ctx, reconcile.BuildPlan(gatewaySpec).RequiredPackages); err != nil {
			return &commandapi.Error{Code: "package_reconciliation_failed", Message: err.Error(), ExitCode: commandapi.ExitApply}
		}
	}
	if err := ensureGatewayHostAccounts(gatewaySpec); err != nil {
		return &commandapi.Error{Code: "account_reconciliation_failed", Message: err.Error(), ExitCode: commandapi.ExitApply}
	}
	var validateAndReload func() error
	var reload func() error
	if *reloadSystemd {
		validateAndReload = func() error {
			ctx, cancel := context.WithTimeout(context.Background(), 30*time.Second)
			defer cancel()
			if gatewaySpec.Panel.Port > 0 {
				if err := (host.OSSudoersValidator{}).Validate(ctx, filepath.Join("/etc/sudoers.d", "omnirelay-connector-core-"+gatewaySpec.RelayID)); err != nil {
					return err
				}
			}
			return (host.OSSystemd{}).DaemonReload(ctx)
		}
		reload = func() error {
			ctx, cancel := context.WithTimeout(context.Background(), 30*time.Second)
			defer cancel()
			return (host.OSSystemd{}).DaemonReload(ctx)
		}
	}
	result, err := reconcile.Migrate(gatewaySpec, reconcile.MigrateOptions{
		ApplyOptions: reconcile.ApplyOptions{
			ConfigRoot: *configRoot, TransactionRoot: *transactionRoot, SystemdRoot: *systemdRoot, NginxRoot: *nginxRoot, DNSMasqRoot: *dnsmasqRoot,
			ValidatePersisted: func(path string) error {
				_, validateErr := spec.LoadFile(path)
				return validateErr
			},
			AfterWrite: validateAndReload, AfterRollback: reload,
		},
		LegacyBinaryRoot: *legacyBinaryRoot, GlobalRoot: *globalRoot, PanelAppRoot: *panelAppRoot, NginxEnabledRoot: *nginxEnabledRoot,
	})
	if err != nil {
		return &commandapi.Error{Code: "migration_failed", Message: err.Error(), ExitCode: commandapi.ExitApply}
	}
	return commandapi.WriteJSON(os.Stdout, commandapi.Result{OK: true, Command: "gateway migrate", Data: result})
}

func runGatewayMigrationLifecycle(action string, args []string) error {
	flags := flag.NewFlagSet("gateway "+action, flag.ContinueOnError)
	flags.SetOutput(os.Stderr)
	relayID := flags.String("relay-id", "", "relay identifier")
	configRoot := flags.String("config-root", "/etc/omnirelay", "managed configuration root")
	transactionRoot := flags.String("transaction-root", "/var/lib/omnirelay/transactions", "transaction journal root")
	systemdRoot := flags.String("systemd-root", "/etc/systemd/system", "systemd unit root")
	nginxRoot := flags.String("nginx-root", "/etc/nginx/sites-available", "nginx sites-available root")
	nginxEnabledRoot := flags.String("nginx-enabled-root", "/etc/nginx/sites-enabled", "nginx sites-enabled root")
	dnsmasqRoot := flags.String("dnsmasq-root", "/etc/dnsmasq.d", "dnsmasq configuration root")
	legacyBinaryRoot := flags.String("legacy-binary-root", "/usr/local/sbin", "legacy binary root")
	globalRoot := flags.String("global-root", "/etc", "external daemon configuration root")
	panelAppRoot := flags.String("panel-app-root", "/opt/omnirelay/relays", "OmniPanel application root")
	reloadSystemd := flags.Bool("reload-systemd", true, "apply systemd lifecycle changes")
	_ = flags.Bool("json", false, "emit JSON result")
	if err := flags.Parse(args); err != nil {
		return &commandapi.Error{Code: "usage", Message: err.Error(), ExitCode: commandapi.ExitUsage}
	}
	if *relayID == "" {
		return &commandapi.Error{Code: "usage", Message: "--relay-id is required", ExitCode: commandapi.ExitUsage}
	}
	if err := host.VerifySupportedPlatform(); err != nil {
		return &commandapi.Error{Code: "unsupported_platform", Message: err.Error(), ExitCode: commandapi.ExitValidation}
	}
	manager := host.OSSystemd{}
	ctx, cancel := context.WithTimeout(context.Background(), 90*time.Second)
	defer cancel()
	options := reconcile.MigrateOptions{
		ApplyOptions: reconcile.ApplyOptions{
			ConfigRoot: *configRoot, TransactionRoot: *transactionRoot, SystemdRoot: *systemdRoot,
			NginxRoot: *nginxRoot, DNSMasqRoot: *dnsmasqRoot,
		},
		LegacyBinaryRoot: *legacyBinaryRoot, GlobalRoot: *globalRoot, PanelAppRoot: *panelAppRoot, NginxEnabledRoot: *nginxEnabledRoot,
	}
	var result reconcile.MigrationLifecycleResult
	var err error
	if action == "finalize-migration" {
		if *reloadSystemd {
			_ = manager.DisableNow(ctx,
				"omnirelay-singbox-"+*relayID+".service",
				"omnirelay-accounting-sync-"+*relayID+".timer",
				"omnirelay-accounting-sync-"+*relayID+".service",
			)
		}
		result, err = reconcile.FinalizeMigration(*relayID, options)
	} else {
		if *reloadSystemd {
			_ = manager.DisableNow(ctx, host.GatewayTarget(*relayID))
		}
		result, err = reconcile.RollbackMigration(*relayID, options)
	}
	if err != nil {
		return &commandapi.Error{Code: "migration_" + strings.ReplaceAll(action, "-", "_") + "_failed", Message: err.Error(), ExitCode: commandapi.ExitApply}
	}
	if *reloadSystemd {
		if err := manager.DaemonReload(ctx); err != nil {
			return &commandapi.Error{Code: "migration_systemd_reload_failed", Message: err.Error(), ExitCode: commandapi.ExitApply}
		}
	}
	if action == "rollback-migration" && *reloadSystemd {
		if len(result.RestartUnits) > 0 {
			if err := manager.Restart(ctx, result.RestartUnits...); err != nil {
				return &commandapi.Error{Code: "migration_rollback_shared_restart_failed", Message: err.Error(), ExitCode: commandapi.ExitApply}
			}
		}
		legacyUnits := existingLegacyUnits(*systemdRoot, *relayID)
		if len(legacyUnits) > 0 {
			if err := manager.EnableNow(ctx, legacyUnits...); err != nil {
				return &commandapi.Error{Code: "migration_rollback_start_failed", Message: err.Error(), ExitCode: commandapi.ExitApply}
			}
		}
	}
	return commandapi.WriteJSON(os.Stdout, commandapi.Result{OK: true, Command: "gateway " + action, Data: result})
}

func existingLegacyUnits(systemdRoot string, relayID string) []string {
	candidates := []string{
		"omnirelay-singbox-" + relayID + ".service",
		"omnirelay-omnipanel-" + relayID + ".service",
		"omnirelay-openvpn-" + relayID + ".service",
		"omnirelay-accounting-sync-" + relayID + ".timer",
		"omnirelay-clock-sync-" + relayID + ".timer",
		"omnirelay-openvpn-enforce-" + relayID + ".timer",
		"omnirelay-ipsec-enforce-" + relayID + ".timer",
	}
	result := make([]string, 0, len(candidates))
	for _, unit := range candidates {
		if info, err := os.Stat(filepath.Join(systemdRoot, unit)); err == nil && info.Mode().IsRegular() {
			result = append(result, unit)
		}
	}
	return result
}

func runPanelActivate(args []string) error {
	flags := flag.NewFlagSet("panel activate", flag.ContinueOnError)
	flags.SetOutput(os.Stderr)
	relayID := flags.String("relay-id", "", "relay identifier")
	configRoot := flags.String("config-root", "/etc/omnirelay", "managed configuration root")
	appRoot := flags.String("app-root", "", "OmniPanel application root override")
	nginxAvailableRoot := flags.String("nginx-available-root", "/etc/nginx/sites-available", "nginx sites-available root")
	nginxEnabledRoot := flags.String("nginx-enabled-root", "/etc/nginx/sites-enabled", "nginx sites-enabled root")
	artifactCacheRoot := flags.String("artifact-cache-root", "/var/cache/omnirelay/omnipanel", "OmniPanel artifact cache root")
	restartServices := flags.Bool("restart-services", true, "restart nginx and OmniPanel services after activation")
	_ = flags.Bool("json", false, "emit JSON result")
	if err := flags.Parse(args); err != nil {
		return &commandapi.Error{Code: "usage", Message: err.Error(), ExitCode: commandapi.ExitUsage}
	}
	if *relayID == "" {
		return &commandapi.Error{Code: "usage", Message: "--relay-id is required", ExitCode: commandapi.ExitUsage}
	}
	if err := host.VerifySupportedPlatform(); err != nil {
		return &commandapi.Error{Code: "unsupported_platform", Message: err.Error(), ExitCode: commandapi.ExitValidation}
	}
	gatewaySpec, err := loadPersistedRelaySpec(*configRoot, *relayID)
	if err != nil {
		return commandapi.ValidationError(err)
	}
	if gatewaySpec.Panel.Port == 0 {
		return commandapi.ValidationError(fmt.Errorf("OmniPanel is not enabled for this relay"))
	}
	if *appRoot == "" {
		*appRoot = filepath.Join("/opt/omnirelay/relays", *relayID, "omnipanel")
	}
	artifactChanged := false
	artifactPath := gatewaySpec.Panel.ArtifactFile
	if gatewaySpec.Panel.ArtifactURL != "" {
		download, err := panelapi.DownloadArtifact(context.Background(), panelapi.DownloadOptions{
			URL: gatewaySpec.Panel.ArtifactURL, SHA256: gatewaySpec.Panel.ArtifactSHA256,
			CacheRoot:    *artifactCacheRoot,
			SOCKSAddress: net.JoinHostPort(gatewaySpec.Tunnel.BackendHost, fmt.Sprint(gatewaySpec.Tunnel.BackendPort)),
		})
		if err != nil {
			return &commandapi.Error{Code: "panel_artifact_download_failed", Message: err.Error(), ExitCode: commandapi.ExitApply}
		}
		artifactPath = download.Path
		artifactChanged = download.Changed
	}
	if artifactPath != "" {
		if _, statErr := os.Stat(artifactPath); statErr == nil {
			result, err := panelapi.InstallArtifact(artifactPath, gatewaySpec.Panel.ArtifactSHA256, *appRoot)
			if err != nil {
				return &commandapi.Error{Code: "panel_artifact_failed", Message: err.Error(), ExitCode: commandapi.ExitApply}
			}
			artifactChanged = artifactChanged || result.Changed
		} else if !os.IsNotExist(statErr) {
			return &commandapi.Error{Code: "panel_artifact_failed", Message: statErr.Error(), ExitCode: commandapi.ExitApply}
		} else if err := panelapi.ValidateCurrent(*appRoot); err != nil {
			return &commandapi.Error{Code: "panel_artifact_missing", Message: err.Error(), ExitCode: commandapi.ExitValidation}
		}
	} else if err := panelapi.ValidateCurrent(*appRoot); err != nil {
		return &commandapi.Error{Code: "panel_artifact_missing", Message: err.Error(), ExitCode: commandapi.ExitValidation}
	}
	if err := repairPanelAppPermissions(*appRoot, "omnigateway", "omnigateway"); err != nil {
		return &commandapi.Error{Code: "panel_permissions_failed", Message: err.Error(), ExitCode: commandapi.ExitApply}
	}
	siteName := "omnirelay-omnipanel-" + *relayID + ".conf"
	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Minute)
	defer cancel()
	panelRoot := filepath.Join(*configRoot, "relays", *relayID, "gateway", "panel")
	tlsChanged, err := panelapi.EnsureTLS(ctx, gatewaySpec, panelRoot, panelapi.OSCommandRunner{})
	if err != nil {
		return &commandapi.Error{Code: "panel_tls_failed", Message: err.Error(), ExitCode: commandapi.ExitApply}
	}
	siteChanged, err := panelapi.EnableNginxSite(ctx, filepath.Join(*nginxAvailableRoot, siteName), filepath.Join(*nginxEnabledRoot, siteName), panelapi.OSCommandRunner{})
	if err != nil {
		return &commandapi.Error{Code: "panel_nginx_failed", Message: err.Error(), ExitCode: commandapi.ExitApply}
	}
	manager := host.OSSystemd{}
	if *restartServices {
		if err := manager.DaemonReload(ctx); err != nil {
			return &commandapi.Error{Code: "panel_service_reload_failed", Message: err.Error(), ExitCode: commandapi.ExitApply}
		}
		if err := manager.Restart(ctx, "nginx.service", "omnirelay-omnipanel-"+*relayID+".service"); err != nil {
			return &commandapi.Error{Code: "panel_service_restart_failed", Message: err.Error(), ExitCode: commandapi.ExitApply}
		}
	}
	return commandapi.WriteJSON(os.Stdout, commandapi.Result{
		OK: true, Command: "panel activate",
		Data: map[string]any{"relayId": *relayID, "artifactChanged": artifactChanged, "tlsChanged": tlsChanged, "nginxChanged": siteChanged},
	})
}

func runOpenVPNLimits(args []string) error {
	flags := flag.NewFlagSet("openvpn limits", flag.ContinueOnError)
	flags.SetOutput(os.Stderr)
	relayID := flags.String("relay-id", "", "relay identifier")
	action := flags.String("action", "apply", "speed-limit action: apply or cleanup")
	configRoot := flags.String("config-root", "/etc/omnirelay", "managed configuration root")
	databasePath := flags.String("database", "", "accounting database path override")
	_ = flags.Bool("json", false, "emit JSON result")
	if err := flags.Parse(args); err != nil {
		return &commandapi.Error{Code: "usage", Message: err.Error(), ExitCode: commandapi.ExitUsage}
	}
	if *relayID == "" || (*action != "apply" && *action != "cleanup") {
		return &commandapi.Error{Code: "usage", Message: "--relay-id is required and --action must be apply or cleanup", ExitCode: commandapi.ExitUsage}
	}
	if err := host.VerifySupportedPlatform(); err != nil {
		return &commandapi.Error{Code: "unsupported_platform", Message: err.Error(), ExitCode: commandapi.ExitValidation}
	}
	gatewaySpec, err := loadPersistedRelaySpec(*configRoot, *relayID)
	if err != nil {
		return commandapi.ValidationError(err)
	}
	if gatewaySpec.Gateway.Protocol != "openvpn_tcp_singbox" {
		return commandapi.ValidationError(fmt.Errorf("persisted gateway protocol is not OpenVPN"))
	}
	ctx, cancel := context.WithTimeout(context.Background(), 30*time.Second)
	defer cancel()
	if *action == "cleanup" {
		openvpnapi.CleanupSpeedLimits(ctx, *relayID, openvpnapi.OSLimitRunner{})
		return commandapi.WriteJSON(os.Stdout, commandapi.Result{OK: true, Command: "openvpn limits", Data: map[string]any{"relayId": *relayID, "action": *action}})
	}
	paths := relayGatewayPaths(*configRoot, *relayID)
	db, err := sql.Open("sqlite", valueOrDefault(*databasePath, paths.accountingDB))
	if err != nil {
		return &commandapi.Error{Code: "openvpn_limits_failed", Message: err.Error(), ExitCode: commandapi.ExitApply}
	}
	defer db.Close()
	if err := accounting.Migrate(db); err != nil {
		return &commandapi.Error{Code: "openvpn_limits_failed", Message: err.Error(), ExitCode: commandapi.ExitApply}
	}
	result, err := openvpnapi.ApplySpeedLimits(ctx, db, gatewaySpec.Gateway.Protocol, *relayID, paths.openVPNRoot, openvpnapi.OSLimitRunner{})
	if err != nil {
		return &commandapi.Error{Code: "openvpn_limits_failed", Message: err.Error(), ExitCode: commandapi.ExitApply}
	}
	return commandapi.WriteJSON(os.Stdout, commandapi.Result{OK: true, Command: "openvpn limits", Data: result})
}

func runIPSecActivation(action string, args []string) error {
	flags := flag.NewFlagSet("ipsec "+action, flag.ContinueOnError)
	flags.SetOutput(os.Stderr)
	relayID := flags.String("relay-id", "", "relay identifier")
	configRoot := flags.String("config-root", "/etc/omnirelay", "managed configuration root")
	globalRoot := flags.String("global-root", "/etc", "external daemon configuration root")
	_ = flags.Bool("json", false, "emit JSON result")
	if err := flags.Parse(args); err != nil {
		return &commandapi.Error{Code: "usage", Message: err.Error(), ExitCode: commandapi.ExitUsage}
	}
	if *relayID == "" {
		return &commandapi.Error{Code: "usage", Message: "--relay-id is required", ExitCode: commandapi.ExitUsage}
	}
	if err := host.VerifySupportedPlatform(); err != nil {
		return &commandapi.Error{Code: "unsupported_platform", Message: err.Error(), ExitCode: commandapi.ExitValidation}
	}
	gatewaySpec, err := loadPersistedRelaySpec(*configRoot, *relayID)
	if err != nil {
		return commandapi.ValidationError(err)
	}
	if gatewaySpec.Gateway.Protocol != "ipsec_l2tp_singbox" {
		return commandapi.ValidationError(fmt.Errorf("relay protocol is not IPsec/L2TP"))
	}
	ctx, cancel := context.WithTimeout(context.Background(), 90*time.Second)
	defer cancel()
	paths := ipsecapi.ActivationPaths{ConfigRoot: *configRoot, GlobalRoot: *globalRoot}
	var result ipsecapi.ActivationResult
	if action == "activate" {
		result, err = ipsecapi.Activate(ctx, *relayID, paths, host.OSSystemd{})
	} else {
		result, err = ipsecapi.Deactivate(ctx, *relayID, paths, host.OSSystemd{})
	}
	if err != nil {
		return &commandapi.Error{Code: "ipsec_" + action + "_failed", Message: err.Error(), ExitCode: commandapi.ExitApply}
	}
	return commandapi.WriteJSON(os.Stdout, commandapi.Result{OK: true, Command: "ipsec " + action, Data: result})
}

func runIPSecSession(args []string) error {
	flags := flag.NewFlagSet("ipsec session", flag.ContinueOnError)
	flags.SetOutput(os.Stderr)
	relayID := flags.String("relay-id", "", "relay identifier")
	action := flags.String("action", "", "session action: up or down")
	interfaceName := flags.String("interface", "", "PPP interface name")
	username := flags.String("username", "__unknown__", "authenticated PPP username")
	sessionsPath := flags.String("sessions-file", "", "PPP sessions file override")
	_ = flags.Bool("json", false, "emit JSON result")
	if err := flags.Parse(args); err != nil {
		return &commandapi.Error{Code: "usage", Message: err.Error(), ExitCode: commandapi.ExitUsage}
	}
	if *relayID == "" || *interfaceName == "" || (*action != "up" && *action != "down") {
		return &commandapi.Error{Code: "usage", Message: "--relay-id, --interface, and --action up|down are required", ExitCode: commandapi.ExitUsage}
	}
	path := valueOrDefault(*sessionsPath, filepath.Join("/run/omnirelay", *relayID, "ppp-sessions.tsv"))
	changed, err := ipsecapi.UpdateSession(path, *action, ipsecapi.Session{Interface: *interfaceName, Username: *username})
	if err != nil {
		return &commandapi.Error{Code: "ipsec_session_failed", Message: err.Error(), ExitCode: commandapi.ExitApply}
	}
	return commandapi.WriteJSON(os.Stdout, commandapi.Result{
		OK: true, Command: "ipsec session", Data: map[string]any{"relayId": *relayID, "action": *action, "changed": changed},
	})
}

func runIPSecEnforce(args []string) error {
	flags := flag.NewFlagSet("ipsec enforce", flag.ContinueOnError)
	flags.SetOutput(os.Stderr)
	relayID := flags.String("relay-id", "", "relay identifier")
	configRoot := flags.String("config-root", "/etc/omnirelay", "managed configuration root")
	databasePath := flags.String("database", "", "accounting database path override")
	sessionsPath := flags.String("sessions-file", "", "PPP sessions file override")
	procRoot := flags.String("proc-root", "/proc", "process filesystem root")
	_ = flags.Bool("json", false, "emit JSON result")
	if err := flags.Parse(args); err != nil {
		return &commandapi.Error{Code: "usage", Message: err.Error(), ExitCode: commandapi.ExitUsage}
	}
	if *relayID == "" {
		return &commandapi.Error{Code: "usage", Message: "--relay-id is required", ExitCode: commandapi.ExitUsage}
	}
	gatewaySpec, err := loadPersistedRelaySpec(*configRoot, *relayID)
	if err != nil {
		return commandapi.ValidationError(err)
	}
	if gatewaySpec.Gateway.Protocol != "ipsec_l2tp_singbox" {
		return commandapi.ValidationError(fmt.Errorf("persisted gateway protocol is not IPsec/L2TP"))
	}
	paths := relayGatewayPaths(*configRoot, *relayID)
	db, err := sql.Open("sqlite", valueOrDefault(*databasePath, paths.accountingDB))
	if err != nil {
		return &commandapi.Error{Code: "ipsec_enforce_failed", Message: err.Error(), ExitCode: commandapi.ExitApply}
	}
	defer db.Close()
	result, err := ipsecapi.EnforceSessions(ipsecapi.EnforceOptions{
		Database: db, ProtocolID: gatewaySpec.Gateway.Protocol,
		SessionsPath: valueOrDefault(*sessionsPath, filepath.Join("/run/omnirelay", *relayID, "ppp-sessions.tsv")),
		ProcRoot:     *procRoot,
	})
	if err != nil {
		return &commandapi.Error{Code: "ipsec_enforce_failed", Message: err.Error(), ExitCode: commandapi.ExitApply}
	}
	return commandapi.WriteJSON(os.Stdout, commandapi.Result{OK: true, Command: "ipsec enforce", Data: result})
}

func runFirewall(action string, args []string) error {
	flags := flag.NewFlagSet("firewall "+action, flag.ContinueOnError)
	flags.SetOutput(os.Stderr)
	relayID := flags.String("relay-id", "", "relay identifier")
	configRoot := flags.String("config-root", "/etc/omnirelay", "managed configuration root")
	_ = flags.Bool("json", false, "emit JSON result")
	if err := flags.Parse(args); err != nil {
		return &commandapi.Error{Code: "usage", Message: err.Error(), ExitCode: commandapi.ExitUsage}
	}
	if *relayID == "" {
		return &commandapi.Error{Code: "usage", Message: "--relay-id is required", ExitCode: commandapi.ExitUsage}
	}
	if err := host.VerifySupportedPlatform(); err != nil {
		return &commandapi.Error{Code: "unsupported_platform", Message: err.Error(), ExitCode: commandapi.ExitValidation}
	}
	gatewaySpec, err := loadPersistedRelaySpec(*configRoot, *relayID)
	if err != nil {
		return commandapi.ValidationError(err)
	}
	desired, managed, err := firewallDesired(gatewaySpec)
	if err != nil {
		return commandapi.ValidationError(err)
	}
	if !managed {
		return commandapi.WriteJSON(os.Stdout, commandapi.Result{
			OK: true, Command: "firewall " + action, Data: map[string]any{"relayId": *relayID, "managed": false},
		})
	}
	ctx, cancel := context.WithTimeout(context.Background(), 30*time.Second)
	defer cancel()
	if action == "cleanup" {
		if err := firewallapi.Cleanup(ctx, firewallapi.OSRunner{}, desired); err != nil {
			return &commandapi.Error{Code: "firewall_cleanup_failed", Message: err.Error(), ExitCode: commandapi.ExitApply}
		}
		return commandapi.WriteJSON(os.Stdout, commandapi.Result{
			OK: true, Command: "firewall cleanup", Data: map[string]any{"relayId": *relayID, "managed": true},
		})
	}
	result, err := firewallapi.Apply(ctx, firewallapi.OSRunner{}, desired)
	if err != nil {
		return &commandapi.Error{Code: "firewall_apply_failed", Message: err.Error(), ExitCode: commandapi.ExitApply}
	}
	return commandapi.WriteJSON(os.Stdout, commandapi.Result{OK: true, Command: "firewall apply", Data: result})
}

func firewallDesired(gatewaySpec spec.GatewaySpec) (firewallapi.Desired, bool, error) {
	desired := firewallapi.Desired{
		RelayID: gatewaySpec.RelayID, RedirectPort: singboxInternalRedirectPort(gatewaySpec.RelayID),
		DNSListenAddress: gatewaySpec.DNS.ListenAddress,
	}
	switch gatewaySpec.Gateway.Protocol {
	case "openvpn_tcp_singbox":
		desired.Interface = openvpnapi.InterfaceName(gatewaySpec.RelayID)
		return desired, true, nil
	case "ipsec_l2tp_singbox":
		localIP, _, _, err := ipsecapi.NetworkAddresses(gatewaySpec.IPSecL2TP.Network)
		if err != nil {
			return firewallapi.Desired{}, false, err
		}
		desired.Interface = "ppp+"
		desired.DNSRedirect = true
		desired.DNSListenAddress = localIP.String()
		return desired, true, nil
	default:
		return firewallapi.Desired{}, false, nil
	}
}

func singboxInternalRedirectPort(relayID string) int {
	return singboxconfig.InternalRedirectPort(relayID)
}

func runDNSApply(args []string) error {
	flags := flag.NewFlagSet("dns apply", flag.ContinueOnError)
	flags.SetOutput(os.Stderr)
	relayID := flags.String("relay-id", "", "relay identifier")
	configRoot := flags.String("config-root", "/etc/omnirelay", "managed configuration root")
	transactionRoot := flags.String("transaction-root", "/var/lib/omnirelay/transactions", "transaction journal root")
	systemdRoot := flags.String("systemd-root", "/etc/systemd/system", "systemd unit root")
	nginxRoot := flags.String("nginx-root", "/etc/nginx/sites-available", "nginx sites-available root")
	dnsmasqRoot := flags.String("dnsmasq-root", "/etc/dnsmasq.d", "dnsmasq configuration root")
	_ = flags.Bool("json", false, "emit JSON result")
	if err := flags.Parse(args); err != nil {
		return &commandapi.Error{Code: "usage", Message: err.Error(), ExitCode: commandapi.ExitUsage}
	}
	if *relayID == "" {
		return &commandapi.Error{Code: "usage", Message: "--relay-id is required", ExitCode: commandapi.ExitUsage}
	}
	gatewaySpec, err := loadPersistedRelaySpec(*configRoot, *relayID)
	if err != nil {
		return commandapi.ValidationError(err)
	}
	if err := host.VerifySupportedPlatform(); err != nil {
		return &commandapi.Error{Code: "unsupported_platform", Message: err.Error(), ExitCode: commandapi.ExitValidation}
	}
	manager := host.OSSystemd{}
	reconcileAndRestart := func() error {
		ctx, cancel := context.WithTimeout(context.Background(), 45*time.Second)
		defer cancel()
		if err := manager.DaemonReload(ctx); err != nil {
			return err
		}
		units := []string{"omnirelay-connector-" + *relayID + ".service"}
		if runtime := protocolRuntime(gatewaySpec.Gateway.Protocol); runtime == "openvpn" || runtime == "ipsec_l2tp" {
			units = append(units, "dnsmasq.service")
		}
		return manager.Restart(ctx, units...)
	}
	result, err := reconcile.Apply(gatewaySpec, reconcile.ApplyOptions{
		ConfigRoot: *configRoot, TransactionRoot: *transactionRoot, SystemdRoot: *systemdRoot,
		NginxRoot: *nginxRoot, DNSMasqRoot: *dnsmasqRoot,
		ValidatePersisted: func(path string) error {
			_, validateErr := spec.LoadFile(path)
			return validateErr
		},
		AfterWrite: reconcileAndRestart, AfterRollback: reconcileAndRestart,
	})
	if err != nil {
		return &commandapi.Error{Code: "dns_apply_failed", Message: err.Error(), ExitCode: commandapi.ExitApply}
	}
	return commandapi.WriteJSON(os.Stdout, commandapi.Result{
		OK: true, Command: "dns apply", Data: map[string]any{"relayId": *relayID, "transactionId": result.TransactionID, "changed": result.Changed},
	})
}

func runDNSStatus(args []string) error {
	flags := flag.NewFlagSet("dns status", flag.ContinueOnError)
	flags.SetOutput(os.Stderr)
	relayID := flags.String("relay-id", "", "relay identifier")
	configRoot := flags.String("config-root", "/etc/omnirelay", "managed configuration root")
	timeoutSeconds := flags.Int("timeout", 10, "DNS query timeout in seconds")
	_ = flags.Bool("json", false, "emit JSON result")
	if err := flags.Parse(args); err != nil {
		return &commandapi.Error{Code: "usage", Message: err.Error(), ExitCode: commandapi.ExitUsage}
	}
	if *relayID == "" || *timeoutSeconds < 1 || *timeoutSeconds > 60 {
		return &commandapi.Error{Code: "usage", Message: "--relay-id is required and timeout must be between 1 and 60", ExitCode: commandapi.ExitUsage}
	}
	gatewaySpec, err := loadPersistedRelaySpec(*configRoot, *relayID)
	if err != nil {
		return commandapi.ValidationError(err)
	}
	result := dnsapi.Status(context.Background(), gatewaySpec.DNS.ListenAddress, gatewaySpec.DNS.ListenPort, time.Duration(*timeoutSeconds)*time.Second)
	if err := commandapi.WriteJSON(os.Stdout, commandapi.Result{OK: result.Healthy, Command: "dns status", Data: result}); err != nil {
		return err
	}
	if !result.Healthy {
		return &commandapi.Error{Code: result.ReasonCode, Message: "DNS path is unhealthy", ExitCode: commandapi.ExitProbe}
	}
	return nil
}

func runGatewayRepair(args []string) error {
	flags := flag.NewFlagSet("gateway repair", flag.ContinueOnError)
	flags.SetOutput(os.Stderr)
	relayID := flags.String("relay-id", "", "relay identifier")
	level := flags.String("level", "safe", "repair level: safe or full")
	configRoot := flags.String("config-root", "/etc/omnirelay", "managed configuration root")
	transactionRoot := flags.String("transaction-root", "/var/lib/omnirelay/transactions", "transaction journal root")
	systemdRoot := flags.String("systemd-root", "/etc/systemd/system", "systemd unit root")
	nginxRoot := flags.String("nginx-root", "/etc/nginx/sites-available", "nginx sites-available root")
	dnsmasqRoot := flags.String("dnsmasq-root", "/etc/dnsmasq.d", "dnsmasq configuration root")
	_ = flags.Bool("json", false, "emit JSON result")
	if err := flags.Parse(args); err != nil {
		return &commandapi.Error{Code: "usage", Message: err.Error(), ExitCode: commandapi.ExitUsage}
	}
	if *relayID == "" || (*level != "safe" && *level != "full") {
		return &commandapi.Error{Code: "usage", Message: "--relay-id is required and --level must be safe or full", ExitCode: commandapi.ExitUsage}
	}
	gatewaySpec, err := loadPersistedRelaySpec(*configRoot, *relayID)
	if err != nil {
		return commandapi.ValidationError(err)
	}
	if err := host.VerifySupportedPlatform(); err != nil {
		return &commandapi.Error{Code: "unsupported_platform", Message: err.Error(), ExitCode: commandapi.ExitValidation}
	}
	manager := host.OSSystemd{}
	reload := func() error {
		ctx, cancel := context.WithTimeout(context.Background(), 30*time.Second)
		defer cancel()
		return manager.DaemonReload(ctx)
	}
	result, err := reconcile.Apply(gatewaySpec, reconcile.ApplyOptions{
		ConfigRoot: *configRoot, TransactionRoot: *transactionRoot, SystemdRoot: *systemdRoot, NginxRoot: *nginxRoot, DNSMasqRoot: *dnsmasqRoot,
		ValidatePersisted: func(path string) error {
			_, validateErr := spec.LoadFile(path)
			return validateErr
		},
		AfterWrite: reload, AfterRollback: reload,
	})
	if err != nil {
		return &commandapi.Error{Code: "gateway_repair_failed", Message: err.Error(), ExitCode: commandapi.ExitApply}
	}
	started := false
	if *level == "full" {
		ctx, cancel := context.WithTimeout(context.Background(), 45*time.Second)
		defer cancel()
		if err := manager.EnableNow(ctx, host.GatewayTarget(*relayID)); err != nil {
			return &commandapi.Error{Code: "gateway_repair_start_failed", Message: err.Error(), ExitCode: commandapi.ExitApply}
		}
		started = true
	}
	return commandapi.WriteJSON(os.Stdout, commandapi.Result{
		OK: true, Command: "gateway repair",
		Data: map[string]any{"relayId": *relayID, "level": *level, "changed": result.Changed, "started": started, "transactionId": result.TransactionID},
	})
}

func runGatewayUninstall(args []string) error {
	flags := flag.NewFlagSet("gateway uninstall", flag.ContinueOnError)
	flags.SetOutput(os.Stderr)
	relayID := flags.String("relay-id", "", "relay identifier")
	configRoot := flags.String("config-root", "/etc/omnirelay", "managed configuration root")
	transactionRoot := flags.String("transaction-root", "/var/lib/omnirelay/transactions", "transaction journal root")
	systemdRoot := flags.String("systemd-root", "/etc/systemd/system", "systemd unit root")
	nginxRoot := flags.String("nginx-root", "/etc/nginx/sites-available", "nginx sites-available root")
	dnsmasqRoot := flags.String("dnsmasq-root", "/etc/dnsmasq.d", "dnsmasq configuration root")
	_ = flags.Bool("json", false, "emit JSON result")
	if err := flags.Parse(args); err != nil {
		return &commandapi.Error{Code: "usage", Message: err.Error(), ExitCode: commandapi.ExitUsage}
	}
	if *relayID == "" {
		return &commandapi.Error{Code: "usage", Message: "--relay-id is required", ExitCode: commandapi.ExitUsage}
	}
	if _, err := loadPersistedRelaySpec(*configRoot, *relayID); err != nil {
		return commandapi.ValidationError(err)
	}
	if err := host.VerifySupportedPlatform(); err != nil {
		return &commandapi.Error{Code: "unsupported_platform", Message: err.Error(), ExitCode: commandapi.ExitValidation}
	}
	manager := host.OSSystemd{}
	ctx, cancel := context.WithTimeout(context.Background(), 45*time.Second)
	defer cancel()
	if err := manager.DisableNow(ctx, host.GatewayTarget(*relayID)); err != nil {
		return &commandapi.Error{Code: "gateway_uninstall_stop_failed", Message: err.Error(), ExitCode: commandapi.ExitApply}
	}
	reload := func() error {
		reloadCtx, reloadCancel := context.WithTimeout(context.Background(), 30*time.Second)
		defer reloadCancel()
		return manager.DaemonReload(reloadCtx)
	}
	result, err := reconcile.Uninstall(*relayID, reconcile.ApplyOptions{
		ConfigRoot: *configRoot, TransactionRoot: *transactionRoot, SystemdRoot: *systemdRoot, NginxRoot: *nginxRoot, DNSMasqRoot: *dnsmasqRoot,
		AfterWrite: reload, AfterRollback: reload,
	})
	if err != nil {
		restoreCtx, restoreCancel := context.WithTimeout(context.Background(), 45*time.Second)
		defer restoreCancel()
		if restoreErr := manager.EnableNow(restoreCtx, host.GatewayTarget(*relayID)); restoreErr != nil {
			err = fmt.Errorf("%w; failed to restore gateway target state: %v", err, restoreErr)
		}
		return &commandapi.Error{Code: "gateway_uninstall_failed", Message: err.Error(), ExitCode: commandapi.ExitApply}
	}
	frpsResult, err := frpapi.Reconcile(frpapi.ReconcileOptions{ConfigRoot: *configRoot, SystemdRoot: *systemdRoot})
	if err != nil {
		return &commandapi.Error{Code: "frps_reconcile_failed", Message: err.Error(), ExitCode: commandapi.ExitApply}
	}
	var frpsLifecycleErr error
	if frpsResult.Active {
		frpsLifecycleErr = manager.DaemonReload(ctx)
		if frpsLifecycleErr == nil && frpsResult.Changed {
			frpsLifecycleErr = manager.Restart(ctx, frpapi.ServiceName)
		}
	} else {
		_ = manager.DisableNow(ctx, frpapi.ServiceName)
		frpsLifecycleErr = manager.DaemonReload(ctx)
	}
	if frpsLifecycleErr != nil {
		return &commandapi.Error{Code: "frps_lifecycle_failed", Message: frpsLifecycleErr.Error(), ExitCode: commandapi.ExitApply}
	}
	return commandapi.WriteJSON(os.Stdout, commandapi.Result{OK: true, Command: "gateway uninstall", Data: result})
}

func runOpenVPNAuthenticate(args []string) error {
	flags := flag.NewFlagSet("openvpn authenticate", flag.ContinueOnError)
	flags.SetOutput(os.Stderr)
	relayID := flags.String("relay-id", "", "relay identifier")
	configRoot := flags.String("config-root", "/etc/omnirelay", "managed configuration root")
	databasePath := flags.String("database", "", "accounting database path override")
	credentialsFile := flags.String("credentials-file", "", "OpenVPN via-file credentials path")
	jsonOutput := flags.Bool("json", false, "emit JSON result")
	if err := flags.Parse(args); err != nil {
		return &commandapi.Error{Code: "usage", Message: err.Error(), ExitCode: commandapi.ExitUsage}
	}
	if *relayID == "" {
		return &commandapi.Error{Code: "usage", Message: "--relay-id is required", ExitCode: commandapi.ExitUsage}
	}
	if *credentialsFile == "" && len(flags.Args()) == 1 {
		*credentialsFile = flags.Args()[0]
	}
	if *credentialsFile == "" {
		return &commandapi.Error{Code: "usage", Message: "credentials file is required", ExitCode: commandapi.ExitUsage}
	}
	gatewaySpec, err := loadPersistedRelaySpec(*configRoot, *relayID)
	if err != nil {
		return commandapi.ValidationError(err)
	}
	if gatewaySpec.Gateway.Protocol != "openvpn_tcp_singbox" {
		return commandapi.ValidationError(fmt.Errorf("persisted gateway protocol is not OpenVPN"))
	}
	dbPath := valueOrDefault(*databasePath, relayGatewayPaths(*configRoot, *relayID).accountingDB)
	db, err := sql.Open("sqlite", dbPath)
	if err != nil {
		return &commandapi.Error{Code: "openvpn_auth_failed", Message: err.Error(), ExitCode: commandapi.ExitApply}
	}
	defer db.Close()
	if err := accounting.Migrate(db); err != nil {
		return &commandapi.Error{Code: "openvpn_auth_failed", Message: err.Error(), ExitCode: commandapi.ExitApply}
	}
	result, err := openvpnapi.Authenticate(db, gatewaySpec.Gateway.Protocol, *credentialsFile)
	if err != nil {
		return &commandapi.Error{Code: "openvpn_auth_failed", Message: err.Error(), ExitCode: commandapi.ExitApply}
	}
	if *jsonOutput {
		if err := commandapi.WriteJSON(os.Stdout, commandapi.Result{OK: result.Allowed, Command: "openvpn authenticate", Data: result}); err != nil {
			return err
		}
	}
	if !result.Allowed {
		return &commandapi.Error{Code: result.ReasonCode, Message: "OpenVPN authentication denied", ExitCode: 1}
	}
	return nil
}

func runOpenVPNEnforce(args []string) error {
	flags := flag.NewFlagSet("openvpn enforce", flag.ContinueOnError)
	flags.SetOutput(os.Stderr)
	relayID := flags.String("relay-id", "", "relay identifier")
	configRoot := flags.String("config-root", "/etc/omnirelay", "managed configuration root")
	databasePath := flags.String("database", "", "accounting database path override")
	_ = flags.Bool("json", false, "emit JSON result")
	if err := flags.Parse(args); err != nil {
		return &commandapi.Error{Code: "usage", Message: err.Error(), ExitCode: commandapi.ExitUsage}
	}
	if *relayID == "" {
		return &commandapi.Error{Code: "usage", Message: "--relay-id is required", ExitCode: commandapi.ExitUsage}
	}
	gatewaySpec, err := loadPersistedRelaySpec(*configRoot, *relayID)
	if err != nil {
		return commandapi.ValidationError(err)
	}
	if gatewaySpec.Gateway.Protocol != "openvpn_tcp_singbox" {
		return commandapi.ValidationError(fmt.Errorf("persisted gateway protocol is not OpenVPN"))
	}
	dbPath := valueOrDefault(*databasePath, relayGatewayPaths(*configRoot, *relayID).accountingDB)
	db, err := sql.Open("sqlite", dbPath)
	if err != nil {
		return &commandapi.Error{Code: "openvpn_enforce_failed", Message: err.Error(), ExitCode: commandapi.ExitApply}
	}
	defer db.Close()
	if err := accounting.Migrate(db); err != nil {
		return &commandapi.Error{Code: "openvpn_enforce_failed", Message: err.Error(), ExitCode: commandapi.ExitApply}
	}
	ctx, cancel := context.WithTimeout(context.Background(), 15*time.Second)
	defer cancel()
	result, err := openvpnapi.Enforce(ctx, db, gatewaySpec.Gateway.Protocol, openvpnapi.ManagementPort(*relayID))
	if err != nil {
		return &commandapi.Error{Code: "openvpn_enforce_failed", Message: err.Error(), ExitCode: commandapi.ExitApply}
	}
	return commandapi.WriteJSON(os.Stdout, commandapi.Result{OK: true, Command: "openvpn enforce", Data: result})
}

func runGatewayValidate(args []string) error {
	flags := flag.NewFlagSet("gateway validate", flag.ContinueOnError)
	flags.SetOutput(os.Stderr)
	specPath := flags.String("spec", "", "gateway specification path")
	jsonOutput := flags.Bool("json", false, "emit JSON result")
	if err := flags.Parse(args); err != nil {
		return &commandapi.Error{Code: "usage", Message: err.Error(), ExitCode: commandapi.ExitUsage}
	}
	if *specPath == "" {
		return &commandapi.Error{Code: "usage", Message: "--spec is required", ExitCode: commandapi.ExitUsage}
	}
	gatewaySpec, err := spec.LoadFile(*specPath)
	if err != nil {
		return commandapi.ValidationError(err)
	}
	if *jsonOutput {
		return commandapi.WriteJSON(os.Stdout, commandapi.Result{
			OK: true, Command: "gateway validate", Message: "gateway specification is valid",
			Data: map[string]string{"relayId": gatewaySpec.RelayID, "protocol": gatewaySpec.Gateway.Protocol},
		})
	}
	fmt.Fprintln(os.Stdout, "Gateway specification is valid.")
	return nil
}

func runGatewayPlan(args []string) error {
	flags := flag.NewFlagSet("gateway plan", flag.ContinueOnError)
	flags.SetOutput(os.Stderr)
	specPath := flags.String("spec", "", "gateway specification path")
	_ = flags.Bool("json", false, "emit JSON result")
	if err := flags.Parse(args); err != nil {
		return &commandapi.Error{Code: "usage", Message: err.Error(), ExitCode: commandapi.ExitUsage}
	}
	if *specPath == "" {
		return &commandapi.Error{Code: "usage", Message: "--spec is required", ExitCode: commandapi.ExitUsage}
	}
	gatewaySpec, err := spec.LoadFile(*specPath)
	if err != nil {
		return commandapi.ValidationError(err)
	}
	return commandapi.WriteJSON(os.Stdout, commandapi.Result{
		OK: true, Command: "gateway plan", Data: reconcile.BuildPlan(gatewaySpec),
	})
}

func runGatewayApply(args []string) error {
	flags := flag.NewFlagSet("gateway apply", flag.ContinueOnError)
	flags.SetOutput(os.Stderr)
	specPath := flags.String("spec", "", "gateway specification path")
	configRoot := flags.String("config-root", "/etc/omnirelay", "managed configuration root")
	transactionRoot := flags.String("transaction-root", "/var/lib/omnirelay/transactions", "transaction journal root")
	systemdRoot := flags.String("systemd-root", "/etc/systemd/system", "systemd unit root")
	nginxRoot := flags.String("nginx-root", "/etc/nginx/sites-available", "nginx sites-available root")
	dnsmasqRoot := flags.String("dnsmasq-root", "/etc/dnsmasq.d", "dnsmasq configuration root")
	reloadSystemd := flags.Bool("reload-systemd", true, "validate generated units with systemd daemon-reload")
	installPackages := flags.Bool("install-packages", true, "ensure required Ubuntu/Debian packages before apply")
	_ = flags.Bool("json", false, "emit JSON result")
	if err := flags.Parse(args); err != nil {
		return &commandapi.Error{Code: "usage", Message: err.Error(), ExitCode: commandapi.ExitUsage}
	}
	if *specPath == "" {
		return &commandapi.Error{Code: "usage", Message: "--spec is required", ExitCode: commandapi.ExitUsage}
	}
	gatewaySpec, err := spec.LoadFile(*specPath)
	if err != nil {
		return commandapi.ValidationError(err)
	}
	if err := host.VerifySupportedPlatform(); err != nil {
		return &commandapi.Error{Code: "unsupported_platform", Message: err.Error(), ExitCode: commandapi.ExitValidation}
	}
	if *installPackages {
		ctx, cancel := context.WithTimeout(context.Background(), 15*time.Minute)
		defer cancel()
		if err := (host.OSPackageManager{}).Ensure(ctx, reconcile.BuildPlan(gatewaySpec).RequiredPackages); err != nil {
			return &commandapi.Error{Code: "package_reconciliation_failed", Message: err.Error(), ExitCode: commandapi.ExitApply}
		}
	}
	if err := ensureGatewayHostAccounts(gatewaySpec); err != nil {
		return &commandapi.Error{Code: "account_reconciliation_failed", Message: err.Error(), ExitCode: commandapi.ExitApply}
	}
	applyOptions := reconcile.ApplyOptions{
		ConfigRoot: *configRoot, TransactionRoot: *transactionRoot, SystemdRoot: *systemdRoot, NginxRoot: *nginxRoot, DNSMasqRoot: *dnsmasqRoot,
		ValidatePersisted: func(path string) error {
			_, validateErr := spec.LoadFile(path)
			return validateErr
		},
	}
	if *reloadSystemd {
		validateAndReload := func() error {
			ctx, cancel := context.WithTimeout(context.Background(), 30*time.Second)
			defer cancel()
			if gatewaySpec.Panel.Port > 0 {
				if err := (host.OSSudoersValidator{}).Validate(ctx, filepath.Join("/etc/sudoers.d", "omnirelay-connector-core-"+gatewaySpec.RelayID)); err != nil {
					return err
				}
			}
			return (host.OSSystemd{}).DaemonReload(ctx)
		}
		reload := func() error {
			ctx, cancel := context.WithTimeout(context.Background(), 30*time.Second)
			defer cancel()
			return (host.OSSystemd{}).DaemonReload(ctx)
		}
		applyOptions.AfterWrite = validateAndReload
		applyOptions.AfterRollback = reload
	}
	result, err := reconcile.Apply(gatewaySpec, applyOptions)
	if err != nil {
		return &commandapi.Error{Code: "apply_failed", Message: err.Error(), ExitCode: commandapi.ExitApply}
	}
	return commandapi.WriteJSON(os.Stdout, commandapi.Result{
		OK: true, Command: "gateway apply", Data: result,
	})
}

func ensureGatewayHostAccounts(gatewaySpec spec.GatewaySpec) error {
	if gatewaySpec.Panel.Port == 0 {
		return nil
	}
	ctx, cancel := context.WithTimeout(context.Background(), 30*time.Second)
	defer cancel()
	return (host.OSAccountManager{}).EnsureSystemAccount(ctx, "omnigateway", filepath.Join("/opt/omnirelay/relays", gatewaySpec.RelayID, "omnipanel"))
}

func runGatewayLifecycle(action string, args []string) error {
	flags := flag.NewFlagSet("gateway "+action, flag.ContinueOnError)
	flags.SetOutput(os.Stderr)
	relayID := flags.String("relay-id", "", "relay identifier")
	configRoot := flags.String("config-root", "/etc/omnirelay", "managed configuration root")
	_ = flags.Bool("json", false, "emit JSON result")
	if err := flags.Parse(args); err != nil {
		return &commandapi.Error{Code: "usage", Message: err.Error(), ExitCode: commandapi.ExitUsage}
	}
	if *relayID == "" {
		return &commandapi.Error{Code: "usage", Message: "--relay-id is required", ExitCode: commandapi.ExitUsage}
	}
	gatewaySpec, err := loadPersistedRelaySpec(*configRoot, *relayID)
	if err != nil {
		return commandapi.ValidationError(err)
	}
	if action == "start" {
		if err := reconcile.ValidateRuntimeAssets(gatewaySpec, *configRoot); err != nil {
			return &commandapi.Error{Code: "runtime_assets_invalid", Message: err.Error(), ExitCode: commandapi.ExitValidation}
		}
		if err := prepareProtocolClients(gatewaySpec, *configRoot); err != nil {
			return &commandapi.Error{Code: "client_prepare_failed", Message: err.Error(), ExitCode: commandapi.ExitApply}
		}
	}
	manager := host.OSSystemd{}
	ctx, cancel := context.WithTimeout(context.Background(), 45*time.Second)
	defer cancel()
	target := host.GatewayTarget(*relayID)
	err = nil
	if action == "start" {
		// Legacy sing-box used a different unit name and can otherwise retain the
		// public listener during a coordinated migration.
		_ = manager.DisableNow(ctx, "omnirelay-singbox-"+*relayID+".service")
		if gatewaySpec.Gateway.Type == "remote" {
			if _, err = frpapi.Reconcile(frpapi.ReconcileOptions{ConfigRoot: *configRoot}); err != nil {
				return &commandapi.Error{Code: "frps_reconcile_failed", Message: err.Error(), ExitCode: commandapi.ExitApply}
			}
		}
		if err = manager.DaemonReload(ctx); err == nil && gatewaySpec.Gateway.Type == "remote" {
			err = manager.EnableNow(ctx, frpapi.ServiceName)
		}
		if err == nil {
			err = manager.EnableNow(ctx, target)
		}
		if err == nil {
			err = manager.Restart(ctx, gatewayRuntimeRestartUnits(gatewaySpec)...)
		}
	} else {
		err = manager.Stop(ctx, target)
	}
	if err != nil {
		return &commandapi.Error{Code: "gateway_" + action + "_failed", Message: err.Error(), ExitCode: commandapi.ExitApply}
	}
	return commandapi.WriteJSON(os.Stdout, commandapi.Result{
		OK: true, Command: "gateway " + action, Data: map[string]string{"relayId": *relayID, "target": target},
	})
}

func gatewayRuntimeRestartUnits(gatewaySpec spec.GatewaySpec) []string {
	relayID := gatewaySpec.RelayID
	units := []string{"omnirelay-connector-" + relayID + ".service"}
	switch gatewaySpec.Gateway.Protocol {
	case "openvpn_tcp_singbox":
		units = append(units, "omnirelay-openvpn-"+relayID+".service", "omnirelay-firewall-"+relayID+".service")
	case "ipsec_l2tp_singbox":
		units = append(units, "omnirelay-ipsec-"+relayID+".service", "omnirelay-firewall-"+relayID+".service")
	}
	return units
}

func prepareProtocolClients(gatewaySpec spec.GatewaySpec, configRoot string) error {
	paths := relayGatewayPaths(configRoot, gatewaySpec.RelayID)
	if err := os.MkdirAll(filepath.Dir(paths.accountingDB), 0o750); err != nil {
		return err
	}
	db, err := sql.Open("sqlite", paths.accountingDB)
	if err != nil {
		return err
	}
	defer db.Close()
	if err := accounting.Migrate(db); err != nil {
		return err
	}
	if err := seedInitialClient(db, gatewaySpec.Gateway.Protocol); err != nil {
		return err
	}
	_, _, err = syncProtocolClients(gatewaySpec, paths, db, paths.connectorConfig)
	return err
}

func seedInitialClient(db *sql.DB, protocolID string) error {
	definition, ok := protocol.Lookup(protocolID)
	if !ok {
		return fmt.Errorf("unsupported protocol %q", protocolID)
	}
	if !definition.PerClient && definition.Runtime != "openvpn" && definition.Runtime != "ipsec_l2tp" {
		return nil
	}
	var existing int
	if err := db.QueryRow(`SELECT COUNT(1) FROM clients WHERE protocol_id=?`, protocolID).Scan(&existing); err != nil {
		return err
	}
	if existing > 0 {
		return nil
	}
	clientID, err := randomUUID()
	if err != nil {
		return err
	}
	var secret string
	switch protocolID {
	case "shadowsocks_singbox", "shadowtls_v3_shadowsocks_singbox":
		// Shadowsocks 2022 (2022-blake3-aes-128-gcm) requires each user PSK to
		// decode to exactly 16 bytes, matching the cipher key size and the
		// format produced by the panel's random2022Key() helper.
		secret, err = randomShadowsocks2022Secret()
	default:
		secret, err = randomSecret(18)
	}
	if err != nil {
		return err
	}
	email := "omni-client@local"
	username := email
	authUsername := ""
	switch definition.Runtime {
	case "openvpn":
		username = "ovpn_client"
		authUsername = "ovpn_client"
	case "ipsec_l2tp":
		username = "l2tp_client"
		authUsername = "l2tp_client"
	}
	now := time.Now().Unix()
	tx, err := db.Begin()
	if err != nil {
		return err
	}
	defer tx.Rollback()
	if _, err := tx.Exec(`
INSERT INTO clients(client_id,protocol_id,email,username,auth_username,auth_secret,enabled,total_bytes_limit,speed_limit_kbps,expiry_unix_ms,created_at,updated_at)
VALUES(?,?,?,?,?,?,1,0,0,0,?,?)`,
		clientID, protocolID, email, username, authUsername, secret, now, now); err != nil {
		return err
	}
	if _, err := tx.Exec(`INSERT OR IGNORE INTO usage_totals(client_id,used_bytes,updated_at) VALUES(?,0,?)`, clientID, now); err != nil {
		return err
	}
	if _, err := tx.Exec(`INSERT OR IGNORE INTO connection_counters(client_id,active_connections,last_seen_at) VALUES(?,0,0)`, clientID); err != nil {
		return err
	}
	if _, err := tx.Exec(`INSERT OR IGNORE INTO enforcement_state(client_id,disabled_reason,disabled_at,updated_at) VALUES(?,'',0,0)`, clientID); err != nil {
		return err
	}
	return tx.Commit()
}

func randomUUID() (string, error) {
	var value [16]byte
	if _, err := rand.Read(value[:]); err != nil {
		return "", err
	}
	value[6] = (value[6] & 0x0f) | 0x40
	value[8] = (value[8] & 0x3f) | 0x80
	return fmt.Sprintf("%08x-%04x-%04x-%04x-%012x",
		value[0:4], value[4:6], value[6:8], value[8:10], value[10:16]), nil
}

func randomSecret(byteCount int) (string, error) {
	if byteCount <= 0 {
		byteCount = 18
	}
	value := make([]byte, byteCount)
	if _, err := rand.Read(value); err != nil {
		return "", err
	}
	return base64.RawURLEncoding.EncodeToString(value), nil
}

// randomShadowsocks2022Secret returns a 16-byte key encoded with standard
// (padded) base64, matching the PSK format required by sing-box for
// 2022-blake3-aes-128-gcm users and produced by the panel's random2022Key().
func randomShadowsocks2022Secret() (string, error) {
	value := make([]byte, 16)
	if _, err := rand.Read(value); err != nil {
		return "", err
	}
	return base64.StdEncoding.EncodeToString(value), nil
}

func runGatewayStatus(kind string, args []string) error {
	flags := flag.NewFlagSet("gateway "+kind, flag.ContinueOnError)
	flags.SetOutput(os.Stderr)
	relayID := flags.String("relay-id", "", "relay identifier")
	configRoot := flags.String("config-root", "/etc/omnirelay", "managed configuration root")
	_ = flags.Bool("json", false, "emit JSON result")
	if err := flags.Parse(args); err != nil {
		return &commandapi.Error{Code: "usage", Message: err.Error(), ExitCode: commandapi.ExitUsage}
	}
	if *relayID == "" {
		return &commandapi.Error{Code: "usage", Message: "--relay-id is required", ExitCode: commandapi.ExitUsage}
	}
	gatewaySpec, err := loadPersistedRelaySpec(*configRoot, *relayID)
	if err != nil {
		return commandapi.ValidationError(err)
	}
	result := statusapi.Collect(context.Background(), gatewaySpec, statusapi.Options{IncludeProbe: kind == "health"})
	return commandapi.WriteJSON(os.Stdout, commandapi.Result{
		OK: result.Healthy, Command: "gateway " + kind, Data: result,
	})
}

func runAccountingMigrate(args []string) error {
	flags := flag.NewFlagSet("accounting migrate", flag.ContinueOnError)
	flags.SetOutput(os.Stderr)
	relayID := flags.String("relay-id", "", "relay identifier")
	configRoot := flags.String("config-root", "/etc/omnirelay", "managed configuration root")
	databasePath := flags.String("database", "", "accounting database path override")
	_ = flags.Bool("json", false, "emit JSON result")
	if err := flags.Parse(args); err != nil {
		return &commandapi.Error{Code: "usage", Message: err.Error(), ExitCode: commandapi.ExitUsage}
	}
	if *relayID == "" {
		return &commandapi.Error{Code: "usage", Message: "--relay-id is required", ExitCode: commandapi.ExitUsage}
	}
	specPath := filepath.Join(*configRoot, "relays", *relayID, "gateway", "spec.json")
	gatewaySpec, err := spec.LoadFile(specPath)
	if err != nil {
		return commandapi.ValidationError(err)
	}
	if gatewaySpec.RelayID != *relayID {
		return commandapi.ValidationError(fmt.Errorf("relay-id does not match persisted gateway specification"))
	}
	dbPath := *databasePath
	if dbPath == "" {
		dbPath = filepath.Join(*configRoot, "relays", *relayID, "gateway", "connector", "accounting.db")
	}
	if err := os.MkdirAll(filepath.Dir(dbPath), 0o770); err != nil {
		return &commandapi.Error{Code: "accounting_migration_failed", Message: err.Error(), ExitCode: commandapi.ExitApply}
	}
	db, err := sql.Open("sqlite", dbPath)
	if err != nil {
		return &commandapi.Error{Code: "accounting_migration_failed", Message: err.Error(), ExitCode: commandapi.ExitApply}
	}
	defer db.Close()
	if err := accounting.Migrate(db); err != nil {
		return &commandapi.Error{Code: "accounting_migration_failed", Message: err.Error(), ExitCode: commandapi.ExitApply}
	}
	return commandapi.WriteJSON(os.Stdout, commandapi.Result{
		OK: true, Command: "accounting migrate",
		Data: map[string]any{"relayId": gatewaySpec.RelayID, "schemaVersion": accounting.CurrentSchemaVersion},
	})
}

func runAccountingSync(args []string) error {
	flags := flag.NewFlagSet("accounting sync", flag.ContinueOnError)
	flags.SetOutput(os.Stderr)
	relayID := flags.String("relay-id", "", "relay identifier")
	configRoot := flags.String("config-root", "/etc/omnirelay", "managed configuration root")
	databasePath := flags.String("database", "", "accounting database path override")
	connectorConfigPath := flags.String("config", "", "connector config path override")
	lockPath := flags.String("lock-file", "", "operation lock path override")
	openVPNStatusPath := flags.String("openvpn-status", "", "OpenVPN status file override")
	pppSessionsPath := flags.String("ppp-sessions", "", "PPP sessions file override")
	sysClassNetRoot := flags.String("sys-class-net-root", "/sys/class/net", "network interface statistics root")
	_ = flags.Bool("json", false, "emit JSON result")
	if err := flags.Parse(args); err != nil {
		return &commandapi.Error{Code: "usage", Message: err.Error(), ExitCode: commandapi.ExitUsage}
	}
	if *relayID == "" {
		return &commandapi.Error{Code: "usage", Message: "--relay-id is required", ExitCode: commandapi.ExitUsage}
	}
	gatewaySpec, err := loadPersistedRelaySpec(*configRoot, *relayID)
	if err != nil {
		return commandapi.ValidationError(err)
	}
	paths := relayGatewayPaths(*configRoot, *relayID)
	dbPath := valueOrDefault(*databasePath, paths.accountingDB)
	configPath := valueOrDefault(*connectorConfigPath, paths.connectorConfig)
	operationLockPath := valueOrDefault(*lockPath, filepath.Join("/run/omnirelay", *relayID, "accounting.lock"))
	definition, ok := protocol.Lookup(gatewaySpec.Gateway.Protocol)
	if !ok {
		return commandapi.ValidationError(fmt.Errorf("persisted gateway protocol is unsupported"))
	}
	statusPath := valueOrDefault(*openVPNStatusPath, filepath.Join("/var/log/openvpn", "omnirelay-status-"+*relayID+".log"))
	sessionsPath := valueOrDefault(*pppSessionsPath, filepath.Join("/run/omnirelay", *relayID, "ppp-sessions.tsv"))
	var accountingResult accounting.SyncResult
	var collectResult accounting.CollectResult
	var clientChanged bool
	var activeUsers int
	err = withFileLock(operationLockPath, func() error {
		db, openErr := sql.Open("sqlite", dbPath)
		if openErr != nil {
			return openErr
		}
		defer db.Close()
		var syncErr error
		collectResult, syncErr = accounting.Collect(accounting.CollectInput{
			Database: db, ProtocolID: gatewaySpec.Gateway.Protocol, Source: definition.AccountingSource,
			OpenVPNStatusPath: statusPath, PPPSessionsPath: sessionsPath, SysClassNetRoot: *sysClassNetRoot, Now: time.Now(),
		})
		if syncErr != nil {
			return syncErr
		}
		accountingResult, syncErr = accounting.Sync(accounting.SyncInput{
			Database: db, ProtocolID: gatewaySpec.Gateway.Protocol, Now: time.Now(),
			UsageDeltas: collectResult.UsageDeltas, ActiveConnections: collectResult.ActiveConnections,
		})
		if syncErr != nil || len(accountingResult.EnableChanges) == 0 {
			return syncErr
		}
		clientChanged, activeUsers, syncErr = syncProtocolClients(gatewaySpec, paths, db, configPath)
		return syncErr
	})
	if err != nil {
		return &commandapi.Error{Code: "accounting_sync_failed", Message: err.Error(), ExitCode: commandapi.ExitApply}
	}
	var runtimeReloaded bool
	var reloadErr error
	if clientChanged && protocolRuntime(gatewaySpec.Gateway.Protocol) == "singbox" {
		runtimeReloaded, reloadErr = reloadConnectorRuntime(context.Background(), gatewaySpec.RelayID)
	}
	data := map[string]any{
		"relayId":            gatewaySpec.RelayID,
		"protocol":           gatewaySpec.Gateway.Protocol,
		"updatedClients":     accountingResult.UpdatedClients,
		"clientStateChanged": clientChanged,
		"activeUsers":        activeUsers,
		"observedSessions":   collectResult.ObservedSessions,
		"attributedSessions": collectResult.AttributedSessions,
		"runtimeReloaded":    runtimeReloaded,
	}
	if reloadErr != nil {
		data["runtimeReloadError"] = reloadErr.Error()
	}
	return commandapi.WriteJSON(os.Stdout, commandapi.Result{
		OK: true, Command: "accounting sync",
		Data: data,
	})
}

func runClientsSync(args []string) error {
	flags := flag.NewFlagSet("clients sync", flag.ContinueOnError)
	flags.SetOutput(os.Stderr)
	relayID := flags.String("relay-id", "", "relay identifier")
	configRoot := flags.String("config-root", "/etc/omnirelay", "managed configuration root")
	databasePath := flags.String("database", "", "accounting database path override")
	connectorConfigPath := flags.String("config", "", "connector config path override")
	lockPath := flags.String("lock-file", "", "operation lock path override")
	_ = flags.Bool("json", false, "emit JSON result")
	if err := flags.Parse(args); err != nil {
		return &commandapi.Error{Code: "usage", Message: err.Error(), ExitCode: commandapi.ExitUsage}
	}
	if *relayID == "" {
		return &commandapi.Error{Code: "usage", Message: "--relay-id is required", ExitCode: commandapi.ExitUsage}
	}
	gatewaySpec, err := loadPersistedRelaySpec(*configRoot, *relayID)
	if err != nil {
		return commandapi.ValidationError(err)
	}
	paths := relayGatewayPaths(*configRoot, *relayID)
	dbPath := valueOrDefault(*databasePath, paths.accountingDB)
	configPath := valueOrDefault(*connectorConfigPath, paths.connectorConfig)
	operationLockPath := valueOrDefault(*lockPath, filepath.Join("/run/omnirelay", *relayID, "accounting.lock"))

	var changed bool
	var activeUsers int
	err = withFileLock(operationLockPath, func() error {
		db, openErr := sql.Open("sqlite", dbPath)
		if openErr != nil {
			return openErr
		}
		defer db.Close()
		if migrateErr := accounting.Migrate(db); migrateErr != nil {
			return migrateErr
		}
		var syncErr error
		changed, activeUsers, syncErr = syncProtocolClients(gatewaySpec, paths, db, configPath)
		return syncErr
	})
	if err != nil {
		return &commandapi.Error{Code: "client_sync_failed", Message: err.Error(), ExitCode: commandapi.ExitApply}
	}
	var runtimeReloaded bool
	var reloadErr error
	if changed && protocolRuntime(gatewaySpec.Gateway.Protocol) == "singbox" {
		runtimeReloaded, reloadErr = reloadConnectorRuntime(context.Background(), gatewaySpec.RelayID)
	}
	data := map[string]any{
		"relayId":         gatewaySpec.RelayID,
		"protocol":        gatewaySpec.Gateway.Protocol,
		"changed":         changed,
		"activeUsers":     activeUsers,
		"runtimeReloaded": runtimeReloaded,
	}
	if reloadErr != nil {
		data["runtimeReloadError"] = reloadErr.Error()
	}
	return commandapi.WriteJSON(os.Stdout, commandapi.Result{
		OK: true, Command: "clients sync",
		Data: data,
	})
}

func runProbe(kind string, args []string) error {
	flags := flag.NewFlagSet("probe "+kind, flag.ContinueOnError)
	flags.SetOutput(os.Stderr)
	relayID := flags.String("relay-id", "", "relay identifier")
	configRoot := flags.String("config-root", "/etc/omnirelay", "managed configuration root")
	backendHost := flags.String("backend-host", "", "backend host override")
	backendPort := flags.Int("backend-port", 0, "backend port override")
	probeURLs := flags.String("probe-urls", "", "comma-separated HTTP(S) probe URL overrides")
	timeoutSeconds := flags.Int("timeout", 0, "per-target timeout override")
	insecureSkipVerify := flags.Bool("insecure-skip-verify", false, "skip TLS certificate verification")
	_ = flags.Bool("json", false, "emit JSON result")
	if err := flags.Parse(args); err != nil {
		return &commandapi.Error{Code: "usage", Message: err.Error(), ExitCode: commandapi.ExitUsage}
	}
	if *relayID == "" {
		return &commandapi.Error{Code: "usage", Message: "--relay-id is required", ExitCode: commandapi.ExitUsage}
	}
	gatewaySpec, err := loadPersistedRelaySpec(*configRoot, *relayID)
	if err != nil {
		return commandapi.ValidationError(err)
	}
	host := valueOrDefault(strings.TrimSpace(*backendHost), gatewaySpec.Tunnel.BackendHost)
	port := *backendPort
	if port == 0 {
		port = gatewaySpec.Tunnel.BackendPort
	}
	targets := gatewaySpec.Tunnel.ProbeURLs
	if strings.TrimSpace(*probeURLs) != "" {
		targets = splitNonEmpty(*probeURLs)
	}
	timeout := *timeoutSeconds
	if timeout == 0 {
		timeout = gatewaySpec.Tunnel.TimeoutSecond
	}
	if port < 1 || port > 65535 || timeout < 1 || timeout > 120 || len(targets) == 0 {
		return &commandapi.Error{Code: "usage", Message: "invalid probe endpoint, timeout, or targets", ExitCode: commandapi.ExitUsage}
	}
	result := probe.Backend(context.Background(), probe.BackendOptions{
		Host: host, Port: port, Targets: targets, Timeout: time.Duration(timeout) * time.Second,
		InsecureSkipVerify: *insecureSkipVerify,
	})
	return commandapi.WriteJSON(os.Stdout, commandapi.Result{
		OK: result.OK, Command: "probe " + kind, Data: result,
	})
}

func runClockSync(args []string) error {
	flags := flag.NewFlagSet("clock sync", flag.ContinueOnError)
	flags.SetOutput(os.Stderr)
	relayID := flags.String("relay-id", "", "relay identifier")
	configRoot := flags.String("config-root", "/etc/omnirelay", "managed configuration root")
	applyThreshold := flags.Int("apply-threshold", 5, "clock adjustment threshold in seconds")
	maxSkew := flags.Int("max-skew", 120, "maximum accepted skew after synchronization")
	_ = flags.Bool("json", false, "emit JSON result")
	if err := flags.Parse(args); err != nil {
		return &commandapi.Error{Code: "usage", Message: err.Error(), ExitCode: commandapi.ExitUsage}
	}
	if *relayID == "" {
		return &commandapi.Error{Code: "usage", Message: "--relay-id is required", ExitCode: commandapi.ExitUsage}
	}
	gatewaySpec, err := loadPersistedRelaySpec(*configRoot, *relayID)
	if err != nil {
		return commandapi.ValidationError(err)
	}
	result := clockapi.Sync(context.Background(), clockapi.Options{
		Probe: probe.BackendOptions{
			Host: gatewaySpec.Tunnel.BackendHost, Port: gatewaySpec.Tunnel.BackendPort,
			Targets: gatewaySpec.Tunnel.ProbeURLs, Timeout: time.Duration(gatewaySpec.Tunnel.TimeoutSecond) * time.Second,
		},
		Set: setSystemClock, ApplyThreshold: time.Duration(*applyThreshold) * time.Second,
		MaxSkew: time.Duration(*maxSkew) * time.Second,
	})
	if err := commandapi.WriteJSON(os.Stdout, commandapi.Result{
		OK: result.OK, Command: "clock sync", Data: result,
	}); err != nil {
		return err
	}
	if err := clockapi.Failure(result); err != nil {
		return &commandapi.Error{Code: result.ReasonCode, Message: err.Error(), ExitCode: commandapi.ExitProbe}
	}
	return nil
}

func splitNonEmpty(value string) []string {
	values := strings.Split(value, ",")
	result := make([]string, 0, len(values))
	for _, item := range values {
		if trimmed := strings.TrimSpace(item); trimmed != "" {
			result = append(result, trimmed)
		}
	}
	return result
}

type gatewayPaths struct {
	configRoot      string
	accountingDB    string
	connectorConfig string
	openVPNRoot     string
	ipsecChap       string
}

func relayGatewayPaths(configRoot string, relayID string) gatewayPaths {
	gatewayRoot := filepath.Join(configRoot, "relays", relayID, "gateway")
	return gatewayPaths{
		configRoot:      configRoot,
		accountingDB:    filepath.Join(gatewayRoot, "connector", "accounting.db"),
		connectorConfig: filepath.Join(gatewayRoot, "connector", "config.json"),
		openVPNRoot:     filepath.Join(gatewayRoot, "openvpn"),
		ipsecChap:       filepath.Join(gatewayRoot, "ipsec-l2tp", "chap-secrets"),
	}
}

func syncProtocolClients(gatewaySpec spec.GatewaySpec, paths gatewayPaths, db *sql.DB, connectorConfigPath string) (bool, int, error) {
	switch protocolRuntime(gatewaySpec.Gateway.Protocol) {
	case "singbox":
		result, err := clients.Sync(clients.SyncOptions{
			ConfigPath: connectorConfigPath, Database: db, ProtocolID: gatewaySpec.Gateway.Protocol,
			Validate: validateConfigContent,
		})
		return result.Changed, result.ActiveUsers, err
	case "openvpn":
		result, err := openvpnapi.SyncClients(db, gatewaySpec.Gateway.Protocol, gatewaySpec.OpenVPN.Network, paths.openVPNRoot)
		if err != nil {
			return false, 0, err
		}
		exports, err := openvpnapi.SyncExports(db, gatewaySpec.Gateway.Protocol, gatewaySpec.OpenVPN.PublicHost, gatewaySpec.Gateway.PublicPort, paths.openVPNRoot)
		return result.Changed || exports.Changed, result.ActiveUsers, err
	case "ipsec_l2tp":
		result, err := ipsecapi.SyncClients(db, gatewaySpec.Gateway.Protocol, paths.ipsecChap)
		if err != nil {
			return false, 0, err
		}
		activeChanged, err := ipsecapi.SyncActiveChapSecrets(gatewaySpec.RelayID, ipsecapi.ActivationPaths{ConfigRoot: paths.configRoot})
		return result.Changed || activeChanged, result.ActiveUsers, err
	default:
		return false, 0, fmt.Errorf("unsupported client synchronization runtime for protocol %q", gatewaySpec.Gateway.Protocol)
	}
}

func protocolRuntime(protocolID string) string {
	definition, _ := protocol.Lookup(protocolID)
	return definition.Runtime
}

// reloadConnectorRuntime asks the long-running connector-core run daemon for
// a relay to reload its in-memory sing-box instance from the on-disk config.
// clients.Sync only rewrites config.json/the accounting database; without
// this signal the running daemon keeps serving its stale in-memory user
// list until its own accounting cycle detects an enable/disable change or
// the service is restarted, so newly added clients are rejected as unknown.
// If the connector service isn't active there is nothing to reload — it
// will pick up the new config on its next start.
func reloadConnectorRuntime(ctx context.Context, relayID string) (bool, error) {
	systemd := host.OSSystemd{}
	unit := "omnirelay-connector-" + relayID + ".service"
	state, err := systemd.IsActive(ctx, unit)
	if err != nil || state != "active" {
		return false, nil
	}
	if err := systemd.Kill(ctx, "HUP", unit); err != nil {
		return false, err
	}
	return true, nil
}

func loadPersistedRelaySpec(configRoot string, relayID string) (spec.GatewaySpec, error) {
	specPath := filepath.Join(configRoot, "relays", relayID, "gateway", "spec.json")
	gatewaySpec, err := spec.LoadFile(specPath)
	if err != nil {
		return spec.GatewaySpec{}, err
	}
	if gatewaySpec.RelayID != relayID {
		return spec.GatewaySpec{}, fmt.Errorf("relay-id does not match persisted gateway specification")
	}
	return gatewaySpec, nil
}

func valueOrDefault(value string, fallback string) string {
	if value != "" {
		return value
	}
	return fallback
}

func modernUsage() {
	fmt.Fprintln(os.Stderr, "  connector-core version")
	fmt.Fprintln(os.Stderr, "  connector-core gateway validate --spec <path> [--json]")
	fmt.Fprintln(os.Stderr, "  connector-core gateway plan --spec <path> --json")
	fmt.Fprintln(os.Stderr, "  connector-core gateway apply --spec <path> --json")
	fmt.Fprintln(os.Stderr, "  connector-core gateway migrate --spec <path> --json")
	fmt.Fprintln(os.Stderr, "  connector-core gateway finalize-migration --relay-id <id> --json")
	fmt.Fprintln(os.Stderr, "  connector-core gateway rollback-migration --relay-id <id> --json")
	fmt.Fprintln(os.Stderr, "  connector-core gateway start --relay-id <id> --json")
	fmt.Fprintln(os.Stderr, "  connector-core gateway stop --relay-id <id> --json")
	fmt.Fprintln(os.Stderr, "  connector-core gateway uninstall --relay-id <id> --json")
	fmt.Fprintln(os.Stderr, "  connector-core gateway repair --relay-id <id> --level <safe|full> --json")
	fmt.Fprintln(os.Stderr, "  connector-core gateway status --relay-id <id> --json")
	fmt.Fprintln(os.Stderr, "  connector-core gateway health --relay-id <id> --json")
	fmt.Fprintln(os.Stderr, "  connector-core accounting migrate --relay-id <id> --json")
	fmt.Fprintln(os.Stderr, "  connector-core accounting sync --relay-id <id> --json")
	fmt.Fprintln(os.Stderr, "  connector-core clients sync --relay-id <id> --json")
	fmt.Fprintln(os.Stderr, "  connector-core probe backend --relay-id <id> --json")
	fmt.Fprintln(os.Stderr, "  connector-core probe egress --relay-id <id> --json")
	fmt.Fprintln(os.Stderr, "  connector-core clock sync --relay-id <id> --json")
	fmt.Fprintln(os.Stderr, "  connector-core openvpn authenticate --relay-id <id> <credentials-file>")
	fmt.Fprintln(os.Stderr, "  connector-core openvpn enforce --relay-id <id> --json")
	fmt.Fprintln(os.Stderr, "  connector-core dns apply --relay-id <id> --json")
	fmt.Fprintln(os.Stderr, "  connector-core dns status --relay-id <id> --json")
	fmt.Fprintln(os.Stderr, "  connector-core firewall apply --relay-id <id> --json")
	fmt.Fprintln(os.Stderr, "  connector-core firewall cleanup --relay-id <id> --json")
}

func exitCodeForError(err error) int {
	if typed, ok := err.(*commandapi.Error); ok && typed.ExitCode > 0 {
		return typed.ExitCode
	}
	return 1
}
