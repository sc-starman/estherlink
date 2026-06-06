using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OmniRelay.Core.Configuration;
using OmniRelay.Core.Networking;
using OmniRelay.Core.Policy;
using OmniRelay.Core.Status;
using OmniRelay.UI.Models;
using OmniRelay.UI.Services;
using System.Text.Json;
using OmniRelay.UI.Views.Dialogs;
using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;

namespace OmniRelay.UI.ViewModels;

public partial class RelaysViewModel : ObservableObject
{
    private static readonly JsonSerializerOptions BackupJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly GatewayOrchestratorService _orchestrator;
    private readonly GatewayStateStore _state;
    private readonly IGatewayDeploymentService _gatewayDeployment;
    private readonly IGatewayHealthService _gatewayHealth;
    private readonly IDeploymentProgressAggregator _progressAggregator;
    private readonly ISudoSessionSecretCache _sudoCache;
    private readonly IServiceControlService _serviceControl;

    public RelaysViewModel(
        GatewayOrchestratorService orchestrator,
        GatewayStateStore state,
        IGatewayDeploymentService gatewayDeployment,
        IGatewayHealthService gatewayHealth,
        IDeploymentProgressAggregator progressAggregator,
        ISudoSessionSecretCache sudoCache,
        IServiceControlService serviceControl)
    {
        _orchestrator = orchestrator;
        _state = state;
        _gatewayDeployment = gatewayDeployment;
        _gatewayHealth = gatewayHealth;
        _progressAggregator = progressAggregator;
        _sudoCache = sudoCache;
        _serviceControl = serviceControl;
        _state.PropertyChanged += OnStateChanged;
        _state.Relays.CollectionChanged += (_, _) => RefreshRows();
        RefreshRows();
    }

    public ObservableCollection<RelayRowViewModel> Relays { get; } = [];
    public ObservableCollection<AdapterChoiceModel> Adapters => _state.Adapters;

    [ObservableProperty]
    private RelayRowViewModel? selectedRelay;

    [ObservableProperty]
    private string feedback = string.Empty;

    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    private bool isRelayServiceRunning;

    private bool CanRun() => !IsBusy;
    private bool HasSelected() => !IsBusy && SelectedRelay is not null;

    partial void OnIsBusyChanged(bool value)
    {
        AddRelayCommand.NotifyCanExecuteChanged();
        EditRelayCommand.NotifyCanExecuteChanged();
        DeleteRelayCommand.NotifyCanExecuteChanged();
        ToggleRelayCommand.NotifyCanExecuteChanged();
        RefreshCommand.NotifyCanExecuteChanged();
        BackupRelaysCommand.NotifyCanExecuteChanged();
        RestoreRelaysCommand.NotifyCanExecuteChanged();
        InstallStartRelayServiceCommand.NotifyCanExecuteChanged();
        StopRelayServiceCommand.NotifyCanExecuteChanged();
        UninstallRelayServiceCommand.NotifyCanExecuteChanged();
        ToggleRelayServiceCommand.NotifyCanExecuteChanged();
        ReinstallStartRelayServiceCommand.NotifyCanExecuteChanged();
    }

