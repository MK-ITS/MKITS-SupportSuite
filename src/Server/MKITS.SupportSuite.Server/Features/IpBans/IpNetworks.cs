// SPDX-License-Identifier: AGPL-3.0-only
// Copyright (C) MK-ITSolutions and contributors

using System.Net;
using System.Net.Sockets;

namespace MKITS.SupportSuite.Server.Features.IpBans;

/// <summary>
/// Helpers for normalizing client addresses and turning them into the network that is counted and banned.
/// </summary>
public static class IpNetworks
{
    /// <summary>IPv4 addresses mapped into IPv6 (<c>::ffff:1.2.3.4</c>) are treated as plain IPv4.</summary>
    public static IPAddress Normalize(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        return address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
    }

    /// <summary>
    /// The network a client is counted and banned by: <c>/32</c> for IPv4, <c>/<paramref name="ipv6PrefixLength"/></c> for IPv6.
    /// </summary>
    public static System.Net.IPNetwork ClientNetwork(IPAddress address, int ipv6PrefixLength)
    {
        var normalized = Normalize(address);
        return normalized.AddressFamily == AddressFamily.InterNetwork
            ? new System.Net.IPNetwork(normalized, 32)
            : new System.Net.IPNetwork(Mask(normalized, ipv6PrefixLength), ipv6PrefixLength);
    }

    /// <summary>Parses CIDR notation; a plain address is accepted as a single-host network.</summary>
    public static bool TryParseNetwork(string value, out System.Net.IPNetwork network)
    {
        if (System.Net.IPNetwork.TryParse(value, out network))
        {
            return true;
        }

        if (IPAddress.TryParse(value, out var address))
        {
            var normalized = Normalize(address);
            network = new System.Net.IPNetwork(normalized, normalized.AddressFamily == AddressFamily.InterNetwork ? 32 : 128);
            return true;
        }

        return false;
    }

    /// <summary>Clears all bits after <paramref name="prefixLength"/>.</summary>
    internal static IPAddress Mask(IPAddress address, int prefixLength)
    {
        var bytes = address.GetAddressBytes();
        for (var bit = prefixLength; bit < bytes.Length * 8; bit++)
        {
            bytes[bit / 8] &= (byte)~(0x80 >> (bit % 8));
        }

        return new IPAddress(bytes);
    }
}
