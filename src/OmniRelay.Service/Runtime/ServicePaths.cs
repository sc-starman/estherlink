namespace OmniRelay.Service.Runtime;

public static class ServicePaths
{
    public static string RootDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "OmniRelay");

    public static string ConfigPath { get; } = Path.Combine(RootDirectory, "config.json");
    public static string PolicyDbPath { get; } = Path.Combine(RootDirectory, "policy.db");
    public static string LicenseCachePath { get; } = Path.Combine(RootDirectory, "license.cache");
    public static string LicensePublicKeysCachePath { get; } = Path.Combine(RootDirectory, "license_public_keys.cache");
    public static string LogsDirectory { get; } = Path.Combine(RootDirectory, "logs");
    public static string ServiceLogPath { get; } = Path.Combine(LogsDirectory, "service.log");
    public static string LocalGatewayDirectory { get; } = Path.Combine(RootDirectory, "local-gateway");
    public static string LocalGatewayClientsPath { get; } = Path.Combine(LocalGatewayDirectory, "clients.json");
    public static string LocalGatewayXrayConfigPath { get; } = Path.Combine(LocalGatewayDirectory, "xray.config.json");
    public static string LocalGatewayXrayStdoutLogPath { get; } = Path.Combine(LocalGatewayDirectory, "xray.stdout.log");
    public static string LocalGatewayXrayStderrLogPath { get; } = Path.Combine(LocalGatewayDirectory, "xray.stderr.log");
    public static string LocalGatewayFirewallRuleName { get; } = "OmniRelay Local Gateway";

    public static string ResolveXrayExecutablePath()
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "xray", "xray.exe");
        if (File.Exists(bundled))
        {
            return bundled;
        }

        var besideService = Path.Combine(AppContext.BaseDirectory, "xray.exe");
        if (File.Exists(besideService))
        {
            return besideService;
        }

        return bundled;
    }

    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(LocalGatewayDirectory);
    }
}
