using System.Net;
using System.Net.Sockets;

namespace NightlyDawn.Storage;

/// <summary>
/// Decides which IP addresses an avatar fetch is allowed to connect to.
///
/// <para>This exists because a profile's <c>picture</c> URL is written by a stranger. Without a policy, anyone
/// who can get a kind:0 event in front of the app can make the app issue GETs against the operator's own
/// machine and LAN -- <c>http://127.0.0.1:8080/</c>, <c>http://192.168.1.1/</c>, a cloud instance's
/// <c>169.254.169.254</c>. The fetch result is never shown to the author, so this is not a read primitive for
/// them, but it is an unconsented request originated from inside the operator's network, and that is not
/// something a decoration should be able to do.</para>
///
/// <para><b>The check is applied to the address actually dialled, not to the hostname.</b> A hostname check is
/// defeated by DNS: a name can resolve to a public address when validated and to <c>127.0.0.1</c> a moment
/// later when connected (DNS rebinding). <see cref="RemoteImageLoader"/> therefore does its own connect and
/// asks this policy about each resolved <see cref="IPAddress"/>, so the address approved is the address
/// used.</para>
/// </summary>
public interface IRemoteAddressPolicy
{
    /// <summary>True when a connection to <paramref name="address"/> is allowed.</summary>
    bool IsAllowed(IPAddress address);
}

/// <summary>
/// The production policy: public unicast addresses only.
///
/// <para>IPv4-mapped IPv6 (<c>::ffff:127.0.0.1</c>) is unwrapped before the decision. Not unwrapping it is a
/// recurring real-world bypass -- two separate MCP servers shipped SSRF filters in 2026 that a mapped address
/// walked straight through -- because the mapped form passes every IPv4-shaped check while the kernel connects
/// to the IPv4 address.</para>
/// </summary>
public sealed class PublicUnicastOnlyPolicy : IRemoteAddressPolicy
{
    public bool IsAllowed(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
        {
            return false;
        }

        return address.AddressFamily switch
        {
            AddressFamily.InterNetwork => IsPublicV4(address.GetAddressBytes()),
            AddressFamily.InterNetworkV6 => IsPublicV6(address),
            _ => false, // Anything that is not IP at all (e.g. an AppleTalk relic) is not something to dial.
        };
    }

    /// <summary>
    /// Pulls the IPv4 address out of an IPv6 form that embeds one, so the v4 rules decide. Covers NAT64's
    /// well-known <c>64:ff9b::/96</c>, 6to4's <c>2002::/16</c> and the deprecated IPv4-compatible <c>::/96</c>.
    ///
    /// <para>This is not theoretical tidiness: a filter that reasoned only about the IPv6 form let
    /// <c>64:ff9b::a9fe:a9fe</c> through to the cloud metadata address in the wild (CVE-2026-82648). On an
    /// IPv6-only network behind a NAT64 gateway -- the normal shape of a mobile network now -- that address is
    /// a route to 169.254.169.254, and on a dual-stack machine it is simply unreachable. Treating it as the
    /// IPv4 address it encodes is right in both cases.</para>
    /// </summary>
    private static bool TryGetEmbeddedV4(byte[] b, out byte[] embedded)
    {
        // 64:ff9b::/96 -- NAT64 well-known prefix, IPv4 in the last four bytes.
        if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xFF && b[3] == 0x9B
            && b[4] == 0 && b[5] == 0 && b[6] == 0 && b[7] == 0
            && b[8] == 0 && b[9] == 0 && b[10] == 0 && b[11] == 0)
        {
            embedded = [b[12], b[13], b[14], b[15]];
            return true;
        }

        // 2002::/16 -- 6to4, IPv4 in bytes 2..5.
        if (b[0] == 0x20 && b[1] == 0x02)
        {
            embedded = [b[2], b[3], b[4], b[5]];
            return true;
        }

        // ::/96 -- deprecated IPv4-compatible. :: itself is already rejected as IPv6Any.
        if (b.Take(12).All(octet => octet == 0))
        {
            embedded = [b[12], b[13], b[14], b[15]];
            return true;
        }

        embedded = [];
        return false;
    }

    private static bool IsPublicV4(byte[] b) =>
        b[0] != 0 &&                                  // 0.0.0.0/8 "this network"
        b[0] != 10 &&                                 // 10/8 private
        b[0] != 127 &&                                // loopback (also covered above, kept for the whole /8)
        !(b[0] == 100 && b[1] >= 64 && b[1] <= 127) && // 100.64/10 CGNAT
        !(b[0] == 169 && b[1] == 254) &&              // 169.254/16 link-local, incl. the cloud metadata address
        !(b[0] == 172 && b[1] >= 16 && b[1] <= 31) && // 172.16/12 private
        !(b[0] == 192 && b[1] == 168) &&              // 192.168/16 private
        !(b[0] == 192 && b[1] == 0 && b[2] == 0) &&   // 192.0.0/24 IETF protocol assignments
        b[0] < 224;                                   // 224/4 multicast and 240/4 reserved

    private static bool IsPublicV6(IPAddress address)
    {
        if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast || address.IsIPv6Teredo)
        {
            return false;
        }

        var b = address.GetAddressBytes();

        if (TryGetEmbeddedV4(b, out var embedded))
        {
            return IsPublicV4(embedded);
        }

        // fc00::/7 unique-local. The unspecified address :: is already rejected as IPv6Any above.
        return (b[0] & 0xFE) != 0xFC;
    }
}
