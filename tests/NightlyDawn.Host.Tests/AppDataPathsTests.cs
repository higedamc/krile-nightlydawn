using NightlyDawn.Host;
using Xunit;

namespace NightlyDawn.Host.Tests;

public class AppDataPathsTests
{
    [Fact]
    public void MacOS_UsesApplicationSupport()
    {
        Assert.Equal("/Users/me/Library/Application Support/NightlyDawn", AppDataPaths.Resolve(null, "/ignored", "/Users/me", isMacOS: true));
    }

    [Fact]
    public void Linux_UsesXdgDataHome_OrTheDotLocalFallback()
    {
        Assert.Equal("/data/NightlyDawn", AppDataPaths.Resolve(null, " /data ", "/home/me", isMacOS: false));
        Assert.Equal("/home/me/.local/share/NightlyDawn", AppDataPaths.Resolve(null, null, "/home/me", isMacOS: false));
        Assert.Equal("/home/me/.local/share/NightlyDawn", AppDataPaths.Resolve(null, "   ", "/home/me", isMacOS: false));
    }

    [Fact]
    public void Override_WinsEverywhere()
    {
        var expected = Path.GetFullPath("/tmp/nd-test");
        Assert.Equal(expected, AppDataPaths.Resolve(" /tmp/nd-test ", "/data", "/home/me", isMacOS: false));
        Assert.Equal(expected, AppDataPaths.Resolve("/tmp/nd-test", null, "/Users/me", isMacOS: true));
    }

    [Fact]
    public void EnsureLocalKeyFile_CreatesA0700Directory_AndReturnsTheKeyPath()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nd-" + Guid.NewGuid().ToString("N"));
        try
        {
            var keyFile = AppDataPaths.EnsureLocalKeyFile(dir);

            Assert.Equal(Path.Combine(dir, AppDataPaths.LocalKeyFileName), keyFile);
            Assert.True(Directory.Exists(dir));
            Assert.False(File.Exists(keyFile)); // the store creates it on first Generate/Import, not the host
            if (!OperatingSystem.IsWindows())
            {
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(dir));
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
