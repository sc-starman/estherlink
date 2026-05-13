using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OmniRelay.UI.Models;
using OmniRelay.UI.Services;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows.Threading;

namespace OmniRelay.UI.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    private const string LicenseRoute = "license";

    private readonly INavigationService _navigationService;
    private readonly GatewayOrchestratorService _orchestrator;
    private readonly GatewayStateStore _state;
    private readonly IThemeService _themeService;
    private readonly IUiSettingsService _uiSettingsService;
    private readonly IServiceControlService _serviceControl;
    private readonly DispatcherTimer _statusTimer;

    public MainWindowViewModel(
        INavigationService navigationService,
        GatewayOrchestratorService orchestrator,
        GatewayStateStore state,
        IThemeService themeService,
        IUiSettingsService uiSettingsService,
        IServiceControlService serviceControl)
    {
        _navigationService = navigationService;
        _orchestrator = orchestrator;
        _state = state;
        _themeService = themeService;
        _uiSettingsService = uiSettingsService;
        _serviceControl = serviceControl;

        NavigationItems = new ObservableCollection<NavigationItemViewModel>(
            _navigationService.Items.Select(x => new NavigationItemViewModel(x)));

        _navigationService.RouteChanged += OnRouteChanged;
        _state.PropertyChanged += OnStateChanged;
        _themeService.ThemeChanged += (_, _) => ThemeLabel = _themeService.CurrentTheme;
        _uiSettingsService.SettingsChanged += OnSettingsChanged;

        ThemeLabel = _themeService.CurrentTheme;
        _statusTimer = new DispatcherTimer { Interval = GetRefreshInterval() };
        _statusTimer.Tick += async (_, _) => await _orchestrator.RefreshStatusAsync();
    }

    public ObservableCollection<NavigationItemViewModel> NavigationItems { get; }

    [ObservableProperty]
    private string pageTitle = "License Management";

    [ObservableProperty]
    private string pageIconGlyph = "\uE8A7";

    [ObservableProperty]
    private string statusSummary = "Ready.";

    [ObservableProperty]
    private string themeLabel = "Dark";

    [ObservableProperty]
    private string sidebarVersionText = $"Version {ResolveInstallerPackageVersion()}";

    [ObservableProperty]
    private bool isRelayServiceRunning;

    [ObservableProperty]
    private bool isRelayServiceActionBusy;

    public async Task InitializeAsync()
    {
        _orchestrator.Initialize();
        var settings = _uiSettingsService.Load();

        await _orchestrator.RefreshStatusAsync();
        IsRelayServiceRunning = string.Equals(_state.ServiceState, "Running", StringComparison.OrdinalIgnoreCase);
        _navigationService.Navigate(IsLicenseActivated() ? "relays" : "license");
        UpdateNavigationLockState();
        UpdateStatusSummary();
        _statusTimer.Interval = GetRefreshInterval();
        _statusTimer.Start();
    }

    [RelayCommand]
    private void Navigate(string? route)
    {
        if (string.IsNullOrWhiteSpace(route))
        {
            return;
        }

        if (RequiresLicenseActivation(route) && !IsLicenseActivated())
        {
            _state.LastAction = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} License activation is required before accessing this page.";
            _navigationService.Navigate(LicenseRoute);
            UpdateStatusSummary();
            return;
        }

        _navigationService.Navigate(route);
    }

    [RelayCommand]
    private void ToggleTheme()
    {
        var next = string.Equals(_themeService.CurrentTheme, "Dark", StringComparison.OrdinalIgnoreCase)
            ? "Light"
            : "Dark";
        _themeService.ApplyTheme(next);
        ThemeLabel = _themeService.CurrentTheme;
    }

    [RelayCommand]
    private async Task ToggleRelayServiceAsync()
    {
        if (IsRelayServiceActionBusy)
        {
            return;
        }

        try
        {
            IsRelayServiceActionBusy = true;
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

            _state.LastAction = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} {result.Message}";
            await _orchestrator.RefreshStatusAsync();
            IsRelayServiceRunning = string.Equals(_state.ServiceState, "Running", StringComparison.OrdinalIgnoreCase);
            UpdateStatusSummary();
        }
        finally
        {
            IsRelayServiceActionBusy = false;
        }
    }

    [RelayCommand]
    private async Task ReinstallStartRelayServiceAsync()
    {
        if (IsRelayServiceActionBusy)
        {
            return;
        }

        try
        {
            IsRelayServiceActionBusy = true;
            await _orchestrator.StopServiceAsync();
            await _serviceControl.UninstallWindowsServiceAsync();
            var installed = await _serviceControl.InstallOrStartWindowsServiceAsync();
            if (!installed)
            {
                var state = await _serviceControl.QueryServiceStateAsync();
                _state.LastAction = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} Service reinstall/start canceled or failed. Service state: {state}.";
            }
            else
            {
                var startProxy = await _orchestrator.StartProxyAsync();
                _state.LastAction = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} {startProxy.Message}";
            }

            await _orchestrator.RefreshStatusAsync();
            IsRelayServiceRunning = string.Equals(_state.ServiceState, "Running", StringComparison.OrdinalIgnoreCase);
            UpdateStatusSummary();
        }
        finally
        {
            IsRelayServiceActionBusy = false;
        }
    }

    private void OnRouteChanged(object? sender, string route)
    {
        if (RequiresLicenseActivation(route) && !IsLicenseActivated())
        {
            if (!string.Equals(_navigationService.CurrentRoute, LicenseRoute, StringComparison.OrdinalIgnoreCase))
            {
                _navigationService.Navigate(LicenseRoute);
            }

            route = LicenseRoute;
        }

        foreach (var item in NavigationItems)
        {
            item.IsActive = string.Equals(item.Item.Route, route, StringComparison.OrdinalIgnoreCase);
        }
        UpdateNavigationLockState();
        PageIconGlyph = NavigationItems
            .FirstOrDefault(x => string.Equals(x.Item.Route, route, StringComparison.OrdinalIgnoreCase))
            ?.Item.IconGlyph ?? "\uE72E";

        PageTitle = route switch
        {
            "relays" => "Relays",
            "license" => "License Management",
            "logs" => "Logs",
            _ => "OmniRelay"
        };
    }

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(GatewayStateStore.Status) or nameof(GatewayStateStore.AppStatus) or nameof(GatewayStateStore.ServiceState) or nameof(GatewayStateStore.LastAction) or nameof(GatewayStateStore.LicenseActivated))
        {
            IsRelayServiceRunning = string.Equals(_state.ServiceState, "Running", StringComparison.OrdinalIgnoreCase);
            UpdateStatusSummary();
            UpdateNavigationLockState();

            if (e.PropertyName is nameof(GatewayStateStore.Status) &&
                RequiresLicenseActivation(_navigationService.CurrentRoute) &&
                !IsLicenseActivated())
            {
                _navigationService.Navigate(LicenseRoute);
            }
        }
    }

    private void UpdateStatusSummary()
    {
        var status = _state.Status;
        var licenseValid = IsLicenseActivated();
        var appStatus = _state.AppStatus;
        StatusSummary = appStatus is not null
            ? $"Service: {_state.ServiceState} | License: {(licenseValid ? "Valid" : "Invalid")} | Relays: {appStatus.Relays.Count} | Last: {_state.LastAction}"
            : status is null
            ? $"Service: {_state.ServiceState} | IPC: offline | Last: {_state.LastAction}"
            : $"Service: {_state.ServiceState} | License: {(licenseValid ? "Valid" : "Invalid")} | Tunnel: {(status.TunnelConnected ? "Connected" : "Disconnected")} | Last: {_state.LastAction}";
    }

    private TimeSpan GetRefreshInterval()
    {
        var settings = _uiSettingsService.Load();
        var seconds = settings.RefreshIntervalSeconds <= 0 ? 5 : settings.RefreshIntervalSeconds;
        return TimeSpan.FromSeconds(seconds);
    }

    private void OnSettingsChanged(object? sender, UiSettingsModel settings)
    {
        _statusTimer.Interval = TimeSpan.FromSeconds(settings.RefreshIntervalSeconds <= 0 ? 5 : settings.RefreshIntervalSeconds);
    }

    private bool IsLicenseActivated()
    {
        var status = _state.Status;
        if (status is not null &&
            status.LicenseCheckedAtUtc is not null &&
            status.LicenseValid)
        {
            return true;
        }

        if (!_state.LicenseActivated)
        {
            return false;
        }

        if (_state.LicenseActivatedExpiresAtUtc is null)
        {
            return true;
        }

        return _state.LicenseActivatedExpiresAtUtc > DateTimeOffset.UtcNow;
    }

    private static bool RequiresLicenseActivation(string route)
    {
        return !string.Equals(route, LicenseRoute, StringComparison.OrdinalIgnoreCase);
    }

    private void UpdateNavigationLockState()
    {
        var unlocked = IsLicenseActivated();
        foreach (var item in NavigationItems)
        {
            item.IsEnabled = unlocked ||
                             string.Equals(item.Item.Route, LicenseRoute, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static string ResolveInstallerPackageVersion()
    {
        try
        {
            var metadataDir = Path.Combine(AppContext.BaseDirectory, "InstallerMetadata");
            var metadataPath = Path.Combine(metadataDir, "Product.metadata.xml");
            if (!File.Exists(metadataPath))
            {
                // Backward-compatibility for existing installs built before metadata filename change.
                metadataPath = Path.Combine(metadataDir, "Product.wxs");
            }

            if (!File.Exists(metadataPath))
            {
                return "unknown";
            }

            var contents = File.ReadAllText(metadataPath);
            var match = Regex.Match(
                contents,
                "<Package\\b[^>]*\\bVersion\\s*=\\s*\"([^\"]+)\"",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

            return match.Success ? match.Groups[1].Value : "unknown";
        }
        catch
        {
            return "unknown";
        }
    }
}
