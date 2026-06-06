using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using OmniRelay.Core.Configuration;
using OmniRelay.UI.Models;

namespace OmniRelay.UI.Services;

public static class ConnectorCoreGatewaySpecBuilder
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true
    };

    public static string BuildJson(GatewayDeploymentRequest request, ConnectorCoreGatewaySpecOptions options)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(options);

        var relayId = request.RelayId.Trim().ToLowerInvariant();
        if (!Regex.IsMatch(relayId, "^[a-f0-9]{32}$"))
        {
            throw new InvalidOperationException("Connector-core relay id must contain exactly 32 hexadecimal characters.");
        }

        var protocol = GatewayProtocols.Normalize(request.SelectedGatewayProtocol);
        var gatewayType = GatewayTypes.Normalize(request.Config.GatewayType);
        var internalTunnel = protocol is GatewayProtocols.OpenVpnTcpSingbox or GatewayProtocols.IpsecL2tpSingbox;
        var panelHost = FirstNonEmpty(options.PanelPublicHost, request.GatewayPanelDomain, request.Config.TunnelHost);
        var panelTlsMode = MapPanelTlsMode(request.GatewayPanelSslMode);
        var protocolCert = FirstNonEmpty(options.ProtocolCertRemotePath, request.GatewayProtocolCertPath);
        var protocolKey = FirstNonEmpty(options.ProtocolKeyRemotePath, request.GatewayProtocolKeyPath);
        var proxyPassword = request.GatewayProxyPassword;
        var shadowsocksPassword = FirstNonEmpty(options.ShadowsocksServerPassword, proxyPassword);

        var document = new GatewaySpecDocument
        {
            ApiVersion = "omnirelay.io/v1alpha1",
            Kind = "Gateway",
            RelayId = relayId,
            Release = new ReleaseDocument
            {
                Channel = NormalizeChannel(options.ReleaseChannel),
                ConnectorCoreVersion = Require(options.ConnectorCoreVersion, "Connector-core version")
            },
            Gateway = new GatewayDocument
            {
                Type = gatewayType,
                Protocol = protocol,
                PublicPort = request.GatewayPublicPort,
                ConnectorMode = internalTunnel ? "internal_tunnel" : "full_tunnel"
            },
            Tunnel = new TunnelDocument
            {
                BackendHost = "127.0.0.1",
                BackendPort = request.Config.TunnelRemotePort,
                FrpServerPort = request.Config.FrpServerPort,
                FrpAuthToken = gatewayType == GatewayTypes.Remote
                    ? Require(request.Config.FrpRuntimeToken, "FRP auth token")
                    : request.Config.FrpRuntimeToken.Trim(),
                ProbeUrls = [Require(request.TunnelProbeUrl, "Tunnel probe URL")],
                TimeoutSeconds = 20
            },
            Dns = new DnsDocument
            {
                DohEndpoints = SplitCsv(request.GatewayDohEndpoints)
            },
            SingBox = new SingBoxDocument
            {
                Tls = new TlsDocument
                {
                    Enabled = request.GatewayProtocolTlsEnabled,
                    ServerName = request.GatewayProtocolTlsServerName.Trim(),
                    CertFile = protocolCert,
                    KeyFile = protocolKey
                },
                Proxy = new CredentialDocument
                {
                    Username = request.GatewayProxyUsername.Trim(),
                    Password = proxyPassword
                },
                VlessFlow = request.VlessTlsFlow.Trim(),
                Hysteria2 = new Hysteria2Document
                {
                    UpMbps = request.Hysteria2UpMbps,
                    DownMbps = request.Hysteria2DownMbps,
                    ObfsPassword = request.Hysteria2ObfsPassword.Trim(),
                    IgnoreClientBandwidth = request.Hysteria2IgnoreClientBandwidth,
                    MasqueradeUrl = request.Hysteria2MasqueradeUrl.Trim()
                },
                Naive = new NaiveDocument
                {
                    Network = request.NaiveNetwork.Trim(),
                    QuicCongestionControl = request.NaiveQuicCongestionControl.Trim()
                },
                ShadowsocksServerPassword = shadowsocksPassword,
                ShadowTls = new ShadowTlsDocument
                {
                    CamouflageServer = request.ShadowTlsCamouflageServer.Trim(),
                    StrictMode = request.ShadowTlsStrictMode,
                    WildcardSni = request.ShadowTlsWildcardSni.Trim()
                }
            },
            Panel = new PanelDocument
            {
                Port = request.GatewayPanelPort,
                Username = FirstNonEmpty(options.PanelUsername, request.GatewayPanelUser),
                Password = FirstNonEmpty(options.PanelPassword, request.GatewayPanelPassword),
                PublicHost = Require(panelHost, "Panel public host"),
                Domain = request.GatewayPanelDomain.Trim(),
                DomainOnly = request.GatewayPanelDomainOnly,
                TlsEnabled = request.GatewayPanelSslEnabled,
                TlsMode = request.GatewayPanelSslEnabled ? panelTlsMode : string.Empty,
                CertFile = request.GatewayPanelSslEnabled && panelTlsMode == "uploaded"
                    ? FirstNonEmpty(options.PanelCertRemotePath, request.GatewayPanelCertRemotePath)
                    : string.Empty,
                KeyFile = request.GatewayPanelSslEnabled && panelTlsMode == "uploaded"
                    ? FirstNonEmpty(options.PanelKeyRemotePath, request.GatewayPanelKeyRemotePath)
                    : string.Empty,
                ArtifactFile = options.PanelArtifactRemotePath.Trim(),
                ArtifactUrl = options.PanelArtifactUrl.Trim(),
                ArtifactSha256 = options.PanelArtifactSha256.Trim().ToLowerInvariant()
            },
            OpenVpn = new OpenVpnDocument
            {
                Network = request.OpenVpnNetwork.Trim(),
                PublicHost = request.Config.TunnelHost.Trim(),
                SharedCaCertFile = options.OpenVpnSharedCaCertRemotePath.Trim(),
                SharedClientCertFile = options.OpenVpnSharedClientCertRemotePath.Trim(),
                SharedClientKeyFile = options.OpenVpnSharedClientKeyRemotePath.Trim(),
                SharedTlsCryptKeyFile = options.OpenVpnSharedTlsCryptKeyRemotePath.Trim()
            },
            IpsecL2tp = new IpsecL2tpDocument
            {
                Network = request.IpsecL2tpNetwork.Trim(),
                PreSharedKey = request.IpsecL2tpPreSharedKey.Trim()
            }
        };

        ValidateProtocolInputs(document);
        return JsonSerializer.Serialize(document, JsonOptions) + Environment.NewLine;
    }

    private static void ValidateProtocolInputs(GatewaySpecDocument document)
    {
        var protocol = document.Gateway.Protocol;
        if (protocol == GatewayProtocols.IpsecL2tpSingbox && document.IpsecL2tp.PreSharedKey.Length < 16)
        {
            throw new InvalidOperationException("IPSec/L2TP pre-shared key must contain at least 16 characters.");
        }
        if (protocol is GatewayProtocols.ShadowsocksSingbox or GatewayProtocols.ShadowTlsV3ShadowsocksSingbox &&
            string.IsNullOrWhiteSpace(document.SingBox.ShadowsocksServerPassword))
        {
            throw new InvalidOperationException("Shadowsocks server password is required for connector-core cutover.");
        }
        if (protocol == GatewayProtocols.OpenVpnTcpSingbox)
        {
            var assets = new[]
            {
                document.OpenVpn.SharedCaCertFile,
                document.OpenVpn.SharedClientCertFile,
                document.OpenVpn.SharedClientKeyFile,
                document.OpenVpn.SharedTlsCryptKeyFile
            };
            if (assets.Any(string.IsNullOrWhiteSpace))
            {
                throw new InvalidOperationException("All remote OpenVPN shared asset paths are required for connector-core cutover.");
            }
        }
    }

    private static string[] SplitCsv(string value) =>
        value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string FirstNonEmpty(params string?[] values) =>
        values.Select(value => value?.Trim() ?? string.Empty).FirstOrDefault(value => value.Length > 0) ?? string.Empty;

    private static string Require(string? value, string label)
    {
        var normalized = value?.Trim() ?? string.Empty;
        return normalized.Length > 0 ? normalized : throw new InvalidOperationException($"{label} is required.");
    }

    private static string NormalizeChannel(string value) =>
        string.Equals(value.Trim(), "beta", StringComparison.OrdinalIgnoreCase) ? "beta" : "stable";

    private static string MapPanelTlsMode(string value) => value.Trim().ToLowerInvariant() switch
    {
        "uploaded" => "uploaded",
        "self_signed" => "self_signed",
        "self-signed" => "self_signed",
        _ => "certbot"
    };

    private sealed class GatewaySpecDocument
    {
        public required string ApiVersion { get; init; }
        public required string Kind { get; init; }
        public required string RelayId { get; init; }
        public required ReleaseDocument Release { get; init; }
        public required GatewayDocument Gateway { get; init; }
        public required TunnelDocument Tunnel { get; init; }
        public required DnsDocument Dns { get; init; }
        public required SingBoxDocument SingBox { get; init; }
        public required PanelDocument Panel { get; init; }
        public required OpenVpnDocument OpenVpn { get; init; }
        public required IpsecL2tpDocument IpsecL2tp { get; init; }
    }

    private sealed class ReleaseDocument
    {
        public required string Channel { get; init; }
        public required string ConnectorCoreVersion { get; init; }
    }

    private sealed class GatewayDocument
    {
        public required string Type { get; init; }
        public required string Protocol { get; init; }
        public int PublicPort { get; init; }
        public required string ConnectorMode { get; init; }
    }

    private sealed class TunnelDocument
    {
        public required string BackendHost { get; init; }
        public int BackendPort { get; init; }
        public int FrpServerPort { get; init; }
        public required string FrpAuthToken { get; init; }
        public required string[] ProbeUrls { get; init; }
        public int TimeoutSeconds { get; init; }
    }

    private sealed class DnsDocument
    {
        public required string[] DohEndpoints { get; init; }
    }

    private sealed class SingBoxDocument
    {
        public required TlsDocument Tls { get; init; }
        public required CredentialDocument Proxy { get; init; }
        public required string VlessFlow { get; init; }
        public required Hysteria2Document Hysteria2 { get; init; }
        public required NaiveDocument Naive { get; init; }
        public required string ShadowsocksServerPassword { get; init; }
        public required ShadowTlsDocument ShadowTls { get; init; }
    }

    private sealed class TlsDocument
    {
        public bool Enabled { get; init; }
        public required string ServerName { get; init; }
        public required string CertFile { get; init; }
        public required string KeyFile { get; init; }
    }

    private sealed class CredentialDocument
    {
        public required string Username { get; init; }
        public required string Password { get; init; }
    }

    private sealed class Hysteria2Document
    {
        public int UpMbps { get; init; }
        public int DownMbps { get; init; }
        public required string ObfsPassword { get; init; }
        public bool IgnoreClientBandwidth { get; init; }
        public required string MasqueradeUrl { get; init; }
    }

    private sealed class NaiveDocument
    {
        public required string Network { get; init; }
        public required string QuicCongestionControl { get; init; }
    }

    private sealed class ShadowTlsDocument
    {
        public required string CamouflageServer { get; init; }
        public bool StrictMode { get; init; }
        public required string WildcardSni { get; init; }
    }

    private sealed class PanelDocument
    {
        public int Port { get; init; }
        public required string Username { get; init; }
        public required string Password { get; init; }
        public required string PublicHost { get; init; }
        public required string Domain { get; init; }
        public bool DomainOnly { get; init; }
        public bool TlsEnabled { get; init; }
        public required string TlsMode { get; init; }
        public required string CertFile { get; init; }
        public required string KeyFile { get; init; }
        public required string ArtifactFile { get; init; }
        public required string ArtifactUrl { get; init; }
        public required string ArtifactSha256 { get; init; }
    }

    private sealed class OpenVpnDocument
    {
        public required string Network { get; init; }
        public required string PublicHost { get; init; }
        public required string SharedCaCertFile { get; init; }
        public required string SharedClientCertFile { get; init; }
        public required string SharedClientKeyFile { get; init; }
        public required string SharedTlsCryptKeyFile { get; init; }
    }

    private sealed class IpsecL2tpDocument
    {
        public required string Network { get; init; }
        public required string PreSharedKey { get; init; }
    }
}
