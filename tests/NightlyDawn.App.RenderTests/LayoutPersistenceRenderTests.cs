using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NightlyDawn.App.RenderTests.Fakes;
using NightlyDawn.App.Theming;
using NightlyDawn.App.Timelines;
using NightlyDawn.Core;
using NightlyDawn.Keys;
using NightlyDawn.Storage;
using Xunit;

namespace NightlyDawn.App.RenderTests;

/// <summary>
/// L9 (plan §32) end to end through the shipped path: a real <see cref="JsonAppStateStore"/> on a temp
/// directory goes into <see cref="AppServices.AppStateStore"/>, exactly as <c>Host/Program.cs</c> does, and
/// then <see cref="MainWindow"/> is constructed -- so what is measured is the shipped parser, the shipped
/// fallback, and the shipped restore, not a copy of any of them (plan §32.7: a test that re-implemented the
/// load in order to call it would be green no matter what the app did).
///
/// <para>The happy-path fixture is a hand-written JSON string, not the output of <c>SaveAsync</c>: if the
/// writer and the reader drifted together, a round trip would stay green while every file on every user's disk
/// stopped loading. The "next launch" tests do use the real writer, because that <em>is</em> what they measure:
/// that a change made through the window comes back when a second window is built over the same directory.
/// That is the acceptance Lead promoted from §30/§31 -- assert the side that the guarantee is supposed to
/// change (the next window's columns), not the guarantee itself (that <c>SaveAsync</c> was called).</para>
///
/// <para>Mutations run by hand while writing this class, each reverted after confirming the colour (the exact
/// outputs are quoted in the PR body): (1) deleting <c>_persistence.Restore()</c> from <c>MainWindow</c>'s
/// constructor turns 9 of the 10 tests red -- <see cref="SavedFile_ComesBackAsColumnsTabsQueriesAndTheme"/>
/// (two columns expected, one found), <see cref="TypedQuery_IsRestoredByTheNextLaunch"/> and
/// <see cref="StateFile_NeverContainsKeyMaterial"/> (no file is ever written because nothing is ever armed),
/// <see cref="ThemeChoice_IsRestoredByTheNextLaunch"/> (Default, not Dark), and every case of
/// <see cref="UntrustedFile_StartsWithTheDefaultLayout_WithoutThrowing_AndLeavesItAlone"/>, because the load
/// that would have reported the problem never ran. Only <see cref="NoFile_StartsWithTheDefaultLayout_AndWritesNothing"/>
/// stays green, as it must: a window that never persists trivially starts with the defaults and writes nothing.
/// (2) replacing the write inside <c>JsonAppStateStore.SaveAsync</c> with a no-op turns the two "next launch"
/// tests and the key-material test red at the file-exists check while the restore-only tests stay green.
/// (3) letting <c>JsonException</c> escape the store turns the truncated and binary cases of the untrusted-file
/// theory red with the exception escaping the window constructor -- the crash loop plan §32.3 forbids.
/// (4) skipping <c>AppStateShape.Check</c> on the read side turns the "selection names nothing" case red (the
/// stale file's tab is shown instead of the default). (5) arming <c>LayoutPersistence</c>'s listeners
/// <em>before</em> the restore instead of after turns <see cref="SavedFile_ComesBackAsColumnsTabsQueriesAndTheme"/>
/// red at its last assertion -- the file was rewritten on close although the user changed nothing -- and
/// <c>LayoutPersistenceTests.Restore_ItselfNeverSaves_EvenThoughItChangesEverything</c> red in App.Tests.</para>
/// </summary>
public sealed class LayoutPersistenceRenderTests
{
    private const string QueryA1 = "from home";
    private const string QueryA2 = "from kind(\"1\")";
    private const string QueryB1 = "from search(\"nostr\")";