    partial void OnSelectedRelayChanged(RelayRowViewModel? value)
    {
        EditRelayCommand.NotifyCanExecuteChanged();
        DeleteRelayCommand.NotifyCanExecuteChanged();
        ToggleRelayCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RefreshAsync()
    {
        await RunBusyAsync(async () =>
        {
            RefreshAdapterCatalog();
            var result = await _orchestrator.LoadRelaysAsync();
            await _orchestrator.RefreshStatusAsync();
            Feedback = result.Message;
            RefreshRows();
        });
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task InstallStartRelayServiceAsync()
    {
        await RunBusyAsync(async () =>
        {
            var result = await _orchestrator.InstallStartServiceAsync();
            Feedback = result.Message;
            await _orchestrator.LoadRelaysAsync();
            await _orchestrator.RefreshStatusAsync();
            RefreshRows();
        });
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task StopRelayServiceAsync()
    {
        await RunBusyAsync(async () =>
        {
            var result = await _orchestrator.StopServiceAsync();
            Feedback = result.Message;
            await _orchestrator.RefreshStatusAsync();
            RefreshRows();
        });
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task UninstallRelayServiceAsync()
    {
        await RunBusyAsync(async () =>
        {
            var stopped = await _orchestrator.StopServiceAsync();
            var uninstalled = await _serviceControl.UninstallWindowsServiceAsync();
            Feedback = uninstalled
                ? "Relay service uninstall requested."
                : $"Relay service uninstall failed: {stopped.Message}";

            await _orchestrator.RefreshStatusAsync();
            RefreshRows();
        });
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task ToggleRelayServiceAsync()
    {
        await RunBusyAsync(async () =>
        {
            OperationResult result;
            if (IsRelayServiceRunning)
            {
                result = await _orchestrator.StopServiceAsync();
            }
            else
            {
                var installed = await _serviceControl.InstallOrStartWindowsServiceAsync();
                if (!installed)
                {
                    var state = await _serviceControl.QueryServiceStateAsync();
                    result = new OperationResult(false, $"Service install/start canceled or failed. Service state: {state}.");
                }
                else
                {
                    var startProxy = await _orchestrator.StartProxyAsync();
                    result = new OperationResult(startProxy.Success, startProxy.Message);
                }
            }

            Feedback = result.Message;
            await _orchestrator.LoadRelaysAsync();
            await _orchestrator.RefreshStatusAsync();
            RefreshRows();
        });
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task ReinstallStartRelayServiceAsync()
    {
        await RunBusyAsync(async () =>
        {
            var relayBackup = _state.Relays
                .Where(r => r is not null)
                .Select(Clone)
                .ToList();

            await _orchestrator.StopServiceAsync();
            await _serviceControl.UninstallWindowsServiceAsync();
            var installed = await _serviceControl.InstallOrStartWindowsServiceAsync();
            if (!installed)
            {
                var state = await _serviceControl.QueryServiceStateAsync();
                Feedback = $"Service reinstall/start canceled or failed. Service state: {state}.";
            }
            else
            {
                var startProxy = await _orchestrator.StartProxyAsync();
                Feedback = startProxy.Message;
            }

            await _orchestrator.LoadRelaysAsync();
            if (_state.Relays.Count == 0 && relayBackup.Count > 0)
            {
                var restored = 0;
                foreach (var relay in relayBackup)
                {
                    var restore = await _orchestrator.UpsertRelayAsync(relay);
                    if (restore.Success)
                    {
                        restored++;
                    }
                }

                Feedback = $"{Feedback} Restored {restored}/{relayBackup.Count} relay entries.";
            }

            await _orchestrator.RefreshStatusAsync();
            RefreshRows();
        });
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private Task AddRelayAsync()
    {
        var typeDialog = new RelayTypeDialog();
        if (typeDialog.ShowDialog() != true)
        {
            return Task.CompletedTask;
        }

        RefreshAdapterCatalog();
        var relay = CreateDefaultRelay(typeDialog.SelectedGatewayType);
        var editDialog = new RelayEditDialog(relay, Adapters, null, isRelaySaved: false);
        WireRelayDialog(editDialog);
        if (editDialog.ShowDialog() != true)
        {
            return Task.CompletedTask;
        }

        return Task.CompletedTask;
    }

    [RelayCommand(CanExecute = nameof(HasSelected))]
    private async Task EditRelayAsync()
    {
        if (SelectedRelay is null)
        {
            return;
        }

        var loaded = await _orchestrator.GetRelayAsync(SelectedRelay.RelayId);
        if (!loaded.Success || loaded.Relay is null)
        {
            Feedback = loaded.Message;
            return;
        }

        var source = loaded.Relay;
        var status = _state.AppStatus?.Relays.FirstOrDefault(x => string.Equals(x.RelayId, source.Id, StringComparison.Ordinal));
        RefreshAdapterCatalog();
        var editDialog = new RelayEditDialog(Clone(source), Adapters, status, isRelaySaved: true);
        WireRelayDialog(editDialog);
        if (editDialog.ShowDialog() != true)
        {
            return;
        }
    }

    [RelayCommand(CanExecute = nameof(HasSelected))]
    private async Task DeleteRelayAsync()
    {
        if (SelectedRelay is null)
        {
            return;
        }

        var relayId = SelectedRelay.RelayId;
        await RunBusyAsync(async () =>
        {
            var loaded = await _orchestrator.GetRelayAsync(relayId);
            if (!loaded.Success || loaded.Relay is null)
            {
                Feedback = loaded.Message;
                return;
            }

            var relay = loaded.Relay;
            if (string.Equals(GatewayTypes.Normalize(relay.GatewayType), GatewayTypes.Remote, StringComparison.OrdinalIgnoreCase))
            {
                var choice = MessageBox.Show(
                    "Uninstall this relay's remote gateway from the VPS before deleting the local relay record?\n\nChoose Yes to uninstall from the VPS first. Choose No to delete only the local relay record.",
                    "Delete Remote Relay",
                    MessageBoxButton.YesNoCancel,
                    MessageBoxImage.Warning);

                if (choice == MessageBoxResult.Cancel)
                {
                    Feedback = "Relay delete canceled.";
                    return;
                }

                if (choice == MessageBoxResult.Yes)
                {
                    var uninstall = await RunRelayGatewayOperationFromDialogAsync(relay, "uninstall");
                    if (!uninstall.Success)
                    {
                        Feedback = $"Remote gateway uninstall failed; local relay was not deleted. {uninstall.Message}";
                        return;
                    }
                }
            }

            var result = await _orchestrator.DeleteRelayAsync(relayId);
            Feedback = result.Message;
            RefreshRows();
        });
    }

    [RelayCommand(CanExecute = nameof(HasSelected))]
    private async Task ToggleRelayAsync()
    {
        if (SelectedRelay is null)
        {
            return;
        }

        await RunBusyAsync(async () =>
        {
            var result = await _orchestrator.SetRelayEnabledAsync(SelectedRelay.RelayId, !SelectedRelay.Enabled);
            Feedback = result.Message;
            RefreshRows();
        });
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task BackupRelaysAsync()
    {
        await RunBusyAsync(async () =>
        {
            await _orchestrator.LoadRelaysAsync();
            var snapshot = _state.Relays
                .Where(r => r is not null)
                .Select(Clone)
                .ToList();

            var backupRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "OmniRelay",
                "backups",
                "relays");
            Directory.CreateDirectory(backupRoot);

            var saveDialog = new SaveFileDialog
            {
                Title = "Save Relay Backup",
                Filter = "Relay Backup (*.json)|*.json|All Files (*.*)|*.*",
                InitialDirectory = backupRoot,
                FileName = $"relays-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.json",
                AddExtension = true,
                DefaultExt = ".json",
                OverwritePrompt = true
            };
            if (saveDialog.ShowDialog() != true || string.IsNullOrWhiteSpace(saveDialog.FileName))
            {
                Feedback = "Relay backup canceled.";
                return;
            }

            var backupPath = saveDialog.FileName;
            File.WriteAllText(backupPath, JsonSerializer.Serialize(snapshot, BackupJsonOptions));
            Feedback = $"Relay backup created: {backupPath}";
        });
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RestoreRelaysAsync()
    {
        await RunBusyAsync(async () =>
        {
            await _orchestrator.LoadRelaysAsync();
            if (_state.Relays.Count > 0)
            {
                var prompt = MessageBox.Show(
                    "Current relay list is not empty. Restore will upsert relays from backup and may overwrite existing entries with the same relay IDs.\n\nContinue?",
                    "Restore Relay Backup",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning,
                    MessageBoxResult.No);
                if (prompt != MessageBoxResult.Yes)
                {
                    Feedback = "Relay restore canceled.";
                    return;
                }
            }

            var backupRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "OmniRelay",
                "backups",
                "relays");
            var openDialog = new OpenFileDialog
            {
                Title = "Open Relay Backup",
                Filter = "Relay Backup (*.json)|*.json|All Files (*.*)|*.*",
                InitialDirectory = Directory.Exists(backupRoot) ? backupRoot : Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                CheckFileExists = true,
                Multiselect = false
            };
            if (openDialog.ShowDialog() != true || string.IsNullOrWhiteSpace(openDialog.FileName))
            {
                Feedback = "Relay restore canceled.";
                return;
            }

            var payload = File.ReadAllText(openDialog.FileName);
            var relays = JsonSerializer.Deserialize<List<RelayConfig>>(payload, BackupJsonOptions) ?? [];
            if (relays.Count == 0)
            {
                Feedback = $"Backup file is empty: {openDialog.FileName}";
                return;
            }

            var restored = 0;
            foreach (var relay in relays.Select(Clone))
            {
                var result = await _orchestrator.UpsertRelayAsync(relay);
                if (result.Success)
                {
                    restored++;
                }
            }

            await _orchestrator.LoadRelaysAsync();
            await _orchestrator.RefreshStatusAsync();
            RefreshRows();
            Feedback = $"Restored {restored}/{relays.Count} relays from: {openDialog.FileName}";
        });
    }

    private async Task<bool> ApplyRelayFromDialogAsync(RelayConfig relay)
    {
        try
        {
            await PersistRelayFromDialogAsync(relay);
            return true;
        }
        catch (Exception ex)
        {
            Feedback = $"Relay save failed: {ex.Message}";
            return false;
        }
    }

    private async Task PersistRelayFromDialogAsync(RelayConfig relay)
    {
        var result = await _orchestrator.UpsertRelayAsync(relay);
        if (!result.Success)
        {
            Feedback = result.Message;
            RefreshRows();
            throw new InvalidOperationException(result.Message);
        }

        await RefreshRelayFromServiceAsync(relay);
        Feedback = result.Message;
        RefreshRows();
    }

    private async Task RefreshRelayFromServiceAsync(RelayConfig relay)
    {
        if (relay is null || string.IsNullOrWhiteSpace(relay.Id))
        {
            return;
        }

        var refreshed = await _orchestrator.GetRelayAsync(relay.Id);
        if (!refreshed.Success || refreshed.Relay is null)
        {
            return;
        }

        OverwriteRelay(relay, refreshed.Relay);
    }

    private static void OverwriteRelay(RelayConfig target, RelayConfig source)
    {
        target.Id = source.Id;
        target.Name = source.Name;
        target.GatewayType = source.GatewayType;
        target.Enabled = source.Enabled;
        target.IncomingAdapterId = source.IncomingAdapterId;
        target.IncomingAdapterIfIndex = source.IncomingAdapterIfIndex;
        target.OutgoingAdapterId = source.OutgoingAdapterId;
        target.OutgoingAdapterIfIndex = source.OutgoingAdapterIfIndex;
        target.DataPlaneLocalPort = source.DataPlaneLocalPort;
        target.BootstrapSocksLocalPort = source.BootstrapSocksLocalPort;
        target.FrpProfilePortOverride = source.FrpProfilePortOverride is > 0 and <= 65535 ? source.FrpProfilePortOverride : 7000;
        target.FrpProfileTokenOverride = source.FrpProfileTokenOverride ?? string.Empty;
        var sourcePanel = source.OmniPanel ?? new RelayOmniPanelConfig();
        target.OmniPanel = new RelayOmniPanelConfig
        {
            Port = sourcePanel.Port,
            Username = sourcePanel.Username,
            Password = sourcePanel.Password,
            Domain = sourcePanel.Domain,
            DomainOnly = sourcePanel.DomainOnly,
            UseSsl = sourcePanel.UseSsl,
            SslMode = sourcePanel.SslMode,
            UploadedCertPath = sourcePanel.UploadedCertPath,
            UploadedKeyPath = sourcePanel.UploadedKeyPath,
            PublicUrl = sourcePanel.PublicUrl,
            LastError = sourcePanel.LastError
        };

        var sourceRemote = source.RemoteGateway ?? new RemoteGatewayConfig();
        target.RemoteGateway = new RemoteGatewayConfig
        {
            TunnelHost = sourceRemote.TunnelHost,
            TunnelSshPort = sourceRemote.TunnelSshPort,
            TunnelRemotePort = sourceRemote.TunnelRemotePort,
            TunnelUser = sourceRemote.TunnelUser,
            TunnelAuthMethod = sourceRemote.TunnelAuthMethod,
            TunnelPrivateKeyPath = sourceRemote.TunnelPrivateKeyPath,
            TunnelPrivateKeyPassphrase = sourceRemote.TunnelPrivateKeyPassphrase,
            TunnelPassword = sourceRemote.TunnelPassword,
            TunnelProbeUrl = sourceRemote.TunnelProbeUrl,
            BootstrapMode = sourceRemote.BootstrapMode,
            Protocol = sourceRemote.Protocol,
            PublicPort = sourceRemote.PublicPort,
            PanelPort = sourceRemote.PanelPort,
            PanelUser = sourceRemote.PanelUser,
            PanelPassword = sourceRemote.PanelPassword,
            PanelDomain = sourceRemote.PanelDomain,
            PanelDomainOnly = sourceRemote.PanelDomainOnly,
            PanelUseSsl = sourceRemote.PanelUseSsl,
            PanelSslMode = sourceRemote.PanelSslMode,
            PanelUploadedCertPath = sourceRemote.PanelUploadedCertPath,
            PanelUploadedKeyPath = sourceRemote.PanelUploadedKeyPath,
            ProtocolTlsEnabled = sourceRemote.ProtocolTlsEnabled,
            ProtocolTlsServerName = sourceRemote.ProtocolTlsServerName,
            ProtocolCertPath = sourceRemote.ProtocolCertPath,
            ProtocolKeyPath = sourceRemote.ProtocolKeyPath,
            ProtocolTlsMode = sourceRemote.ProtocolTlsMode,
            ProtocolAlpnCsv = sourceRemote.ProtocolAlpnCsv,
            ProxyUsername = sourceRemote.ProxyUsername,
            ProxyPassword = sourceRemote.ProxyPassword,
            VlessTlsFlow = sourceRemote.VlessTlsFlow,
            Hysteria2UpMbps = sourceRemote.Hysteria2UpMbps,
            Hysteria2DownMbps = sourceRemote.Hysteria2DownMbps,
            Hysteria2ObfsPassword = sourceRemote.Hysteria2ObfsPassword,
            Hysteria2IgnoreClientBandwidth = sourceRemote.Hysteria2IgnoreClientBandwidth,
            Hysteria2MasqueradeUrl = sourceRemote.Hysteria2MasqueradeUrl,
            NaiveNetwork = sourceRemote.NaiveNetwork,
            NaiveQuicCongestionControl = sourceRemote.NaiveQuicCongestionControl,
            ShadowTlsCamouflageServer = sourceRemote.ShadowTlsCamouflageServer,
            ShadowTlsStrictMode = sourceRemote.ShadowTlsStrictMode,
            ShadowTlsWildcardSni = sourceRemote.ShadowTlsWildcardSni,
            OpenVpnNetwork = sourceRemote.OpenVpnNetwork,
            IpsecL2tpNetwork = sourceRemote.IpsecL2tpNetwork,
            IpsecL2tpPreSharedKey = sourceRemote.IpsecL2tpPreSharedKey,
            OpenVpnSharedCaCertPath = sourceRemote.OpenVpnSharedCaCertPath,
            OpenVpnSharedClientCertPath = sourceRemote.OpenVpnSharedClientCertPath,
            OpenVpnSharedClientKeyPath = sourceRemote.OpenVpnSharedClientKeyPath,
            OpenVpnSharedTlsCryptKeyPath = sourceRemote.OpenVpnSharedTlsCryptKeyPath,
            DohEndpoints = sourceRemote.DohEndpoints
        };

        var sourceLocal = source.LocalGateway ?? new LocalGatewayConfig();
        target.LocalGateway = new LocalGatewayConfig
        {
            Protocol = sourceLocal.Protocol,
            Port = sourceLocal.Port,
            BindAddress = sourceLocal.BindAddress,
            RemoteAddress = sourceLocal.RemoteAddress,
            Remark = sourceLocal.Remark,
            RuntimeEnabled = sourceLocal.RuntimeEnabled
        };
    }

    private void WireRelayDialog(RelayEditDialog editDialog)
    {
        editDialog.ApplyRequested += ApplyRelayFromDialogAsync;
        editDialog.TestTunnelRequested += TestRelayTunnelFromDialogAsync;
        editDialog.LoadPolicyListsRequested += LoadRelayPolicyListsFromDialogAsync;
        editDialog.LoadPolicyListRequested += LoadRelayPolicyListFromDialogAsync;
        editDialog.CreatePolicyListRequested += CreateRelayPolicyListFromDialogAsync;
        editDialog.UpdatePolicyListMetaRequested += UpdateRelayPolicyListMetaFromDialogAsync;
        editDialog.ReorderPolicyListsRequested += ReorderRelayPolicyListsFromDialogAsync;
        editDialog.ReplacePolicyEntriesRequested += ReplaceRelayPolicyEntriesFromDialogAsync;
        editDialog.DeletePolicyListRequested += DeleteRelayPolicyListFromDialogAsync;
        editDialog.RefreshRelayStatusRequested += RefreshRelayStatusFromDialogAsync;
        editDialog.GatewayOperationRequested += RunRelayGatewayOperationFromDialogAsync;
        editDialog.ResolveFrpProfileForHostRequested += ResolveFrpProfileForHostFromDialog;
        editDialog.ClearCachedSudoRequested += () =>
        {
            _sudoCache.Clear();
            return Task.FromResult(new OperationResult(true, "Cached sudo password cleared for this session."));
        };
    }

    private RelayEditDialog.FrpHostProfileResolution? ResolveFrpProfileForHostFromDialog(string host)
    {
        var normalizedHost = (host ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalizedHost))
        {
            return null;
        }

        var hostRelays = _state.Relays
            .Where(x =>
                string.Equals(GatewayTypes.Normalize(x.GatewayType), GatewayTypes.Remote, StringComparison.OrdinalIgnoreCase) &&
                string.Equals((x.RemoteGateway?.TunnelHost ?? string.Empty).Trim(), normalizedHost, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (hostRelays.Count == 0)
        {
            return new RelayEditDialog.FrpHostProfileResolution { Found = false };
        }

        var nextTunnelRemotePort = hostRelays
            .Select(x => x.RemoteGateway?.TunnelRemotePort is > 0 and <= 65535 ? x.RemoteGateway.TunnelRemotePort : 0)
            .Where(x => x > 0)
            .DefaultIfEmpty(14999)
            .Max() + 1;
        if (nextTunnelRemotePort <= 0 || nextTunnelRemotePort > 65535)
        {
            nextTunnelRemotePort = 15000;
        }

        var ports = hostRelays
            .Select(x => x.FrpProfilePortOverride is > 0 and <= 65535 ? x.FrpProfilePortOverride : 7000)
            .Distinct()
            .ToList();

        if (ports.Count > 1)
        {
            var selectedPort = hostRelays
                .GroupBy(x => x.FrpProfilePortOverride is > 0 and <= 65535 ? x.FrpProfilePortOverride : 7000)
                .OrderByDescending(g => g.Count())
                .ThenBy(g => g.Key)
                .First()
                .Key;
            var selectedToken = hostRelays
                .Select(x => (x.FrpProfileTokenOverride ?? string.Empty).Trim())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .GroupBy(x => x, StringComparer.Ordinal)
                .OrderByDescending(g => g.Count())
                .Select(g => g.Key)
                .FirstOrDefault() ?? string.Empty;

            return new RelayEditDialog.FrpHostProfileResolution
            {
                Found = true,
                HasConflict = true,
                FrpServerPort = selectedPort,
                AuthToken = selectedToken,
                SuggestedTunnelRemotePort = nextTunnelRemotePort,
                ConflictMessage = $"FRP profile conflict for host '{normalizedHost}': multiple FRPS ports are configured. Using the most common profile in UI; save/apply must resolve conflict."
            };
        }

        var tokens = hostRelays
            .Select(x => (x.FrpProfileTokenOverride ?? string.Empty).Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (tokens.Count > 1)
        {
            var selectedPort = ports.Count == 0 ? 7000 : ports[0];
            var selectedToken = tokens
                .GroupBy(x => x, StringComparer.Ordinal)
                .OrderByDescending(g => g.Count())
                .Select(g => g.Key)
                .First();
            return new RelayEditDialog.FrpHostProfileResolution
            {
                Found = true,
                HasConflict = true,
                FrpServerPort = selectedPort,
                AuthToken = selectedToken,
                SuggestedTunnelRemotePort = nextTunnelRemotePort,
                ConflictMessage = $"FRP profile conflict for host '{normalizedHost}': multiple FRP tokens are configured. Using the most common profile in UI; save/apply must resolve conflict."
            };
        }

        return new RelayEditDialog.FrpHostProfileResolution
        {
            Found = true,
            FrpServerPort = ports[0],
            AuthToken = tokens.Count == 0 ? string.Empty : tokens[0],
            SuggestedTunnelRemotePort = nextTunnelRemotePort
        };
    }

    private async Task<RelayStatus?> RefreshRelayStatusFromDialogAsync(string relayId)
    {
        if (string.IsNullOrWhiteSpace(relayId))
        {
            return null;
        }

        await _orchestrator.RefreshStatusAsync();
        return _state.AppStatus?.Relays.FirstOrDefault(x => string.Equals(x.RelayId, relayId, StringComparison.Ordinal));
    }

    private async Task<OperationResult> TestRelayTunnelFromDialogAsync(RelayConfig relay)
    {
        return await _orchestrator.TestRelayTunnelConnectionAsync(relay);
    }

    private async Task<RelayPolicyListsResult> LoadRelayPolicyListsFromDialogAsync(string relayId)
    {
        return await _orchestrator.GetRelayPolicyListsAsync(relayId);
    }

    private async Task<RelayPolicyListDetailsResult> LoadRelayPolicyListFromDialogAsync(string relayId, string listId)
    {
        return await _orchestrator.GetRelayPolicyListDetailsAsync(relayId, listId);
    }

    private async Task<RelayPolicyMutationResult> CreateRelayPolicyListFromDialogAsync(string relayId, string label, string listType, int? priority)
    {
        return await _orchestrator.CreateRelayPolicyListAsync(relayId, label, listType, priority);
    }

    private async Task<RelayPolicyMutationResult> UpdateRelayPolicyListMetaFromDialogAsync(string relayId, string listId, string label, string listType)
    {
        return await _orchestrator.UpdateRelayPolicyListMetaAsync(relayId, listId, label, listType);
    }

    private async Task<OperationResult> ReorderRelayPolicyListsFromDialogAsync(string relayId, IReadOnlyList<string> orderedListIds)
    {
        return await _orchestrator.ReorderRelayPolicyListsAsync(relayId, orderedListIds);
    }

    private async Task<PolicyCommitSummary> ReplaceRelayPolicyEntriesFromDialogAsync(string relayId, string listId, IReadOnlyList<string> entries)
    {
        return await _orchestrator.ReplaceRelayPolicyListEntriesAsync(relayId, listId, entries);
    }

    private async Task<OperationResult> DeleteRelayPolicyListFromDialogAsync(string relayId, string listId)
    {
        return await _orchestrator.DeleteRelayPolicyListAsync(relayId, listId);
    }

    private async Task<OperationResult> RunRelayGatewayOperationFromDialogAsync(RelayConfig relay, string operation)
    {
        if (!string.Equals(GatewayTypes.Normalize(relay.GatewayType), GatewayTypes.Remote, StringComparison.OrdinalIgnoreCase))
        {
            return new OperationResult(false, "Gateway operations are only available for Remote Relays.");
        }

        var normalizedOperation = (operation ?? string.Empty).Trim().ToLowerInvariant();
        var requiresSudo = normalizedOperation is not ("health_check" or "refresh");
        var sudo = requiresSudo ? EnsureSudoPassword() : (_sudoCache.Get() ?? string.Empty);
        if (requiresSudo && string.IsNullOrWhiteSpace(sudo))
        {
            return new OperationResult(false, "Operation canceled. Sudo password is required.");
        }
        var sudoPassword = sudo ?? string.Empty;

        try
        {
            var relayFetch = await _orchestrator.GetRelayAsync(relay.Id);
            if (!relayFetch.Success || relayFetch.Relay is null)
            {
                return new OperationResult(false, $"Failed to refresh relay from service before operation: {relayFetch.Message}");
            }

            var effectiveRelay = relayFetch.Relay;
            OverwriteRelay(relay, effectiveRelay);

            GatewayDeploymentRequest EnsureRequest()
            {
                return BuildGatewayDeploymentRequest(effectiveRelay);
            }

            var title = operation switch
            {
                "test_tunnel" => "Gateway Test Tunnel",
                "bootstrap_check" => "Gateway Test Tunnel",
                "install" => "Gateway Install",
                "start" => "Gateway Start",
                "stop" => "Gateway Stop",
                "uninstall" => "Gateway Uninstall",
                "health_check" => "Gateway Health Check",
                "apply_dns" => "Gateway DNS Apply",
                "check_dns" => "Gateway DNS Check",
                "repair_dns" => "Gateway DNS Repair",
                "refresh" => "Gateway Refresh",
                _ => "Gateway Operation"
            };

            Func<IProgress<DeploymentProgressSnapshot>, CancellationToken, Task<GatewayOperationResult>> execute = operation switch
            {
                "test_tunnel" => (progress, token) => _gatewayDeployment.TestGatewayTunnelAsync(EnsureRequest(), sudoPassword, progress, token),
                "bootstrap_check" => (progress, token) => _gatewayDeployment.CheckGatewayBootstrapAsync(EnsureRequest(), sudoPassword, progress, token),
                "install" => (progress, token) => _gatewayDeployment.InstallGatewayAsync(EnsureRequest(), sudoPassword, progress, token),
                "start" => (progress, token) => _gatewayDeployment.StartGatewayAsync(EnsureRequest(), sudoPassword, progress, token),
                "stop" => (progress, token) => _gatewayDeployment.StopGatewayAsync(EnsureRequest(), sudoPassword, progress, token),
                "uninstall" => (progress, token) => _gatewayDeployment.UninstallGatewayAsync(EnsureRequest(), sudoPassword, progress, token),
                "health_check" => async (progress, token) =>
                {
                    var health = await _gatewayHealth.GetHealthAsync(EnsureRequest(), sudoPassword, progress, token);
                    return new GatewayOperationResult(health.Healthy, "Gateway health check completed.");
                },
                "apply_dns" => (progress, token) => _gatewayDeployment.ApplyGatewayDnsAsync(EnsureRequest(), sudoPassword, progress, token),
                "check_dns" => (progress, token) => _gatewayDeployment.CheckGatewayDnsAsync(EnsureRequest(), sudoPassword, progress, token),
                "repair_dns" => (progress, token) => _gatewayDeployment.RepairGatewayDnsAsync(EnsureRequest(), sudoPassword, progress, token),
                "refresh" => async (progress, token) =>
                {
                    var health = await _gatewayHealth.GetHealthAsync(EnsureRequest(), sudoPassword, progress, token);
                    return new GatewayOperationResult(health.Healthy, "Gateway status refresh completed.");
                },
                _ => (_, _) => Task.FromResult(new GatewayOperationResult(false, "Unknown gateway operation."))
            };

            Func<IProgress<DeploymentProgressSnapshot>, GatewayOperationResult, CancellationToken, Task>? afterOperation = null;
            if (string.Equals(operation, "install", StringComparison.Ordinal))
            {
                afterOperation = async (progress, opResult, token) =>
                {
                    if (!opResult.Success)
                    {
                        return;
                    }

                    await PollRelayGatewayHealthAfterInstallAsync(EnsureRequest(), sudoPassword, progress, token);
                };
            }

            var opVm = new RelayGatewayOperationDialogViewModel(title, _progressAggregator, execute, afterOperation);
            var opDialog = new GatewayOperationDialog
            {
                Owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(x => x.IsActive) ?? Application.Current?.MainWindow,
                DataContext = opVm
            };
            opVm.RequestClose += () =>
            {
                if (opDialog.IsVisible)
                {
                    opDialog.Close();
                }
            };

            opDialog.ContentRendered += async (_, _) => await opVm.RunAsync();
            opDialog.ShowDialog();

            return new OperationResult(opVm.LastOperationSuccess, opVm.LastOperationMessage);
        }
        catch (Exception ex)
        {
            return new OperationResult(false, $"Gateway operation failed: {ex.Message}");
        }
    }

    private async Task PollRelayGatewayHealthAfterInstallAsync(
        GatewayDeploymentRequest request,
        string sudoPassword,
        IProgress<DeploymentProgressSnapshot> progress,
        CancellationToken cancellationToken)
    {
        const int pollIntervalSeconds = 5;
        const int maxPollSeconds = 25;
        var attempts = Math.Max(1, maxPollSeconds / pollIntervalSeconds);

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            var healthy = (await _gatewayHealth.GetHealthAsync(request, sudoPassword, progress, cancellationToken)).Healthy;
            if (healthy || attempt >= attempts)
            {
                break;
            }

            progress.Report(new DeploymentProgressSnapshot
            {
                Phase = DeploymentPhases.GatewayHealth,
                Percent = 0,
                Message = $"Waiting for gateway services to stabilize ({attempt}/{attempts})..."
            });

            await Task.Delay(TimeSpan.FromSeconds(pollIntervalSeconds), cancellationToken);
        }
    }

    private string? EnsureSudoPassword()
    {
        var cached = _sudoCache.Get();
        if (!string.IsNullOrWhiteSpace(cached))
        {
            return cached;
        }

        var dialog = new SecretInputDialog(
            "Gateway Sudo Password",
            "Enter the VPS sudo password for gateway install/start/stop operations. The value is kept only for this app session.",
            string.Empty)
        {
            Owner = Application.Current?.MainWindow
        };

        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.SecretValue))
        {
            return null;
        }

        _sudoCache.Set(dialog.SecretValue);
        return dialog.SecretValue;
    }

    private static GatewayDeploymentRequest BuildGatewayDeploymentRequest(RelayConfig relay)
    {
        var remote = relay.RemoteGateway ?? new RemoteGatewayConfig();
        var panel = relay.OmniPanel ?? new RelayOmniPanelConfig();
        var protocol = GatewayProtocols.Normalize(remote.Protocol);
        if (string.Equals(protocol, GatewayProtocols.ShadowTlsV3ShadowsocksSingbox, StringComparison.OrdinalIgnoreCase) &&
            string.IsNullOrWhiteSpace(remote.ShadowTlsCamouflageServer))
        {
            throw new InvalidOperationException("Camouflage server is required for ShadowTLS.");
        }

        var panelSslEnabled = panel.UseSsl;
        var panelSslMode = panelSslEnabled ? NormalizePanelSslMode(panel.SslMode) : "none";
        if ((panel.DomainOnly || panelSslEnabled) && string.IsNullOrWhiteSpace(panel.Domain))
        {
            throw new InvalidOperationException("OmniPanel domain is required when Domain Only or SSL is enabled.");
        }

        return new GatewayDeploymentRequest
        {
            RelayId = relay.Id,
            Config = BuildServiceConfigForRelay(relay),
            BootstrapMode = GatewayBootstrapModes.Normalize(remote.BootstrapMode),
            TunnelProbeUrl = string.IsNullOrWhiteSpace(remote.TunnelProbeUrl) ? "https://1.1.1.1/cdn-cgi/trace" : remote.TunnelProbeUrl.Trim(),
            SelectedGatewayProtocol = protocol,
            GatewayPublicPort = remote.PublicPort is > 0 and <= 65535 ? remote.PublicPort : 443,
            GatewayPanelPort = panel.Port is > 0 and <= 65535 ? panel.Port : 2054,
            GatewayPanelUser = panel.Username.Trim(),
            GatewayPanelPassword = panel.Password,
            GatewayPanelDomain = panel.Domain.Trim(),
            GatewayPanelDomainOnly = panel.DomainOnly,
            GatewayPanelSslEnabled = panelSslEnabled,
            GatewayPanelSslMode = panelSslMode,
            GatewayPanelCertLocalPath = panel.UploadedCertPath.Trim(),
            GatewayPanelKeyLocalPath = panel.UploadedKeyPath.Trim(),
            GatewayProtocolTlsEnabled = remote.ProtocolTlsEnabled,
            GatewayProtocolTlsServerName = remote.ProtocolTlsServerName.Trim(),
            GatewayProtocolCertPath = remote.ProtocolCertPath.Trim(),
            GatewayProtocolKeyPath = remote.ProtocolKeyPath.Trim(),
            GatewayProtocolTlsMode = remote.ProtocolTlsMode.Trim(),
            GatewayProtocolAlpnCsv = remote.ProtocolAlpnCsv.Trim(),
            GatewayProxyUsername = remote.ProxyUsername.Trim(),
            GatewayProxyPassword = remote.ProxyPassword,
            VlessTlsFlow = remote.VlessTlsFlow.Trim(),
            Hysteria2UpMbps = remote.Hysteria2UpMbps,
            Hysteria2DownMbps = remote.Hysteria2DownMbps,
            Hysteria2ObfsPassword = remote.Hysteria2ObfsPassword.Trim(),
            Hysteria2IgnoreClientBandwidth = remote.Hysteria2IgnoreClientBandwidth,
            Hysteria2MasqueradeUrl = remote.Hysteria2MasqueradeUrl.Trim(),
            NaiveNetwork = remote.NaiveNetwork.Trim(),
            NaiveQuicCongestionControl = remote.NaiveQuicCongestionControl.Trim(),
            ShadowTlsCamouflageServer = remote.ShadowTlsCamouflageServer.Trim(),
            ShadowTlsStrictMode = remote.ShadowTlsStrictMode,
            ShadowTlsWildcardSni = remote.ShadowTlsWildcardSni.Trim(),
            OpenVpnNetwork = string.IsNullOrWhiteSpace(remote.OpenVpnNetwork) ? "10.29.0.0/24" : remote.OpenVpnNetwork.Trim(),
            IpsecL2tpNetwork = string.IsNullOrWhiteSpace(remote.IpsecL2tpNetwork) ? "10.39.0.0/24" : remote.IpsecL2tpNetwork.Trim(),
            IpsecL2tpPreSharedKey = remote.IpsecL2tpPreSharedKey,
            OpenVpnSharedCaCertLocalPath = remote.OpenVpnSharedCaCertPath.Trim(),
            OpenVpnSharedClientCertLocalPath = remote.OpenVpnSharedClientCertPath.Trim(),
            OpenVpnSharedClientKeyLocalPath = remote.OpenVpnSharedClientKeyPath.Trim(),
            OpenVpnSharedTlsCryptKeyLocalPath = remote.OpenVpnSharedTlsCryptKeyPath.Trim(),
            GatewayDohEndpoints = string.IsNullOrWhiteSpace(remote.DohEndpoints) ? "https://1.1.1.1/dns-query,https://8.8.8.8/dns-query" : remote.DohEndpoints.Trim(),
        };
    }

    private static ServiceConfig BuildServiceConfigForRelay(RelayConfig relay)
    {
        var remote = relay.RemoteGateway ?? new RemoteGatewayConfig();
        var incomingIfIndex = NetworkAdapterCatalog.TryResolveIfIndex(relay.IncomingAdapterId, relay.IncomingAdapterIfIndex, out var inIf) ? inIf : relay.IncomingAdapterIfIndex;
        var outgoingIfIndex = NetworkAdapterCatalog.TryResolveIfIndex(relay.OutgoingAdapterId, relay.OutgoingAdapterIfIndex, out var outIf) ? outIf : relay.OutgoingAdapterIfIndex;
        var profile = new FrpServerProfile
        {
            TunnelHost = remote.TunnelHost.Trim(),
            FrpServerPort = NormalizePortOrDefault(relay.FrpProfilePortOverride, 7000),
            AuthToken = relay.FrpProfileTokenOverride ?? string.Empty
        };
        return new ServiceConfig
        {
            GatewayType = GatewayTypes.Normalize(relay.GatewayType),
            LocalProxyListenPort = NormalizePortOrDefault(relay.DataPlaneLocalPort, 24080),
            BootstrapSocksLocalPort = NormalizePortOrDefault(relay.BootstrapSocksLocalPort, 24081),
            TunnelRemotePort = NormalizePortOrDefault(remote.TunnelRemotePort, 15000),
            WhitelistAdapterIfIndex = incomingIfIndex,
            DefaultAdapterIfIndex = outgoingIfIndex,
            TunnelHost = profile.TunnelHost,
            TunnelSshPort = NormalizePortOrDefault(remote.TunnelSshPort, 22),
            FrpServerPort = profile.FrpServerPort,
            FrpRuntimeToken = profile.AuthToken,
            FrpServerProfiles = new List<FrpServerProfile> { profile },
            TunnelUser = string.IsNullOrWhiteSpace(remote.TunnelUser) ? "OmniRelay" : remote.TunnelUser.Trim(),
            TunnelAuthMethod = TunnelAuthMethods.Normalize(remote.TunnelAuthMethod),
            TunnelPrivateKeyPath = remote.TunnelPrivateKeyPath,
            TunnelPrivateKeyPassphrase = remote.TunnelPrivateKeyPassphrase,
            TunnelPassword = remote.TunnelPassword
        };
    }

    private static int NormalizePortOrDefault(int port, int fallback)
    {
        return port > 0 && port <= 65535 ? port : fallback;
    }

    private static string NormalizePanelSslMode(string? value)
    {
        var normalized = (value ?? string.Empty).Trim();
        return string.Equals(normalized, "uploaded", StringComparison.OrdinalIgnoreCase) ? "uploaded" : "letsencrypt";
    }

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(GatewayStateStore.AppStatus) or nameof(GatewayStateStore.ServiceState))
        {
            RefreshRows();
        }
    }

    private void RefreshRows()
    {
        IsRelayServiceRunning = string.Equals(_state.ServiceState, "Running", StringComparison.OrdinalIgnoreCase);
        var statuses = BuildStatusMap(_state.AppStatus?.Relays);
        Relays.Clear();
        foreach (var relay in _state.Relays)
        {
            if (relay is null)
            {
                continue;
            }

            var relayId = string.IsNullOrWhiteSpace(relay.Id) ? string.Empty : relay.Id.Trim();
            statuses.TryGetValue(relayId, out var status);
            Relays.Add(new RelayRowViewModel(SanitizeRelay(relay), status));
        }
    }

    private static Dictionary<string, RelayStatus> BuildStatusMap(IEnumerable<RelayStatus>? statuses)
    {
        var map = new Dictionary<string, RelayStatus>(StringComparer.Ordinal);
        if (statuses is null)
        {
            return map;
        }

        foreach (var status in statuses)
        {
            if (status is null || string.IsNullOrWhiteSpace(status.RelayId))
            {
                continue;
            }

            map[status.RelayId.Trim()] = status;
        }

        return map;
    }

    private void RefreshAdapterCatalog()
    {
        var adapters = NetworkAdapterCatalog.ListIpv4Adapters()
            .Select(x => new AdapterChoiceModel
            {
                AdapterId = x.AdapterId,
                IfIndex = x.IfIndex,
                MacAddress = x.MacAddress,
                Display = $"{x.Name} (IfIndex={x.IfIndex}) | MAC={x.MacAddress} | IPv4={string.Join(",", x.IPv4Addresses)} | GW={(x.HasDefaultGateway ? "Yes" : "No")}"
            })
            .ToList();

        _state.Adapters.Clear();
        foreach (var adapter in adapters)
        {
            _state.Adapters.Add(adapter);
        }
    }

    private async Task RunBusyAsync(Func<Task> action)
    {
        if (IsBusy)
        {
            return;
        }

        try
        {
            IsBusy = true;
            await action();
        }
        catch (Exception ex)
        {
            Feedback = $"Operation failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static RelayConfig CreateDefaultRelay(string gatewayType)
    {
        var type = GatewayTypes.Normalize(gatewayType);
        return new RelayConfig
        {
            Id = Guid.NewGuid().ToString("N"),
            Name = type == GatewayTypes.Local ? "Local Relay" : "Remote Relay",
            GatewayType = type,
            Enabled = true,
            OmniPanel = new RelayOmniPanelConfig
            {
                Port = 2054
            },
            RemoteGateway = new RemoteGatewayConfig
            {
                TunnelRemotePort = 0
            }
        };
    }

    private static RelayConfig Clone(RelayConfig relay)
    {
        return SanitizeRelay(relay);
    }

    private static RelayConfig SanitizeRelay(RelayConfig? relay)
    {
        return new RelayConfig
        {
            Id = string.IsNullOrWhiteSpace(relay?.Id) ? Guid.NewGuid().ToString("N") : relay.Id.Trim(),
            Name = string.IsNullOrWhiteSpace(relay?.Name) ? "Relay" : relay.Name.Trim(),
            GatewayType = GatewayTypes.Normalize(relay?.GatewayType),
            Enabled = relay?.Enabled ?? false,
            IncomingAdapterId = relay?.IncomingAdapterId ?? string.Empty,
            IncomingAdapterIfIndex = relay?.IncomingAdapterIfIndex ?? -1,
            OutgoingAdapterId = relay?.OutgoingAdapterId ?? string.Empty,
            OutgoingAdapterIfIndex = relay?.OutgoingAdapterIfIndex ?? -1,
            DataPlaneLocalPort = relay?.DataPlaneLocalPort ?? 0,
            BootstrapSocksLocalPort = relay?.BootstrapSocksLocalPort ?? 0,
            FrpProfilePortOverride = relay?.FrpProfilePortOverride is > 0 and <= 65535 ? relay.FrpProfilePortOverride : 7000,
            FrpProfileTokenOverride = relay?.FrpProfileTokenOverride ?? string.Empty,
            OmniPanel = new RelayOmniPanelConfig
            {
                Port = relay?.OmniPanel?.Port is > 0 and <= 65535 ? relay.OmniPanel.Port : (relay?.RemoteGateway?.PanelPort is > 0 and <= 65535 ? relay.RemoteGateway.PanelPort : 2054),
                Username = relay?.OmniPanel?.Username ?? relay?.RemoteGateway?.PanelUser ?? string.Empty,
                Password = relay?.OmniPanel?.Password ?? relay?.RemoteGateway?.PanelPassword ?? string.Empty,
                Domain = relay?.OmniPanel?.Domain ?? relay?.RemoteGateway?.PanelDomain ?? string.Empty,
                DomainOnly = relay?.OmniPanel?.DomainOnly ?? relay?.RemoteGateway?.PanelDomainOnly ?? false,
                UseSsl = relay?.OmniPanel?.UseSsl ?? relay?.RemoteGateway?.PanelUseSsl ?? false,
                SslMode = string.IsNullOrWhiteSpace(relay?.OmniPanel?.SslMode)
                    ? (string.IsNullOrWhiteSpace(relay?.RemoteGateway?.PanelSslMode) ? "letsencrypt" : relay.RemoteGateway.PanelSslMode.Trim())
                    : relay.OmniPanel.SslMode.Trim(),
                UploadedCertPath = relay?.OmniPanel?.UploadedCertPath ?? relay?.RemoteGateway?.PanelUploadedCertPath ?? string.Empty,
                UploadedKeyPath = relay?.OmniPanel?.UploadedKeyPath ?? relay?.RemoteGateway?.PanelUploadedKeyPath ?? string.Empty,
                PublicUrl = relay?.OmniPanel?.PublicUrl ?? string.Empty,
                LastError = relay?.OmniPanel?.LastError ?? string.Empty
            },
            RemoteGateway = new RemoteGatewayConfig
            {
                TunnelHost = relay?.RemoteGateway?.TunnelHost ?? string.Empty,
                TunnelSshPort = relay?.RemoteGateway?.TunnelSshPort is > 0 and <= 65535 ? relay.RemoteGateway.TunnelSshPort : 22,
                TunnelRemotePort = relay?.RemoteGateway?.TunnelRemotePort is > 0 and <= 65535 ? relay.RemoteGateway.TunnelRemotePort : 0,
                TunnelUser = string.IsNullOrWhiteSpace(relay?.RemoteGateway?.TunnelUser) ? "OmniRelay" : relay.RemoteGateway.TunnelUser.Trim(),
                TunnelAuthMethod = TunnelAuthMethods.Normalize(relay?.RemoteGateway?.TunnelAuthMethod),
                TunnelPrivateKeyPath = relay?.RemoteGateway?.TunnelPrivateKeyPath ?? string.Empty,
                TunnelPrivateKeyPassphrase = relay?.RemoteGateway?.TunnelPrivateKeyPassphrase ?? string.Empty,
                TunnelPassword = relay?.RemoteGateway?.TunnelPassword ?? string.Empty,
                TunnelProbeUrl = string.IsNullOrWhiteSpace(relay?.RemoteGateway?.TunnelProbeUrl) ? "https://1.1.1.1/cdn-cgi/trace" : relay.RemoteGateway.TunnelProbeUrl.Trim(),
                BootstrapMode = GatewayBootstrapModes.Normalize(relay?.RemoteGateway?.BootstrapMode),
                Protocol = GatewayProtocols.Normalize(relay?.RemoteGateway?.Protocol),
                PublicPort = relay?.RemoteGateway?.PublicPort is > 0 and <= 65535 ? relay.RemoteGateway.PublicPort : 443,
                PanelPort = relay?.OmniPanel?.Port is > 0 and <= 65535
                    ? relay.OmniPanel.Port
                    : (relay?.RemoteGateway?.PanelPort is > 0 and <= 65535 ? relay.RemoteGateway.PanelPort : 2054),
                PanelUser = relay?.OmniPanel?.Username ?? relay?.RemoteGateway?.PanelUser ?? string.Empty,
                PanelPassword = relay?.OmniPanel?.Password ?? relay?.RemoteGateway?.PanelPassword ?? string.Empty,
                PanelDomain = relay?.OmniPanel?.Domain ?? relay?.RemoteGateway?.PanelDomain ?? string.Empty,
                PanelDomainOnly = relay?.OmniPanel?.DomainOnly ?? relay?.RemoteGateway?.PanelDomainOnly ?? false,
                PanelUseSsl = relay?.OmniPanel?.UseSsl ?? relay?.RemoteGateway?.PanelUseSsl ?? false,
                PanelSslMode = !string.IsNullOrWhiteSpace(relay?.OmniPanel?.SslMode)
                    ? relay.OmniPanel.SslMode.Trim()
                    : (string.IsNullOrWhiteSpace(relay?.RemoteGateway?.PanelSslMode) ? "letsencrypt" : relay.RemoteGateway.PanelSslMode.Trim()),
                PanelUploadedCertPath = relay?.OmniPanel?.UploadedCertPath ?? relay?.RemoteGateway?.PanelUploadedCertPath ?? string.Empty,
                PanelUploadedKeyPath = relay?.OmniPanel?.UploadedKeyPath ?? relay?.RemoteGateway?.PanelUploadedKeyPath ?? string.Empty,
                ProtocolTlsEnabled = relay?.RemoteGateway?.ProtocolTlsEnabled ?? false,
                ProtocolTlsServerName = relay?.RemoteGateway?.ProtocolTlsServerName ?? string.Empty,
                ProtocolCertPath = relay?.RemoteGateway?.ProtocolCertPath ?? string.Empty,
                ProtocolKeyPath = relay?.RemoteGateway?.ProtocolKeyPath ?? string.Empty,
                ProtocolTlsMode = string.IsNullOrWhiteSpace(relay?.RemoteGateway?.ProtocolTlsMode) ? "uploaded" : relay.RemoteGateway.ProtocolTlsMode.Trim(),
                ProtocolAlpnCsv = relay?.RemoteGateway?.ProtocolAlpnCsv ?? string.Empty,
                ProxyUsername = string.IsNullOrWhiteSpace(relay?.RemoteGateway?.ProxyUsername) ? "omni" : relay.RemoteGateway.ProxyUsername.Trim(),
                ProxyPassword = relay?.RemoteGateway?.ProxyPassword ?? string.Empty,
                VlessTlsFlow = relay?.RemoteGateway?.VlessTlsFlow ?? string.Empty,
                Hysteria2UpMbps = relay?.RemoteGateway?.Hysteria2UpMbps is > 0 ? relay.RemoteGateway.Hysteria2UpMbps : 100,
                Hysteria2DownMbps = relay?.RemoteGateway?.Hysteria2DownMbps is > 0 ? relay.RemoteGateway.Hysteria2DownMbps : 100,
                Hysteria2ObfsPassword = relay?.RemoteGateway?.Hysteria2ObfsPassword ?? string.Empty,
                Hysteria2IgnoreClientBandwidth = relay?.RemoteGateway?.Hysteria2IgnoreClientBandwidth ?? false,
                Hysteria2MasqueradeUrl = relay?.RemoteGateway?.Hysteria2MasqueradeUrl ?? string.Empty,
                NaiveNetwork = relay?.RemoteGateway?.NaiveNetwork ?? string.Empty,
                NaiveQuicCongestionControl = relay?.RemoteGateway?.NaiveQuicCongestionControl ?? string.Empty,
                ShadowTlsCamouflageServer = relay?.RemoteGateway?.ShadowTlsCamouflageServer ?? string.Empty,
                ShadowTlsStrictMode = relay?.RemoteGateway?.ShadowTlsStrictMode ?? false,
                ShadowTlsWildcardSni = relay?.RemoteGateway?.ShadowTlsWildcardSni ?? string.Empty,
                OpenVpnNetwork = string.IsNullOrWhiteSpace(relay?.RemoteGateway?.OpenVpnNetwork) ? "10.29.0.0/24" : relay.RemoteGateway.OpenVpnNetwork.Trim(),
                IpsecL2tpNetwork = string.IsNullOrWhiteSpace(relay?.RemoteGateway?.IpsecL2tpNetwork) ? "10.39.0.0/24" : relay.RemoteGateway.IpsecL2tpNetwork.Trim(),
                IpsecL2tpPreSharedKey = relay?.RemoteGateway?.IpsecL2tpPreSharedKey ?? string.Empty,
                OpenVpnSharedCaCertPath = relay?.RemoteGateway?.OpenVpnSharedCaCertPath ?? string.Empty,
                OpenVpnSharedClientCertPath = relay?.RemoteGateway?.OpenVpnSharedClientCertPath ?? string.Empty,
                OpenVpnSharedClientKeyPath = relay?.RemoteGateway?.OpenVpnSharedClientKeyPath ?? string.Empty,
                OpenVpnSharedTlsCryptKeyPath = relay?.RemoteGateway?.OpenVpnSharedTlsCryptKeyPath ?? string.Empty,
                DohEndpoints = relay?.RemoteGateway?.DohEndpoints ?? "https://1.1.1.1/dns-query,https://8.8.8.8/dns-query",
            },
            LocalGateway = new LocalGatewayConfig
            {
                Protocol = LocalGatewayProtocols.Normalize(relay?.LocalGateway?.Protocol),
                Port = relay?.LocalGateway?.Port is > 0 and <= 65535 ? relay.LocalGateway.Port : 443,
                BindAddress = string.IsNullOrWhiteSpace(relay?.LocalGateway?.BindAddress) ? "0.0.0.0" : relay.LocalGateway.BindAddress.Trim(),
                RemoteAddress = relay?.LocalGateway?.RemoteAddress ?? string.Empty,
                Remark = string.IsNullOrWhiteSpace(relay?.LocalGateway?.Remark) ? "OmniRelay Local Gateway" : relay.LocalGateway.Remark.Trim(),
                RuntimeEnabled = relay?.LocalGateway?.RuntimeEnabled ?? true
            }
        };
    }
}

public sealed class RelayRowViewModel
{
    public RelayRowViewModel(RelayConfig relay, RelayStatus? status)
    {
        RelayId = string.IsNullOrWhiteSpace(relay.Id) ? string.Empty : relay.Id.Trim();
        Name = string.IsNullOrWhiteSpace(relay.Name) ? "Relay" : relay.Name.Trim();
        GatewayType = GatewayTypes.Normalize(relay.GatewayType);
        Enabled = relay.Enabled;
        TunnelState = status?.TunnelState ?? "Not running";
        HealthState = status?.HealthState ?? "Unknown";
        HealthReason = status?.HealthReasonCode ?? status?.TunnelLastError ?? string.Empty;
    }

    public string RelayId { get; }
    public string Name { get; }
    public string GatewayType { get; }
    public bool Enabled { get; }
    public string TunnelState { get; }
    public string HealthState { get; }
    public string HealthReason { get; }
    public bool ShowHealthReasonOnStatusHover =>
        !string.IsNullOrWhiteSpace(HealthReason) &&
        (!IsHealthyState(TunnelState) || !IsHealthyState(HealthState));

    private static bool IsHealthyState(string? value) =>
        string.Equals((value ?? string.Empty).Trim(), "healthy", StringComparison.OrdinalIgnoreCase);
}



