using System.Text;

namespace OmniRelay.Service.Runtime;

public sealed class FileLogWriter
{
    public const int RetentionDays = 15;
    private readonly object _sync = new();

    public void Info(string message)
    {
        Write("INFO", message);
    }

    public void Warn(string message)
    {
        Write("WARN", message);
    }

    public void Error(string message, Exception? exception = null)
    {
        if (exception is not null)
        {
            message = $"{message}{Environment.NewLine}{exception}";
        }

        Write("ERROR", message);
    }

    private void Write(string level, string message)
    {
        var line = $"{DateTimeOffset.UtcNow:O} [{level}] {message}{Environment.NewLine}";

        lock (_sync)
        {
            ServicePaths.EnsureDirectories();
            File.AppendAllText(ServicePaths.ServiceLogPath, line, Encoding.UTF8);
        }
    }

    public void RunRetentionSweep()
    {
        lock (_sync)
        {
            ServicePaths.EnsureDirectories();
            var cutoffUtc = DateTimeOffset.UtcNow.AddDays(-RetentionDays);
            CompactServiceLog(ServicePaths.ServiceLogPath, cutoffUtc);
            DeleteOldLogFiles(ServicePaths.LogsDirectory, cutoffUtc, ServicePaths.ServiceLogPath);
        }
    }

    private static void CompactServiceLog(string logPath, DateTimeOffset cutoffUtc)
    {
        if (!File.Exists(logPath))
        {
            return;
        }

        var tempPath = $"{logPath}.retention.tmp";
        using (var reader = new StreamReader(logPath, Encoding.UTF8, detectEncodingFromByteOrderMarks: true))
        using (var writer = new StreamWriter(tempPath, append: false, Encoding.UTF8))
        {
            while (!reader.EndOfStream)
            {
                var line = reader.ReadLine();
                if (line is null)
                {
                    continue;
                }

                if (!TryParseLogTimestamp(line, out var timestampUtc) || timestampUtc >= cutoffUtc)
                {
                    writer.WriteLine(line);
                }
            }
        }

        File.Copy(tempPath, logPath, overwrite: true);
        File.Delete(tempPath);
    }

    private static bool TryParseLogTimestamp(string line, out DateTimeOffset timestampUtc)
    {
        timestampUtc = DateTimeOffset.MinValue;
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        var index = line.IndexOf(' ');
        if (index <= 0)
        {
            return false;
        }

        var token = line[..index];
        if (!DateTimeOffset.TryParse(token, out var parsed))
        {
            return false;
        }

        timestampUtc = parsed.ToUniversalTime();
        return true;
    }

    private static void DeleteOldLogFiles(string logsDirectory, DateTimeOffset cutoffUtc, string protectedPath)
    {
        if (!Directory.Exists(logsDirectory))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(logsDirectory, "*.log", SearchOption.TopDirectoryOnly))
        {
            if (string.Equals(file, protectedPath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                var lastWriteUtc = File.GetLastWriteTimeUtc(file);
                if (lastWriteUtc < cutoffUtc.UtcDateTime)
                {
                    File.Delete(file);
                }
            }
            catch
            {
            }
        }
    }
}
