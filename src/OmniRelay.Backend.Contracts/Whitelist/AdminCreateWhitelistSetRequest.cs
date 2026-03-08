namespace OmniRelay.Backend.Contracts.Whitelist;

public sealed class AdminCreateWhitelistSetRequest
{
    public string CountryCode { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Category { get; set; }
    public IReadOnlyList<string> Entries { get; set; } = [];
}
