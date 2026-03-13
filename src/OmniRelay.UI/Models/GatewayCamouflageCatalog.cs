namespace OmniRelay.UI.Models;

public static class GatewayCamouflageCatalog
{
    public static IReadOnlyList<string> All { get; } =
    [
        "www.apple.com:443",
        "www.icloud.com:443",
        "www.amazon.com:443",
        "aws.amazon.com:443",
        "www.oracle.com:443",
        "www.nvidia.com:443",
        "www.amd.com:443",
        "www.intel.com:443",
        "www.tesla.com:443",
        "www.sony.com:443"
    ];

    public static string GetRandom()
    {
        var index = Random.Shared.Next(All.Count);
        return All[index];
    }
}
