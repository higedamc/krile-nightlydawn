using System.Runtime.Versioning;

namespace NightlyDawn.Keys;

/// <summary>
/// Persists the <c>ncryptsec1...</c> blob to a single file at 0600 (owner read/write
/// only). Windows is out of scope for Krile NightlyDawn (plan §1 D1: macOS and Omarchy
/// only), hence the explicit platform attribute rather than a cross-platform fallback.
///
/// <see cref="WriteAsync"/> deletes any existing file before creating a fresh one with
/// <see cref="FileStreamOptions.UnixCreateMode"/> set, rather than overwriting in place.
/// This matters beyond style: <c>UnixCreateMode</c> only applies when the file is newly
/// created — reusing <see cref="FileMode.Create"/> to truncate an *existing* file leaves
/// whatever permissions that file already had (confirmed empirically: writing 0600,
/// manually loosening to 0644, then writing again with the same <c>UnixCreateMode</c>
/// left it at 0644). Delete-then-create makes every write a fresh, atomic 0600 creation,
/// closing that gap instead of only covering the first write.
/// </summary>
[UnsupportedOSPlatform("windows")]
public sealed class FileKeyFileStore(string filePath) : IKeyFileStore
{
    public async Task<string?> ReadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(filePath))
        {
            return null;
        }

        return await File.ReadAllTextAsync(filePath, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteAsync(string ncryptsec, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (File.Exists(filePath))
        {
            File.Delete(filePath);
        }

        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
        };

        await using var stream = new FileStream(filePath, options);
        var bytes = System.Text.Encoding.UTF8.GetBytes(ncryptsec);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
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
