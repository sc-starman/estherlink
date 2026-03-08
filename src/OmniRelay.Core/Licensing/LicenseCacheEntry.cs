namespace OmniRelay.Core.Licensing;

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
    bool TransferRequired,
    int TransferLimitPerRollingYear,
    int TransfersUsedInWindow,
    int TransfersRemainingInWindow,
    DateTimeOffset? TransferWindowStartAt,
    string? ActiveDeviceIdHint,
    string? Plan,
    DateTimeOffset? LicenseExpiresAtUtc,
    DateTimeOffset ServerTimeUtc,
    string RequestId);
