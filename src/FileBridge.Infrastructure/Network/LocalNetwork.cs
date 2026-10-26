namespace FileBridge.Infrastructure.Network;

using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

/// <summary>Helpers to reason about this device's IPv4 addresses.</summary>
public static class LocalNetwork
{
    public sealed record LocalAddress(IPAddress Address, IPAddress? Mask, string InterfaceName);

    /// <summary>IPv4 addresses of the interfaces that are up (loopback excluded). Best effort: empty on failure.</summary>
    public static IReadOnlyList<LocalAddress> GetLocalIPv4Addresses()
    {
        var result = new List<LocalAddress>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    continue;

                foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                {
                    var address = unicast.Address;
                    if (address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(address))
                        continue;

                    IPAddress? mask = null;
                    try { mask = unicast.IPv4Mask; } catch { /* not available on every platform */ }
                    result.Add(new LocalAddress(address, mask, nic.Name));
                }
            }
        }
        catch
        {
            // NetworkInterface enumeration can fail on some Android versions; callers fall back gracefully.
        }
        return result;
    }

    /// <summary>
    /// Picks the address of a remote device most likely to be reachable from here. A PC usually advertises
    /// every adapter (Wi-Fi, Hyper-V/WSL vEthernet, VirtualBox, VPN...), and taking the first one can give an
    /// unreachable IP. Order: IPv4 inside one of our subnets → private IPv4 → any other IPv4.
    /// IPv6 is skipped because <see cref="TcpServer"/> listens on IPv4 only.
    /// </summary>
    public static string? ChooseBestAddress(IEnumerable<string?> candidates, IReadOnlyList<LocalAddress>? localAddresses = null)
    {
        var local = localAddresses ?? GetLocalIPv4Addresses();
        var ipv4 = candidates
            .Select(c => IPAddress.TryParse(c, out var ip) ? ip : null)
            .Where(ip => ip is not null
                         && ip.AddressFamily == AddressFamily.InterNetwork
                         && !IPAddress.IsLoopback(ip)
                         && !IsLinkLocal(ip))
            .Select(ip => ip!)
            .Distinct()
            .ToList();

        var sameSubnet = ipv4.FirstOrDefault(ip => local.Any(l => l.Mask is not null && SameSubnet(ip, l.Address, l.Mask)));
        if (sameSubnet is not null)
            return sameSubnet.ToString();

        return (ipv4.FirstOrDefault(IsPrivate) ?? ipv4.FirstOrDefault())?.ToString();
    }

    private static bool SameSubnet(IPAddress a, IPAddress b, IPAddress mask)
    {
        var ab = a.GetAddressBytes();
        var bb = b.GetAddressBytes();
        var mb = mask.GetAddressBytes();
        if (mb.Length != 4 || mb.All(x => x == 0))
            return false;
        for (var i = 0; i < 4; i++)
        {
            if ((ab[i] & mb[i]) != (bb[i] & mb[i]))
                return false;
        }
        return true;
    }

    private static bool IsLinkLocal(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        return b[0] == 169 && b[1] == 254;
    }

    private static bool IsPrivate(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168);
    }
}
