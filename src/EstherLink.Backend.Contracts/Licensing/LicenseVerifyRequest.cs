using System.Text.Json;

namespace EstherLink.Backend.Contracts.Licensing;

public sealed class LicenseVerifyRequest
{
    public string LicenseKey { get; set; } = string.Empty;
    public JsonElement Fingerprint { get; set; }
    public string AppVersion { get; set; } = string.Empty;
    public string Nonce { get; set; } = string.Empty;
    public bool TransferRequested { get; set; }
}
