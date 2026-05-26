using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OmniRelay.Core.Configuration;

namespace OmniRelay.Service.Runtime;

public sealed class ConfigStore
{
    public const int CurrentSchemaVersion = 10;

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
                FrpServerProfiles = (stored.FrpServerProfiles ?? [])
                    .Where(x => x is not null)
                    .Select(x => new FrpServerProfile
                    {
                        TunnelHost = x!.TunnelHost ?? string.Empty,
                        FrpServerPort = x.FrpServerPort,
                        AuthToken = Decrypt(x.EncryptedAuthToken)
                    })
                    .ToList(),
                Relays = (stored.Relays ?? [])
                    .Where(x => x is not null)
                    .Select(x => ToRelayConfig(x!))
                    .Where(x => !string.IsNullOrWhiteSpace(x.Id))
                    .ToList()
            };

            EnsureRelayPorts(config.Relays);
            EnsureFrpServerProfiles(config);
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
            EnsureFrpServerProfiles(config);

            var stored = new PersistedConfig
            {
                SchemaVersion = CurrentSchemaVersion,
                EncryptedLicenseKey = Encrypt(config.LicenseKey),
                FrpServerProfiles = (config.FrpServerProfiles ?? [])
                    .Where(x => x is not null && !string.IsNullOrWhiteSpace(x.TunnelHost))
                    .Select(x => new PersistedFrpServerProfile
                    {
                        TunnelHost = x.TunnelHost.Trim(),
                        FrpServerPort = x.FrpServerPort is > 0 and <= 65535 ? x.FrpServerPort : 7000,
                        EncryptedAuthToken = Encrypt(x.AuthToken)
                    })
                    .ToList(),
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
        var usedRemotePortsByHost = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);
        var nextRemoteByHost = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var usedLocalGatewayPorts = new HashSet<int>();
        var usedLocalOmniPanelPorts = new HashSet<int>();
        var usedRelayIds = new HashSet<string>(StringComparer.Ordinal);
        var next = 24080;
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
                relay.FrpProfilePortOverride = relay.FrpProfilePortOverride is > 0 and <= 65535
                    ? relay.FrpProfilePortOverride
                    : 7000;

                var hostKey = BuildFrpServerProfileKey(relay.RemoteGateway.TunnelHost, relay.FrpProfilePortOverride);
                if (!usedRemotePortsByHost.TryGetValue(hostKey, out var usedRemotePorts))
                {
                    usedRemotePorts = new HashSet<int>();
                    usedRemotePortsByHost[hostKey] = usedRemotePorts;
                    nextRemoteByHost[hostKey] = 15000;
                }

                var requestedRemotePort = relay.RemoteGateway.TunnelRemotePort;
                if (requestedRemotePort > 0 && requestedRemotePort <= 65535 && usedRemotePorts.Add(requestedRemotePort))
                {
                    if (requestedRemotePort >= nextRemoteByHost[hostKey])
                    {
                        nextRemoteByHost[hostKey] = requestedRemotePort + 1;
                    }
                }
                else
                {
                    var nextRemote = nextRemoteByHost[hostKey];
                    relay.RemoteGateway.TunnelRemotePort = AllocatePort(usedRemotePorts, ref nextRemote);
                    nextRemoteByHost[hostKey] = nextRemote;
                }

                // FRP runtime uses a single data remote port on RemoteGateway.
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
            // Legacy field is no longer used; keep backwards compatibility by mirroring into RemoteGateway if needed.
            OmniPanel = stored.OmniPanel ?? new RelayOmniPanelConfig(),
            RemoteGateway = stored.RemoteGateway ?? new RemoteGatewayConfig(),
            LocalGateway = stored.LocalGateway ?? new LocalGatewayConfig()
        };

        relay.RemoteGateway.TunnelPrivateKeyPassphrase = Decrypt(stored.EncryptedTunnelKeyPassphrase);
        relay.RemoteGateway.TunnelPassword = Decrypt(stored.EncryptedTunnelPassword);
        if (relay.RemoteGateway.TunnelRemotePort <= 0 && stored.TunnelRemotePort > 0)
        {
            relay.RemoteGateway.TunnelRemotePort = stored.TunnelRemotePort;
        }
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
            TunnelRemotePort = relay.RemoteGateway?.TunnelRemotePort is > 0 and <= 65535
                ? relay.RemoteGateway.TunnelRemotePort
                : 0,
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

    internal static void EnsureFrpServerProfiles(ServiceConfig config)
    {
        config.FrpServerProfiles ??= [];
        var profiles = new Dictionary<string, FrpServerProfile>(StringComparer.OrdinalIgnoreCase);

        foreach (var existing in config.FrpServerProfiles.Where(x => x is not null))
        {
            var host = (existing.TunnelHost ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(host))
            {
                continue;
            }

            var port = existing.FrpServerPort is > 0 and <= 65535 ? existing.FrpServerPort : 7000;
            var key = BuildFrpServerProfileKey(host, port);
            var token = string.IsNullOrWhiteSpace(existing.AuthToken) ? GenerateFrpToken() : existing.AuthToken.Trim();
            if (profiles.TryGetValue(key, out var current))
            {
                if (string.IsNullOrWhiteSpace(current.AuthToken))
                {
                    current.AuthToken = token;
                }
                continue;
            }

            profiles[key] = new FrpServerProfile
            {
                TunnelHost = host,
                FrpServerPort = port,
                AuthToken = token
            };
        }

        foreach (var relay in config.Relays.Where(x => x is not null))
        {
            relay.RemoteGateway ??= new RemoteGatewayConfig();
            if (!string.Equals(GatewayTypes.Normalize(relay.GatewayType), GatewayTypes.Remote, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var host = (relay.RemoteGateway.TunnelHost ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(host))
            {
                continue;
            }

            var requestedPort = relay.FrpProfilePortOverride is > 0 and <= 65535 ? relay.FrpProfilePortOverride : 7000;
            var key = BuildFrpServerProfileKey(host, requestedPort);
            if (!profiles.TryGetValue(key, out var profile))
            {
                var seedToken = string.IsNullOrWhiteSpace(relay.FrpProfileTokenOverride)
                    ? GenerateFrpToken()
                    : relay.FrpProfileTokenOverride.Trim();
                profile = new FrpServerProfile
                {
                    TunnelHost = host,
                    FrpServerPort = requestedPort,
                    AuthToken = seedToken
                };
                profiles[key] = profile;
            }

            relay.FrpProfileTokenOverride = profile.AuthToken;
            relay.FrpProfilePortOverride = profile.FrpServerPort is > 0 and <= 65535 ? profile.FrpServerPort : 7000;
        }

        config.FrpServerProfiles = profiles.Values
            .OrderBy(x => x.TunnelHost, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.FrpServerPort)
            .ToList();

        config.FrpRuntimeToken = config.FrpServerProfiles.FirstOrDefault()?.AuthToken ?? string.Empty;
    }

    internal static string BuildFrpServerProfileKey(string tunnelHost, int frpServerPort)
    {
        var host = (tunnelHost ?? string.Empty).Trim().ToLowerInvariant();
        return host;
    }

    private static string GenerateFrpToken()
    {
        return Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
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
        public List<PersistedFrpServerProfile>? FrpServerProfiles { get; set; }
        public List<PersistedRelayConfig>? Relays { get; set; }
    }

    private sealed class PersistedFrpServerProfile
    {
        public string? TunnelHost { get; set; }
        public int FrpServerPort { get; set; } = 7000;
        public string? EncryptedAuthToken { get; set; }
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
        public int TunnelRemotePort { get; set; } = 16080;
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



