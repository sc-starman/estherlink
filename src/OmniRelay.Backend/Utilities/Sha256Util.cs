using System.Security.Cryptography;
using System.Text;

namespace OmniRelay.Backend.Utilities;

public static class Sha256Util
{
    public static string HashHex(string input)
    {
        using var sha = SHA256.Create();
        var hashBytes = sha.ComputeHash(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }
}
