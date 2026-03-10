namespace OmniRelay.UI.Models;

public static class GatewayRealityTargetCatalog
{
    public static IReadOnlyList<(string Target, string Sni)> All { get; } =
    [
        ("www.apple.com:443", "www.apple.com"),
        ("www.icloud.com:443", "www.icloud.com"),
        ("www.amazon.com:443", "www.amazon.com"),
        ("aws.amazon.com:443", "aws.amazon.com"),
        ("www.oracle.com:443", "www.oracle.com"),
        ("www.nvidia.com:443", "www.nvidia.com"),
        ("www.amd.com:443", "www.amd.com"),
        ("www.intel.com:443", "www.intel.com"),
        ("www.tesla.com:443", "www.tesla.com"),
        ("www.sony.com:443", "www.sony.com")
    ];

    public static (string Target, string Sni) GetRandom()
    {
        var index = Random.Shared.Next(All.Count);
        return All[index];
    }
}

