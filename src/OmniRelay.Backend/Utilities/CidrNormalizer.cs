using System.Net;

namespace OmniRelay.Backend.Utilities;

public static class CidrNormalizer
{
    public static bool TryNormalize(string input, out string normalized, out string? error)
    {
        normalized = string.Empty;
        error = null;

        var value = (input ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            error = "Entry is empty.";
            return false;
        }

        if (!value.Contains('/'))
        {
            if (!IPAddress.TryParse(value, out var hostIp))
            {
                error = $"Invalid IP '{value}'.";
                return false;
            }

            normalized = hostIp.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                ? $"{hostIp}/32"
                : $"{hostIp}/128";
            return true;
        }

        if (!IPNetwork.TryParse(value, out var network))
        {
            error = $"Invalid CIDR '{value}'.";
            return false;
        }

        normalized = network.ToString();
        return true;
    }
}