    /// <summary>Written by hand, deliberately not by <c>SaveAsync</c> (see the class remarks). Two columns; the
    /// first has two tabs with the second selected; the second column is active; theme Dark.</summary>
    private static readonly string SavedLayoutJson = """
        {
          "schemaVersion": 1,
          "columns": [
            {
              "id": "col-a",
              "tabs": [
                { "id": "tab-a1", "title": "from home", "kqlQuery": "from home" },
                { "id": "tab-a2", "title": "from kind(\"1\")", "kqlQuery": "from kind(\"1\")" }
              ],
              "selectedTabId": "tab-a2"
            },
            {
              "id": "col-b",
              "tabs": [
                { "id": "tab-b1", "title": "from search(\"nostr\")", "kqlQuery": "from search(\"nostr\")" }
              ],
              "selectedTabId": "tab-b1"
            }
          ],
          "activeColumnId": "col-b",
          "settings": { "theme": "Dark" }
        }
        """;

    [AvaloniaFact]
    public void SavedFile_ComesBackAsColumnsTabsQueriesAndTheme()
    {
        using var dir = new StateDirectory();
        dir.WriteState(SavedLayoutJson);
        var original = dir.ReadStateBytes();

        using var session = new Session(dir);

        // The visual tree, not the view model: two column roots, three tab headers carrying the saved queries.
        Assert.Equal(2, session.FindAll<DockPanel>("ColumnRoot").Count);
        Assert.Equal([QueryA1, QueryA2, QueryB1], session.FindAll<Button>("TabHeaderButton").Select(b => b.Content?.ToString()));

        // Restored tabs subscribe themselves: both visible timelines show the fake factory's two notes.
        var lists = session.FindAll<ListBox>("TimelineList");
        Assert.Equal(2, lists.Count);
        session.Pump(() => lists.All(l => l.ItemCount == 2), "the restored tabs never showed their notes");

        // Selection came back by id, and the second column's query is there when its settings open (real click).
        var workspace = (WorkspaceViewModel)session.Window.DataContext!;
        Assert.Equal("col-b", workspace.SelectedColumn!.Id);
        Assert.Equal("tab-a2", workspace.Columns[0].SelectedTab!.Id);
        session.Click(session.FindAll<ToggleButton>("SettingsToggle")[1]);
        var queryBox = session.Find<TextBox>("QueryBox") ?? throw new Xunit.Sdk.XunitException("QueryBox not found after opening column 2's settings");
        Assert.Equal(QueryB1, queryBox.Text);

        Assert.Equal(ThemeVariant.Dark, Application.Current!.RequestedThemeVariant);
        Assert.Empty(dir.Problems);

        // Opening and closing a window the user did not touch is not a change: the file must be byte-for-byte what
        // it was (the restore itself raises every LayoutChanged there is; none of them may reach the store). The
        // hand-written fixture is formatted differently from what SaveAsync writes, so a rewrite cannot hide.
        session.Dispose();
        Assert.Equal(original, dir.ReadStateBytes());
    }

    /// <summary>Negative control ① (plan §32.4): an untrusted file costs the layout, never the launch. Each case
    /// is a different way the file can be wrong; every one must open the window with the built-in layout, report
    /// exactly one problem, and leave the file byte-for-byte as it was after the window has closed.</summary>
    [AvaloniaTheory]
    [InlineData("truncated", "{\"schemaVersion\":1,\"columns\":[{\"id\":\"col-a\",\"tabs\":[{\"id\":")]
    [InlineData("binary", "\0\u00ff{\"\u001b[31m")]
    [InlineData("unknown schema version", "{\"schemaVersion\":7,\"columns\":[],\"activeColumnId\":null,\"settings\":{\"theme\":\"System\"}}")]
    [InlineData("selection names nothing", "{\"schemaVersion\":1,\"columns\":[{\"id\":\"a\",\"tabs\":[{\"id\":\"t\",\"title\":\"x\",\"kqlQuery\":\"x\"}],\"selectedTabId\":\"gone\"}],\"activeColumnId\":\"a\",\"settings\":{\"theme\":\"System\"}}")]
    [InlineData("too many columns", null)]
    public void UntrustedFile_StartsWithTheDefaultLayout_WithoutThrowing_AndLeavesItAlone(string label, string? content)
    {
        content ??= TooManyColumns();
        using var dir = new StateDirectory();
        var original = Encoding.UTF8.GetBytes(content);
        dir.WriteState(original);

        using (var session = new Session(dir)) // Constructing MainWindow is the act under test: it must not throw.
        {
            Assert.Single(session.FindAll<DockPanel>("ColumnRoot"));
            var header = Assert.Single(session.FindAll<Button>("TabHeaderButton"));
            Assert.Equal("(new tab)", header.Content?.ToString());
            Assert.Equal(ThemeVariant.Default, Application.Current!.RequestedThemeVariant);
        }

        Assert.Single(dir.Problems);
        Assert.Equal(original, dir.ReadStateBytes()); // Not repaired, not replaced by a default, not deleted.
        Assert.False(File.Exists(dir.StatePath + ".tmp"), $"{label}: a failed load must not leave a write behind");
    }

