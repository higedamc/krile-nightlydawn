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
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        File.Move(tempPath, filePath, overwrite: true);
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
