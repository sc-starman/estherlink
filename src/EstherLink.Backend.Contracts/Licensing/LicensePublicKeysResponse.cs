namespace EstherLink.Backend.Contracts.Licensing;

public sealed class LicensePublicKeysResponse
{
    public DateTimeOffset ServerTime { get; set; }
    public IReadOnlyList<LicensePublicKeyItem> Keys { get; set; } = [];
}
