package reconcile

import (
	"encoding/json"
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"sort"
	"strings"
	"time"

	dnsmasqconfig "github.com/omnirelay/connector-core/internal/dnsmasq"
	"github.com/omnirelay/connector-core/internal/host"
	ipsecconfig "github.com/omnirelay/connector-core/internal/ipsec"
	openvpnconfig "github.com/omnirelay/connector-core/internal/openvpn"
	panelconfig "github.com/omnirelay/connector-core/internal/panel"
	"github.com/omnirelay/connector-core/internal/protocol"
	singboxconfig "github.com/omnirelay/connector-core/internal/singbox"
	"github.com/omnirelay/connector-core/internal/spec"
	"github.com/omnirelay/connector-core/internal/systemd"
)

type ApplyOptions struct {
	ConfigRoot        string
	TransactionRoot   string
	SystemdRoot       string
	NginxRoot         string
	DNSMasqRoot       string
	PPPHookRoot       string
	SudoersRoot       string
	Now               func() time.Time
	ValidatePersisted func(path string) error
	AfterWrite        func() error
	AfterRollback     func() error
}

type ApplyResult struct {
	RelayID       string   `json:"relayId"`
	TransactionID string   `json:"transactionId"`
	Changed       bool     `json:"changed"`
	SpecPath      string   `json:"specPath"`
	ChangedFiles  []string `json:"changedFiles,omitempty"`
	DeletedFiles  []string `json:"deletedFiles,omitempty"`
}

type transactionJournal struct {
	ID             string            `json:"id"`
	RelayID        string            `json:"relayId"`
	StartedAtUTC   time.Time         `json:"startedAtUtc"`
	CompletedAtUTC time.Time         `json:"completedAtUtc,omitempty"`
	State          string            `json:"state"`
	Changed        bool              `json:"changed"`
	SpecPath       string            `json:"specPath"`
	Backups        map[string]string `json:"backups,omitempty"`
	CreatedPaths   []string          `json:"createdPaths,omitempty"`
	DeletedPaths   []string          `json:"deletedPaths,omitempty"`
	Error          string            `json:"error,omitempty"`
}

type managedManifest struct {
	APIVersion string   `json:"apiVersion"`
	RelayID    string   `json:"relayId"`
	Paths      []string `json:"paths"`
}

type managedFile struct {
	Path          string
	Content       []byte
	Mode          os.FileMode
	PanelReadable bool
}

