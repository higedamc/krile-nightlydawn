using System.Text;
using System.Text.Json;
using NightlyDawn.Core;
using Xunit;

namespace NightlyDawn.Storage.Tests;

/// <summary>
/// <see cref="JsonAppStateStore"/> against a real temp directory. The file is the user's and the user's
/// mistakes' (plan §32.3), so most of this class is what a hostile or merely damaged file must <em>not</em> be
/// able to do: throw out of <c>LoadAsync</c>, make the app build thousands of columns, or get itself repaired
/// or deleted. The happy path is one round trip plus the atomic-write and permission checks.
///
/// <para>Mutations run by hand while writing this class, all red as expected and all reverted: (1) deleting the
/// <c>AppStateShape.Check</c> call from <c>TryMaterialize</c> turns every bounds/selection theory case green on
/// the wrong side (they return the oversized state) -- i.e. red here; (2) replacing the temp + rename sequence
/// with <c>File.WriteAllBytesAsync(_filePath)</c> turns <see cref="Save_LeavesNoTempFile_AndCreatesTheFile0600"/>
/// red on the mode assertion (0644 under the default umask); (3) swapping the <c>catch (JsonException)</c> for a
/// rethrow turns <see cref="Load_UntrustedContent_FallsBackToDefault_ReportsOnce_AndLeavesTheFileUntouched"/> red
/// on its first case.</para>
/// </summary>
public sealed class JsonAppStateStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nightlydawn-app-state-" + Guid.NewGuid().ToString("n"));
    private readonly List<string> _problems = [];

    private string FilePath => Path.Combine(_dir, "app-state.json");

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private JsonAppStateStore NewStore() => new(FilePath, _problems.Add);

    private static AppState Sample() => new(
        AppState.CurrentSchemaVersion,
        [
            new Column("col-a", [new Timeline("tab-1", "from home", "from home"), new Timeline("tab-2", "from kind(\"1\")", "from kind(\"1\")")], SelectedTabId: "tab-2"),
            new Column("col-b", [new Timeline("tab-3", "日本語 \"quoted\" \n newline", "日本語 \"quoted\" \n newline")], SelectedTabId: "tab-3"),
            new Column("col-c", [], SelectedTabId: null),
        ],
        ActiveColumnId: "col-b",
        new Settings(Theme: AppTheme.Dark));

    [Fact]
    public async Task Load_NoFile_ReturnsDefault_WritesNothing_AndReportsNothing()
    {
        var store = NewStore();

        var state = await store.LoadAsync();

        Assert.Equal(AppState.Default, state);
        Assert.False(Directory.Exists(_dir), "LoadAsync must not even create the directory: absence is a state, not an error");
        Assert.Empty(_problems);
    }

    [Fact]
    public async Task SaveThenLoad_RoundTrips_EveryField()
    {
        var store = NewStore();
        var original = Sample();

        await store.SaveAsync(original);
        var loaded = await new JsonAppStateStore(FilePath, _problems.Add).LoadAsync(); // A fresh instance: nothing may be cached.

        Assert.Equal(original.SchemaVersion, loaded.SchemaVersion);
        Assert.Equal(original.ActiveColumnId, loaded.ActiveColumnId);
        Assert.Equal(original.Settings, loaded.Settings);
        Assert.Equal(original.Columns.Count, loaded.Columns.Count);
        for (var c = 0; c < original.Columns.Count; c++)
        {
            Assert.Equal(original.Columns[c].Id, loaded.Columns[c].Id);
            Assert.Equal(original.Columns[c].SelectedTabId, loaded.Columns[c].SelectedTabId);
            Assert.Equal(original.Columns[c].Tabs, loaded.Columns[c].Tabs);
        }

        Assert.Empty(_problems);
    }

    [Fact]
    public async Task Save_WritesExactlyTheContractedMembers_AndNothingElse()
    {
        await NewStore().SaveAsync(Sample());

        using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(FilePath));
        var root = document.RootElement;
        Assert.Equal(["schemaVersion", "columns", "activeColumnId", "settings"], root.EnumerateObject().Select(p => p.Name));
        Assert.Equal(["id", "tabs", "selectedTabId"], root.GetProperty("columns")[0].EnumerateObject().Select(p => p.Name));
        Assert.Equal(["id", "title", "kqlQuery"], root.GetProperty("columns")[0].GetProperty("tabs")[0].EnumerateObject().Select(p => p.Name));
        Assert.Equal(["theme"], root.GetProperty("settings").EnumerateObject().Select(p => p.Name)); // Theme only: Locale/Extra have no chooser yet.
        Assert.Equal("Dark", root.GetProperty("settings").GetProperty("theme").GetString());
    }

    [Fact]
    public async Task Save_LeavesNoTempFile_AndCreatesTheFile0600()
    {
        var store = NewStore();

        await store.SaveAsync(Sample());

        Assert.True(File.Exists(FilePath));
        Assert.False(File.Exists(FilePath + ".tmp"), "the temp file must have been renamed over the target, not left beside it");
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(FilePath));
    }

    [Fact]
    public async Task Save_ReplacesAnExistingFile_AndCleansUpAStaleTempFile()
    {
        var store = NewStore();
        await store.SaveAsync(Sample());
        await File.WriteAllTextAsync(FilePath + ".tmp", "a crash left this behind");

        var updated = Sample() with { ActiveColumnId = "col-a", Settings = new Settings(Theme: AppTheme.Light) };
        await store.SaveAsync(updated);

        var loaded = await store.LoadAsync();
        Assert.Equal("col-a", loaded.ActiveColumnId);
        Assert.Equal(AppTheme.Light, loaded.Settings.Theme);
        Assert.False(File.Exists(FilePath + ".tmp"));
    }

    [Fact]
    public async Task Save_OutOfBounds_ThrowsArgumentException_AndWritesNothing()
    {
        var store = NewStore();
        var tooManyColumns = new AppState(
            AppState.CurrentSchemaVersion,
            Enumerable.Range(0, AppState.MaxColumns + 1).Select(i => new Column($"c{i}", [], null)).ToArray(),
            ActiveColumnId: "c0",
            new Settings());

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => store.SaveAsync(tooManyColumns));

        Assert.Contains("columns is above the limit", ex.Message);
        Assert.False(File.Exists(FilePath));
        Assert.False(File.Exists(FilePath + ".tmp"));
    }

    [Theory]
    [MemberData(nameof(UntrustedFiles))]
    public async Task Load_UntrustedContent_FallsBackToDefault_ReportsOnce_AndLeavesTheFileUntouched(string label, byte[] content, string expectedProblemFragment)
    {
        Directory.CreateDirectory(_dir);
        await File.WriteAllBytesAsync(FilePath, content);
        var store = NewStore();

        var state = await store.LoadAsync();

        Assert.Equal(AppState.Default, state);
        var problem = Assert.Single(_problems);
        Assert.Contains(expectedProblemFragment, problem);
        Assert.Equal(content, await File.ReadAllBytesAsync(FilePath)); // Never repaired, truncated or deleted.
        Assert.False(File.Exists(FilePath + ".tmp"), $"{label}: a load must not write");
    }

    public static IEnumerable<object[]> UntrustedFiles()
    {
        static object[] Case(string label, string json, string fragment) => [label, Encoding.UTF8.GetBytes(json), fragment];

        yield return Case("truncated mid-object", "{\"schemaVersion\":1,\"columns\":[{\"id\":\"a\",\"tabs\":[{\"id\":", "malformed JSON");
        yield return ["random bytes", new byte[] { 0x00, 0xFF, 0xFE, 0x7B, 0x22, 0x0A, 0x1B, 0x5B, 0x33, 0x31, 0x6D }, "malformed JSON"];
        yield return Case("empty file", string.Empty, "malformed JSON");
        yield return Case("root is null", "null", "literal null");
        yield return Case("root is an array", "[]", "malformed JSON");
        yield return Case("root is a string", "\"hello\"", "malformed JSON");
        yield return Case("unknown schema version", "{\"schemaVersion\":2,\"columns\":[],\"activeColumnId\":null,\"settings\":{\"theme\":\"System\"}}", "schemaVersion 2 is not 1");
        yield return Case("missing schema version", "{\"columns\":[],\"activeColumnId\":null,\"settings\":{\"theme\":\"System\"}}", "schemaVersion (missing)");
        yield return Case("columns missing", "{\"schemaVersion\":1,\"activeColumnId\":null,\"settings\":{\"theme\":\"System\"}}", "columns is missing");
        yield return Case("column without tabs", "{\"schemaVersion\":1,\"columns\":[{\"id\":\"a\"}],\"activeColumnId\":\"a\",\"settings\":{\"theme\":\"System\"}}", "columns[0] is missing id or tabs");
        yield return Case("tab without query", "{\"schemaVersion\":1,\"columns\":[{\"id\":\"a\",\"tabs\":[{\"id\":\"t\",\"title\":\"x\"}],\"selectedTabId\":\"t\"}],\"activeColumnId\":\"a\",\"settings\":{\"theme\":\"System\"}}", "columns[0].tabs[0] is missing");
        yield return Case("unknown theme", "{\"schemaVersion\":1,\"columns\":[],\"activeColumnId\":null,\"settings\":{\"theme\":\"Neon\"}}", "settings.theme");
        yield return Case("theme as number", "{\"schemaVersion\":1,\"columns\":[],\"activeColumnId\":null,\"settings\":{\"theme\":2}}", "malformed JSON");
        yield return Case("settings missing", "{\"schemaVersion\":1,\"columns\":[],\"activeColumnId\":null}", "settings is missing");
        yield return Case("selected tab names nothing", "{\"schemaVersion\":1,\"columns\":[{\"id\":\"a\",\"tabs\":[{\"id\":\"t\",\"title\":\"x\",\"kqlQuery\":\"x\"}],\"selectedTabId\":\"zzz\"}],\"activeColumnId\":\"a\",\"settings\":{\"theme\":\"System\"}}", "selectedTabId does not name");
        yield return Case("active column names nothing", "{\"schemaVersion\":1,\"columns\":[{\"id\":\"a\",\"tabs\":[],\"selectedTabId\":null}],\"activeColumnId\":\"b\",\"settings\":{\"theme\":\"System\"}}", "activeColumnId does not name");
        yield return Case("active column null with columns present", "{\"schemaVersion\":1,\"columns\":[{\"id\":\"a\",\"tabs\":[],\"selectedTabId\":null}],\"activeColumnId\":null,\"settings\":{\"theme\":\"System\"}}", "activeColumnId does not name");
        yield return Case("duplicate column ids", "{\"schemaVersion\":1,\"columns\":[{\"id\":\"a\",\"tabs\":[],\"selectedTabId\":null},{\"id\":\"a\",\"tabs\":[],\"selectedTabId\":null}],\"activeColumnId\":\"a\",\"settings\":{\"theme\":\"System\"}}", "repeats an earlier column");
        yield return Case("empty column id", "{\"schemaVersion\":1,\"columns\":[{\"id\":\"\",\"tabs\":[],\"selectedTabId\":null}],\"activeColumnId\":\"\",\"settings\":{\"theme\":\"System\"}}", "columns[0].id is empty");
        yield return Case("too many columns", Document(columns: AppState.MaxColumns + 1, tabsPerColumn: 0, queryLength: 1), $"above the limit of {AppState.MaxColumns}");
        yield return Case("too many tabs in one column", Document(columns: 1, tabsPerColumn: AppState.MaxTabsPerColumn + 1, queryLength: 1), $"above the limit of {AppState.MaxTabsPerColumn}");
        yield return Case("query too long", Document(columns: 1, tabsPerColumn: 1, queryLength: AppState.MaxQueryLength + 1), $"above the limit of {AppState.MaxQueryLength}");
        yield return Case("id too long", "{\"schemaVersion\":1,\"columns\":[{\"id\":\"" + new string('x', AppState.MaxIdLength + 1) + "\",\"tabs\":[],\"selectedTabId\":null}],\"activeColumnId\":\"a\",\"settings\":{\"theme\":\"System\"}}", "columns[0].id is empty or longer");
        yield return Case("trailing comma", "{\"schemaVersion\":1,\"columns\":[],\"activeColumnId\":null,\"settings\":{\"theme\":\"System\"},}", "malformed JSON");
        yield return Case("comment", "{/* hi */\"schemaVersion\":1,\"columns\":[],\"activeColumnId\":null,\"settings\":{\"theme\":\"System\"}}", "malformed JSON");
    }

    /// <summary>Builds an otherwise valid document whose counts/lengths are the one thing under test, so an
    /// over-the-limit case fails for the limit and nothing else.</summary>
    private static string Document(int columns, int tabsPerColumn, int queryLength)
    {
        var query = new string('q', queryLength);
        var cols = Enumerable.Range(0, columns).Select(c =>
        {
            var tabs = Enumerable.Range(0, tabsPerColumn).Select(t => $"{{\"id\":\"t{c}-{t}\",\"title\":\"{query}\",\"kqlQuery\":\"{query}\"}}");
            var selected = tabsPerColumn == 0 ? "null" : $"\"t{c}-0\"";
            return $"{{\"id\":\"c{c}\",\"tabs\":[{string.Join(',', tabs)}],\"selectedTabId\":{selected}}}";
        });
        return $"{{\"schemaVersion\":1,\"columns\":[{string.Join(',', cols)}],\"activeColumnId\":\"c0\",\"settings\":{{\"theme\":\"System\"}}}}";
    }

    [Fact]
    public async Task Load_FileAboveTheSizeLimit_FallsBackWithoutParsing()
    {
        Directory.CreateDirectory(_dir);
        // A sparse file: the length is what is checked, no 64 MiB actually hits the disk.
        using (var stream = new FileStream(FilePath, FileMode.CreateNew))
        {
            stream.SetLength(JsonAppStateStore.MaxFileBytes + 1);
        }

        var state = await NewStore().LoadAsync();

        Assert.Equal(AppState.Default, state);
        Assert.Contains("above the", Assert.Single(_problems));
    }

    [Fact]
    public async Task Load_PathIsADirectory_FallsBackAndReports()
    {
        Directory.CreateDirectory(FilePath); // The "file" is a directory: ReadAllBytes fails with an I/O error, not "not found".

        var state = await NewStore().LoadAsync();

        Assert.Equal(AppState.Default, state);
        Assert.Contains("is a directory", Assert.Single(_problems));
    }

    [Fact]
    public async Task Load_IgnoresUnknownMembers_ButKeepsTheOnesItKnows()
    {
        Directory.CreateDirectory(_dir);
        await File.WriteAllTextAsync(FilePath, "{\"schemaVersion\":1,\"future\":{\"x\":1},\"columns\":[{\"id\":\"a\",\"extra\":true,\"tabs\":[],\"selectedTabId\":null}],\"activeColumnId\":\"a\",\"settings\":{\"theme\":\"Light\",\"later\":\"y\"}}");

        var state = await NewStore().LoadAsync();

        Assert.Equal("a", Assert.Single(state.Columns).Id);
        Assert.Equal(AppTheme.Light, state.Settings.Theme);
        Assert.Empty(_problems);
    }

    [Fact]
    public async Task Load_EmptyColumns_IsAValidNoLayout()
    {
        Directory.CreateDirectory(_dir);
        await File.WriteAllTextAsync(FilePath, "{\"schemaVersion\":1,\"columns\":[],\"activeColumnId\":null,\"settings\":{\"theme\":\"Dark\"}}");

        var state = await NewStore().LoadAsync();

        Assert.Empty(state.Columns);
        Assert.Null(state.ActiveColumnId);
        Assert.Equal(AppTheme.Dark, state.Settings.Theme);
        Assert.Empty(_problems);
    }

    [Fact]
    public async Task Diagnostics_NeverQuoteTheFilesText()
    {
        Directory.CreateDirectory(_dir);
        const string marker = "SECRET-LOOKING-TEXT-\u001b[31m";
        await File.WriteAllTextAsync(FilePath, "{\"schemaVersion\":1,\"columns\":[{\"id\":\"" + marker + "\",\"tabs\":[],\"selectedTabId\":null}],\"activeColumnId\":\"nope\",\"settings\":{\"theme\":\"System\"}}");

        await NewStore().LoadAsync();

        Assert.DoesNotContain(marker, Assert.Single(_problems));
    }
}
