namespace EstherLink.Core.Licensing;

public sealed record LicenseValidationResult(
    bool IsValid,
    bool FromCache,
    DateTimeOffset CheckedAtUtc,
    DateTimeOffset? ExpiresAtUtc,
    string? Error,
    string? Reason = null,
    string? RequestId = null,
    string? KeyId = null);