    /// <summary>Negative control ② (plan §32.4): no file means the defaults, and -- the half a round-trip test
    /// cannot see -- nothing is written: not on construction, not on show, not on close. A first launch that
    /// wrote its defaults would make "never chose" and "chose the default" the same file forever
    /// ([[persisted-default-erases-unset]]).</summary>
    [AvaloniaFact]
    public void NoFile_StartsWithTheDefaultLayout_AndWritesNothing()
    {
        using var dir = new StateDirectory();

        using (var session = new Session(dir))
        {
            Assert.Single(session.FindAll<DockPanel>("ColumnRoot"));
            Assert.Equal("(new tab)", Assert.Single(session.FindAll<Button>("TabHeaderButton")).Content?.ToString());
            session.Pump();
        }

        Assert.Equal([], dir.Files().Where(f => f != "key.ncryptsec").ToArray());
        Assert.Empty(dir.Problems);
    }

    /// <summary>The acceptance Lead promoted from §30/§31: not "SaveAsync was called" but "the next window
    /// shows what was typed into this one". Typed through the real settings popup with real text input.</summary>
    [AvaloniaFact]
    public void TypedQuery_IsRestoredByTheNextLaunch()
    {
        using var dir = new StateDirectory();

        using (var first = new Session(dir))
        {
            first.Click(first.Find<ToggleButton>("SettingsToggle")!);
            var queryBox = first.Find<TextBox>("QueryBox") ?? throw new Xunit.Sdk.XunitException("QueryBox not found");
            queryBox.Focus();
            first.Window.KeyTextInput(QueryA2);
            first.Pump();
            Assert.Equal(QueryA2, queryBox.Text); // The keystrokes landed where the user would see them.
            first.Click(first.Find<Button>("SubscribeButton")!);
            first.Pump(() => first.Find<ListBox>("TimelineList")!.ItemCount == 2, "the typed query never produced notes");
        }

        // The close flushed: the file exists and carries the typed query (the real writer wrote it).
        Assert.True(File.Exists(dir.StatePath), "closing the window must write the pending layout");
        using (var document = JsonDocument.Parse(dir.ReadStateBytes()))
        {
            Assert.Equal(QueryA2, document.RootElement.GetProperty("columns")[0].GetProperty("tabs")[0].GetProperty("kqlQuery").GetString());
        }

        using (var second = new Session(dir))
        {
            Assert.Equal(QueryA2, Assert.Single(second.FindAll<Button>("TabHeaderButton")).Content?.ToString());
            second.Pump(() => second.Find<ListBox>("TimelineList")!.ItemCount == 2, "the restored query never produced notes");
            second.Click(second.Find<ToggleButton>("SettingsToggle")!);
            Assert.Equal(QueryA2, second.Find<TextBox>("QueryBox")!.Text);
        }

        Assert.Empty(dir.Problems);
    }

