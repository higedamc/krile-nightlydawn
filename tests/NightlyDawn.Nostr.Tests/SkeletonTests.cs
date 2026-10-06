using System.Reflection;
using Xunit;

namespace NightlyDawn.Nostr.Tests;

public class SkeletonTests
{
    [Fact]
    public void NostrAssembly_Loads()
    {
        var assembly = Assembly.Load("NightlyDawn.Nostr");

        Assert.Equal("NightlyDawn.Nostr", assembly.GetName().Name);
    }
}
