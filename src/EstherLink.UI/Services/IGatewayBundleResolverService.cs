namespace EstherLink.UI.Services;

public interface IGatewayBundleResolverService
{
    GatewayBundleDescriptor Resolve();
}

public sealed class GatewayBundleDescriptor
{
    public required string BundleFilePath { get; init; }
    public required string BundleSha256 { get; init; }
    public required string BundleVersion { get; init; }
}
