namespace OmniRelay.Backend.Contracts.Whitelist;

public sealed class WhitelistLatestResponse
{
    public Guid SetId { get; set; }
    public string CountryCode { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Category { get; set; }
    public int Version { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public IReadOnlyList<string> Entries { get; set; } = [];
    public DateTimeOffset UpdatedAt { get; set; }
}
