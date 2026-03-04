namespace EstherLink.Core.Licensing;

public sealed record LicenseCacheEntry(
    bool IsValid,
    string LicenseKeyHash,
    DateTimeOffset CheckedAtUtc,
    DateTimeOffset ExpiresAtUtc);
