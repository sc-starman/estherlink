using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EstherLink.Core.Configuration;
using EstherLink.Core.Policy;

namespace EstherLink.Service.Runtime;

public sealed class ConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("EstherLink.Config.v1");
    private readonly FileLogWriter _log;

    public ConfigStore(FileLogWriter log)
    {
        _log = log;
    }

    public PersistedState Load()
    {
        try
        {
            ServicePaths.EnsureDirectories();
            if (!File.Exists(ServicePaths.ConfigPath))
            {
                return PersistedState.Empty;
            }

            var json = File.ReadAllText(ServicePaths.ConfigPath);
            var stored = JsonSerializer.Deserialize<PersistedConfig>(json, JsonOptions);
            if (stored is null)
            {
                return PersistedState.Empty;
            }

            return new PersistedState(
                new ServiceConfig
                {
                    VpsHost = stored.VpsHost ?? string.Empty,
                    VpsPort = stored.VpsPort,
                    LocalProxyListenPort = stored.LocalProxyListenPort,
                    WhitelistAdapterIfIndex = stored.WhitelistAdapterIfIndex,
                    DefaultAdapterIfIndex = stored.DefaultAdapterIfIndex,
                    WhitelistMode = stored.WhitelistMode,
                    ExpectProxyProtocolV2 = stored.ExpectProxyProtocolV2,
                    LicenseServerUrl = stored.LicenseServerUrl ?? string.Empty,
                    LicenseKey = Decrypt(stored.EncryptedLicenseKey)
                },
                stored.WhitelistEntries ?? []);
        }
        catch (Exception ex)
        {
            _log.Error("Failed loading persisted config.", ex);
            return PersistedState.Empty;
        }
    }

    public void Save(ServiceConfig config, IReadOnlyList<string> whitelistEntries)
    {
        try
        {
            ServicePaths.EnsureDirectories();
            var stored = new PersistedConfig
            {
                VpsHost = config.VpsHost,
                VpsPort = config.VpsPort,
                LocalProxyListenPort = config.LocalProxyListenPort,
                WhitelistAdapterIfIndex = config.WhitelistAdapterIfIndex,
                DefaultAdapterIfIndex = config.DefaultAdapterIfIndex,
                WhitelistMode = config.WhitelistMode,
                ExpectProxyProtocolV2 = config.ExpectProxyProtocolV2,
                LicenseServerUrl = config.LicenseServerUrl,
                EncryptedLicenseKey = Encrypt(config.LicenseKey),
                WhitelistEntries = whitelistEntries.ToList()
            };

            var json = JsonSerializer.Serialize(stored, JsonOptions);
            File.WriteAllText(ServicePaths.ConfigPath, json, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            _log.Error("Failed saving persisted config.", ex);
            throw;
        }
    }

    private static string Encrypt(string plainText)
    {
        var bytes = Encoding.UTF8.GetBytes(plainText ?? string.Empty);
        var protectedBytes = ProtectedData.Protect(bytes, Entropy, DataProtectionScope.LocalMachine);
        return Convert.ToBase64String(protectedBytes);
    }

    private static string Decrypt(string? cipherText)
    {
        if (string.IsNullOrWhiteSpace(cipherText))
        {
            return string.Empty;
        }

        try
        {
            var protectedBytes = Convert.FromBase64String(cipherText);
            var plainBytes = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.LocalMachine);
            return Encoding.UTF8.GetString(plainBytes);
        }
        catch
        {
            return string.Empty;
        }
    }

    private sealed class PersistedConfig
    {
        public string? VpsHost { get; set; }
        public int VpsPort { get; set; } = 443;
        public int LocalProxyListenPort { get; set; } = 19080;
        public int WhitelistAdapterIfIndex { get; set; } = -1;
        public int DefaultAdapterIfIndex { get; set; } = -1;
        public RoutingPolicyMode WhitelistMode { get; set; } = RoutingPolicyMode.DestinationOnly;
        public bool ExpectProxyProtocolV2 { get; set; }
        public string? LicenseServerUrl { get; set; }
        public string? EncryptedLicenseKey { get; set; }
        public List<string>? WhitelistEntries { get; set; }
    }
}

public sealed record PersistedState(ServiceConfig Config, IReadOnlyList<string> WhitelistEntries)
{
    public static PersistedState Empty { get; } = new(new ServiceConfig(), []);
}
