namespace EstherLink.Service.Runtime;

public static class ServicePaths
{
    public static string RootDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "EstherLink");

    public static string ConfigPath { get; } = Path.Combine(RootDirectory, "config.json");
    public static string LicenseCachePath { get; } = Path.Combine(RootDirectory, "license.cache");
    public static string LogsDirectory { get; } = Path.Combine(RootDirectory, "logs");
    public static string ServiceLogPath { get; } = Path.Combine(LogsDirectory, "service.log");

    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(LogsDirectory);
    }
}
