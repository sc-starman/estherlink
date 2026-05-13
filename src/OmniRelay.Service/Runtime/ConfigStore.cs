using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OmniRelay.Core.Configuration;

namespace OmniRelay.Service.Runtime;

public sealed class ConfigStore
{
    public const int CurrentSchemaVersion = 8;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("OmniRelay.Config.v2");
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

            var config = new ServiceConfig
            {
                SchemaVersion = CurrentSchemaVersion,
                LicenseKey = Decrypt(stored.EncryptedLicenseKey),
                Relays = (stored.Relays ?? [])
                    .Where(x => x is not null)
                    .Select(x => ToRelayConfig(x!))
                    .Where(x => !string.IsNullOrWhiteSpace(x.Id))
                    .ToList()
            };

            EnsureRelayPorts(config.Relays);
            return new PersistedState(config, []);
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
            EnsureRelayPorts(config.Relays);

            var stored = new PersistedConfig
            {
                SchemaVersion = CurrentSchemaVersion,
                EncryptedLicenseKey = Encrypt(config.LicenseKey),
                Relays = config.Relays.Select(ToPersistedRelayConfig).ToList()
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

    internal static void EnsureRelayPorts(IReadOnlyList<RelayConfig> relays)
    {
        var used = new HashSet<int>();
        var usedRemotePorts = new HashSet<int>();
        var usedLocalGatewayPorts = new HashSet<int>();
        var usedLocalOmniPanelPorts = new HashSet<int>();
        var usedRelayIds = new HashSet<string>(StringComparer.Ordinal);
        var next = 24080;
        var nextRemote = 15000;
        var nextLocalGatewayPort = 2443;
        var nextLocalOmniPanelPort = 2054;

        foreach (var relay in relays)
        {
            if (relay is null)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(relay.Id) || !usedRelayIds.Add(relay.Id.Trim()))
            {
                relay.Id = Guid.NewGuid().ToString("N");
            }
            else
            {
                relay.Id = relay.Id.Trim();
            }

            relay.Name = string.IsNullOrWhiteSpace(relay.Name) ? "Relay" : relay.Name.Trim();
            relay.GatewayType = GatewayTypes.Normalize(relay.GatewayType);
            relay.RemoteGateway ??= new RemoteGatewayConfig();
            relay.LocalGateway ??= new LocalGatewayConfig();
            relay.OmniPanel ??= new RelayOmniPanelConfig();
            relay.OmniPanel.Port = relay.OmniPanel.Port is > 0 and <= 65535
                ? relay.OmniPanel.Port
                : (relay.RemoteGateway.PanelPort is > 0 and <= 65535 ? relay.RemoteGateway.PanelPort : 2054);
            relay.OmniPanel.Username = string.IsNullOrWhiteSpace(relay.OmniPanel.Username) ? relay.RemoteGateway.PanelUser : relay.OmniPanel.Username;
            relay.OmniPanel.Password = string.IsNullOrWhiteSpace(relay.OmniPanel.Password) ? relay.RemoteGateway.PanelPassword : relay.OmniPanel.Password;
            relay.OmniPanel.Domain = string.IsNullOrWhiteSpace(relay.OmniPanel.Domain) ? relay.RemoteGateway.PanelDomain : relay.OmniPanel.Domain;
            relay.OmniPanel.DomainOnly = relay.OmniPanel.DomainOnly || relay.RemoteGateway.PanelDomainOnly;
            relay.OmniPanel.UseSsl = relay.OmniPanel.UseSsl || relay.RemoteGateway.PanelUseSsl;
            relay.OmniPanel.SslMode = string.IsNullOrWhiteSpace(relay.OmniPanel.SslMode)
                ? (string.IsNullOrWhiteSpace(relay.RemoteGateway.PanelSslMode) ? "letsencrypt" : relay.RemoteGateway.PanelSslMode.Trim())
                : relay.OmniPanel.SslMode.Trim();
            relay.OmniPanel.UploadedCertPath = string.IsNullOrWhiteSpace(relay.OmniPanel.UploadedCertPath) ? relay.RemoteGateway.PanelUploadedCertPath : relay.OmniPanel.UploadedCertPath;
            relay.OmniPanel.UploadedKeyPath = string.IsNullOrWhiteSpace(relay.OmniPanel.UploadedKeyPath) ? relay.RemoteGateway.PanelUploadedKeyPath : relay.OmniPanel.UploadedKeyPath;

            relay.RemoteGateway.PanelPort = relay.OmniPanel.Port;
            relay.RemoteGateway.PanelUser = relay.OmniPanel.Username;
            relay.RemoteGateway.PanelPassword = relay.OmniPanel.Password;
            relay.RemoteGateway.PanelDomain = relay.OmniPanel.Domain;
            relay.RemoteGateway.PanelDomainOnly = relay.OmniPanel.DomainOnly;
            relay.RemoteGateway.PanelUseSsl = relay.OmniPanel.UseSsl;
            relay.RemoteGateway.PanelSslMode = relay.OmniPanel.SslMode;
            relay.RemoteGateway.PanelUploadedCertPath = relay.OmniPanel.UploadedCertPath;
            relay.RemoteGateway.PanelUploadedKeyPath = relay.OmniPanel.UploadedKeyPath;

            if (string.Equals(relay.GatewayType, GatewayTypes.Local, StringComparison.OrdinalIgnoreCase))
            {
                if (relay.LocalGateway.Port <= 0 || relay.LocalGateway.Port > 65535 || !usedLocalGatewayPorts.Add(relay.LocalGateway.Port))
                {
                    relay.LocalGateway.Port = AllocatePort(usedLocalGatewayPorts, ref nextLocalGatewayPort);
                }

                if (relay.OmniPanel.Port <= 0 || relay.OmniPanel.Port > 65535 || !usedLocalOmniPanelPorts.Add(relay.OmniPanel.Port))
                {
                    relay.OmniPanel.Port = AllocatePort(usedLocalOmniPanelPorts, ref nextLocalOmniPanelPort);
                    relay.RemoteGateway.PanelPort = relay.OmniPanel.Port;
                }
            }

            if (relay.DataPlaneLocalPort <= 0 || !used.Add(relay.DataPlaneLocalPort))
            {
                relay.DataPlaneLocalPort = AllocatePort(used, ref next);
            }

            if (relay.BootstrapSocksLocalPort <= 0 || !used.Add(relay.BootstrapSocksLocalPort))
            {
                relay.BootstrapSocksLocalPort = AllocatePort(used, ref next);
            }

            if (string.Equals(relay.GatewayType, GatewayTypes.Remote, StringComparison.OrdinalIgnoreCase))
            {
                if (relay.RemoteGateway.TunnelRemotePort <= 0 || !usedRemotePorts.Add(relay.RemoteGateway.TunnelRemotePort))
                {
                    relay.RemoteGateway.TunnelRemotePort = AllocatePort(usedRemotePorts, ref nextRemote);
                }

                if (relay.BootstrapSocksRemotePort <= 0 || !usedRemotePorts.Add(relay.BootstrapSocksRemotePort))
                {
                    relay.BootstrapSocksRemotePort = AllocatePort(usedRemotePorts, ref nextRemote);
                }
            }
        }
    }

