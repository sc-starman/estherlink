using OmniRelay.Core.Configuration;
using OmniRelay.Core.Status;
using OmniRelay.Ipc;
using OmniRelay.UI.Models;
using OmniRelay.UI.Services;
using OmniRelay.UI.ViewModels;

namespace OmniRelay.UI.Tests;

public class ViewModelBehaviorTests
{
    [Fact]
    public async Task DashboardRefresh_DisablesCommandWhileRunning()
    {
        var state = new GatewayStateStore();
        var gatewayClient = new FakeGatewayClientService
        {
            GetStatusDelay = TimeSpan.FromMilliseconds(120),
            StatusToReturn = new GatewayStatus
            {
                ProxyRunning = true,
                LicenseValid = true,
                TunnelConnected = true,
                WhitelistCount = 7,
                HealthState = "Connected",
                LastStatusUpdateUtc = DateTimeOffset.UtcNow
            }
        };

        var serviceControl = new FakeServiceControlService
        {
            QueryDelay = TimeSpan.FromMilliseconds(120),
            ServiceState = "Running"
        };

        var orchestrator = new GatewayOrchestratorService(state, gatewayClient, serviceControl, new FakeGatewayStatePersistenceService());
        var vm = new DashboardViewModel(orchestrator, state);

        Assert.True(vm.RefreshCommand.CanExecute(null));

        var runTask = vm.RefreshCommand.ExecuteAsync(null);
        await Task.Delay(25);

        Assert.False(vm.RefreshCommand.CanExecute(null));

        await runTask;

        Assert.True(vm.RefreshCommand.CanExecute(null));
        Assert.Equal("Running", vm.ServiceState);
        Assert.Equal("Running", vm.ProxyState);
        Assert.Equal("Valid", vm.LicenseState);
        Assert.Equal("Connected", vm.TunnelState);
        Assert.Equal(7, vm.WhitelistCount);
    }

    [Fact]
    public async Task WhitelistUpdate_InvalidEntry_DoesNotCallService()
    {
        var state = new GatewayStateStore();

        var gatewayClient = new FakeGatewayClientService();
        var serviceControl = new FakeServiceControlService();
        var orchestrator = new GatewayOrchestratorService(state, gatewayClient, serviceControl, new FakeGatewayStatePersistenceService());
        var vm = new WhitelistViewModel(orchestrator)
        {
            WhitelistText = "1.2.3.0/24\nnot-a-cidr"
        };

        await vm.UpdateWhitelistCommand.ExecuteAsync(null);

        Assert.Equal("whitelist contains invalid entries.", vm.Feedback);
        Assert.Contains("Line 2", vm.ValidationSummary);
        Assert.Equal(0, gatewayClient.UpdateWhitelistCallCount);
    }

    [Fact]
    public void SettingsSave_PersistsAndAppliesTheme()
    {
        var settingsService = new FakeUiSettingsService();
        var themeService = new FakeThemeService();
        var vm = new SettingsViewModel(settingsService, themeService)
        {
            DarkThemeEnabled = false,
            RefreshIntervalSeconds = 15
        };

        vm.SaveCommand.Execute(null);

        Assert.NotNull(settingsService.LastSaved);
        Assert.Equal("Light", settingsService.LastSaved!.Theme);
        Assert.Equal(15, settingsService.LastSaved.RefreshIntervalSeconds);
        Assert.Equal("Light", themeService.LastAppliedTheme);
        Assert.Equal("Settings saved.", vm.Feedback);
    }

    [Fact]
    public async Task BuildLocalGatewayClientConfig_ParsesOpenVpnBundle()
    {
        var state = new GatewayStateStore();
        var gatewayClient = new FakeGatewayClientService
        {
            ClientConfigToReturn = new LocalGatewayClientConfigResponse(
                "openvpn_bundle",
                "client\nproto tcp-client\n",
                "OpenVPN Client Bundle",
                "ovpn_user",
                "ovpn_pass",
                "client.ovpn",
                "client\nproto tcp-client\n")
        };

        var orchestrator = new GatewayOrchestratorService(state, gatewayClient, new FakeServiceControlService(), new FakeGatewayStatePersistenceService());
        var result = await orchestrator.BuildLocalGatewayClientConfigAsync("abc");

        Assert.True(result.Success);
        Assert.Equal("openvpn_bundle", result.Mode);
        Assert.Equal("OpenVPN Client Bundle", result.Title);
        Assert.Equal("ovpn_user", result.Username);
        Assert.Equal("ovpn_pass", result.Password);
        Assert.Equal("client.ovpn", result.OvpnFileName);
        Assert.Contains("proto tcp-client", result.OvpnContent);
    }

