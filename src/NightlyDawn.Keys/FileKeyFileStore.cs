using System.Runtime.Versioning;

namespace NightlyDawn.Keys;

/// <summary>
/// Persists the <c>ncryptsec1...</c> blob to a single file at 0600 (owner read/write
/// only). Windows is out of scope for Krile NightlyDawn (plan §1 D1: macOS and Omarchy
/// only), hence the explicit platform attribute rather than a cross-platform fallback.
///
/// <see cref="WriteAsync"/> writes to a <c>.tmp</c> sibling and atomically renames it
/// over <paramref name="filePath"/> (<see cref="File.Move(string, string, bool)"/> is
/// <c>rename(2)</c> on POSIX when both paths share a filesystem, which they do here by
/// construction). An earlier version deleted the existing file before recreating it to
/// fix a permissions bug (see B1 in the leaf's review history) — that traded a
/// permissions bug for a worse one: between the delete and the new file existing, a
/// crash/disk-full/process-kill left the user's only copy of their key gone, with no
/// plaintext backup anywhere (this module's only key-export path is NIP-49 ciphertext).
/// Write-then-rename never has a window where the path exists with no valid content: a
/// reader always sees either the old key or the new one. The rename also carries the
/// temp file's own 0600 (set via <see cref="FileStreamOptions.UnixCreateMode"/> at its
/// creation) onto the destination, since <c>rename(2)</c> replaces the destination
/// inode — so this keeps the permission guarantee the delete-then-create approach was
/// chasing, without its data-loss window.
///
/// What each failure mode this protects against, precisely (so "crash-safe" doesn't get
/// read as "safe against every crash"): a process kill, an exception mid-write, or the
/// disk filling up all happen before the rename, so they leave the <c>.tmp</c> file
/// damaged or partial and the real <paramref name="filePath"/> untouched — covered.
/// <see cref="NativeFileSync.SyncToDisk"/> additionally calls down to <c>fsync</c>
/// (Linux) / <c>fcntl(F_FULLFSYNC)</c> (macOS) before the rename, so the new content is
/// durable on the storage device itself, not just handed to the OS page cache — covering
/// a power loss or kernel panic between the write and the rename. This deliberately does
/// not use <see cref="FileStream.Flush(bool)"/>'s built-in <c>flushToDisk: true</c>: every
/// .NET release from 6 through at least 10.0.12 has a confirmed bug
/// (dotnet/runtime#135201) where that path silently reports success even when the
/// underlying fsync fails (full disk, I/O error, a flaky network filesystem) — see
/// <see cref="NativeFileSync"/>'s doc for the mechanism. <c>rename(2)</c> replacing the
/// directory entry is itself only guaranteed durable once the *containing directory* is
/// fsynced — <see cref="WriteAsync"/> does that too, via
/// <see cref="NativeFileSync.SyncDirectoryToDisk"/>, right after the rename — so a power
/// loss between the rename and the directory metadata reaching the device is covered as
/// well, both for the common case (key rotation; the old key survives either way) and the
/// one case that would otherwise be unrecoverable (a first-ever
/// <c>GenerateLocalKeyAsync</c>, where losing the rename loses the only copy of the key).
/// Not covered: a crash between the file fsync and the directory fsync still leaves the
/// new file's *data* durable (that fsync already completed) but the directory entry
/// pointing at it possibly not yet durable — narrower than the original gap, not zero,
/// because POSIX has no single syscall that makes both atomic together.
/// </summary>
[UnsupportedOSPlatform("windows")]
public sealed class FileKeyFileStore(string filePath) : IKeyFileStore
{
    public async Task<string?> ReadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await File.ReadAllTextAsync(filePath, cancellationToken).ConfigureAwait(false);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    public async Task WriteAsync(string ncryptsec, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = filePath + ".tmp";
        if (File.Exists(tempPath))
        {
            File.Delete(tempPath); // leftover from a prior write that never reached the rename
        }

        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
        };

        await using (var stream = new FileStream(tempPath, options))
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(ncryptsec);
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false); // managed buffers -> OS

            // OS page cache -> physical storage, with a real error check (see class doc
            // for why this isn't FileStream.Flush(flushToDisk: true)).
            NativeFileSync.SyncToDisk(stream.SafeFileHandle);
        }

        File.Move(tempPath, filePath, overwrite: true);

        // The rename above is only guaranteed durable once the directory entry itself
        // reaches storage — see the class doc's "Not covered" paragraph, now covered.
        var absoluteDirectory = Path.GetDirectoryName(Path.GetFullPath(filePath));
        if (!string.IsNullOrEmpty(absoluteDirectory))
        {
            NativeFileSync.SyncDirectoryToDisk(absoluteDirectory);
        }
    }

    public Task DeleteAsync(CancellationToken cancellationToken = default)
    {
        if (File.Exists(filePath))
        {
            File.Delete(filePath);
        }

        return Task.CompletedTask;
    }
}
