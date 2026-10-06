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
        // untouched (confirmed empirically). The write-then-rename in WriteAsync still
        // fixes this: the temp file is created fresh at 0600, and renaming it over an
        // existing (possibly loosened) file replaces that file's inode entirely, carrying
        // the temp file's 0600 onto the destination path.
        var store = new FileKeyFileStore(_filePath);
        await store.WriteAsync("ncryptsec1firstvalue");
        File.SetUnixFileMode(_filePath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        await store.WriteAsync("ncryptsec1secondvalue");

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(_filePath));
        Assert.Equal("ncryptsec1secondvalue", await store.ReadAsync());
    }

    [Fact]
    public async Task WriteAsync_NeverLeavesTheKeyFileMissing_IfAWriteIsInterruptedBeforeTheRename()
    {
        // B1: an earlier delete-then-create implementation deleted the existing file
        // before writing the new one, so a crash/disk-full between those two steps lost
        // the user's only copy of their key permanently (this module never keeps a
        // plaintext copy; ExportLocalKeyAsync is the only re-encryption path, and it
        // needs the key to already be loaded). Simulate that interruption directly: leave
        // a partially-written ".tmp" sibling on disk (as a crash before the rename would)
        // and confirm the real key file is untouched, then confirm the next successful
        // write cleans up the stale temp file and replaces the content.
        var store = new FileKeyFileStore(_filePath);
        await store.WriteAsync("ncryptsec1firstvalue");

        await File.WriteAllTextAsync(_filePath + ".tmp", "ncryptsec1partiallywritten");

        Assert.Equal("ncryptsec1firstvalue", await store.ReadAsync());

        await store.WriteAsync("ncryptsec1secondvalue");

        Assert.Equal("ncryptsec1secondvalue", await store.ReadAsync());
        Assert.False(File.Exists(_filePath + ".tmp"));
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