func Apply(gatewaySpec spec.GatewaySpec, options ApplyOptions) (ApplyResult, error) {
	gatewaySpec.ApplyDefaults()
	switch gatewaySpec.Gateway.Protocol {
	case "shadowsocks_singbox", "shadowtls_v3_shadowsocks_singbox":
		if !singboxconfig.IsValidShadowsocks2022PSK(gatewaySpec.SingBox.ShadowsocksServerPassword) {
			generated, err := singboxconfig.RandomShadowsocks2022PSK()
			if err != nil {
				return ApplyResult{}, err
			}
			gatewaySpec.SingBox.ShadowsocksServerPassword = generated
		}
	}
	if err := gatewaySpec.Validate(); err != nil {
		return ApplyResult{}, err
	}
	options = defaultApplyOptions(options)
	now := options.Now().UTC()
	transactionID := fmt.Sprintf("%s-%s", now.Format("20060102T150405.000000000Z"), gatewaySpec.RelayID)
	relayRoot := filepath.Join(options.ConfigRoot, "relays", gatewaySpec.RelayID, "gateway")
	specPath := filepath.Join(relayRoot, "spec.json")
	lockPath := filepath.Join(options.TransactionRoot, "locks", gatewaySpec.RelayID+".lock")
	transactionDir := filepath.Join(options.TransactionRoot, transactionID)
	journalPath := filepath.Join(transactionDir, "journal.json")

	release, err := acquireLock(lockPath)
	if err != nil {
		return ApplyResult{}, err
	}
	defer release()

	if err := os.MkdirAll(transactionDir, 0o700); err != nil {
		return ApplyResult{}, err
	}
	journal := transactionJournal{
		ID: transactionID, RelayID: gatewaySpec.RelayID, StartedAtUTC: now,
		State: "applying", SpecPath: specPath, Backups: make(map[string]string),
	}
	files, err := renderManagedFiles(gatewaySpec, options, specPath)
	if err != nil {
		return ApplyResult{}, err
	}
	previousPaths, err := loadManagedManifest(filepath.Join(relayRoot, "managed-files.json"), gatewaySpec.RelayID, options)
	if err != nil {
		return ApplyResult{}, err
	}
	desiredPaths := make(map[string]struct{}, len(files))
	for _, file := range files {
		desiredPaths[file.Path] = struct{}{}
	}
	stalePaths := make([]string, 0)
	for _, path := range previousPaths {
		if _, desired := desiredPaths[path]; !desired {
			stalePaths = append(stalePaths, path)
		}
	}
	sort.Strings(stalePaths)
	for index, file := range files {
		if err := snapshotManagedFile(file.Path, filepath.Join(transactionDir, "backups", fmt.Sprintf("%03d", index)), &journal); err != nil {
			return ApplyResult{}, err
		}
	}
	for index, path := range stalePaths {
		if err := snapshotManagedFile(path, filepath.Join(transactionDir, "backups", fmt.Sprintf("%03d", len(files)+index)), &journal); err != nil {
			return ApplyResult{}, err
		}
	}
	if err := writeJournal(journalPath, journal); err != nil {
		return ApplyResult{}, err
	}

	changedFiles := make([]string, 0, len(files))
	for _, file := range files {
		changed, writeErr := host.WriteFileAtomic(file.Path, file.Content, file.Mode)
		if writeErr != nil {
			return ApplyResult{}, failAndRollback(journalPath, &journal, writeErr, options.AfterRollback)
		}
		if file.PanelReadable {
			if err := repairPanelReadableFile(file.Path, file.Mode); err != nil {
				return ApplyResult{}, failAndRollback(journalPath, &journal, err, options.AfterRollback)
			}
		}
		if changed {
			changedFiles = append(changedFiles, file.Path)
		}
	}
	deletedFiles := make([]string, 0, len(stalePaths))
	for _, path := range stalePaths {
		if err := os.Remove(path); err != nil {
			if errors.Is(err, os.ErrNotExist) {
				continue
			}
			return ApplyResult{}, failAndRollback(journalPath, &journal, err, options.AfterRollback)
		}
		deletedFiles = append(deletedFiles, path)
		journal.DeletedPaths = append(journal.DeletedPaths, path)
	}
	if options.ValidatePersisted != nil {
		if err := options.ValidatePersisted(specPath); err != nil {
			return ApplyResult{}, failAndRollback(journalPath, &journal, err, options.AfterRollback)
		}
	}
	if options.AfterWrite != nil {
		if err := options.AfterWrite(); err != nil {
			return ApplyResult{}, failAndRollback(journalPath, &journal, err, options.AfterRollback)
		}
	}

	journal.State = "completed"
	journal.Changed = len(changedFiles) > 0 || len(deletedFiles) > 0
	journal.CompletedAtUTC = options.Now().UTC()
	if err := writeJournal(journalPath, journal); err != nil {
		return ApplyResult{}, err
	}
	return ApplyResult{
		RelayID: gatewaySpec.RelayID, TransactionID: transactionID,
		Changed: len(changedFiles) > 0 || len(deletedFiles) > 0, SpecPath: specPath,
		ChangedFiles: changedFiles, DeletedFiles: deletedFiles,
	}, nil
}

