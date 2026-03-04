using EstherLink.Core.Policy;

namespace EstherLink.Core.Configuration;

public sealed class ServiceConfig
{
    public string VpsHost { get; set; } = string.Empty;
    public int VpsPort { get; set; } = 443;
    public int LocalProxyListenPort { get; set; } = 19080;
    public int WhitelistAdapterIfIndex { get; set; } = -1;
    public int DefaultAdapterIfIndex { get; set; } = -1;
    public RoutingPolicyMode WhitelistMode { get; set; } = RoutingPolicyMode.DestinationOnly;
    public bool ExpectProxyProtocolV2 { get; set; }
    public string LicenseServerUrl { get; set; } = string.Empty;
    public string LicenseKey { get; set; } = string.Empty;
}
