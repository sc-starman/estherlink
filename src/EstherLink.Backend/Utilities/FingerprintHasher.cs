using System.Text.Json;

namespace EstherLink.Backend.Utilities;

public static class FingerprintHasher
{
    public static string ComputeHash(JsonElement fingerprint)
    {
        var normalized = NormalizeFingerprint(fingerprint);
        return Sha256Util.HashHex(normalized);
    }

    private static string NormalizeFingerprint(JsonElement fingerprint)
    {
        return fingerprint.ValueKind switch
        {
            JsonValueKind.String => fingerprint.GetString() ?? string.Empty,
            JsonValueKind.Object or JsonValueKind.Array =>
                JsonSerializer.Serialize(fingerprint),
            _ => fingerprint.GetRawText()
        };
    }
}
