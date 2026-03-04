namespace EstherLink.Backend.Data.Entities;

public sealed class PayKryptIntentEntity
{
    public Guid Id { get; set; }
    public Guid OrderId { get; set; }
    public string PayKryptIntentId { get; set; } = string.Empty;
    public string Status { get; set; } = "awaiting_payment";
    public DateTimeOffset? ExpiresAt { get; set; }
    public string RawJson { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public CommerceOrderEntity Order { get; set; } = null!;
}