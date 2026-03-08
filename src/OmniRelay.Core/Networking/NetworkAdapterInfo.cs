namespace OmniRelay.Core.Networking;

public sealed record NetworkAdapterInfo(
    int IfIndex,
    string Name,
    IReadOnlyList<string> IPv4Addresses,
    bool HasDefaultGateway);
