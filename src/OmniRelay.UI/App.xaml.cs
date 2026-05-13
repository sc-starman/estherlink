using System.Windows;
using OmniRelay.UI.Services;
using OmniRelay.UI.ViewModels;
using OmniRelay.UI.Views.Pages;
using Microsoft.Extensions.DependencyInjection;

namespace OmniRelay.UI;

public partial class App : System.Windows.Application
{
    private ServiceProvider? _serviceProvider;
    private ITrayIconService? _trayIconService;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(
                args.Exception.Message,
                "OmniRelay UI error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            args.Handled = true;
        };

        var services = new ServiceCollection();
        ConfigureServices(services);
        _serviceProvider = services.BuildServiceProvider();

        var themeService = _serviceProvider.GetRequiredService<IThemeService>();
        themeService.ApplySavedTheme();

        _trayIconService = _serviceProvider.GetRequiredService<ITrayIconService>();
        _trayIconService.Initialize();

        var mainWindow = _serviceProvider.GetRequiredService<MainWindow>();
        mainWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _trayIconService?.Dispose();
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
        services.AddSingleton<IGatewayDeploymentService, GatewayDeploymentService>();
        services.AddSingleton<IGatewayHealthService, GatewayDeploymentService>();
        services.AddSingleton<IDeploymentProgressAggregator, DeploymentProgressAggregator>();
        services.AddSingleton<ISudoSessionSecretCache, SudoSessionSecretCache>();
        services.AddSingleton<ILogReaderService, LogReaderService>();
        services.AddSingleton<IUiSettingsService, UiSettingsService>();
        services.AddSingleton<IGatewayStatePersistenceService, GatewayStatePersistenceService>();
        services.AddSingleton<IThemeService, ThemeService>();
        services.AddSingleton<INavigationService, NavigationService>();
        services.AddSingleton<GatewayOrchestratorService>();
        services.AddSingleton<ITrayIconService, TrayIconService>();

        services.AddTransient<RelaysViewModel>();
        services.AddTransient<LicenseViewModel>();
        services.AddTransient<LogsViewModel>();

        services.AddTransient<RelaysPage>();
        services.AddTransient<LicensePage>();
        services.AddTransient<LogsPage>();
    }
}
