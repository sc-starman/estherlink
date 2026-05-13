namespace OmniRelay.Core.Networking;

public sealed record NetworkAdapterInfo(
    string AdapterId,
    int IfIndex,
    string Name,
    string MacAddress,
    IReadOnlyList<string> IPv4Addresses,
    bool HasDefaultGateway);
