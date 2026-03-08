namespace OmniRelay.Backend.Data.Entities;

public sealed class AuditEventEntity
{
    public Guid Id { get; set; }
    public string Actor { get; set; } = string.Empty;
    public string Method { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string PayloadHash { get; set; } = string.Empty;
    public int StatusCode { get; set; }
    public string RequestId { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}
