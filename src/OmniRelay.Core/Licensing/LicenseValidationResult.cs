namespace OmniRelay.Core.Licensing;

public sealed record LicenseValidationResult(
    bool IsValid,
    bool FromCache,
    DateTimeOffset CheckedAtUtc,
    DateTimeOffset? ExpiresAtUtc,
    string? Error,
    bool TransferRequired = false,
    int TransferLimitPerRollingYear = 0,
    int TransfersUsedInWindow = 0,
    int TransfersRemainingInWindow = 0,
    DateTimeOffset? TransferWindowStartAt = null,
    string? ActiveDeviceIdHint = null,
    string? Reason = null,
    string? RequestId = null,
    string? KeyId = null);