    private sealed class FakeGatewayClientService : IGatewayClientService
    {
        public int UpdateWhitelistCallCount { get; private set; }
        public TimeSpan GetStatusDelay { get; set; } = TimeSpan.Zero;
        public GatewayStatus StatusToReturn { get; set; } = new();
        public LocalGatewayClientConfigResponse ClientConfigToReturn { get; set; } =
            new("uri", "vless://example", "Config", string.Empty, string.Empty, string.Empty, string.Empty);

        public async Task<IpcResponse?> GetStatusAsync(CancellationToken cancellationToken = default)
        {
            if (GetStatusDelay > TimeSpan.Zero)
            {
                await Task.Delay(GetStatusDelay, cancellationToken);
            }

            var payload = IpcJson.Serialize(new StatusResponse(StatusToReturn));
            return new IpcResponse(true, null, payload);
        }

        public Task<IpcResponse?> SetConfigAsync(ServiceConfig config, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IpcResponse?>(new IpcResponse(true));
        }

        public Task<IpcResponse?> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
        {
            var payload = IpcJson.Serialize(new CapabilitiesResponse("1.0.0", [IpcCommands.SetLicenseKey]));
            return Task.FromResult<IpcResponse?>(new IpcResponse(true, null, payload));
        }

        public Task<IpcResponse?> SetLicenseKeyAsync(string licenseKey, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IpcResponse?>(new IpcResponse(true));
        }

        public Task<IpcResponse?> RequestLicenseTransferAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IpcResponse?>(new IpcResponse(true));
        }

        public Task<IpcResponse?> UpdateWhitelistAsync(IReadOnlyList<string> entries, CancellationToken cancellationToken = default)
        {
            UpdateWhitelistCallCount++;
            return Task.FromResult<IpcResponse?>(new IpcResponse(true));
        }

        public Task<IpcResponse?> GetPolicyListAsync(string listType, CancellationToken cancellationToken = default)
        {
            var payload = IpcJson.Serialize(new GetPolicyListResponse(listType, [], 0, 1, DateTimeOffset.UtcNow));
            return Task.FromResult<IpcResponse?>(new IpcResponse(true, null, payload));
        }

        public Task<IpcResponse?> BeginPolicyUpdateAsync(string listType, string mode, CancellationToken cancellationToken = default)
        {
            var payload = IpcJson.Serialize(new BeginPolicyUpdateResponse(Guid.NewGuid().ToString("N"), listType, mode));
            return Task.FromResult<IpcResponse?>(new IpcResponse(true, null, payload));
        }

        public Task<IpcResponse?> AppendPolicyEntriesAsync(string sessionId, IReadOnlyList<string> entries, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IpcResponse?>(new IpcResponse(true));
        }

        public Task<IpcResponse?> CommitPolicyUpdateAsync(string sessionId, CancellationToken cancellationToken = default)
        {
            var payload = IpcJson.Serialize(new CommitPolicyUpdateResponse("whitelist", "replace", 0, 0, 0, 0, 1, DateTimeOffset.UtcNow));
            return Task.FromResult<IpcResponse?>(new IpcResponse(true, null, payload));
        }

        public Task<IpcResponse?> CancelPolicyUpdateAsync(string sessionId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IpcResponse?>(new IpcResponse(true));
        }

        public Task<IpcResponse?> VerifyLicenseAsync(CancellationToken cancellationToken = default)
        {
            var payload = IpcJson.Serialize(new VerifyLicenseResponse(true, DateTimeOffset.UtcNow.AddDays(30), false, null));
            return Task.FromResult<IpcResponse?>(new IpcResponse(true, null, payload));
        }

