using System.Text;

namespace EstherLink.Service.Runtime;

public sealed class FileLogWriter
{
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
}
