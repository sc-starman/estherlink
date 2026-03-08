using OmniRelay.UI.Models;
using OmniRelay.UI.Views.Pages;
using System.Windows.Controls;

namespace OmniRelay.UI.Services;

public sealed class NavigationService : INavigationService
{
    private readonly List<NavigationItemModel> _items =
    [
        new() { Route = "dashboard", Title = "Dashboard", IconGlyph = "\uE80F" },
        new() { Route = "relay", Title = "Relay Management", IconGlyph = "\uE968" },
        new() { Route = "gateway", Title = "Gateway Management", IconGlyph = "\uE9D9" },
        new() { Route = "whitelist", Title = "Whitelists", IconGlyph = "\uE73E" },
        new() { Route = "license", Title = "License", IconGlyph = "\uE8A7" },
        new() { Route = "settings", Title = "Settings", IconGlyph = "\uE713" },
        new() { Route = "logs", Title = "Logs", IconGlyph = "\uE8A5" }
    ];

    private readonly Dictionary<string, Type> _routes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["dashboard"] = typeof(DashboardPage),
        ["relay"] = typeof(RelayManagementPage),
        ["gateway"] = typeof(GatewayManagementPage),
        ["whitelist"] = typeof(WhitelistPage),
        ["license"] = typeof(LicensePage),
        ["logs"] = typeof(LogsPage),
        ["settings"] = typeof(SettingsPage)
    };

    private Frame? _frame;
    private IServiceProvider? _serviceProvider;
    private string _currentRoute = "license";

    public IReadOnlyList<NavigationItemModel> Items => _items;
    public string CurrentRoute => _currentRoute;
    public event EventHandler<string>? RouteChanged;

    public void Initialize(Frame frame, IServiceProvider serviceProvider)
    {
        _frame = frame;
        _serviceProvider = serviceProvider;
        Navigate(_currentRoute);
    }

    public void Navigate(string route)
    {
        if (_frame is null || _serviceProvider is null)
        {
            return;
        }

        if (!_routes.TryGetValue(route, out var pageType))
        {
            return;
        }

        if (_serviceProvider.GetService(pageType) is not Page page)
        {
            return;
        }

        _frame.Navigate(page);
        _currentRoute = route;
        RouteChanged?.Invoke(this, _currentRoute);
    }
}
