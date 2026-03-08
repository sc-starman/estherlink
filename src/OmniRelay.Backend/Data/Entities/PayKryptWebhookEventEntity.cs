namespace OmniRelay.Backend.Data.Entities;

public sealed class PayKryptWebhookEventEntity
{
    public Guid Id { get; set; }
    public string EventId { get; set; } = string.Empty;
    public string PayloadHash { get; set; } = string.Empty;
    public DateTimeOffset ReceivedAt { get; set; }
    public DateTimeOffset? ProcessedAt { get; set; }
    public string Result { get; set; } = "received";
    public string RawJson { get; set; } = "{}";
}