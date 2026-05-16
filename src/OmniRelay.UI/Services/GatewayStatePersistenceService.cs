using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.IO;
using OmniRelay.UI.Models;

namespace OmniRelay.UI.Services;

public sealed class GatewayStatePersistenceService : IGatewayStatePersistenceService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("OmniRelay.UI.GatewayState.v1");

    private static string RootDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OmniRelay");

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
            if (!ContainsProfileFields(json))
            {
                parsed.RemoteProfile = BuildLegacyRemoteProfile(parsed);
                parsed.LocalProfile = new GatewayLocalUiProfileModel
                {
                    OutgoingAdapterIfIndex = parsed.OutgoingAdapterIfIndex,
                    EncryptedLicenseKey = parsed.EncryptedLicenseKey
                };
            }

            if (ContainsLegacyLicenseFields(json) || ContainsLegacyPolicyFields(json))
            {
                // Rewrite once on startup to remove legacy fields no longer persisted by UI.
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

    private static bool ContainsLegacyPolicyFields(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            return root.ValueKind == JsonValueKind.Object &&
                   (root.TryGetProperty("whitelistText", out _) ||
                    root.TryGetProperty("blacklistText", out _));
        }
        catch
        {
            return false;
        }
    }

    private static bool ContainsProfileFields(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            return root.ValueKind == JsonValueKind.Object &&
                   root.TryGetProperty("remoteProfile", out _);
        }
        catch
        {
            return false;
        }
    }

    private static GatewayRemoteUiProfileModel BuildLegacyRemoteProfile(GatewayUiStateModel state)
    {
        return new GatewayRemoteUiProfileModel
        {
            VpsAdapterIfIndex = state.VpsAdapterIfIndex,
            OutgoingAdapterIfIndex = state.OutgoingAdapterIfIndex,
            ProxyPortText = state.ProxyPortText,
            BootstrapSocksLocalPortText = state.BootstrapSocksLocalPortText,
            BootstrapSocksRemotePortText = state.BootstrapSocksRemotePortText,
            BootstrapMode = state.BootstrapMode,
            TunnelHost = state.TunnelHost,
            TunnelSshPortText = state.TunnelSshPortText,
            TunnelRemotePortText = state.TunnelRemotePortText,
            SelectedGatewayProtocol = state.SelectedGatewayProtocol,
            GatewayPublicPortText = state.GatewayPublicPortText,
            GatewayPanelPortText = state.GatewayPanelPortText,
            GatewayPanelConfiguredUser = state.GatewayPanelConfiguredUser,
            EncryptedGatewayPanelConfiguredPassword = state.EncryptedGatewayPanelConfiguredPassword,
            GatewayPanelDomain = state.GatewayPanelDomain,
            GatewayPanelDomainOnly = state.GatewayPanelDomainOnly,
            GatewayPanelUseSsl = state.GatewayPanelUseSsl,
            GatewayPanelSslMode = state.GatewayPanelSslMode,
            GatewayBackendPortText = state.GatewayBackendPortText,
            GatewaySni = state.GatewaySni,
            GatewayTarget = state.GatewayTarget,
            GatewayProtocolTlsEnabled = state.GatewayProtocolTlsEnabled,
            GatewayProtocolTlsServerName = state.GatewayProtocolTlsServerName,
            GatewayProtocolCertPath = state.GatewayProtocolCertPath,
            GatewayProtocolKeyPath = state.GatewayProtocolKeyPath,
            GatewayProtocolTlsMode = state.GatewayProtocolTlsMode,
            GatewayProtocolAlpnCsv = state.GatewayProtocolAlpnCsv,
            GatewayProxyUsername = state.GatewayProxyUsername,
            GatewayProxyPassword = state.GatewayProxyPassword,
            VlessTlsFlow = state.VlessTlsFlow,
            Hysteria2UpMbpsText = state.Hysteria2UpMbpsText,
            Hysteria2DownMbpsText = state.Hysteria2DownMbpsText,
            Hysteria2ObfsPassword = state.Hysteria2ObfsPassword,
            Hysteria2IgnoreClientBandwidth = state.Hysteria2IgnoreClientBandwidth,
            Hysteria2MasqueradeUrl = state.Hysteria2MasqueradeUrl,
            NaiveNetwork = state.NaiveNetwork,
            NaiveQuicCongestionControl = state.NaiveQuicCongestionControl,
            ShadowTlsCamouflageServer = state.ShadowTlsCamouflageServer,
            ShadowTlsStrictMode = state.ShadowTlsStrictMode,
            ShadowTlsWildcardSni = state.ShadowTlsWildcardSni,
            OpenVpnNetwork = state.OpenVpnNetwork,
            IpsecL2tpNetwork = state.IpsecL2tpNetwork,
            GatewayDohEndpointsText = state.GatewayDohEndpointsText,
            TunnelUser = state.TunnelUser,
            TunnelAuthMethod = state.TunnelAuthMethod,
            TunnelKeyPath = state.TunnelKeyPath,
            EncryptedTunnelKeyPassphrase = state.EncryptedTunnelKeyPassphrase,
            EncryptedTunnelPassword = state.EncryptedTunnelPassword,
            EncryptedLicenseKey = state.EncryptedLicenseKey
        };
    }
}
