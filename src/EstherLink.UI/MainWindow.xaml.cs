using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using EstherLink.Core.Configuration;
using EstherLink.Core.Networking;
using EstherLink.Ipc;

namespace EstherLink.UI;

public partial class MainWindow : Window
{
    private const string ServiceName = "EstherLink.Service";

    private readonly NamedPipeJsonClient _ipcClient = new(PipeNames.Control);
    private readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private string _lastAction = "Ready.";

    public MainWindow()
    {
        InitializeComponent();
        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        LoadAdapters();
        ServiceExePathTextBox.Text = GetDefaultServiceExePath();

        _statusTimer.Tick += async (_, _) => await RefreshStatusAsync();
        _statusTimer.Start();

        await RefreshStatusAsync();
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        _statusTimer.Stop();
    }

    private void LoadAdapters()
    {
        var adapters = NetworkAdapterCatalog.ListIpv4Adapters()
            .Select(x => new AdapterChoice(
                x.IfIndex,
                $"{x.Name} (IfIndex={x.IfIndex}) | IPv4={string.Join(",", x.IPv4Addresses)} | GW={(x.HasDefaultGateway ? "Yes" : "No")}"))
            .ToList();

        VpsNetworkComboBox.ItemsSource = adapters;
        OutgoingNetworkComboBox.ItemsSource = adapters;

        if (adapters.Count > 0)
        {
            VpsNetworkComboBox.SelectedIndex = 0;
            OutgoingNetworkComboBox.SelectedIndex = Math.Min(1, adapters.Count - 1);
        }
    }

