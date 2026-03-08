using System.Diagnostics;
using System.Net;
using EstherLink.Core.Configuration;
using EstherLink.Core.Networking;

namespace EstherLink.Service.Runtime;

internal static class NetworkRouteManager
{
    public static async Task<(bool Success, string Message)> TryEnsureTunnelHostRouteAsync(
        ServiceConfig config,
        CancellationToken cancellationToken = default)
    {
        var host = (config.TunnelHost ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(host))
        {
            return (false, "Tunnel host is empty; route management skipped.");
        }

        if (config.WhitelistAdapterIfIndex <= 0)
        {
            return (false, "IC1 adapter is not selected; route management skipped.");
        }

        if (!NetworkAdapterCatalog.TryGetPrimaryIpv4Gateway(config.WhitelistAdapterIfIndex, out var gateway) || gateway is null)
        {
            return (false, $"IC1 adapter IfIndex={config.WhitelistAdapterIfIndex} has no IPv4 gateway; cannot auto-manage VPS host route.");
        }

        var targets = await ResolveHostIpv4AddressesAsync(host, cancellationToken);
        if (targets.Count == 0)
        {
            return (false, $"Could not resolve IPv4 for tunnel host '{host}'; route management skipped.");
        }

        var errors = new List<string>();
        var applied = 0;
        foreach (var target in targets)
        {
            var routeResult = await EnsureSingleHostRouteAsync(target, gateway, config.WhitelistAdapterIfIndex, cancellationToken);
            if (routeResult.Success)
            {
                applied++;
            }
            else
            {
                errors.Add(routeResult.Message);
            }
        }

        if (applied > 0)
        {
            return (true, $"Host route ensured for {applied}/{targets.Count} tunnel target IP(s) via IC1 ifIndex={config.WhitelistAdapterIfIndex}, gateway={gateway}.");
        }

        return (false, $"Failed to ensure host routes via IC1. {string.Join(" | ", errors)}");
    }

    private static async Task<(bool Success, string Message)> EnsureSingleHostRouteAsync(
        IPAddress target,
        IPAddress gateway,
        int ifIndex,
        CancellationToken cancellationToken)
    {
        if (await HasExpectedHostRouteAsync(target, gateway, ifIndex, cancellationToken))
        {
            return (true, $"route already present for {target}");
        }

        // First try CHANGE (idempotent if route already exists).
        var changeArgs = $"CHANGE {target} MASK 255.255.255.255 {gateway} IF {ifIndex}";
        var change = await ExecuteRouteCommandAsync(changeArgs, cancellationToken);
        if (change.ExitCode == 0 && await HasExpectedHostRouteAsync(target, gateway, ifIndex, cancellationToken))
        {
            return (true, $"route change ok for {target}");
        }

        // Remove any conflicting host route before add.
        _ = await ExecuteRouteCommandAsync($"DELETE {target}", cancellationToken);

        // Then try ADD.
        var addArgs = $"ADD {target} MASK 255.255.255.255 {gateway} IF {ifIndex} METRIC 5";
        var add = await ExecuteRouteCommandAsync(addArgs, cancellationToken);
        if (add.ExitCode == 0 && await HasExpectedHostRouteAsync(target, gateway, ifIndex, cancellationToken))
        {
            return (true, $"route add ok for {target}");
        }

        var details = FirstNonEmpty(add.Output, change.Output) ?? "route command failed";
        if (add.ExitCode == 0 || change.ExitCode == 0)
        {
            details = $"{details}. Route command reported success but expected /32 route was not found in route table.";
        }

        return (false, $"route ensure failed for {target}: {details}");
    }

    private static async Task<(int ExitCode, string Output)> ExecuteRouteCommandAsync(string args, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "route",
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = psi };
        if (!process.Start())
        {
            return (1, $"Failed to start route command: route {args}");
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var stdout = (await stdoutTask).Trim();
        var stderr = (await stderrTask).Trim();
        var merged = string.IsNullOrWhiteSpace(stderr) ? stdout : $"{stdout} {stderr}".Trim();
        return (process.ExitCode, merged);
    }

    private static async Task<IReadOnlyList<IPAddress>> ResolveHostIpv4AddressesAsync(string host, CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(host, out var parsed))
        {
            return parsed.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                ? [parsed]
                : [];
        }

        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken);
            return addresses
                .Where(x => x.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                .Distinct()
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static async Task<bool> HasExpectedHostRouteAsync(
        IPAddress target,
        IPAddress gateway,
        int ifIndex,
        CancellationToken cancellationToken)
    {
        if (!NetworkAdapterCatalog.TryGetPrimaryIpv4(ifIndex, out var interfaceIp) || interfaceIp is null)
        {
            return false;
        }

        var print = await ExecuteRouteCommandAsync($"PRINT {target}", cancellationToken);
        if (print.ExitCode != 0 || string.IsNullOrWhiteSpace(print.Output))
        {
            return false;
        }

        var targetText = target.ToString();
        var gatewayText = gateway.ToString();
        var interfaceText = interfaceIp.ToString();

        foreach (var raw in print.Output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            if (!line.StartsWith(targetText, StringComparison.Ordinal))
            {
                continue;
            }

            var cols = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (cols.Length < 5)
            {
                continue;
            }

            if (!cols[0].Equals(targetText, StringComparison.Ordinal) ||
                !cols[1].Equals("255.255.255.255", StringComparison.Ordinal) ||
                !cols[2].Equals(gatewayText, StringComparison.Ordinal) ||
                !cols[3].Equals(interfaceText, StringComparison.Ordinal))
            {
                continue;
            }

            return true;
        }

        return false;
    }
}
