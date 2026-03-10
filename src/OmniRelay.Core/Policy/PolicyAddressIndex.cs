using System.Net;
using System.Net.Sockets;

namespace OmniRelay.Core.Policy;

public sealed class PolicyAddressIndex
{
    private readonly HashSet<uint> _ipv4Exact = [];
    private readonly HashSet<Ipv6Key> _ipv6Exact = [];
    private readonly IpPrefixTrie _ipv4Prefixes = new(AddressFamily.InterNetwork);
    private readonly IpPrefixTrie _ipv6Prefixes = new(AddressFamily.InterNetworkV6);

    private PolicyAddressIndex()
    {
    }

    public static PolicyAddressIndex Build(IEnumerable<NetworkRule> rules)
    {
        var index = new PolicyAddressIndex();
        foreach (var rule in rules)
        {
            var network = rule.NetworkAddress;
            if (network.AddressFamily == AddressFamily.InterNetwork)
            {
                var bytes = network.GetAddressBytes();
                if (rule.PrefixLength == 32)
                {
                    index._ipv4Exact.Add(ToUInt32(bytes));
                }
                else
                {
                    index._ipv4Prefixes.AddPrefix(bytes, rule.PrefixLength);
                }
            }
            else if (network.AddressFamily == AddressFamily.InterNetworkV6)
            {
                var bytes = network.GetAddressBytes();
                if (rule.PrefixLength == 128)
                {
                    index._ipv6Exact.Add(Ipv6Key.From(bytes));
                }
                else
                {
                    index._ipv6Prefixes.AddPrefix(bytes, rule.PrefixLength);
                }
            }
        }

        return index;
    }

    public bool Matches(IPAddress? address)
    {
        if (address is null)
        {
            return false;
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            return _ipv4Exact.Contains(ToUInt32(bytes)) || _ipv4Prefixes.Matches(bytes);
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var bytes = address.GetAddressBytes();
            return _ipv6Exact.Contains(Ipv6Key.From(bytes)) || _ipv6Prefixes.Matches(bytes);
        }

        return false;
    }

    private static uint ToUInt32(ReadOnlySpan<byte> bytes)
    {
        return ((uint)bytes[0] << 24) |
               ((uint)bytes[1] << 16) |
               ((uint)bytes[2] << 8) |
               bytes[3];
    }

    private readonly record struct Ipv6Key(ulong High, ulong Low)
    {
        public static Ipv6Key From(ReadOnlySpan<byte> bytes)
        {
            var high = ((ulong)bytes[0] << 56) |
                       ((ulong)bytes[1] << 48) |
                       ((ulong)bytes[2] << 40) |
                       ((ulong)bytes[3] << 32) |
                       ((ulong)bytes[4] << 24) |
                       ((ulong)bytes[5] << 16) |
                       ((ulong)bytes[6] << 8) |
                       bytes[7];

            var low = ((ulong)bytes[8] << 56) |
                      ((ulong)bytes[9] << 48) |
                      ((ulong)bytes[10] << 40) |
                      ((ulong)bytes[11] << 32) |
                      ((ulong)bytes[12] << 24) |
                      ((ulong)bytes[13] << 16) |
                      ((ulong)bytes[14] << 8) |
                      bytes[15];

            return new Ipv6Key(high, low);
        }
    }
}
