using System.Windows;
using EstherLink.UI.Services;
using EstherLink.UI.ViewModels;
using EstherLink.UI.Views.Pages;
using Microsoft.Extensions.DependencyInjection;

namespace EstherLink.UI;

public partial class App : Application
{
    private ServiceProvider? _serviceProvider;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var services = new ServiceCollection();
        ConfigureServices(services);
        _serviceProvider = services.BuildServiceProvider();

        var themeService = _serviceProvider.GetRequiredService<IThemeService>();
        themeService.ApplySavedTheme();

        var mainWindow = _serviceProvider.GetRequiredService<MainWindow>();
        mainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _serviceProvider?.Dispose();
        base.OnExit(e);
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        services.AddSingleton<MainWindow>();
        services.AddSingleton<MainWindowViewModel>();

        services.AddSingleton<GatewayStateStore>();
        services.AddSingleton<IGatewayClientService, GatewayClientService>();
        services.AddSingleton<IServiceControlService, ServiceControlService>();
        services.AddSingleton<ISshHostKeyTrustStore, SshHostKeyTrustStore>();
        services.AddSingleton<IGatewayBundleResolverService, GatewayBundleResolverService>();
        services.AddSingleton<IGatewayDeploymentService, GatewayDeploymentService>();
        services.AddSingleton<IGatewayHealthService, GatewayDeploymentService>();
        services.AddSingleton<IDeploymentProgressAggregator, DeploymentProgressAggregator>();
        services.AddSingleton<ISudoSessionSecretCache, SudoSessionSecretCache>();
        services.AddSingleton<ILogReaderService, LogReaderService>();
        services.AddSingleton<IUiSettingsService, UiSettingsService>();
        services.AddSingleton<IThemeService, ThemeService>();
        services.AddSingleton<INavigationService, NavigationService>();
        services.AddSingleton<GatewayOrchestratorService>();

        services.AddTransient<DashboardViewModel>();
        services.AddTransient<NetworkConfigViewModel>();
        services.AddTransient<WhitelistViewModel>();
        services.AddTransient<ServiceStatusViewModel>();
        services.AddTransient<LicenseViewModel>();
        services.AddTransient<LogsViewModel>();
        services.AddTransient<SettingsViewModel>();

        services.AddTransient<DashboardPage>();
        services.AddTransient<NetworkConfigPage>();
        services.AddTransient<WhitelistPage>();
        services.AddTransient<ServiceStatusPage>();
        services.AddTransient<LicensePage>();
        services.AddTransient<LogsPage>();
        services.AddTransient<SettingsPage>();
    }
}
