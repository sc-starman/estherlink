using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.IO;
using EstherLink.UI.Models;

namespace EstherLink.UI.Services;

public sealed class GatewayStatePersistenceService : IGatewayStatePersistenceService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("EstherLink.UI.GatewayState.v1");

    private static string RootDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EstherLink");

    private static string StatePath => Path.Combine(RootDirectory, "gateway.ui.state.json");

    public GatewayUiStateModel Load()
    {
        try
        {
            if (!File.Exists(StatePath))
            {
                return new GatewayUiStateModel();
            }

            var json = File.ReadAllText(StatePath);
            var parsed = JsonSerializer.Deserialize<GatewayUiStateModel>(json, JsonOptions) ?? new GatewayUiStateModel();
            parsed.TunnelKeyPath ??= string.Empty;
            parsed.WhitelistText ??= string.Empty;

            if (ContainsLegacyLicenseFields(json))
            {
                // Rewrite once on startup to remove legacy tamper-prone fields.
                Save(parsed);
            }

            return parsed;
        }
        catch
        {
            return new GatewayUiStateModel();
        }
    }

    public void Save(GatewayUiStateModel state)
    {
        Directory.CreateDirectory(RootDirectory);
        var json = JsonSerializer.Serialize(state, JsonOptions);
        File.WriteAllText(StatePath, json, Encoding.UTF8);
    }

    public static string Protect(string plainText)
    {
        var bytes = Encoding.UTF8.GetBytes(plainText ?? string.Empty);
        var protectedBytes = ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(protectedBytes);
    }

    public static string Unprotect(string? cipherText)
    {
        if (string.IsNullOrWhiteSpace(cipherText))
        {
            return string.Empty;
        }

        try
        {
            var protectedBytes = Convert.FromBase64String(cipherText);
            var bytes = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool ContainsLegacyLicenseFields(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            return root.ValueKind == JsonValueKind.Object &&
                   (root.TryGetProperty("licenseActivated", out _) ||
                    root.TryGetProperty("licenseActivatedExpiresAtUtc", out _));
        }
        catch
        {
            return false;
        }
    }
}
