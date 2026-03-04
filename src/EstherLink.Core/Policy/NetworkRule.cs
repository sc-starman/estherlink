using System.Net;

namespace EstherLink.Core.Policy;

public sealed class NetworkRule
{
    private NetworkRule(string rawValue, IPAddress networkAddress, int prefixLength)
    {
        RawValue = rawValue;
        NetworkAddress = networkAddress;
        PrefixLength = prefixLength;
    }

    public string RawValue { get; }
    public IPAddress NetworkAddress { get; }
    public int PrefixLength { get; }
    public System.Net.Sockets.AddressFamily AddressFamily => NetworkAddress.AddressFamily;

    public static bool TryParse(string text, out NetworkRule? rule, out string? error)
    {
        rule = null;
        error = null;

        var value = (text ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(value))
        {
            error = "Entry is empty.";
            return false;
        }

        if (value.Contains('/'))
        {
            var parts = value.Split('/', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2)
            {
                error = $"Invalid CIDR format: '{value}'.";
                return false;
            }

            if (!IPAddress.TryParse(parts[0], out var address))
            {
                error = $"Invalid IP address in CIDR: '{value}'.";
                return false;
            }

            if (!int.TryParse(parts[1], out var prefix))
            {
                error = $"Invalid prefix in CIDR: '{value}'.";
                return false;
            }

            if (!IsPrefixValid(address, prefix))
            {
                error = $"Prefix out of range for '{value}'.";
                return false;
            }

            rule = new NetworkRule(value, NormalizeToNetwork(address, prefix), prefix);
            return true;
        }

        if (!IPAddress.TryParse(value, out var singleAddress))
        {
            error = $"Invalid IP address: '{value}'.";
            return false;
        }

        var hostPrefix = singleAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128;
        rule = new NetworkRule(value, singleAddress, hostPrefix);
        return true;
    }

    public bool Contains(IPAddress address)
    {
        if (address.AddressFamily != NetworkAddress.AddressFamily)
        {
            return false;
        }

        var candidate = address.GetAddressBytes();
        var network = NetworkAddress.GetAddressBytes();
        var fullBytes = PrefixLength / 8;
        var remainingBits = PrefixLength % 8;

        for (var i = 0; i < fullBytes; i++)
        {
            if (candidate[i] != network[i])
            {
                return false;
            }
        }

        if (remainingBits == 0)
        {
            return true;
        }

        var mask = (byte)~(0xFF >> remainingBits);
        return (candidate[fullBytes] & mask) == (network[fullBytes] & mask);
    }

    private static bool IsPrefixValid(IPAddress address, int prefix)
    {
        return address.AddressFamily switch
        {
            System.Net.Sockets.AddressFamily.InterNetwork => prefix is >= 0 and <= 32,
            System.Net.Sockets.AddressFamily.InterNetworkV6 => prefix is >= 0 and <= 128,
            _ => false
        };
    }

    private static IPAddress NormalizeToNetwork(IPAddress address, int prefixLength)
    {
        var bytes = address.GetAddressBytes();
        var fullBytes = prefixLength / 8;
        var remainingBits = prefixLength % 8;

        for (var i = fullBytes + (remainingBits > 0 ? 1 : 0); i < bytes.Length; i++)
        {
            bytes[i] = 0;
        }

        if (remainingBits > 0 && fullBytes < bytes.Length)
        {
            var mask = (byte)~(0xFF >> remainingBits);
            bytes[fullBytes] = (byte)(bytes[fullBytes] & mask);
        }

        return new IPAddress(bytes);
    }
}
