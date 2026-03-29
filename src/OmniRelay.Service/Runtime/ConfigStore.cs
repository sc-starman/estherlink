using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OmniRelay.Core.Configuration;

namespace OmniRelay.Service.Runtime;

public sealed class ConfigStore
{
    public const int CurrentSchemaVersion = 6;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("OmniRelay.Config.v1");
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
                stored.SchemaVersion = 1;
                migrated = true;
            }

            if (stored.SchemaVersion < 2)
            {
                stored.TunnelAuthMethod = TunnelAuthMethods.HostKey;
                stored.SchemaVersion = 2;
                migrated = true;
            }

            if (stored.SchemaVersion < 3)
            {
                stored.SchemaVersion = 3;
                migrated = true;
            }

            if (stored.SchemaVersion < 4)
            {
                stored.BootstrapSocksLocalPort = 19081;
                stored.BootstrapSocksRemotePort = 16080;
                stored.GatewayOnlineInstallEnabled = true;
                stored.SchemaVersion = 4;
                migrated = true;
            }

            if (stored.SchemaVersion < 5)
            {
                stored.SchemaVersion = 5;
                migrated = true;
            }

            if (stored.SchemaVersion < 6)
            {
                stored.GatewayType = GatewayTypes.Normalize(stored.GatewayType);
                stored.LocalGateway ??= new PersistedLocalGatewayConfig();
                stored.SchemaVersion = 6;
                migrated = true;
            }

            var state = new PersistedState(
                new ServiceConfig
                {
                    SchemaVersion = CurrentSchemaVersion,
                    LocalProxyListenPort = stored.LocalProxyListenPort,
                    BootstrapSocksLocalPort = stored.BootstrapSocksLocalPort <= 0 ? 19081 : stored.BootstrapSocksLocalPort,
                    BootstrapSocksRemotePort = stored.BootstrapSocksRemotePort <= 0 ? 16080 : stored.BootstrapSocksRemotePort,
                    GatewayOnlineInstallEnabled = stored.GatewayOnlineInstallEnabled,
                    GatewayType = GatewayTypes.Normalize(stored.GatewayType),
                    WhitelistAdapterIfIndex = stored.WhitelistAdapterIfIndex,
                    DefaultAdapterIfIndex = stored.DefaultAdapterIfIndex,
                    TunnelHost = stored.TunnelHost ?? string.Empty,
                    TunnelSshPort = stored.TunnelSshPort,
                    TunnelRemotePort = stored.TunnelRemotePort,
                    TunnelUser = stored.TunnelUser ?? "OmniRelay",
                    TunnelAuthMethod = TunnelAuthMethods.Normalize(stored.TunnelAuthMethod),
                    TunnelPrivateKeyPath = stored.TunnelPrivateKeyPath ?? string.Empty,
                    TunnelPrivateKeyPassphrase = Decrypt(stored.EncryptedTunnelKeyPassphrase),
                    TunnelPassword = Decrypt(stored.EncryptedTunnelPassword),
                    LicenseKey = Decrypt(stored.EncryptedLicenseKey),
                    LocalGateway = new LocalGatewayConfig
                    {
                        Protocol = LocalGatewayProtocols.Normalize(stored.LocalGateway?.Protocol),
                        Port = stored.LocalGateway?.Port is > 0 and <= 65535 ? stored.LocalGateway.Port : 443,
                        BindAddress = string.IsNullOrWhiteSpace(stored.LocalGateway?.BindAddress) ? "0.0.0.0" : stored.LocalGateway.BindAddress.Trim(),
                        Remark = string.IsNullOrWhiteSpace(stored.LocalGateway?.Remark) ? "OmniRelay Local Gateway" : stored.LocalGateway.Remark.Trim(),
                        RuntimeEnabled = stored.LocalGateway?.RuntimeEnabled ?? true
                    }
                },
                stored.WhitelistEntries ?? []);

            if (migrated || (stored.WhitelistEntries?.Count ?? 0) > 0)
            {
                Save(state.Config);
                _log.Info($"Migrated legacy config to schema version {CurrentSchemaVersion}.");
            }

            return state;
        }
        catch (Exception ex)
        {
            _log.Error("Failed loading persisted config.", ex);
            return PersistedState.Empty;
        }
    }

    public void Save(ServiceConfig config, IReadOnlyList<string>? whitelistEntries = null)
    {
        try
        {
            ServicePaths.EnsureDirectories();
            var stored = new PersistedConfig
            {
                SchemaVersion = CurrentSchemaVersion,
                LocalProxyListenPort = config.LocalProxyListenPort,
                BootstrapSocksLocalPort = config.BootstrapSocksLocalPort,
                BootstrapSocksRemotePort = config.BootstrapSocksRemotePort,
                GatewayOnlineInstallEnabled = config.GatewayOnlineInstallEnabled,
                GatewayType = GatewayTypes.Normalize(config.GatewayType),
                WhitelistAdapterIfIndex = config.WhitelistAdapterIfIndex,
                DefaultAdapterIfIndex = config.DefaultAdapterIfIndex,
                TunnelHost = config.TunnelHost,
                TunnelSshPort = config.TunnelSshPort,
                TunnelRemotePort = config.TunnelRemotePort,
                TunnelUser = config.TunnelUser,
                TunnelAuthMethod = TunnelAuthMethods.Normalize(config.TunnelAuthMethod),
                TunnelPrivateKeyPath = config.TunnelPrivateKeyPath,
                EncryptedTunnelKeyPassphrase = Encrypt(config.TunnelPrivateKeyPassphrase),
                EncryptedTunnelPassword = Encrypt(config.TunnelPassword),
                EncryptedLicenseKey = Encrypt(config.LicenseKey),
                LocalGateway = new PersistedLocalGatewayConfig
                {
                    Protocol = LocalGatewayProtocols.Normalize(config.LocalGateway?.Protocol),
                    Port = config.LocalGateway?.Port is > 0 and <= 65535 ? config.LocalGateway.Port : 443,
                    BindAddress = string.IsNullOrWhiteSpace(config.LocalGateway?.BindAddress) ? "0.0.0.0" : config.LocalGateway.BindAddress.Trim(),
                    Remark = string.IsNullOrWhiteSpace(config.LocalGateway?.Remark) ? "OmniRelay Local Gateway" : config.LocalGateway.Remark.Trim(),
                    RuntimeEnabled = config.LocalGateway?.RuntimeEnabled ?? true
                },
                WhitelistEntries = null
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
        public int LocalProxyListenPort { get; set; } = 19080;
        public int BootstrapSocksLocalPort { get; set; } = 19081;
        public int BootstrapSocksRemotePort { get; set; } = 16080;
        public bool GatewayOnlineInstallEnabled { get; set; } = true;
        public string GatewayType { get; set; } = GatewayTypes.Remote;
        public int WhitelistAdapterIfIndex { get; set; } = -1;
        public int DefaultAdapterIfIndex { get; set; } = -1;
        public bool TunnelEnabled { get; set; }
        public string? TunnelHost { get; set; }
        public int TunnelSshPort { get; set; } = 22;
        public int TunnelRemotePort { get; set; } = 15000;
        public string? TunnelUser { get; set; }
        public string? TunnelAuthMethod { get; set; }
        public string? TunnelPrivateKeyPath { get; set; }
        public string? EncryptedTunnelKeyPassphrase { get; set; }
        public string? EncryptedTunnelPassword { get; set; }
        public string? EncryptedLicenseKey { get; set; }
        public PersistedLocalGatewayConfig? LocalGateway { get; set; }
        public List<string>? WhitelistEntries { get; set; }
    }

    public sealed class PersistedLocalGatewayConfig
    {
        public string Protocol { get; set; } = LocalGatewayProtocols.VlessTcpPlain;
        public int Port { get; set; } = 443;
        public string BindAddress { get; set; } = "0.0.0.0";
        public string Remark { get; set; } = "OmniRelay Local Gateway";
        public bool RuntimeEnabled { get; set; } = true;
    }
}

public sealed record PersistedState(ServiceConfig Config, IReadOnlyList<string> WhitelistEntries)
{
    public static PersistedState Empty { get; } = new(new ServiceConfig(), []);
}
