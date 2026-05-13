using System.Collections.Specialized;
using System.ComponentModel;
using System.Drawing;
using System.Windows;
using Forms = System.Windows.Forms;

namespace OmniRelay.UI.Services;

public sealed class TrayIconService : ITrayIconService
{
    private readonly GatewayStateStore _state;
    private readonly IServiceControlService _serviceControl;
    private readonly GatewayOrchestratorService _orchestrator;

    private Forms.NotifyIcon? _notifyIcon;
    private Forms.ToolStripMenuItem? _activeRelaysMenuItem;
    private Forms.ToolStripMenuItem? _restartRelayMenuItem;
    private Forms.ToolStripMenuItem? _quitMenuItem;
    private bool _isInitialized;
    private bool _isBusy;
    private bool _isDisposing;

    public TrayIconService(
        GatewayStateStore state,
        IServiceControlService serviceControl,
        GatewayOrchestratorService orchestrator)
    {
        _state = state;
        _serviceControl = serviceControl;
        _orchestrator = orchestrator;
    }

    public bool AllowWindowClose { get; private set; }

    public void Initialize()
    {
        if (_isInitialized)
        {
            return;
        }

        var menu = new Forms.ContextMenuStrip();
        _activeRelaysMenuItem = new Forms.ToolStripMenuItem("Active Relays: 0") { Enabled = false };
        var openMenuItem = new Forms.ToolStripMenuItem("Open OmniRelay");
        _restartRelayMenuItem = new Forms.ToolStripMenuItem("Restart Relay");
        _quitMenuItem = new Forms.ToolStripMenuItem("Quit");

        openMenuItem.Click += (_, _) => OpenMainWindow();
        _restartRelayMenuItem.Click += async (_, _) => await RestartRelayAsync();
        _quitMenuItem.Click += async (_, _) => await QuitAsync();

        menu.Items.Add(_activeRelaysMenuItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(openMenuItem);
        menu.Items.Add(_restartRelayMenuItem);
        menu.Items.Add(_quitMenuItem);

        _notifyIcon = new Forms.NotifyIcon
        {
            Text = "OmniRelay",
            Visible = true,
            Icon = ResolveTrayIcon(),
            ContextMenuStrip = menu
        };
        _notifyIcon.DoubleClick += (_, _) => OpenMainWindow();

        _state.PropertyChanged += OnStatePropertyChanged;
        _state.Relays.CollectionChanged += OnRelaysCollectionChanged;
        UpdateActiveRelayCountMenu();
        _isInitialized = true;
    }

    private static Icon ResolveTrayIcon()
    {
        try
        {
            var processPath = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(processPath))
            {
                var icon = Icon.ExtractAssociatedIcon(processPath);
                if (icon is not null)
                {
                    return icon;
                }
            }
        }
        catch
        {
        }

        return SystemIcons.Application;
    }

    private void OnStatePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(GatewayStateStore.Relays))
        {
            UpdateActiveRelayCountMenu();
        }
    }

    private void OnRelaysCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        UpdateActiveRelayCountMenu();
    }

    private void UpdateActiveRelayCountMenu()
    {
        if (_activeRelaysMenuItem is null)
        {
            return;
        }

        var activeCount = _state.Relays.Count(static relay => relay is not null && relay.Enabled);
        _activeRelaysMenuItem.Text = $"Active Relays: {activeCount}";
        if (_notifyIcon is not null)
        {
            _notifyIcon.Text = $"OmniRelay - Active Relays: {activeCount}";
        }
    }

    private static void OpenMainWindow()
    {
        var app = Application.Current;
        if (app?.MainWindow is not Window mainWindow)
        {
            return;
        }

        if (!mainWindow.IsVisible)
        {
            mainWindow.Show();
        }

        if (mainWindow.WindowState == WindowState.Minimized)
        {
            mainWindow.WindowState = WindowState.Normal;
        }

        mainWindow.Activate();
        mainWindow.Topmost = true;
        mainWindow.Topmost = false;
        mainWindow.Focus();
    }

    private async Task RestartRelayAsync()
    {
        if (_isBusy || _isDisposing)
        {
            return;
        }

        try
        {
            SetBusy(true);
            var stopResult = await _orchestrator.StopServiceAsync();
            if (!stopResult.Success)
            {
                _state.LastAction = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} {stopResult.Message}";
                return;
            }

            var installed = await _serviceControl.InstallOrStartWindowsServiceAsync();
            if (!installed)
            {
                var serviceState = await _serviceControl.QueryServiceStateAsync();
                _state.LastAction = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} Service restart canceled or failed. Service state: {serviceState}.";
                return;
            }

            var startProxy = await _orchestrator.StartProxyAsync();
            _state.LastAction = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} {startProxy.Message}";
            await _orchestrator.RefreshStatusAsync();
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task QuitAsync()
    {
        if (_isBusy || _isDisposing)
        {
            return;
        }

        try
        {
            SetBusy(true);
            var stopResult = await _orchestrator.StopServiceAsync();
            _state.LastAction = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} {stopResult.Message}";
        }
        finally
        {
            AllowWindowClose = true;
            SetBusy(false);
            Application.Current?.Shutdown();
        }
    }

    private void SetBusy(bool value)
    {
        _isBusy = value;
        if (_restartRelayMenuItem is not null)
        {
            _restartRelayMenuItem.Enabled = !value;
        }

        if (_quitMenuItem is not null)
        {
            _quitMenuItem.Enabled = !value;
        }
    }

    public void Dispose()
    {
        if (_isDisposing)
        {
            return;
        }

        _isDisposing = true;
        _state.PropertyChanged -= OnStatePropertyChanged;
        _state.Relays.CollectionChanged -= OnRelaysCollectionChanged;

        if (_notifyIcon is not null)
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _notifyIcon = null;
        }
    }
}