    private static int AllocatePort(ISet<int> used, ref int next)
    {
        while (next <= 65535)
        {
            var candidate = next++;
            if (candidate < 1025 || !used.Add(candidate))
            {
                continue;
            }

            return candidate;
        }

        throw new InvalidOperationException("No available relay listener ports remain.");
    }

    private static RelayConfig ToRelayConfig(PersistedRelayConfig stored)
    {
        var relay = new RelayConfig
        {
            Id = string.IsNullOrWhiteSpace(stored.Id) ? Guid.NewGuid().ToString("N") : stored.Id.Trim(),
            Name = string.IsNullOrWhiteSpace(stored.Name) ? "Relay" : stored.Name.Trim(),
            GatewayType = GatewayTypes.Normalize(stored.GatewayType),
            Enabled = stored.Enabled,
            IncomingAdapterId = stored.IncomingAdapterId ?? string.Empty,
            IncomingAdapterIfIndex = stored.IncomingAdapterIfIndex,
            OutgoingAdapterId = stored.OutgoingAdapterId ?? string.Empty,
            OutgoingAdapterIfIndex = stored.OutgoingAdapterIfIndex,
            DataPlaneLocalPort = stored.DataPlaneLocalPort,
            BootstrapSocksLocalPort = stored.BootstrapSocksLocalPort,
            BootstrapSocksRemotePort = stored.BootstrapSocksRemotePort,
            OmniPanel = stored.OmniPanel ?? new RelayOmniPanelConfig(),
            RemoteGateway = stored.RemoteGateway ?? new RemoteGatewayConfig(),
            LocalGateway = stored.LocalGateway ?? new LocalGatewayConfig()
        };

        relay.RemoteGateway.TunnelPrivateKeyPassphrase = Decrypt(stored.EncryptedTunnelKeyPassphrase);
        relay.RemoteGateway.TunnelPassword = Decrypt(stored.EncryptedTunnelPassword);
        relay.OmniPanel.Port = relay.OmniPanel.Port is > 0 and <= 65535
            ? relay.OmniPanel.Port
            : (relay.RemoteGateway.PanelPort is > 0 and <= 65535 ? relay.RemoteGateway.PanelPort : 2054);
        relay.OmniPanel.Username = string.IsNullOrWhiteSpace(relay.OmniPanel.Username) ? relay.RemoteGateway.PanelUser : relay.OmniPanel.Username;
        relay.OmniPanel.Password = string.IsNullOrWhiteSpace(relay.OmniPanel.Password) ? relay.RemoteGateway.PanelPassword : relay.OmniPanel.Password;
        relay.OmniPanel.Domain = string.IsNullOrWhiteSpace(relay.OmniPanel.Domain) ? relay.RemoteGateway.PanelDomain : relay.OmniPanel.Domain;
        relay.OmniPanel.DomainOnly = relay.OmniPanel.DomainOnly || relay.RemoteGateway.PanelDomainOnly;
        relay.OmniPanel.UseSsl = relay.OmniPanel.UseSsl || relay.RemoteGateway.PanelUseSsl;
        relay.OmniPanel.SslMode = string.IsNullOrWhiteSpace(relay.OmniPanel.SslMode) ? relay.RemoteGateway.PanelSslMode : relay.OmniPanel.SslMode;
        relay.OmniPanel.UploadedCertPath = string.IsNullOrWhiteSpace(relay.OmniPanel.UploadedCertPath) ? relay.RemoteGateway.PanelUploadedCertPath : relay.OmniPanel.UploadedCertPath;
        relay.OmniPanel.UploadedKeyPath = string.IsNullOrWhiteSpace(relay.OmniPanel.UploadedKeyPath) ? relay.RemoteGateway.PanelUploadedKeyPath : relay.OmniPanel.UploadedKeyPath;

        relay.RemoteGateway.PanelPort = relay.OmniPanel.Port;
        relay.RemoteGateway.PanelUser = relay.OmniPanel.Username;
        relay.RemoteGateway.PanelPassword = relay.OmniPanel.Password;
        relay.RemoteGateway.PanelDomain = relay.OmniPanel.Domain;
        relay.RemoteGateway.PanelDomainOnly = relay.OmniPanel.DomainOnly;
        relay.RemoteGateway.PanelUseSsl = relay.OmniPanel.UseSsl;
        relay.RemoteGateway.PanelSslMode = relay.OmniPanel.SslMode;
        relay.RemoteGateway.PanelUploadedCertPath = relay.OmniPanel.UploadedCertPath;
        relay.RemoteGateway.PanelUploadedKeyPath = relay.OmniPanel.UploadedKeyPath;
        return relay;
    }

