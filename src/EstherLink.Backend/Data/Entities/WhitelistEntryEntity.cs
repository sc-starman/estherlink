namespace EstherLink.Backend.Data.Entities;

public sealed class WhitelistEntryEntity
{
    public Guid Id { get; set; }
    public Guid WhitelistSetId { get; set; }
    public string Cidr { get; set; } = string.Empty;
    public string? Note { get; set; }

    public WhitelistSetEntity WhitelistSet { get; set; } = null!;
}
