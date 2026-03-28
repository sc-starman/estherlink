namespace OmniRelay.Backend.Data.Entities;

public sealed class DiscountCouponEntity
{
    public Guid Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string NormalizedCode { get; set; } = string.Empty;
    public int DiscountPercent { get; set; }
    public int MaxUses { get; set; }
    public int TimesRedeemed { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset? DisabledAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public Guid CreatedByUserId { get; set; }

    public Models.ApplicationUser CreatedByUser { get; set; } = null!;
    public ICollection<CommerceOrderEntity> Orders { get; set; } = [];
}
