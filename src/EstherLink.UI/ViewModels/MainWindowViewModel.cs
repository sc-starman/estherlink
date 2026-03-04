using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EstherLink.UI.Models;
using EstherLink.UI.Services;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Threading;

namespace EstherLink.UI.ViewModels;

public partial class MainWindowViewModel : ObservableObject
{
    private readonly INavigationService _navigationService;
    private readonly GatewayOrchestratorService _orchestrator;
    private readonly GatewayStateStore _state;
    private readonly IThemeService _themeService;
    private readonly IUiSettingsService _uiSettingsService;
    private readonly DispatcherTimer _statusTimer;

    public MainWindowViewModel(
        INavigationService navigationService,
        GatewayOrchestratorService orchestrator,
        GatewayStateStore state,
        IThemeService themeService,
        IUiSettingsService uiSettingsService)
    {
        _navigationService = navigationService;
        _orchestrator = orchestrator;
        _state = state;
        _themeService = themeService;
        _uiSettingsService = uiSettingsService;

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
    private string statusSummary = "Ready.";

    [ObservableProperty]
    private string themeLabel = "Dark";

    [ObservableProperty]
    private bool compactMode;

    public async Task InitializeAsync()
    {
        _orchestrator.Initialize();
        var settings = _uiSettingsService.Load();
        CompactMode = settings.CompactMode;

        _navigationService.Navigate("license");
        await _orchestrator.RefreshStatusAsync();
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

    private void OnRouteChanged(object? sender, string route)
    {
        foreach (var item in NavigationItems)
        {
            item.IsActive = string.Equals(item.Item.Route, route, StringComparison.OrdinalIgnoreCase);
        }

        PageTitle = route switch
        {
            "dashboard" => "Dashboard",
            "network" => "Network Configuration",
            "whitelist" => "Whitelist Rules",
            "service" => "Service Status",
            "license" => "License Management",
            "logs" => "Logs",
            "settings" => "Settings",
            _ => "OmniRelay"
        };
    }

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(GatewayStateStore.Status) or nameof(GatewayStateStore.ServiceState) or nameof(GatewayStateStore.LastAction))
        {
            UpdateStatusSummary();
        }
    }

    private void UpdateStatusSummary()
    {
        var status = _state.Status;
        StatusSummary = status is null
            ? $"Service: {_state.ServiceState} | IPC: offline | Last: {_state.LastAction}"
            : $"Service: {_state.ServiceState} | Proxy: {(status.ProxyRunning ? "Running" : "Stopped")} | License: {(status.LicenseValid ? "Valid" : "Invalid")} | Tunnel: {(status.TunnelConnected ? "Connected" : "Disconnected")} | Last: {_state.LastAction}";
    }

    private TimeSpan GetRefreshInterval()
    {
        var settings = _uiSettingsService.Load();
        var seconds = settings.RefreshIntervalSeconds <= 0 ? 5 : settings.RefreshIntervalSeconds;
        return TimeSpan.FromSeconds(seconds);
    }

    private void OnSettingsChanged(object? sender, UiSettingsModel settings)
    {
        CompactMode = settings.CompactMode;
        _statusTimer.Interval = TimeSpan.FromSeconds(settings.RefreshIntervalSeconds <= 0 ? 5 : settings.RefreshIntervalSeconds);
    }
}