        public Task<IpcResponse?> StartProxyAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IpcResponse?>(new IpcResponse(true));
        }

        public Task<IpcResponse?> StopProxyAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IpcResponse?>(new IpcResponse(true));
        }

        public Task<IpcResponse?> TestTunnelConnectionAsync(ServiceConfig config, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IpcResponse?>(new IpcResponse(true));
        }

        public Task<IpcResponse?> ApplyLocalGatewayConfigAsync(LocalGatewayConfig config, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IpcResponse?>(new IpcResponse(true));
        }

        public Task<IpcResponse?> StartLocalGatewayAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IpcResponse?>(new IpcResponse(true));
        }

        public Task<IpcResponse?> StopLocalGatewayAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IpcResponse?>(new IpcResponse(true));
        }

        public Task<IpcResponse?> RestartLocalGatewayAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IpcResponse?>(new IpcResponse(true));
        }

        public Task<IpcResponse?> GetLocalGatewayClientsAsync(CancellationToken cancellationToken = default)
        {
            var payload = IpcJson.Serialize(new LocalGatewayClientsResponse(LocalGatewayProtocols.VlessTcpPlain, 443, []));
            return Task.FromResult<IpcResponse?>(new IpcResponse(true, null, payload));
        }

        public Task<IpcResponse?> AddLocalGatewayClientAsync(string email, string? remark, CancellationToken cancellationToken = default)
        {
            var record = new LocalGatewayClientRecord(
                Guid.NewGuid().ToString("N"),
                email,
                true,
                remark ?? email,
                LocalGatewayProtocols.VlessTcpPlain,
                "ovpn_test",
                "secret",
                DateTimeOffset.UtcNow);
            return Task.FromResult<IpcResponse?>(new IpcResponse(true, null, IpcJson.Serialize(record)));
        }

        public Task<IpcResponse?> UpdateLocalGatewayClientAsync(LocalGatewayClientRecord client, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IpcResponse?>(new IpcResponse(true));
        }

        public Task<IpcResponse?> DeleteLocalGatewayClientAsync(string clientId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<IpcResponse?>(new IpcResponse(true));
        }

        public Task<IpcResponse?> BuildLocalGatewayClientConfigAsync(string clientId, CancellationToken cancellationToken = default)
        {
            var payload = IpcJson.Serialize(ClientConfigToReturn);
            return Task.FromResult<IpcResponse?>(new IpcResponse(true, null, payload));
        }
    }

    private sealed class FakeServiceControlService : IServiceControlService
    {
        public TimeSpan QueryDelay { get; set; } = TimeSpan.Zero;
        public string ServiceState { get; set; } = "Stopped";

        public async Task<string> QueryServiceStateAsync(CancellationToken cancellationToken = default)
        {
            if (QueryDelay > TimeSpan.Zero)
            {
                await Task.Delay(QueryDelay, cancellationToken);
            }

            return ServiceState;
        }

        public Task<bool> InstallOrStartWindowsServiceAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(true);
        }

        public Task<bool> StopWindowsServiceAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(true);
        }

        public Task<bool> UninstallWindowsServiceAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(true);
        }
    }

    private sealed class FakeUiSettingsService : IUiSettingsService
    {
        public UiSettingsModel Current { get; private set; } = new();
        public UiSettingsModel? LastSaved { get; private set; }

        public event EventHandler<UiSettingsModel>? SettingsChanged;

        public UiSettingsModel Load()
        {
            return Current;
        }

        public void Save(UiSettingsModel settings)
        {
            LastSaved = settings;
            Current = settings;
            SettingsChanged?.Invoke(this, settings);
        }
    }

    private sealed class FakeThemeService : IThemeService
    {
        public string CurrentTheme { get; private set; } = "Dark";
        public string? LastAppliedTheme { get; private set; }

        public event EventHandler<string>? ThemeChanged;

        public void ApplySavedTheme()
        {
        }

        public void ApplyTheme(string theme)
        {
            CurrentTheme = theme;
            LastAppliedTheme = theme;
            ThemeChanged?.Invoke(this, theme);
        }
    }

    private sealed class FakeGatewayStatePersistenceService : IGatewayStatePersistenceService
    {
        public GatewayUiStateModel Load() => new();

        public void Save(GatewayUiStateModel state)
        {
        }
    }
}
