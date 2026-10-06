using Microsoft.Win32.SafeHandles;
using NightlyDawn.Keys;
using Xunit;

namespace NightlyDawn.Keys.Tests;

public class NativeFileSyncTests
{
    [Fact]
    public void SyncToDisk_Succeeds_OnARealOpenFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"nightlydawn-fsync-test-{Guid.NewGuid():N}");
        try
        {
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
            stream.Write("hello"u8);
            stream.Flush();

            // Must not throw.
            NativeFileSync.SyncToDisk(stream.SafeFileHandle);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void SyncToDisk_ThrowsIOException_WhenTheDescriptorIsInvalid()
    {
        // fd -1 is guaranteed invalid on every POSIX system — fsync/fcntl on it always
        // fails with EBADF. This is the exact case the fix exists for: dotnet/runtime's
        // own FileStream.Flush(flushToDisk: true) would silently swallow this.
        using var invalidHandle = new SafeFileHandle(new IntPtr(-1), ownsHandle: false);

        Assert.Throws<IOException>(() => NativeFileSync.SyncToDisk(invalidHandle));
    }
}
