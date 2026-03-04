namespace EstherLink.Core.Licensing;

public sealed record LicenseCacheEntry(
    bool IsValid,
    string LicenseKeyHash,
    DateTimeOffset CheckedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    string Nonce,
    string SignatureAlg,
    string KeyId,
    string Signature,
    string Reason,
    string? Plan,
    DateTimeOffset? LicenseExpiresAtUtc,
    DateTimeOffset ServerTimeUtc,
    string RequestId);