func defaultApplyOptions(options ApplyOptions) ApplyOptions {
	if options.ConfigRoot == "" {
		options.ConfigRoot = "/etc/omnirelay"
	}
	if options.TransactionRoot == "" {
		options.TransactionRoot = "/var/lib/omnirelay/transactions"
	}
	if options.SystemdRoot == "" {
		options.SystemdRoot = "/etc/systemd/system"
	}
	if options.NginxRoot == "" {
		options.NginxRoot = "/etc/nginx/sites-available"
	}
	if options.DNSMasqRoot == "" {
		options.DNSMasqRoot = "/etc/dnsmasq.d"
	}
	if options.PPPHookRoot == "" {
		options.PPPHookRoot = "/etc/ppp"
	}
	if options.SudoersRoot == "" {
		options.SudoersRoot = "/etc/sudoers.d"
	}
	if options.Now == nil {
		options.Now = time.Now
	}
	return options
}

func acquireLock(path string) (func(), error) {
	if err := os.MkdirAll(filepath.Dir(path), 0o700); err != nil {
		return nil, err
	}
	file, err := os.OpenFile(path, os.O_CREATE|os.O_EXCL|os.O_WRONLY, 0o600)
	if err != nil {
		if errors.Is(err, os.ErrExist) {
			return nil, fmt.Errorf("another gateway operation is active for this relay")
		}
		return nil, err
	}
	_ = file.Close()
	return func() { _ = os.Remove(path) }, nil
}

func writeJournal(path string, journal transactionJournal) error {
	content, err := json.MarshalIndent(journal, "", "  ")
	if err != nil {
		return err
	}
	_, err = host.WriteFileAtomic(path, append(content, '\n'), 0o600)
	return err
}

func failAndRollback(journalPath string, journal *transactionJournal, applyErr error, afterRollback func() error) error {
	journal.State = "failed"
	journal.Error = applyErr.Error()
	rollbackFailed := false
	for target, backupPath := range journal.Backups {
		backup, err := os.ReadFile(backupPath)
		if err != nil {
			journal.Error += "; rollback read failed: " + err.Error()
			rollbackFailed = true
			continue
		}
		info, err := os.Stat(backupPath)
		if err != nil {
			journal.Error += "; rollback stat failed: " + err.Error()
			rollbackFailed = true
			continue
		}
		if _, err = host.WriteFileAtomic(target, backup, info.Mode().Perm()); err != nil {
			journal.Error += "; rollback write failed: " + err.Error()
			rollbackFailed = true
		}
	}
	for _, target := range journal.CreatedPaths {
		if err := os.Remove(target); err != nil && !errors.Is(err, os.ErrNotExist) {
			journal.Error += "; rollback remove failed: " + err.Error()
			rollbackFailed = true
		}
	}
	if afterRollback != nil {
		if err := afterRollback(); err != nil {
			journal.Error += "; rollback post-action failed: " + err.Error()
			rollbackFailed = true
		}
	}
	if !rollbackFailed {
		journal.State = "rolled_back"
	}
	_ = writeJournal(journalPath, *journal)
	return applyErr
}