    /// <summary>L8 (#33) explicitly left this to L9: the drop-down's choice must survive a relaunch.</summary>
    [AvaloniaFact]
    public void ThemeChoice_IsRestoredByTheNextLaunch()
    {
        using var dir = new StateDirectory();

        using (var first = new Session(dir))
        {
            first.ChooseTheme(AppTheme.Dark);
            Assert.Equal(ThemeVariant.Dark, Application.Current!.RequestedThemeVariant);
        }

        // Session.Dispose put the Application back to the variant it found, so what the next window shows is
        // the file's doing, not a leftover.
        Assert.NotEqual(ThemeVariant.Dark, Application.Current!.RequestedThemeVariant);

        using (var second = new Session(dir))
        {
            Assert.Equal(ThemeVariant.Dark, Application.Current!.RequestedThemeVariant);
            Assert.Equal(AppTheme.Dark, ((ThemeOption)second.Find<ComboBox>("ThemeSelector")!.SelectedItem!).Value);
        }

        Assert.Empty(dir.Problems);
    }

    /// <summary>Plan §32.3: "not one byte of a secret". A real <see cref="LocalKeyStore"/> with a freshly
    /// generated key sits beside the state file while a layout change is saved; the file must then contain
    /// exactly the four contracted members and nothing that came from the key store.</summary>
    [AvaloniaFact]
    public void StateFile_NeverContainsKeyMaterial()
    {
        using var dir = new StateDirectory();
        const string passphrase = "correct horse battery staple";
        SignerDescriptor signer;

        using (var session = new Session(dir))
        {
            signer = AppServices.KeyStore!.GenerateLocalKeyAsync(passphrase.AsMemory()).GetAwaiter().GetResult();
            session.Click(session.Find<Button>("AddColumnButton")!); // A layout change, so a save happens on close.
            Assert.Equal(2, session.FindAll<DockPanel>("ColumnRoot").Count);
        }

        var json = Encoding.UTF8.GetString(dir.ReadStateBytes());
        var keyFile = File.ReadAllText(Path.Combine(dir.Path, "key.ncryptsec"));
        Assert.StartsWith("ncryptsec1", keyFile); // The secret really was on disk, beside the state, while it was written.

        using (var document = JsonDocument.Parse(json))
        {
            Assert.Equal(["schemaVersion", "columns", "activeColumnId", "settings"], document.RootElement.EnumerateObject().Select(p => p.Name));
            Assert.Equal(2, document.RootElement.GetProperty("columns").GetArrayLength());
        }

        Assert.DoesNotContain("nsec", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ncryptsec", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(keyFile.Trim(), json, StringComparison.Ordinal);
        Assert.DoesNotContain(passphrase, json, StringComparison.Ordinal);
        Assert.DoesNotContain(signer.Pubkey, json, StringComparison.OrdinalIgnoreCase);
    }

    private static string TooManyColumns()
    {
        var columns = Enumerable.Range(0, AppState.MaxColumns + 1).Select(i => $"{{\"id\":\"c{i}\",\"tabs\":[],\"selectedTabId\":null}}");
        return $"{{\"schemaVersion\":1,\"columns\":[{string.Join(',', columns)}],\"activeColumnId\":\"c0\",\"settings\":{{\"theme\":\"System\"}}}}";
    }

    /// <summary>A temp data directory standing in for <c>AppDataPaths</c>: the state file and the key file side by
    /// side, the way the host lays them out. Collects the store's diagnostics so a test can assert on them.</summary>
    private sealed class StateDirectory : IDisposable
    {
        public StateDirectory()
        {
            Path = Directory.CreateTempSubdirectory("nightlydawn-layout-persistence-tests").FullName;
        }

        public string Path { get; }

        public string StatePath => System.IO.Path.Combine(Path, "app-state.json");

        public List<string> Problems { get; } = [];

        public JsonAppStateStore NewStore() => new(StatePath, Problems.Add);

        public void WriteState(string json) => File.WriteAllText(StatePath, json);

        public void WriteState(byte[] bytes) => File.WriteAllBytes(StatePath, bytes);

        public byte[] ReadStateBytes() => File.ReadAllBytes(StatePath);

        public IEnumerable<string> Files() => Directory.GetFiles(Path).Select(System.IO.Path.GetFileName)!;

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    /// <summary>One headless window over <paramref name="dir"/>: the real <see cref="JsonAppStateStore"/> in
    /// <see cref="AppServices.AppStateStore"/> and a real <see cref="LocalKeyStore"/> beside it, the same two
    /// assignments <c>Host/Program.cs</c> makes. Theme state is saved on entry and restored on exit so one test's
    /// Dark cannot leak into the next test's "Default".</summary>
    private sealed class Session : IDisposable
    {
        private static readonly TimeSpan PumpTimeout = TimeSpan.FromSeconds(5);
        private readonly AppTheme _previousTheme;
        private readonly ThemeVariant? _previousVariant;

        public MainWindow Window { get; }

        public Session(StateDirectory dir)
        {
            _previousTheme = AppServices.Theme;
            _previousVariant = Application.Current!.RequestedThemeVariant;
            AppServices.Theme = AppTheme.System;
            ThemeController.Apply(Application.Current!, AppTheme.System);

            AppServices.KeyStore = new LocalKeyStore(new FileKeyFileStore(Path.Combine(dir.Path, "key.ncryptsec")));
            AppServices.TimelineSourceFactory = new StaticTimelineSourceFactory(SampleNotes());
            AppServices.ScreenshotPath = null;
            AppServices.AppStateStore = dir.NewStore();

            Window = new MainWindow { Width = 1280, Height = 800 };
            Window.Show();
            HeadlessWindowExtensions.SetRenderScaling(Window, 1.0);
            Dispatcher.UIThread.RunJobs();
        }

        public T? Find<T>(string name) where T : Control =>
            Window.GetVisualDescendants().OfType<T>().FirstOrDefault(c => c.Name == name);

        public IReadOnlyList<T> FindAll<T>(string name) where T : Control =>
            Window.GetVisualDescendants().OfType<T>().Where(c => c.Name == name).ToList();

        /// <summary>Real pointer input (plan §28: a raised <c>ClickEvent</c> skips <c>ToggleButton.OnClick()</c>).</summary>
        public void Click(Control control)
        {
            var center = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), Window)
                ?? throw new Xunit.Sdk.XunitException($"{control.Name}: not attached to the window's visual tree");
            Window.MouseDown(center, MouseButton.Left);
            Pump();
            Window.MouseUp(center, MouseButton.Left);
            Pump();
        }

        /// <summary>Through the control's own selection, the way <c>ThemeSwitcherRenderTests</c> does it.</summary>
        public void ChooseTheme(AppTheme theme)
        {
            var selector = Find<ComboBox>("ThemeSelector") ?? throw new Xunit.Sdk.XunitException("ThemeSelector not found");
            var option = selector.Items.OfType<ThemeOption>().First(o => o.Value == theme);
            selector.SelectedItem = option;
            Pump();
            if (!ReferenceEquals(selector.SelectedItem, option))
            {
                throw new Xunit.Sdk.XunitException("the drop-down did not take the selection");
            }
        }

        public void Pump() => Dispatcher.UIThread.RunJobs();

        public void Pump(Func<bool> condition, string timeoutMessage)
        {
            var deadline = DateTime.UtcNow + PumpTimeout;
            while (DateTime.UtcNow < deadline)
            {
                Dispatcher.UIThread.RunJobs();
                if (condition())
                {
                    return;
                }

                Thread.Sleep(5);
            }

            throw new TimeoutException(timeoutMessage);
        }

        public void Dispose()
        {
            Window.Close(); // Closed -> LayoutPersistence.FlushOnClose: the write, if any, has happened when this returns.
            Dispatcher.UIThread.RunJobs();
            AppServices.AppStateStore = null;
            AppServices.KeyStore = null;
            AppServices.Theme = _previousTheme;
            Application.Current!.RequestedThemeVariant = _previousVariant;
        }

        private static IReadOnlyList<Note> SampleNotes() =>
        [
            MakeNote(1, "first"),
            MakeNote(2, "second"),
        ];

        private static Note MakeNote(int n, string content) => new(
            Id: n.ToString("x64"),
            AuthorPubkey: n.ToString("x64"),
            CreatedAt: 1_700_000_000 - (n * 60),
            Kind: NoteKind.Text,
            Content: content,
            Tags: []);
    }
}
