using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace EstherLink.Core.Networking;

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
                ipv4Props.Index,
                nic.Name,
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
}
