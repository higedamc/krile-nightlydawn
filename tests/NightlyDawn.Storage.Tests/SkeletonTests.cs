using System.Reflection;
using Xunit;

namespace NightlyDawn.Storage.Tests;

public class SkeletonTests
{
    [Fact]
    public void StorageAssembly_Loads()
    {
        var assembly = Assembly.Load("NightlyDawn.Storage");

        Assert.Equal("NightlyDawn.Storage", assembly.GetName().Name);
    }
}
