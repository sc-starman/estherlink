using System.Text.Json;
using OmniRelay.Core.Configuration;
using OmniRelay.UI.Models;
using OmniRelay.UI.Services;

namespace OmniRelay.UI.Tests;

public sealed class ConnectorCoreGatewaySpecBuilderTests
{
    private const string RelayId = "e4ccc282a1004b62ad2cda5770d6e32d";

    [Fact]
    public void BuildJson_MapsRemoteSingBoxGateway()
    {
        var request = BuildRequest(GatewayProtocols.VlessTlsSingbox);
        var json = ConnectorCoreGatewaySpecBuilder.BuildJson(request, new ConnectorCoreGatewaySpecOptions
        {
            ConnectorCoreVersion = "2.2.13",
            PanelArtifactRemotePath = "/var/lib/omnirelay/bootstrap/panel.tar.gz",
            PanelArtifactSha256 = new string('a', 64),
            ProtocolCertRemotePath = "/var/lib/omnirelay/bootstrap/protocol.crt",
            ProtocolKeyRemotePath = "/var/lib/omnirelay/bootstrap/protocol.key"
        });

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal("omnirelay.io/v1alpha1", root.GetProperty("apiVersion").GetString());
        Assert.Equal(RelayId, root.GetProperty("relayId").GetString());
        Assert.Equal("full_tunnel", root.GetProperty("gateway").GetProperty("connectorMode").GetString());
        Assert.Equal(15004, root.GetProperty("tunnel").GetProperty("backendPort").GetInt32());
        Assert.Equal("/var/lib/omnirelay/bootstrap/protocol.crt", root.GetProperty("singBox").GetProperty("tls").GetProperty("certFile").GetString());
        Assert.Equal("certbot", root.GetProperty("panel").GetProperty("tlsMode").GetString());
    }

    [Fact]
    public void BuildJson_MapsIpsecAsInternalTunnelAndKeepsDurablePsk()
    {
        var request = BuildRequest(GatewayProtocols.IpsecL2tpSingbox, ipsecPsk: "fixed-durable-pre-shared-key");

        var json = ConnectorCoreGatewaySpecBuilder.BuildJson(request, new ConnectorCoreGatewaySpecOptions
        {
            ConnectorCoreVersion = "2.2.13"
        });

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal("internal_tunnel", root.GetProperty("gateway").GetProperty("connectorMode").GetString());
        Assert.Equal("fixed-durable-pre-shared-key", root.GetProperty("ipsecL2tp").GetProperty("preSharedKey").GetString());
    }

    [Fact]
    public void BuildJson_RejectsLegacyUnsafeRelayId()
    {
        var request = BuildRequest(GatewayProtocols.VlessTlsSingbox, relayId: "legacy-relay");

        var error = Assert.Throws<InvalidOperationException>(() =>
            ConnectorCoreGatewaySpecBuilder.BuildJson(request, new ConnectorCoreGatewaySpecOptions { ConnectorCoreVersion = "2.2.13" }));

        Assert.Contains("32 hexadecimal", error.Message);
    }

    [Fact]
    public void BuildJson_RequiresRemoteOpenVpnAssets()
    {
        var request = BuildRequest(GatewayProtocols.OpenVpnTcpSingbox);

        var error = Assert.Throws<InvalidOperationException>(() =>
            ConnectorCoreGatewaySpecBuilder.BuildJson(request, new ConnectorCoreGatewaySpecOptions { ConnectorCoreVersion = "2.2.13" }));

        Assert.Contains("remote OpenVPN shared asset paths", error.Message);
    }

    private static GatewayDeploymentRequest BuildRequest(
        string protocol,
        string relayId = RelayId,
        string ipsecPsk = "")
    {
        return new GatewayDeploymentRequest
        {
            RelayId = relayId,
            Config = new ServiceConfig
            {
                TunnelHost = "203.0.113.10",
                TunnelUser = "omnirelay",
                TunnelSshPort = 22,
                TunnelRemotePort = 15004,
                FrpServerPort = 7000,
                FrpRuntimeToken = "frp-token"
            },
            SelectedGatewayProtocol = protocol,
            GatewayPublicPort = protocol == GatewayProtocols.IpsecL2tpSingbox ? 1701 : 443,
            GatewayPanelPort = 3054,
            GatewayPanelUser = "admin",
            GatewayPanelPassword = "panel-secret",
            GatewayPanelDomain = "panel.example.com",
            GatewayPanelSslEnabled = true,
            GatewayPanelSslMode = "letsencrypt",
            GatewayProtocolTlsEnabled = protocol == GatewayProtocols.VlessTlsSingbox,
            GatewayProtocolTlsServerName = "vpn.example.com",
            GatewayProxyUsername = "proxy-user",
            GatewayProxyPassword = "proxy-secret",
            Hysteria2UpMbps = 100,
            Hysteria2DownMbps = 100,
            OpenVpnNetwork = "10.29.0.0/24",
            IpsecL2tpNetwork = "10.39.0.0/24",
            IpsecL2tpPreSharedKey = ipsecPsk,
            TunnelProbeUrl = "https://1.1.1.1/cdn-cgi/trace",
            GatewayDohEndpoints = "https://1.1.1.1/dns-query, https://8.8.8.8/dns-query"
        };
    }
}
