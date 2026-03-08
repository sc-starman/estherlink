namespace OmniRelay.Backend.Contracts.Whitelist;

public sealed class AdminPublishWhitelistRequest
{
    public IReadOnlyList<string> Entries { get; set; } = [];
    public string? Note { get; set; }
}
