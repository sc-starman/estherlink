using System.Security.Cryptography;
using System.Text;

namespace EstherLink.Backend.Security;

public static class AdminApiKeyHasher
{
    public static string Hash(string key, string? pepper)
    {
        var input = $"{key ?? string.Empty}:{pepper ?? string.Empty}";
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes);
    }
}
