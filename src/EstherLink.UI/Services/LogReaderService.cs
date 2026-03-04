using System.IO;

namespace EstherLink.UI.Services;

public sealed class LogReaderService : ILogReaderService
{
    private static string ServiceLogPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "EstherLink",
            "logs",
            "service.log");

    public async Task<IReadOnlyList<string>> ReadLatestAsync(int maxLines, string? search = null, CancellationToken cancellationToken = default)
    {
        if (maxLines <= 0)
        {
            maxLines = 200;
        }

        if (!File.Exists(ServiceLogPath))
        {
            return ["Service log file does not exist yet."];
        }

        var lines = await File.ReadAllLinesAsync(ServiceLogPath, cancellationToken);
        IEnumerable<string> filtered = lines;
        if (!string.IsNullOrWhiteSpace(search))
        {
            filtered = filtered.Where(x => x.Contains(search, StringComparison.OrdinalIgnoreCase));
        }

        return filtered.TakeLast(maxLines).ToArray();
    }
}
