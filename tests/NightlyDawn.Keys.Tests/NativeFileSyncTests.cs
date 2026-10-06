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

    [Fact]
    public void SyncDirectoryToDisk_Succeeds_OnARealDirectory()
    {
        var path = Directory.CreateTempSubdirectory("nightlydawn-dirfsync-test").FullName;
        try
        {
            // Must not throw.
            NativeFileSync.SyncDirectoryToDisk(path);
        }
        finally
        {
            Directory.Delete(path);
        }
    }

    [Fact]
    public void SyncDirectoryToDisk_ThrowsIOException_ForANonexistentPath()
    {
        // No fd is ever obtained here — the exception comes from open(2) itself
        // (ENOENT), not from fsync. This is the directory-fsync counterpart of the bad
        // file descriptor case above: a failure that must surface, not vanish.
        var path = Path.Combine(Path.GetTempPath(), $"nightlydawn-dirfsync-missing-{Guid.NewGuid():N}");

        Assert.Throws<IOException>(() => NativeFileSync.SyncDirectoryToDisk(path));
    }
}
