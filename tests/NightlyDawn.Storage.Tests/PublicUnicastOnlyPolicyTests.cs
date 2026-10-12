using System.Net;
using Xunit;

namespace NightlyDawn.Storage.Tests;

public sealed class PublicUnicastOnlyPolicyTests
{
    private readonly PublicUnicastOnlyPolicy _policy = new();

    [Theory]
    [InlineData("1.1.1.1")]
    [InlineData("93.184.216.34")]
    [InlineData("2606:4700:4700::1111")]
    [InlineData("101.64.0.1")]   // Just outside 100.64/10 CGNAT.
    [InlineData("172.32.0.1")]   // Just outside 172.16/12.
    [InlineData("172.15.0.1")]   // Just below 172.16/12.
    [InlineData("192.167.1.1")]  // Just below 192.168/16.
    [InlineData("192.169.1.1")]  // Just above 192.168/16.
    [InlineData("169.253.0.1")]  // Just below 169.254/16.
    [InlineData("223.255.255.255")] // Last unicast address before 224/4.
    public void AllowsPublicUnicast(string address) =>
        Assert.True(_policy.IsAllowed(IPAddress.Parse(address)), address);

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.1.2.3")]
    [InlineData("0.0.0.0")]
    [InlineData("10.0.0.1")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.1.1")]
    [InlineData("169.254.169.254")] // The cloud metadata address.
    [InlineData("100.64.0.1")]      // CGNAT, which includes a tailnet's address range.
    [InlineData("100.127.255.255")]
    [InlineData("192.0.0.1")]
    [InlineData("224.0.0.1")]       // Multicast.
    [InlineData("255.255.255.255")]
    [InlineData("::1")]
    [InlineData("::")]
    [InlineData("fe80::1")]         // IPv6 link-local.
    [InlineData("fc00::1")]         // Unique-local.
    [InlineData("fd12:3456::1")]
    [InlineData("ff02::1")]         // IPv6 multicast.
    public void RefusesEverythingThatIsNotPublicUnicast(string address) =>
        Assert.False(_policy.IsAllowed(IPAddress.Parse(address)), address);

    /// <summary>
    /// The bypass that keeps shipping in real SSRF filters: <c>::ffff:127.0.0.1</c> is an IPv6 literal that the
    /// kernel connects to as 127.0.0.1. A policy that branches on address family without unwrapping the mapped
    /// form passes it as "some IPv6 address we have no IPv4 rule for".
    /// </summary>
    [Theory]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("::ffff:169.254.169.254")]
    [InlineData("::ffff:10.0.0.1")]
    [InlineData("::ffff:192.168.0.1")]
    public void UnwrapsIPv4MappedIPv6BeforeDeciding(string address)
    {
        var parsed = IPAddress.Parse(address);

        Assert.True(parsed.IsIPv4MappedToIPv6, $"{address} was expected to parse as IPv4-mapped IPv6");
        Assert.False(_policy.IsAllowed(parsed), address);
    }

    [Fact]
    public void AllowsAMappedPublicAddress() =>
        Assert.True(_policy.IsAllowed(IPAddress.Parse("::ffff:1.1.1.1")));

    /// <summary>
    /// The NAT64 / 6to4 / IPv4-compatible forms embed an IPv4 address in an IPv6 literal. A filter that only
    /// knew the IPv6 rules passed <c>64:ff9b::a9fe:a9fe</c> straight through to the cloud metadata address in
    /// the wild (CVE-2026-82648), because it looks like an ordinary global-unicast address.
    /// </summary>
    [Theory]
    [InlineData("64:ff9b::7f00:1")]       // NAT64 of 127.0.0.1
    [InlineData("64:ff9b::a9fe:a9fe")]    // NAT64 of 169.254.169.254
    [InlineData("64:ff9b::c0a8:1")]       // NAT64 of 192.168.0.1
    [InlineData("64:ff9b::a00:1")]        // NAT64 of 10.0.0.1
    [InlineData("2002:7f00:1::1")]        // 6to4 of 127.0.0.1
    [InlineData("2002:a9fe:a9fe::1")]     // 6to4 of 169.254.169.254
    [InlineData("::7f00:1")]              // IPv4-compatible 127.0.0.1
    [InlineData("::a9fe:a9fe")]           // IPv4-compatible 169.254.169.254
    public void RefusesIPv6FormsThatEmbedAPrivateIPv4Address(string address) =>
        Assert.False(_policy.IsAllowed(IPAddress.Parse(address)), address);

    /// <summary>Control for the test above: the same encodings carrying a public address stay allowed, so the
    /// new rule rejects by what is embedded rather than by the prefix.</summary>
    [Theory]
    [InlineData("64:ff9b::101:101")]      // NAT64 of 1.1.1.1
    [InlineData("2002:0101:0101::1")]     // 6to4 of 1.1.1.1
    public void AllowsThoseFormsWhenTheEmbeddedAddressIsPublic(string address) =>
        Assert.True(_policy.IsAllowed(IPAddress.Parse(address)), address);
}
