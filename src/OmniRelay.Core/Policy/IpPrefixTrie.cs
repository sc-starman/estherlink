using System.Net.Sockets;

namespace OmniRelay.Core.Policy;

public sealed class IpPrefixTrie
{
    private sealed class Node
    {
        public int ZeroChild = -1;
        public int OneChild = -1;
        public bool Terminal;
    }

    private readonly int _maxBits;
    private readonly List<Node> _nodes = [new()];

    public IpPrefixTrie(AddressFamily family)
    {
        _maxBits = family == AddressFamily.InterNetwork ? 32 : 128;
    }

    public void AddPrefix(ReadOnlySpan<byte> networkBytes, int prefixLength)
    {
        if (prefixLength < 0 || prefixLength > _maxBits)
        {
            throw new ArgumentOutOfRangeException(nameof(prefixLength));
        }

        var nodeIndex = 0;
        for (var bit = 0; bit < prefixLength; bit++)
        {
            var next = GetBit(networkBytes, bit) == 0
                ? _nodes[nodeIndex].ZeroChild
                : _nodes[nodeIndex].OneChild;

            if (next >= 0)
            {
                nodeIndex = next;
                continue;
            }

            next = _nodes.Count;
            _nodes.Add(new Node());
            if (GetBit(networkBytes, bit) == 0)
            {
                _nodes[nodeIndex].ZeroChild = next;
            }
            else
            {
                _nodes[nodeIndex].OneChild = next;
            }

            nodeIndex = next;
        }

        _nodes[nodeIndex].Terminal = true;
    }

    public bool Matches(ReadOnlySpan<byte> addressBytes)
    {
        var nodeIndex = 0;
        if (_nodes[nodeIndex].Terminal)
        {
            return true;
        }

        for (var bit = 0; bit < _maxBits; bit++)
        {
            var next = GetBit(addressBytes, bit) == 0
                ? _nodes[nodeIndex].ZeroChild
                : _nodes[nodeIndex].OneChild;

            if (next < 0)
            {
                return false;
            }

            nodeIndex = next;
            if (_nodes[nodeIndex].Terminal)
            {
                return true;
            }
        }

        return false;
    }

    private static int GetBit(ReadOnlySpan<byte> bytes, int bitIndex)
    {
        var byteIndex = bitIndex / 8;
        var bitOffset = 7 - (bitIndex % 8);
        return (bytes[byteIndex] >> bitOffset) & 0x01;
    }
}