    private async void VerifyLicenseButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!await PushConfigAsync())
            {
                return;
            }

            var response = await SendIpcAsync(IpcCommands.VerifyLicense);
            if (response?.Success != true)
            {
                return;
            }

            var payload = IpcJson.Deserialize<VerifyLicenseResponse>(response.JsonPayload);
            if (payload is null)
            {
                SetAction("License verification payload was invalid.");
            }
            else
            {
                SetAction(
                    payload.IsValid
                        ? $"License valid. Expires: {payload.ExpiresAtUtc:O}. Source: {(payload.FromCache ? "cache" : "online")}."
                        : $"License invalid: {payload.Error ?? "unknown error"}");
            }

            await RefreshStatusAsync();
        }
        catch (Exception ex)
        {
            SetAction(ex.Message);
            await RefreshStatusAsync();
        }
    }

    private async void UpdateWhitelistButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!await PushWhitelistAsync())
            {
                return;
            }

            SetAction("Whitelist updated.");
            await RefreshStatusAsync();
        }
        catch (Exception ex)
        {
            SetAction(ex.Message);
            await RefreshStatusAsync();
        }
    }

    private async void InstallStartServiceButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var exePath = ServiceExePathTextBox.Text.Trim();
            if (!File.Exists(exePath))
            {
                SetAction($"Service executable not found: {exePath}");
                await RefreshStatusAsync();
                return;
            }

            var installOk = await InstallOrStartWindowsServiceAsync(exePath);
            if (!installOk)
            {
                SetAction("Service install/start canceled or failed.");
                await RefreshStatusAsync();
                return;
            }

            await Task.Delay(1200);

            if (!await PushConfigAsync())
            {
                await RefreshStatusAsync();
                return;
            }

            if (!await PushWhitelistAsync())
            {
                await RefreshStatusAsync();
                return;
            }

            var startProxy = await SendIpcAsync(IpcCommands.StartProxy);
            if (startProxy?.Success == true)
            {
                SetAction("Windows service started and proxy start requested.");
            }

            await RefreshStatusAsync();
        }
        catch (Exception ex)
        {
            SetAction(ex.Message);
            await RefreshStatusAsync();
        }
    }

    private async void StopServiceButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await SendIpcAsync(IpcCommands.StopProxy, showErrors: false);
            var stopOk = await StopWindowsServiceAsync();
            SetAction(stopOk ? "Service stop requested." : "Service stop canceled or failed.");
            await RefreshStatusAsync();
        }
        catch (Exception ex)
        {
            SetAction(ex.Message);
            await RefreshStatusAsync();
        }
    }

    private async void RefreshStatusButton_Click(object sender, RoutedEventArgs e)
    {
        await RefreshStatusAsync();
    }

    private async Task<bool> PushConfigAsync()
    {
        var config = BuildConfig();
        var response = await SendIpcAsync(IpcCommands.SetConfig, new SetConfigRequest(config));
        return response?.Success == true;
    }

    private async Task<bool> PushWhitelistAsync()
    {
        var entries = WhitelistTextBox.Text
            .Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)
            .Select(x => x.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToList();

        var response = await SendIpcAsync(IpcCommands.UpdateWhitelist, new UpdateWhitelistRequest(entries));
        return response?.Success == true;
    }

    private async Task RefreshStatusAsync()
    {
        var serviceState = await QueryServiceStateAsync();
        var lines = new List<string>
        {
            $"ServiceState: {serviceState}"
        };

        var response = await SendIpcAsync(IpcCommands.GetStatus, showErrors: false);
        if (response?.Success == true)
        {
            var payload = IpcJson.Deserialize<StatusResponse>(response.JsonPayload);
            if (payload is not null)
            {
                var status = payload.Status;
                lines.Add($"ProxyRunning: {status.ProxyRunning}");
                lines.Add($"ProxyListenPort: {status.ProxyListenPort}");
                lines.Add($"WhitelistCount: {status.WhitelistCount}");
                lines.Add($"WhitelistAdapterIP: {status.WhitelistAdapterIp ?? "(none)"}");
                lines.Add($"DefaultAdapterIP: {status.DefaultAdapterIp ?? "(none)"}");
                lines.Add($"TunnelConnected: {status.TunnelConnected}");
                lines.Add($"TunnelLastConnectedAtUtc: {status.TunnelLastConnectedAtUtc:O}");
                lines.Add($"TunnelReconnectCount: {status.TunnelReconnectCount}");
                lines.Add($"TunnelLastError: {status.TunnelLastError ?? "(none)"}");
                lines.Add($"LicenseValid: {status.LicenseValid}");
                lines.Add($"LicenseFromCache: {status.LicenseFromCache}");
                lines.Add($"LicenseCheckedAtUtc: {status.LicenseCheckedAtUtc:O}");
                lines.Add($"LicenseExpiresAtUtc: {status.LicenseExpiresAtUtc:O}");
                lines.Add($"LastError: {status.LastError ?? "(none)"}");
            }
            else
            {
                lines.Add("ProxyStatus: invalid IPC payload.");
            }
        }
        else
        {
            lines.Add("ProxyStatus: unavailable (service not reachable over named pipe).");
        }

        lines.Add(string.Empty);
        lines.Add($"LastAction: {_lastAction}");
        StatusTextBlock.Text = string.Join(Environment.NewLine, lines);
    }

    private async Task<IpcResponse?> SendIpcAsync(string command, object? payload = null, bool showErrors = true)
    {
        try
        {
            var request = new IpcRequest(command, payload is null ? null : IpcJson.Serialize(payload));
            var response = await _ipcClient.SendAsync(request);
            if (!response.Success && showErrors)
            {
                SetAction($"Service error ({command}): {response.Error}");
            }

            return response;
        }
        catch (Exception ex)
        {
            if (showErrors)
            {
                SetAction($"IPC error ({command}): {ex.Message}");
            }

            return null;
        }
    }

    private ServiceConfig BuildConfig()
    {
        if (!int.TryParse(VpsPortTextBox.Text.Trim(), out var vpsPort) || vpsPort <= 0)
        {
            throw new InvalidOperationException("VPS port must be a positive integer.");
        }

        if (!int.TryParse(ProxyPortTextBox.Text.Trim(), out var proxyPort) || proxyPort <= 0)
        {
            throw new InvalidOperationException("Proxy listen port must be a positive integer.");
        }

        if (!int.TryParse(TunnelSshPortTextBox.Text.Trim(), out var tunnelSshPort) || tunnelSshPort <= 0)
        {
            throw new InvalidOperationException("Tunnel SSH port must be a positive integer.");
        }

        if (!int.TryParse(TunnelRemotePortTextBox.Text.Trim(), out var tunnelRemotePort) || tunnelRemotePort <= 0)
        {
            throw new InvalidOperationException("Tunnel remote port must be a positive integer.");
        }

        var vpsAdapter = VpsNetworkComboBox.SelectedItem as AdapterChoice;
        var outgoingAdapter = OutgoingNetworkComboBox.SelectedItem as AdapterChoice;
        return new ServiceConfig
        {
            VpsHost = VpsHostTextBox.Text.Trim(),
            VpsPort = vpsPort,
            LocalProxyListenPort = proxyPort,
            WhitelistAdapterIfIndex = vpsAdapter?.IfIndex ?? -1,
            DefaultAdapterIfIndex = outgoingAdapter?.IfIndex ?? -1,
            TunnelEnabled = TunnelEnabledCheckBox.IsChecked == true,
            TunnelHost = TunnelHostTextBox.Text.Trim(),
            TunnelSshPort = tunnelSshPort,
            TunnelRemotePort = tunnelRemotePort,
            TunnelUser = TunnelUserTextBox.Text.Trim(),
            TunnelPrivateKeyPath = TunnelKeyPathTextBox.Text.Trim(),
            LicenseServerUrl = LicenseEndpointTextBox.Text.Trim(),
            LicenseKey = LicenseKeyTextBox.Text.Trim()
        };
    }

    private static string GetDefaultServiceExePath()
    {
        var candidate = Path.GetFullPath(
            Path.Combine(
                AppContext.BaseDirectory,
                "..",
                "..",
                "..",
                "..",
                "EstherLink.Service",
                "bin",
                "Debug",
                "net8.0-windows",
                "EstherLink.Service.exe"));

        return candidate;
    }

    private static async Task<string> QueryServiceStateAsync()
    {
        var result = await RunProcessCaptureAsync("sc.exe", $"query {ServiceName}");
        if (result.ExitCode != 0)
        {
            return "Not Installed";
        }

        var stateLine = result.StdOut
            .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(x => x.TrimStart().StartsWith("STATE", StringComparison.OrdinalIgnoreCase));

        if (string.IsNullOrWhiteSpace(stateLine))
        {
            return "Unknown";
        }

        if (stateLine.Contains("RUNNING", StringComparison.OrdinalIgnoreCase))
        {
            return "Running";
        }

        if (stateLine.Contains("STOPPED", StringComparison.OrdinalIgnoreCase))
        {
            return "Stopped";
        }

        if (stateLine.Contains("START_PENDING", StringComparison.OrdinalIgnoreCase))
        {
            return "Start Pending";
        }

        if (stateLine.Contains("STOP_PENDING", StringComparison.OrdinalIgnoreCase))
        {
            return "Stop Pending";
        }

        return stateLine.Trim();
    }

    private async Task<bool> InstallOrStartWindowsServiceAsync(string exePath)
    {
        var escapedPath = exePath.Replace("'", "''", StringComparison.Ordinal);
        var script = $@"
$ErrorActionPreference = 'Stop'
$serviceName = '{ServiceName}'
$binPath = '{escapedPath}'
if (-not (Get-Service -Name $serviceName -ErrorAction SilentlyContinue)) {{
    New-Service -Name $serviceName -DisplayName 'EstherLink Service' -BinaryPathName $binPath -StartupType Automatic
}}
$svc = Get-Service -Name $serviceName
if ($svc.Status -ne 'Running') {{
    Start-Service -Name $serviceName
}}
";

        return await RunElevatedPowerShellScriptAsync(script);
    }

    private async Task<bool> StopWindowsServiceAsync()
    {
        var script = $@"
$ErrorActionPreference = 'Stop'
$serviceName = '{ServiceName}'
if (Get-Service -Name $serviceName -ErrorAction SilentlyContinue) {{
    Stop-Service -Name $serviceName -Force
}}
";

        return await RunElevatedPowerShellScriptAsync(script);
    }

    private static async Task<bool> RunElevatedPowerShellScriptAsync(string script)
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"estherlink-{Guid.NewGuid():N}.ps1");
        await File.WriteAllTextAsync(scriptPath, script, Encoding.UTF8);

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"",
                UseShellExecute = true,
                Verb = "runas"
            };

            using var process = Process.Start(psi);
            if (process is null)
            {
                return false;
            }

            await process.WaitForExitAsync();
            return process.ExitCode == 0;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return false;
        }
        finally
        {
            try
            {
                File.Delete(scriptPath);
            }
            catch
            {
            }
        }
    }

    private static async Task<ProcessResult> RunProcessCaptureAsync(string fileName, string arguments)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi);
        if (process is null)
        {
            return new ProcessResult(-1, string.Empty, "Failed to start process.");
        }

        var stdOutTask = process.StandardOutput.ReadToEndAsync();
        var stdErrTask = process.StandardError.ReadToEndAsync();

        await process.WaitForExitAsync();

        return new ProcessResult(
            process.ExitCode,
            await stdOutTask,
            await stdErrTask);
    }

    private void SetAction(string message)
    {
        _lastAction = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} {message}";
    }

    private sealed record AdapterChoice(int IfIndex, string Display);
    private sealed record ProcessResult(int ExitCode, string StdOut, string StdErr);
}
