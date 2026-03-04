namespace EstherLink.Backend.Data.Entities;

public sealed class WhitelistSetEntity
{
    public Guid Id { get; set; }
    public Guid SetGroupId { get; set; }
    public string CountryCode { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? Category { get; set; }
    public int Version { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }

    public ICollection<WhitelistEntryEntity> Entries { get; set; } = [];
}