func renderManagedFiles(gatewaySpec spec.GatewaySpec, options ApplyOptions, specPath string) ([]managedFile, error) {
	files := make([]managedFile, 0)
	definition, ok := protocol.Lookup(gatewaySpec.Gateway.Protocol)
	if !ok {
		return nil, fmt.Errorf("unsupported gateway protocol %q", gatewaySpec.Gateway.Protocol)
	}
	if definition.Runtime == "singbox" {
		config, err := singboxconfig.Render(singboxconfig.FromGatewaySpec(gatewaySpec, nil))
		if err != nil {
			return nil, err
		}
		if err := singboxconfig.Validate(config); err != nil {
			return nil, fmt.Errorf("validate rendered sing-box config: %w", err)
		}
		gatewayRoot := filepath.Dir(specPath)
		files = append(files,
			managedFile{Path: filepath.Join(gatewayRoot, "connector", "config.json"), Content: config, Mode: 0o600},
			managedFile{Path: filepath.Join(gatewayRoot, "metadata.json"), Content: renderMetadata(gatewaySpec.Gateway.Protocol, definition.AccountingSource), Mode: 0o600},
		)
	}
	if definition.Runtime == "openvpn" || definition.Runtime == "ipsec_l2tp" {
		config, err := singboxconfig.RenderInternalTunnel(
			gatewaySpec.Tunnel.BackendPort,
			singboxconfig.InternalRedirectPort(gatewaySpec.RelayID),
			singboxconfig.DNS{
				DoHEndpoints: gatewaySpec.DNS.DoHEndpoints, ListenAddress: gatewaySpec.DNS.ListenAddress, ListenPort: gatewaySpec.DNS.ListenPort,
			},
		)
		if err != nil {
			return nil, err
		}
		if err := singboxconfig.Validate(config); err != nil {
			return nil, fmt.Errorf("validate rendered internal tunnel config: %w", err)
		}
		gatewayRoot := filepath.Dir(specPath)
		files = append(files,
			managedFile{Path: filepath.Join(gatewayRoot, "connector", "config.json"), Content: config, Mode: 0o600},
			managedFile{Path: filepath.Join(gatewayRoot, "metadata.json"), Content: renderMetadata(gatewaySpec.Gateway.Protocol, definition.AccountingSource), Mode: 0o600},
		)
	}
	if definition.Runtime == "openvpn" {
		openVPNFiles, err := renderOpenVPNFiles(gatewaySpec, options, filepath.Dir(specPath))
		if err != nil {
			return nil, err
		}
		files = append(files, openVPNFiles...)
	}
	if definition.Runtime == "ipsec_l2tp" {
		ipsecFiles, err := renderIPSecFiles(gatewaySpec, options, filepath.Dir(specPath))
		if err != nil {
			return nil, err
		}
		files = append(files, ipsecFiles...)
	}
	if gatewaySpec.Panel.Port > 0 {
		panelFiles, err := renderPanelFiles(gatewaySpec, options, filepath.Dir(specPath))
		if err != nil {
			return nil, err
		}
		files = append(files, panelFiles...)
	}
	for _, unit := range systemd.RenderGatewayUnits(gatewaySpec, systemd.Options{ConfigRoot: options.ConfigRoot}) {
		files = append(files, managedFile{
			Path: filepath.Join(options.SystemdRoot, unit.Name), Content: []byte(unit.Content), Mode: 0o644,
		})
	}
	persistedSpec := gatewaySpec
	if persistedSpec.Panel.Port > 0 && persistedSpec.Panel.TLSEnabled {
		persistedSpec.Panel.CertFile, persistedSpec.Panel.KeyFile = panelconfig.EffectiveTLSPaths(
			persistedSpec,
			filepath.Join(filepath.Dir(specPath), "panel"),
		)
	}
	content, err := persistedSpec.CanonicalJSON()
	if err != nil {
		return nil, err
	}
	files = append([]managedFile{{Path: specPath, Content: append(content, '\n'), Mode: 0o600}}, files...)
	manifestPath := filepath.Join(filepath.Dir(specPath), "managed-files.json")
	paths := make([]string, 0, len(files)+1)
	for _, file := range files {
		paths = append(paths, file.Path)
	}
	paths = append(paths, manifestPath)
	sort.Strings(paths)
	manifest, err := json.MarshalIndent(managedManifest{APIVersion: "omnirelay.io/managed-files/v1", RelayID: gatewaySpec.RelayID, Paths: paths}, "", "  ")
	if err != nil {
		return nil, err
	}
	files = append(files, managedFile{Path: manifestPath, Content: append(manifest, '\n'), Mode: 0o600})
	return files, nil
}

