using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace EstherLink.UI.Services;

public sealed class ServiceControlService : IServiceControlService
{
    private const string ServiceName = "EstherLink.Service";
    private const string ServiceExeName = "EstherLink.Service.exe";

    public async Task<string> QueryServiceStateAsync(CancellationToken cancellationToken = default)
    {
        var result = await RunProcessCaptureAsync("sc.exe", $"query {ServiceName}", cancellationToken);
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

    public async Task<bool> InstallOrStartWindowsServiceAsync(CancellationToken cancellationToken = default)
    {
        var serviceInstalled = await IsServiceInstalledAsync(cancellationToken);
        var serviceExePath = serviceInstalled ? null : ResolveServiceExecutablePath();
        if (!serviceInstalled && string.IsNullOrWhiteSpace(serviceExePath))
        {
            return false;
        }

        var escapedPath = (serviceExePath ?? string.Empty).Replace("'", "''", StringComparison.Ordinal);
        var createBlock = serviceInstalled
            ? string.Empty
            : @"
$binPath = '" + escapedPath + @"'
$binaryPathName = '""' + $binPath + '""'
New-Service -Name $serviceName -DisplayName 'EstherLink Service' -BinaryPathName $binaryPathName -StartupType Automatic
";

        var script = $@"
$ErrorActionPreference = 'Stop'
$serviceName = '{ServiceName}'
if (-not (Get-Service -Name $serviceName -ErrorAction SilentlyContinue)) {{
{createBlock}
}}
$svc = Get-Service -Name $serviceName
if ($svc.Status -ne 'Running') {{
    Start-Service -Name $serviceName
}}
";

        return await RunElevatedPowerShellScriptAsync(script, cancellationToken);
    }

    public async Task<bool> StopWindowsServiceAsync(CancellationToken cancellationToken = default)
    {
        var script = $@"
$ErrorActionPreference = 'Stop'
$serviceName = '{ServiceName}'
if (Get-Service -Name $serviceName -ErrorAction SilentlyContinue) {{
    Stop-Service -Name $serviceName -Force
}}
";

        return await RunElevatedPowerShellScriptAsync(script, cancellationToken);
    }

    private static async Task<bool> RunElevatedPowerShellScriptAsync(string script, CancellationToken cancellationToken)
    {
        var scriptPath = Path.Combine(Path.GetTempPath(), $"estherlink-ui-{Guid.NewGuid():N}.ps1");
        await File.WriteAllTextAsync(scriptPath, script, Encoding.UTF8, cancellationToken);

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

            await process.WaitForExitAsync(cancellationToken);
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

    private static async Task<ProcessResult> RunProcessCaptureAsync(
        string fileName,
        string arguments,
        CancellationToken cancellationToken)
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

        var stdOutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stdErrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken);

        return new ProcessResult(
            process.ExitCode,
            await stdOutTask,
            await stdErrTask);
    }

    private static async Task<bool> IsServiceInstalledAsync(CancellationToken cancellationToken)
    {
        var result = await RunProcessCaptureAsync("sc.exe", $"query {ServiceName}", cancellationToken);
        return result.ExitCode == 0;
    }

    private static string? ResolveServiceExecutablePath()
    {
        var env = Environment.GetEnvironmentVariable("ESTHERLINK_SERVICE_EXE_PATH");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env))
        {
            return Path.GetFullPath(env);
        }

        var baseDir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDir, ServiceExeName),
            Path.Combine(baseDir, "service", ServiceExeName),
            Path.Combine(baseDir, "..", ServiceExeName),
            Path.Combine(baseDir, "..", "service", ServiceExeName),
            Path.Combine(baseDir, "..", "..", ServiceExeName),
            Path.Combine(baseDir, "..", "..", "service", ServiceExeName),
            Path.Combine(baseDir, "..", "..", "..", "..", "EstherLink.Service", "bin", "Debug", "net8.0-windows", ServiceExeName),
            Path.Combine(baseDir, "..", "..", "..", "..", "EstherLink.Service", "bin", "Release", "net8.0-windows", ServiceExeName),
            Path.Combine(baseDir, "..", "..", "..", "..", "out", "service", ServiceExeName)
        };

        foreach (var candidate in candidates)
        {
            var fullPath = Path.GetFullPath(candidate);
            if (File.Exists(fullPath))
            {
                return fullPath;
            }
        }

        return null;
    }

    private sealed record ProcessResult(int ExitCode, string StdOut, string StdErr);
}
