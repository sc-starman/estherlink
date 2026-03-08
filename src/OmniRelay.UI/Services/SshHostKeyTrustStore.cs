using System.IO;
using System.Linq;
using System.Text;

namespace OmniRelay.UI.Services;

public interface ISshHostKeyTrustStore
{
    bool ValidateAndRemember(string host, int port, byte[] fingerprint);
}

public sealed class SshHostKeyTrustStore : ISshHostKeyTrustStore
{
    private static readonly object Sync = new();

    private static string StorePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "OmniRelay",
        "known_hosts_gateway.txt");

    public bool ValidateAndRemember(string host, int port, byte[] fingerprint)
    {
        var key = $"{host}:{port}";
        var fpHex = Convert.ToHexString(fingerprint).ToLowerInvariant();

        lock (Sync)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);

            var map = Load();
            if (map.TryGetValue(key, out var existing))
            {
                return string.Equals(existing, fpHex, StringComparison.OrdinalIgnoreCase);
            }

            map[key] = fpHex;
            Save(map);
            return true;
        }
    }

    private static Dictionary<string, string> Load()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(StorePath))
        {
            return map;
        }

        foreach (var line in File.ReadAllLines(StorePath, Encoding.UTF8))
        {
            var trimmed = line.Trim();
            if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith('#'))
            {
                continue;
            }

            var parts = trimmed.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2)
            {
                continue;
            }

            map[parts[0]] = parts[1];
        }

        return map;
    }

    private static void Save(Dictionary<string, string> map)
    {
        var lines = map
            .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .Select(x => $"{x.Key} {x.Value}")
            .ToArray();
        File.WriteAllLines(StorePath, lines, Encoding.UTF8);
    }
}