func loadManagedManifest(path string, relayID string, options ApplyOptions) ([]string, error) {
	content, err := os.ReadFile(path)
	if errors.Is(err, os.ErrNotExist) {
		return nil, nil
	}
	if err != nil {
		return nil, err
	}
	var manifest managedManifest
	if err := json.Unmarshal(content, &manifest); err != nil {
		return nil, fmt.Errorf("invalid managed-file manifest: %w", err)
	}
	if manifest.APIVersion != "omnirelay.io/managed-files/v1" || manifest.RelayID != relayID {
		return nil, fmt.Errorf("managed-file manifest identity is invalid")
	}
	result := make([]string, 0, len(manifest.Paths))
	seen := make(map[string]struct{}, len(manifest.Paths))
	for _, path := range manifest.Paths {
		path = filepath.Clean(path)
		if !managedPathAllowed(path, options) {
			return nil, fmt.Errorf("managed-file manifest contains unsafe path %q", path)
		}
		if _, exists := seen[path]; exists {
			continue
		}
		seen[path] = struct{}{}
		result = append(result, path)
	}
	return result, nil
}

func managedPathAllowed(path string, options ApplyOptions) bool {
	for _, root := range []string{options.ConfigRoot, options.SystemdRoot, options.NginxRoot, options.DNSMasqRoot, options.PPPHookRoot, options.SudoersRoot} {
		relative, err := filepath.Rel(filepath.Clean(root), path)
		if err == nil && relative != ".." && !strings.HasPrefix(relative, ".."+string(filepath.Separator)) {
			return true
		}
	}
	return false
}

func renderPanelFiles(gatewaySpec spec.GatewaySpec, options ApplyOptions, gatewayRoot string) ([]managedFile, error) {
	environment, err := panelconfig.RenderEnvironment(gatewaySpec, panelconfig.Options{})
	if err != nil {
		return nil, err
	}
	panelRoot := filepath.Join(gatewayRoot, "panel")
	effectiveSpec := gatewaySpec
	files := []managedFile{{Path: filepath.Join(panelRoot, "panel.env"), Content: environment, Mode: 0o600}}
	files = append(files, managedFile{
		Path:    filepath.Join(options.SudoersRoot, "omnirelay-connector-core-"+gatewaySpec.RelayID),
		Content: panelconfig.RenderSudoers(gatewaySpec), Mode: 0o440,
	})
	if gatewaySpec.Panel.TLSEnabled {
		effectiveSpec.Panel.CertFile, effectiveSpec.Panel.KeyFile = panelconfig.EffectiveTLSPaths(gatewaySpec, panelRoot)
		if gatewaySpec.Panel.TLSMode == "uploaded" {
			assets, available, err := panelconfig.LoadUploadedTLSAssets(gatewaySpec.Panel.CertFile, gatewaySpec.Panel.KeyFile, panelRoot)
			if err != nil {
				return nil, err
			}
			if !available {
				return nil, fmt.Errorf("uploaded panel TLS assets are unavailable")
			}
			files = append(files,
				managedFile{Path: effectiveSpec.Panel.CertFile, Content: assets.Certificate, Mode: 0o644},
				managedFile{Path: effectiveSpec.Panel.KeyFile, Content: assets.PrivateKey, Mode: 0o600},
			)
		}
	}
	nginx, err := panelconfig.RenderNginx(effectiveSpec, panelconfig.Options{})
	if err != nil {
		return nil, err
	}
	files = append(files, managedFile{Path: filepath.Join(options.NginxRoot, "omnirelay-omnipanel-"+gatewaySpec.RelayID+".conf"), Content: nginx, Mode: 0o644})
	return files, nil
}

