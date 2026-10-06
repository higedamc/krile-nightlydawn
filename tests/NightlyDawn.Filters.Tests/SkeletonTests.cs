using System.Reflection;
using Xunit;

namespace NightlyDawn.Filters.Tests;

public class SkeletonTests
{
    [Fact]
    public void FiltersAssembly_Loads()
    {
        var assembly = Assembly.Load("NightlyDawn.Filters");

        Assert.Equal("NightlyDawn.Filters", assembly.GetName().Name);
    }
}
