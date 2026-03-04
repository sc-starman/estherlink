namespace EstherLink.Backend.Contracts.Whitelist;

public sealed class WhitelistDiffResponse
{
    public Guid SetId { get; set; }
    public int FromVersion { get; set; }
    public int ToVersion { get; set; }
    public IReadOnlyList<string> Added { get; set; } = [];
    public IReadOnlyList<string> Removed { get; set; } = [];
}