    private static PersistedRelayConfig ToPersistedRelayConfig(RelayConfig relay)
    {
        var remote = relay.RemoteGateway ?? new RemoteGatewayConfig();
        return new PersistedRelayConfig
        {
            Id = relay.Id,
            Name = relay.Name,
            GatewayType = GatewayTypes.Normalize(relay.GatewayType),
            Enabled = relay.Enabled,
            IncomingAdapterId = relay.IncomingAdapterId,
            IncomingAdapterIfIndex = relay.IncomingAdapterIfIndex,
            OutgoingAdapterId = relay.OutgoingAdapterId,
            OutgoingAdapterIfIndex = relay.OutgoingAdapterIfIndex,
            DataPlaneLocalPort = relay.DataPlaneLocalPort,
            BootstrapSocksLocalPort = relay.BootstrapSocksLocalPort,
            BootstrapSocksRemotePort = relay.BootstrapSocksRemotePort,
            OmniPanel = new RelayOmniPanelConfig
            {
                Port = relay.OmniPanel?.Port is > 0 and <= 65535 ? relay.OmniPanel.Port : remote.PanelPort,
                Username = relay.OmniPanel?.Username ?? remote.PanelUser,
                Password = relay.OmniPanel?.Password ?? remote.PanelPassword,
                Domain = relay.OmniPanel?.Domain ?? remote.PanelDomain,
                DomainOnly = relay.OmniPanel?.DomainOnly ?? remote.PanelDomainOnly,
                UseSsl = relay.OmniPanel?.UseSsl ?? remote.PanelUseSsl,
                SslMode = string.IsNullOrWhiteSpace(relay.OmniPanel?.SslMode) ? remote.PanelSslMode : relay.OmniPanel.SslMode,
                UploadedCertPath = relay.OmniPanel?.UploadedCertPath ?? remote.PanelUploadedCertPath ?? string.Empty,
                UploadedKeyPath = relay.OmniPanel?.UploadedKeyPath ?? remote.PanelUploadedKeyPath ?? string.Empty,
                PublicUrl = relay.OmniPanel?.PublicUrl ?? string.Empty,
                LastError = relay.OmniPanel?.LastError ?? string.Empty
            },
            RemoteGateway = new RemoteGatewayConfig
            {
                TunnelHost = remote.TunnelHost,
                TunnelSshPort = remote.TunnelSshPort,
                TunnelRemotePort = remote.TunnelRemotePort,
                TunnelUser = remote.TunnelUser,
                TunnelAuthMethod = TunnelAuthMethods.Normalize(remote.TunnelAuthMethod),
                TunnelPrivateKeyPath = remote.TunnelPrivateKeyPath,
                BootstrapMode = string.IsNullOrWhiteSpace(remote.BootstrapMode) ? "tunnel" : remote.BootstrapMode.Trim(),
                Protocol = remote.Protocol,
                PublicPort = remote.PublicPort,
                PanelPort = remote.PanelPort,
                PanelUser = remote.PanelUser,
                PanelPassword = remote.PanelPassword,
                PanelDomain = remote.PanelDomain,
                PanelDomainOnly = remote.PanelDomainOnly,
                PanelUseSsl = remote.PanelUseSsl,
                PanelSslMode = remote.PanelSslMode,
                PanelUploadedCertPath = remote.PanelUploadedCertPath,
                PanelUploadedKeyPath = remote.PanelUploadedKeyPath,
                ShadowTlsCamouflageServer = remote.ShadowTlsCamouflageServer,
                OpenVpnNetwork = remote.OpenVpnNetwork,
                OpenVpnSharedCaCertPath = remote.OpenVpnSharedCaCertPath,
                OpenVpnSharedClientCertPath = remote.OpenVpnSharedClientCertPath,
                OpenVpnSharedClientKeyPath = remote.OpenVpnSharedClientKeyPath,
                OpenVpnSharedTlsCryptKeyPath = remote.OpenVpnSharedTlsCryptKeyPath,
                DohEndpoints = remote.DohEndpoints,
            },
            EncryptedTunnelKeyPassphrase = Encrypt(remote.TunnelPrivateKeyPassphrase),
            EncryptedTunnelPassword = Encrypt(remote.TunnelPassword),
            LocalGateway = relay.LocalGateway ?? new LocalGatewayConfig()
        };
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
        public int SchemaVersion { get; set; } = CurrentSchemaVersion;
        public string? EncryptedLicenseKey { get; set; }
        public List<PersistedRelayConfig>? Relays { get; set; }
    }

    private sealed class PersistedRelayConfig
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = "Relay";
        public string GatewayType { get; set; } = GatewayTypes.Remote;
        public bool Enabled { get; set; } = true;
        public string? IncomingAdapterId { get; set; }
        public int IncomingAdapterIfIndex { get; set; } = -1;
        public string? OutgoingAdapterId { get; set; }
        public int OutgoingAdapterIfIndex { get; set; } = -1;
        public int DataPlaneLocalPort { get; set; }
        public int BootstrapSocksLocalPort { get; set; }
        public int BootstrapSocksRemotePort { get; set; } = 16080;
        public RelayOmniPanelConfig? OmniPanel { get; set; }
        public RemoteGatewayConfig? RemoteGateway { get; set; }
        public string? EncryptedTunnelKeyPassphrase { get; set; }
        public string? EncryptedTunnelPassword { get; set; }
        public LocalGatewayConfig? LocalGateway { get; set; }
    }
}

public sealed record PersistedState(ServiceConfig Config, IReadOnlyList<string> WhitelistEntries)
{
    public static PersistedState Empty { get; } = new(new ServiceConfig { SchemaVersion = ConfigStore.CurrentSchemaVersion }, []);
}
