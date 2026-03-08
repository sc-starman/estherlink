namespace OmniRelay.Backend.Data.Entities;

public sealed class UserLicenseEntity
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid LicenseId { get; set; }
    public string Source { get; set; } = "trial";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatesEntitledUntil { get; set; }

    public Models.ApplicationUser User { get; set; } = null!;
    public LicenseEntity License { get; set; } = null!;
}