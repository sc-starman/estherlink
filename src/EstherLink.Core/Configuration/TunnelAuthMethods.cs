namespace EstherLink.Core.Configuration;

public static class TunnelAuthMethods
{
    public const string HostKey = "host_key";
    public const string Password = "password";

    public static string Normalize(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return HostKey;
        }

        if (text.Equals(Password, StringComparison.OrdinalIgnoreCase))
        {
            return Password;
        }

        if (text.Equals(HostKey, StringComparison.OrdinalIgnoreCase) ||
            text.Equals("key_file", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("private_key", StringComparison.OrdinalIgnoreCase) ||
            text.Equals("key", StringComparison.OrdinalIgnoreCase))
        {
            return HostKey;
        }

        return HostKey;
    }
}
