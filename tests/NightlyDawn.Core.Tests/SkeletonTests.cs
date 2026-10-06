using System.Reflection;
using Xunit;

namespace NightlyDawn.Core.Tests;

public class SkeletonTests
{
    [Fact]
    public void CoreAssembly_Loads()
    {
        var assembly = Assembly.Load("NightlyDawn.Core");

        Assert.Equal("NightlyDawn.Core", assembly.GetName().Name);
    }
}