func renderIPSecFiles(gatewaySpec spec.GatewaySpec, options ApplyOptions, gatewayRoot string) ([]managedFile, error) {
	ipsecRoot := filepath.Join(gatewayRoot, "ipsec-l2tp")
	ipsecConfig, err := ipsecconfig.RenderIPSecConfig(ipsecconfig.ConnectionName(gatewaySpec.RelayID))
	if err != nil {
		return nil, err
	}
	secrets, err := ipsecconfig.RenderSecrets(gatewaySpec.IPSecL2TP.PreSharedKey)
	if err != nil {
		return nil, err
	}
	pppOptionsPath := filepath.Join(ipsecRoot, "ppp-options")
	xl2tpdConfig, err := ipsecconfig.RenderXL2TPD(gatewaySpec.IPSecL2TP.Network, ipsecconfig.Paths{PPPOptions: pppOptionsPath})
	if err != nil {
		return nil, err
	}
	localIP, _, _, err := ipsecconfig.NetworkAddresses(gatewaySpec.IPSecL2TP.Network)
	if err != nil {
		return nil, err
	}
	dnsmasq, err := dnsmasqconfig.Render("ppp+", localIP.String(), gatewaySpec.DNS.ListenAddress, gatewaySpec.DNS.ListenPort)
	if err != nil {
		return nil, err
	}
	runtime, err := json.MarshalIndent(map[string]any{
		"connectorRedirectPort": singboxconfig.InternalRedirectPort(gatewaySpec.RelayID),
		"ipsecL2tpNetwork":      gatewaySpec.IPSecL2TP.Network,
		"localAddress":          localIP.String(),
		"connectionName":        ipsecconfig.ConnectionName(gatewaySpec.RelayID),
	}, "", "  ")
	if err != nil {
		return nil, err
	}
	upHook, err := ipsecconfig.RenderSessionHook(gatewaySpec.RelayID, "up", "/usr/local/bin/connector-core")
	if err != nil {
		return nil, err
	}
	downHook, err := ipsecconfig.RenderSessionHook(gatewaySpec.RelayID, "down", "/usr/local/bin/connector-core")
	if err != nil {
		return nil, err
	}
	return []managedFile{
		{Path: filepath.Join(ipsecRoot, "ipsec.conf"), Content: ipsecConfig, Mode: 0o644},
		{Path: filepath.Join(ipsecRoot, "ipsec.secrets"), Content: secrets, Mode: 0o640, PanelReadable: true},
		{Path: filepath.Join(ipsecRoot, "xl2tpd.conf"), Content: xl2tpdConfig, Mode: 0o644},
		{Path: pppOptionsPath, Content: ipsecconfig.RenderPPPOptions(localIP.String()), Mode: 0o644},
		{Path: filepath.Join(ipsecRoot, "runtime.json"), Content: append(runtime, '\n'), Mode: 0o640, PanelReadable: true},
		{Path: filepath.Join(options.DNSMasqRoot, "omnirelay-ipsec-l2tp-"+gatewaySpec.RelayID+".conf"), Content: dnsmasq, Mode: 0o644},
		{Path: filepath.Join(options.PPPHookRoot, "ip-up.d", "99-omnirelay-"+gatewaySpec.RelayID+"-accounting"), Content: upHook, Mode: 0o755},
		{Path: filepath.Join(options.PPPHookRoot, "ip-down.d", "99-omnirelay-"+gatewaySpec.RelayID+"-accounting"), Content: downHook, Mode: 0o755},
	}, nil
}

