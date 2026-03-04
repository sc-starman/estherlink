using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using EstherLink.Core.Configuration;

namespace EstherLink.Service.Runtime;

public sealed class ConfigStore
{
    public const int CurrentSchemaVersion = 1;

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

            var migrated = false;
            if (stored.SchemaVersion <= 0)
            {
                stored.SchemaVersion = CurrentSchemaVersion;
                migrated = true;
            }

            var state = new PersistedState(
                new ServiceConfig
                {
                    SchemaVersion = CurrentSchemaVersion,
                    VpsHost = stored.VpsHost ?? string.Empty,
                    VpsPort = stored.VpsPort,
                    LocalProxyListenPort = stored.LocalProxyListenPort,
                    WhitelistAdapterIfIndex = stored.WhitelistAdapterIfIndex,
                    DefaultAdapterIfIndex = stored.DefaultAdapterIfIndex,
                    TunnelEnabled = stored.TunnelEnabled,
                    TunnelHost = stored.TunnelHost ?? string.Empty,
                    TunnelSshPort = stored.TunnelSshPort,
                    TunnelRemotePort = stored.TunnelRemotePort,
                    TunnelUser = stored.TunnelUser ?? "estherlink",
                    TunnelPrivateKeyPath = stored.TunnelPrivateKeyPath ?? string.Empty,
                    LicenseServerUrl = stored.LicenseServerUrl ?? string.Empty,
                    LicenseKey = Decrypt(stored.EncryptedLicenseKey)
                },
                stored.WhitelistEntries ?? []);

            if (migrated)
            {
                Save(state.Config, state.WhitelistEntries);
                _log.Info("Migrated legacy config to schema version 1.");
            }

            return state;
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
                SchemaVersion = CurrentSchemaVersion,
                VpsHost = config.VpsHost,
                VpsPort = config.VpsPort,
                LocalProxyListenPort = config.LocalProxyListenPort,
                WhitelistAdapterIfIndex = config.WhitelistAdapterIfIndex,
                DefaultAdapterIfIndex = config.DefaultAdapterIfIndex,
                TunnelEnabled = config.TunnelEnabled,
                TunnelHost = config.TunnelHost,
                TunnelSshPort = config.TunnelSshPort,
                TunnelRemotePort = config.TunnelRemotePort,
                TunnelUser = config.TunnelUser,
                TunnelPrivateKeyPath = config.TunnelPrivateKeyPath,
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
        public int SchemaVersion { get; set; }
        public string? VpsHost { get; set; }
        public int VpsPort { get; set; } = 443;
        public int LocalProxyListenPort { get; set; } = 19080;
        public int WhitelistAdapterIfIndex { get; set; } = -1;
        public int DefaultAdapterIfIndex { get; set; } = -1;
        public bool TunnelEnabled { get; set; }
        public string? TunnelHost { get; set; }
        public int TunnelSshPort { get; set; } = 22;
        public int TunnelRemotePort { get; set; } = 15000;
        public string? TunnelUser { get; set; }
        public string? TunnelPrivateKeyPath { get; set; }
        public string? LicenseServerUrl { get; set; }
        public string? EncryptedLicenseKey { get; set; }
        public List<string>? WhitelistEntries { get; set; }
    }
}

public sealed record PersistedState(ServiceConfig Config, IReadOnlyList<string> WhitelistEntries)
{
    public static PersistedState Empty { get; } = new(new ServiceConfig(), []);
}
