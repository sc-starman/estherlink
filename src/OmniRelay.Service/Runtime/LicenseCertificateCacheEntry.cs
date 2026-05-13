using OmniRelay.Backend.Contracts.Licensing;

namespace OmniRelay.Service.Runtime;

public sealed record LicenseCertificateCacheEntry(
    string LicenseKeyHash,
    DateTimeOffset StoredAtUtc,
    LicenseCertificate Certificate);
