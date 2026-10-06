using NightlyDawn.Keys;
using Xunit;

namespace NightlyDawn.Keys.Tests;

public class FileKeyFileStoreTests : IDisposable
{
    private readonly string _directory;
    private readonly string _filePath;

    public FileKeyFileStoreTests()
    {
        _directory = Directory.CreateTempSubdirectory("nightlydawn-keys-tests").FullName;
        _filePath = Path.Combine(_directory, "nested", "key.ncryptsec");
    }

    [Fact]
    public async Task ReadAsync_ReturnsNull_WhenNoFileExists()
    {
        var store = new FileKeyFileStore(_filePath);

        Assert.Null(await store.ReadAsync());
    }

    [Fact]
    public async Task WriteThenRead_RoundTrips()
    {
        var store = new FileKeyFileStore(_filePath);

        await store.WriteAsync("ncryptsec1examplevalue");

        Assert.Equal("ncryptsec1examplevalue", await store.ReadAsync());
    }

    [Fact]
    public async Task WriteAsync_CreatesFileWithOwnerOnlyPermissions()
    {
        var store = new FileKeyFileStore(_filePath);

        await store.WriteAsync("ncryptsec1examplevalue");

        var mode = File.GetUnixFileMode(_filePath);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, mode);
    }

    [Fact]
    public async Task WriteAsync_RestoresOwnerOnlyPermissions_EvenIfTheFileWasPreviouslyLoosened()
    {
        // FileStreamOptions.UnixCreateMode only applies at file *creation* — reusing
        // FileMode.Create to truncate an existing file leaves its prior permissions
        // untouched (confirmed empirically). WriteAsync must delete-then-recreate so a
        // key file an external process (backup tool, cloud sync, a misconfigured
        // deploy step) loosened to 0644 gets locked back down to 0600 on the next write.
        var store = new FileKeyFileStore(_filePath);
        await store.WriteAsync("ncryptsec1firstvalue");
        File.SetUnixFileMode(_filePath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        await store.WriteAsync("ncryptsec1secondvalue");

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(_filePath));
        Assert.Equal("ncryptsec1secondvalue", await store.ReadAsync());
    }

    [Fact]
    public async Task DeleteAsync_RemovesTheFile()
    {
        var store = new FileKeyFileStore(_filePath);
        await store.WriteAsync("ncryptsec1examplevalue");

        await store.DeleteAsync();

        Assert.Null(await store.ReadAsync());
    }

    [Fact]
    public async Task DeleteAsync_WhenNoFileExists_DoesNotThrow()
    {
        var store = new FileKeyFileStore(_filePath);

        await store.DeleteAsync();
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
