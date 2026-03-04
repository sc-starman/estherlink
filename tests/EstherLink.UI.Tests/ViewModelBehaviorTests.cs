using EstherLink.Core.Configuration;
using EstherLink.Core.Status;
using EstherLink.Ipc;
using EstherLink.UI.Models;
using EstherLink.UI.Services;
using EstherLink.UI.ViewModels;

namespace EstherLink.UI.Tests;

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
                WhitelistCount = 7
            }
        };

        var serviceControl = new FakeServiceControlService
        {
            QueryDelay = TimeSpan.FromMilliseconds(120),
            ServiceState = "Running"
        };

        var orchestrator = new GatewayOrchestratorService(state, gatewayClient, serviceControl);
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
        var state = new GatewayStateStore
        {
            WhitelistText = "1.2.3.0/24\nnot-a-cidr"
        };

        var gatewayClient = new FakeGatewayClientService();
        var serviceControl = new FakeServiceControlService();
        var orchestrator = new GatewayOrchestratorService(state, gatewayClient, serviceControl);
        var vm = new WhitelistViewModel(orchestrator, state);

        await vm.UpdateWhitelistCommand.ExecuteAsync(null);

        Assert.Equal("Whitelist contains invalid entries.", vm.Feedback);
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
            RefreshIntervalSeconds = 15,
            CompactMode = true
        };

        vm.SaveCommand.Execute(null);

        Assert.NotNull(settingsService.LastSaved);
        Assert.Equal("Light", settingsService.LastSaved!.Theme);
        Assert.Equal(15, settingsService.LastSaved.RefreshIntervalSeconds);
        Assert.True(settingsService.LastSaved.CompactMode);
        Assert.Equal("Light", themeService.LastAppliedTheme);
        Assert.Equal("Settings saved.", vm.Feedback);
    }

    private sealed class FakeGatewayClientService : IGatewayClientService
    {
        public int UpdateWhitelistCallCount { get; private set; }
        public TimeSpan GetStatusDelay { get; set; } = TimeSpan.Zero;
        public GatewayStatus StatusToReturn { get; set; } = new();

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

        public Task<IpcResponse?> UpdateWhitelistAsync(IReadOnlyList<string> entries, CancellationToken cancellationToken = default)
        {
            UpdateWhitelistCallCount++;
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

        public Task<bool> InstallOrStartWindowsServiceAsync(string exePath, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(true);
        }

        public Task<bool> StopWindowsServiceAsync(CancellationToken cancellationToken = default)
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
}