namespace OmniRelay.Backend.Data.Entities;

public sealed class CommerceOrderEntity
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string OrderType { get; set; } = "license";
    public decimal BaseFiatAmount { get; set; }
    public decimal DiscountAmount { get; set; }
    public decimal FiatAmount { get; set; }
    public string Currency { get; set; } = "USD";
    public string Status { get; set; } = "created";
    public Guid? DiscountCouponId { get; set; }
    public string? DiscountCode { get; set; }
    public int? DiscountPercent { get; set; }
    public bool DiscountConsumed { get; set; }
    public Guid? IssuedLicenseId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public Models.ApplicationUser User { get; set; } = null!;
    public DiscountCouponEntity? DiscountCoupon { get; set; }
    public LicenseEntity? IssuedLicense { get; set; }
    public ICollection<PayKryptIntentEntity> PayKryptIntents { get; set; } = [];
}
