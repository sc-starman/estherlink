using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace OmniRelay.Core.Networking;

public static class NetworkAdapterCatalog
{
    public static IReadOnlyList<NetworkAdapterInfo> ListIpv4Adapters()
    {
        var list = new List<NetworkAdapterInfo>();

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus == OperationalStatus.Down)
            {
                continue;
            }

            var properties = nic.GetIPProperties();
            var ipv4Props = properties.GetIPv4Properties();
            if (ipv4Props is null)
            {
                continue;
            }

            var addresses = properties.UnicastAddresses
                .Where(x => x.Address.AddressFamily == AddressFamily.InterNetwork)
                .Select(x => x.Address.ToString())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (addresses.Count == 0)
            {
                continue;
            }

            var hasGateway = properties.GatewayAddresses.Any(
                x => x.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.Any.Equals(x.Address));

            list.Add(new NetworkAdapterInfo(
                nic.Id,
                ipv4Props.Index,
                nic.Name,
                FormatMacAddress(nic.GetPhysicalAddress()),
                addresses,
                hasGateway));
        }

        return list
            .OrderByDescending(x => x.HasDefaultGateway)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static bool TryGetPrimaryIpv4(int ifIndex, out IPAddress? ipAddress)
    {
        ipAddress = null;

        var adapter = ListIpv4Adapters().FirstOrDefault(x => x.IfIndex == ifIndex);
        if (adapter is null || adapter.IPv4Addresses.Count == 0)
        {
            return false;
        }

        return IPAddress.TryParse(adapter.IPv4Addresses[0], out ipAddress);
    }

    public static bool TryGetPrimaryIpv4(string? adapterId, int fallbackIfIndex, out IPAddress? ipAddress, out int resolvedIfIndex)
    {
        ipAddress = null;
        resolvedIfIndex = -1;
        var adapter = ResolveAdapter(adapterId, fallbackIfIndex);
        if (adapter is null || adapter.IPv4Addresses.Count == 0)
        {
            return false;
        }

        resolvedIfIndex = adapter.IfIndex;
        return IPAddress.TryParse(adapter.IPv4Addresses[0], out ipAddress);
    }

    public static bool TryResolveIfIndex(string? adapterId, int fallbackIfIndex, out int resolvedIfIndex)
    {
        resolvedIfIndex = -1;
        var adapter = ResolveAdapter(adapterId, fallbackIfIndex);
        if (adapter is null)
        {
            return false;
        }

        resolvedIfIndex = adapter.IfIndex;
        return true;
    }

    public static bool TryGetPrimaryIpv4Gateway(int ifIndex, out IPAddress? gatewayAddress)
    {
        gatewayAddress = null;

        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus == OperationalStatus.Down)
            {
                continue;
            }

            var properties = nic.GetIPProperties();
            var ipv4Props = properties.GetIPv4Properties();
            if (ipv4Props is null || ipv4Props.Index != ifIndex)
            {
                continue;
            }

            var gateway = properties.GatewayAddresses
                .Select(x => x.Address)
                .FirstOrDefault(x => x.AddressFamily == AddressFamily.InterNetwork && !IPAddress.Any.Equals(x));
            if (gateway is null)
            {
                return false;
            }

            gatewayAddress = gateway;
            return true;
        }

        return false;
    }

    private static NetworkAdapterInfo? ResolveAdapter(string? adapterId, int fallbackIfIndex)
    {
        var adapters = ListIpv4Adapters();
        var normalizedAdapterId = (adapterId ?? string.Empty).Trim();
        if (!string.IsNullOrWhiteSpace(normalizedAdapterId))
        {
            var byId = adapters.FirstOrDefault(x => string.Equals(x.AdapterId, normalizedAdapterId, StringComparison.OrdinalIgnoreCase));
            if (byId is not null)
            {
                return byId;
            }
        }

        if (fallbackIfIndex > 0)
        {
            var byIfIndex = adapters.FirstOrDefault(x => x.IfIndex == fallbackIfIndex);
            if (byIfIndex is not null)
            {
                return byIfIndex;
            }
        }

        return null;
    }

    private static string FormatMacAddress(PhysicalAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes.Length == 0 ? string.Empty : string.Join(":", bytes.Select(x => x.ToString("X2")));
    }
}
