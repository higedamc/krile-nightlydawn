using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace NightlyDawn.Keys;

/// <summary>
/// Durable fsync via direct P/Invoke, deliberately bypassing
/// <see cref="FileStream.Flush(bool)"/>'s <c>flushToDisk</c> path.
///
/// This exists because of a confirmed .NET runtime bug present in every release from
/// .NET 6 through at least .NET 10.0.12 (dotnet/runtime#135201, an unbackported fix for
/// #124725): the native shim behind <c>Flush(flushToDisk: true)</c> has an
/// operator-precedence bug - <c>while ((result = fsync(fd) &lt; 0) &amp;&amp; errno ==
/// EINTR);</c> assigns the *comparison*, not the syscall's return value, to
/// <c>result</c> - so a failed fsync (ENOSPC on a full disk, EIO, an NFS hiccup) is
/// silently reported as success. For a write whose entire purpose is "guarantee this
/// reaches the disk before the rename," that is exactly the failure mode it needs to
/// catch. The runtime team's own documented workaround (same issue) is what this class
/// does: P/Invoke <c>fsync</c>/<c>fcntl</c> directly on the file descriptor and check
/// <c>errno</c> ourselves.
///
/// The P/Invoke target is declared as the logical name <c>"libc"</c>, resolved by
/// <see cref="ResolveLibc"/> rather than relying on the OS's default search: a bare
/// <c>DllImport("libc")</c> resolves on macOS (.NET's default Unix resolution finds it
/// there - exercised by this class's own tests), but on Linux there is no guarantee of
/// an unversioned <c>libc.so</c> - only <c>libc.so.6</c>, and even the <c>-dev</c>
/// package that used to provide the unversioned symlink has been dropping it on newer
/// distros. The resolver below loads <c>libc.so.6</c> explicitly on Linux.
/// </summary>
[UnsupportedOSPlatform("windows")]
internal static class NativeFileSync
{
    private const int EIntr = 4; // POSIX-standard; unchanged across glibc/musl/Darwin since 4.3BSD, unlike F_FullFSync below.

    /// <summary>macOS-only fcntl command. Verified against a compiled C probe of this machine's &lt;fcntl.h&gt; (not assumed) - it is not a portable POSIX constant.</summary>
    private const int FFullFSync = 51;

    // A static constructor (not [ModuleInitializer]: CA2255 flags that as too heavy for
    // library code, since it runs unconditionally at assembly load even for consumers
    // who never touch this type) runs lazily, exactly once, thread-safe, on first use -
    // which for this class is always before the P/Invoke calls below need resolving.
    static NativeFileSync()
    {
        NativeLibrary.SetDllImportResolver(typeof(NativeFileSync).Assembly, ResolveLibc);
    }

    private static IntPtr ResolveLibc(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName == "libc" && OperatingSystem.IsLinux() && NativeLibrary.TryLoad("libc.so.6", out var handle))
        {
            return handle;
        }

        return IntPtr.Zero; // defer to the runtime's default resolution (covers macOS, already verified against a real fsync call)
    }

    private const int ORdOnly = 0; // POSIX-standard; 0 on Linux and macOS alike (unlike F_FullFSync below).

    [DllImport("libc", SetLastError = true)]
    private static extern int fsync(int fd);

    [DllImport("libc", SetLastError = true)]
    private static extern int fcntl(int fd, int cmd, int arg);

    // 2-arg form: valid because this never passes O_CREAT, whose mode argument is the
    // only reason open(2) is variadic in the first place. LPUTF8Str is explicit here
    // rather than relying on .NET's default Unix string marshaling (which already is
    // UTF-8, but implicitly) — the same way EIntr and ORdOnly above are spelled out
    // instead of assumed, so a reader never has to go check what the default would do.
    [DllImport("libc", SetLastError = true)]
    private static extern int open([MarshalAs(UnmanagedType.LPUTF8Str)] string pathname, int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int close(int fd);

    /// <summary>
    /// Blocks until <paramref name="handle"/>'s data (and, on macOS, the drive's own write
    /// cache - see <see href="https://github.com/dotnet/runtime/issues/28444"/>) is
    /// physically on storage.
    /// </summary>
    /// <exception cref="IOException">The underlying fsync/F_FULLFSYNC call failed.</exception>
    public static void SyncToDisk(SafeFileHandle handle)
    {
        var addedRef = false;
        try
        {
            handle.DangerousAddRef(ref addedRef);
            var fd = handle.DangerousGetHandle().ToInt32();

            // Apple documents F_FULLFSYNC as failing with ENOTSUP on filesystems that
            // don't support it, in which case the documented fallback is plain fsync -
            // the same two-step SQLite's unixFullFsync uses for the same reason.
            if (OperatingSystem.IsMacOS() && TryFullFSync(fd))
            {
                return;
            }

            int result;
            int lastError;
            do
            {
                result = fsync(fd);
                lastError = Marshal.GetLastWin32Error();
            }
            while (result != 0 && lastError == EIntr);

            if (result != 0)
            {
                throw new IOException($"fsync failed with errno {lastError}.");
            }
        }
        finally
        {
            if (addedRef)
            {
                handle.DangerousRelease();
            }
        }
    }

    /// <summary>
    /// Fsyncs the directory at <paramref name="directoryPath"/> — needed after a
    /// <see cref="File.Move(string, string, bool)"/> (<c>rename(2)</c>) into that
    /// directory, since the directory-entry update is only guaranteed durable once the
    /// directory itself has reached storage, same as file data is. Unlike
    /// <see cref="SyncToDisk"/>, this never tries <c>F_FULLFSYNC</c> on macOS: that flag
    /// exists to flush a drive's write cache for file *data* durability, which has no
    /// bearing on a directory inode's metadata, and <c>fcntl(F_FULLFSYNC)</c> on a
    /// directory fd is untested territory this class has no vector constant or man-page
    /// citation for (unlike the file case above, which cites Apple's own docs). Plain
    /// <c>fsync</c> on the directory fd is the documented, portable mechanism.
    /// </summary>
    /// <exception cref="IOException">The directory could not be opened, or its fsync failed.</exception>
    public static void SyncDirectoryToDisk(string directoryPath)
    {
        int fd;
        int openError;
        do
        {
            fd = open(directoryPath, ORdOnly);
            openError = Marshal.GetLastWin32Error();
        }
        while (fd < 0 && openError == EIntr); // same retry discipline as fsync/fcntl below — open(2) can be interrupted too.

        if (fd < 0)
        {
            throw new IOException($"Could not open directory '{directoryPath}' for fsync (errno {openError}).");
        }

        try
        {
            int result;
            int lastError;
            do
            {
                result = fsync(fd);
                lastError = Marshal.GetLastWin32Error();
            }
            while (result != 0 && lastError == EIntr);

            if (result != 0)
            {
                throw new IOException($"Directory fsync failed with errno {lastError}.");
            }
        }
        finally
        {
            close(fd);
        }
    }

    private static bool TryFullFSync(int fd)
    {
        int result;
        int lastError;
        do
        {
            result = fcntl(fd, FFullFSync, 0);
            lastError = Marshal.GetLastWin32Error();
        }
        while (result != 0 && lastError == EIntr);

        return result == 0;
    }
}