func renderOpenVPNFiles(gatewaySpec spec.GatewaySpec, options ApplyOptions, gatewayRoot string) ([]managedFile, error) {
	openVPNRoot := filepath.Join(gatewayRoot, "openvpn")
	relayID := gatewaySpec.RelayID
	assets, assetsAvailable, err := openvpnconfig.LoadSharedAssets(openvpnconfig.SharedAssetSources{
		CACert: gatewaySpec.OpenVPN.SharedCACertFile, ClientCert: gatewaySpec.OpenVPN.SharedClientCertFile,
		ClientKey: gatewaySpec.OpenVPN.SharedClientKeyFile, TLSCryptKey: gatewaySpec.OpenVPN.SharedTLSCryptKeyFile,
	}, openVPNRoot)
	if err != nil {
		return nil, err
	}
	serverAddress, err := openvpnconfig.ServerAddress(gatewaySpec.OpenVPN.Network)
	if err != nil {
		return nil, err
	}
	serverConfig, err := openvpnconfig.RenderServerConfig(openvpnconfig.ServerOptions{
		RelayID: relayID, PublicPort: gatewaySpec.Gateway.PublicPort, Network: gatewaySpec.OpenVPN.Network,
		Interface: openvpnconfig.InterfaceName(relayID),
		CAFile:    filepath.Join(openVPNRoot, "ca.crt"), CertFile: filepath.Join(openVPNRoot, "client-shared.crt"),
		KeyFile:           filepath.Join(openVPNRoot, "client-shared.key"),
		TLSCryptFile:      filepath.Join(openVPNRoot, "ta.key"),
		AuthVerifyCommand: fmt.Sprintf("/usr/local/bin/connector-core openvpn authenticate --relay-id %s", relayID),
		CCDDir:            filepath.Join(openVPNRoot, "ccd"), PoolFile: filepath.Join(openVPNRoot, "ipp.txt"),
		StatusFile: filepath.Join("/var/log/openvpn", "omnirelay-status-"+relayID+".log"),
		ClientDNS:  serverAddress.String(),
	})
	if err != nil {
		return nil, err
	}
	runtime, err := json.MarshalIndent(map[string]any{
		"connectorRedirectPort": singboxconfig.InternalRedirectPort(relayID),
		"openVpnNetwork":        gatewaySpec.OpenVPN.Network,
		"interface":             openvpnconfig.InterfaceName(relayID),
		"managementPort":        openvpnconfig.ManagementPort(relayID),
	}, "", "  ")
	if err != nil {
		return nil, err
	}
	dnsmasq, err := dnsmasqconfig.Render(openvpnconfig.InterfaceName(relayID), serverAddress.String(), gatewaySpec.DNS.ListenAddress, gatewaySpec.DNS.ListenPort)
	if err != nil {
		return nil, err
	}
	files := []managedFile{
		{Path: filepath.Join(openVPNRoot, "server.conf"), Content: serverConfig, Mode: 0o600},
		{Path: filepath.Join(openVPNRoot, "runtime.json"), Content: append(runtime, '\n'), Mode: 0o640, PanelReadable: true},
		{Path: filepath.Join(options.DNSMasqRoot, "omnirelay-openvpn-"+relayID+".conf"), Content: dnsmasq, Mode: 0o644},
	}
	if assetsAvailable {
		files = append(files,
			managedFile{Path: filepath.Join(openVPNRoot, "ca.crt"), Content: assets.CACert, Mode: 0o644},
			managedFile{Path: filepath.Join(openVPNRoot, "client-shared.crt"), Content: assets.ClientCert, Mode: 0o644},
			managedFile{Path: filepath.Join(openVPNRoot, "client-shared.key"), Content: assets.ClientKey, Mode: 0o600},
			managedFile{Path: filepath.Join(openVPNRoot, "ta.key"), Content: assets.TLSCryptKey, Mode: 0o600},
		)
	}
	return files, nil
}

func renderMetadata(protocolID string, accountingSource string) []byte {
	content, _ := json.MarshalIndent(map[string]any{
		"active_protocol": protocolID,
		"accounting": map[string]string{
			"source": accountingSource, "clientsFile": "",
		},
	}, "", "  ")
	return append(content, '\n')
}

func snapshotManagedFile(target string, backupPath string, journal *transactionJournal) error {
	current, err := os.ReadFile(target)
	if err != nil {
		if errors.Is(err, os.ErrNotExist) {
			journal.CreatedPaths = append(journal.CreatedPaths, target)
			return nil
		}
		return err
	}
	info, err := os.Stat(target)
	if err != nil {
		return err
	}
	if _, err := host.WriteFileAtomic(backupPath, current, info.Mode().Perm()); err != nil {
		return err
	}
	journal.Backups[target] = backupPath
	return nil
}
