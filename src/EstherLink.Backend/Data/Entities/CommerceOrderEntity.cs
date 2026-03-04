namespace EstherLink.Backend.Data.Entities;

public sealed class CommerceOrderEntity
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string OrderType { get; set; } = "license";
    public decimal FiatAmount { get; set; }
    public string Currency { get; set; } = "USD";
    public string Status { get; set; } = "created";
    public Guid? IssuedLicenseId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public Models.ApplicationUser User { get; set; } = null!;
    public LicenseEntity? IssuedLicense { get; set; }
    public ICollection<PayKryptIntentEntity> PayKryptIntents { get; set; } = [];
}