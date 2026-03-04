using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;

namespace EstherLink.UI.Services;

public sealed class GatewayBundleResolverService : IGatewayBundleResolverService
{
    private const string BundleFileName = "omnirelay-vps-bundle-x64.tar.gz";

    public GatewayBundleDescriptor Resolve()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "GatewayBundle", BundleFileName),
            Path.Combine(AppContext.BaseDirectory, "Assets", "GatewayBundle", BundleFileName),
            Path.Combine(AppContext.BaseDirectory, BundleFileName)
        }
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();

        var bundlePath = candidates.FirstOrDefault(File.Exists);
        if (string.IsNullOrWhiteSpace(bundlePath))
        {
            throw new InvalidOperationException($"Gateway bundle was not found. Expected one of: {string.Join(", ", candidates)}");
        }

        var shaFile = bundlePath + ".sha256";
        if (!File.Exists(shaFile))
        {
            throw new InvalidOperationException($"Gateway bundle checksum file not found: {shaFile}");
        }

        var expectedSha = ParseSha(File.ReadAllText(shaFile));
        var actualSha = ComputeSha256(bundlePath);
        if (!string.Equals(expectedSha, actualSha, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Gateway bundle checksum validation failed.");
        }

        var version = ReadBundleVersion(bundlePath) ?? "unknown";

        return new GatewayBundleDescriptor
        {
            BundleFilePath = bundlePath,
            BundleSha256 = actualSha,
            BundleVersion = version
        };
    }

    private static string ParseSha(string content)
    {
        var first = content
            .Split(new[] { '\r', '\n', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();

        if (string.IsNullOrWhiteSpace(first))
        {
            throw new InvalidOperationException("Invalid checksum file format.");
        }

        return first.Trim();
    }

    private static string ComputeSha256(string filePath)
    {
        using var file = File.OpenRead(filePath);
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(file);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string? ReadBundleVersion(string tarPath)
    {
        using var file = File.OpenRead(tarPath);
        using var gzip = new GZipStream(file, CompressionMode.Decompress, leaveOpen: false);
        using var reader = new TarReader(gzip, leaveOpen: false);

        TarEntry? entry;
        while ((entry = reader.GetNextEntry()) is not null)
        {
            if (entry.DataStream is null)
            {
                continue;
            }

            if (!entry.Name.EndsWith("manifest.json", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            using var sr = new StreamReader(entry.DataStream);
            var json = sr.ReadToEnd();
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("bundleVersion", out var version))
            {
                return version.GetString();
            }

            return null;
        }

        return null;
    }
}
