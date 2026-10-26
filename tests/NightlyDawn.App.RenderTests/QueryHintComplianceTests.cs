using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NightlyDawn.App.RenderTests.Fakes;
using NightlyDawn.Core;
using NightlyDawn.Filters;
using NightlyDawn.Keys;
using Xunit;

namespace NightlyDawn.App.RenderTests;

/// <summary>
/// Plan §44 / L10: <c>TabStripView.axaml</c>'s <c>QueryBox.PlaceholderText</c> shows three example queries, and
/// every one of them must be real, compilable KQL -- the fork's own hint (<c>from home | from user(npub1...) |
/// from search("nostr")</c>) was not: <c>where</c> is mandatory (<see cref="FilterParseException"/> says so), and
/// a bare <c>npub1...</c> source argument is not a quoted <c>STRING</c> token at all. Typing the hint verbatim
/// always errored (plan §42 N4 / §44).
///
/// <para>This reads <c>PlaceholderText</c> live off the realized <see cref="TextBox"/> rather than copying the
/// three examples into the test as separate string literals -- the whole point (plan §44) is that the hint can
/// never again drift out of sync with the grammar without this test catching it, which a hand-copied string
/// could not do.</para>
///
/// <para>Mutation check performed by hand (reverted after confirming red): putting the fork's original,
/// unparseable hint back into <c>TabStripView.axaml</c> turns <see cref="PlaceholderText_EveryExampleParsesAndCompiles"/>
/// red with a <see cref="FilterParseException"/> ("KQL query must contain a 'where' clause") on the very first
/// example.</para>
/// </summary>
public sealed class QueryHintComplianceTests
{
    // Account-bound sources (`from home`) need a pubkey and a resolved follow list to compile (plan §7.6,
    // NostrFilterQueryCompiler.ResolveHomeAsync) -- that resolution is an I/O step this test has no relay for,
    // so an empty follow list stands in for "signed in, follows nobody yet", which KqlCompiler already treats
    // as a meaningful, compilable answer rather than an error (plan §13.2 item 4).
    private const string AccountPubkeyHex = "0000000000000000000000000000000000000000000000000000000000000001";

    [AvaloniaFact]
    public void PlaceholderText_EveryExampleParsesAndCompiles()
    {
        using var session = new Session();

        var settingsToggle = session.Find<ToggleButton>("SettingsToggle") ?? throw new Xunit.Sdk.XunitException("SettingsToggle not found");
        session.Click(settingsToggle);

        var queryBox = session.Find<TextBox>("QueryBox") ?? throw new Xunit.Sdk.XunitException("QueryBox not found after opening the settings toggle");
        var hint = queryBox.PlaceholderText;
        Assert.False(string.IsNullOrWhiteSpace(hint), "QueryBox.PlaceholderText should not be empty");

        var examples = hint!.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, examples.Length); // guards against the hint collapsing to one example unnoticed

        var compiler = new FilterCompiler();
        foreach (var example in examples)
        {
            var ast = compiler.Parse(example);
            var compiled = ast.Source.Kind == FilterSourceKind.Home
                ? compiler.Compile(ast, accountPubkey: AccountPubkeyHex, resolvedFollows: [])
                : compiler.Compile(ast);

            Assert.NotNull(compiled);
        }
    }

    private sealed class Session : IDisposable
    {
        private readonly string _tempKeyDir;

        public MainWindow Window { get; }

        public Session()
        {
            _tempKeyDir = Directory.CreateTempSubdirectory("nightlydawn-query-hint-tests").FullName;

            AppServices.KeyStore = new LocalKeyStore(new FileKeyFileStore(Path.Combine(_tempKeyDir, "key.ncryptsec")));
            AppServices.TimelineSourceFactory = new StaticTimelineSourceFactory([]);
            AppServices.ScreenshotPath = null;

            Window = new MainWindow { Width = 1280, Height = 800 };
            Window.Show();
            HeadlessWindowExtensions.SetRenderScaling(Window, 1.0);
            Dispatcher.UIThread.RunJobs();
        }

        public T? Find<T>(string name) where T : Control =>
            Window.GetVisualDescendants().OfType<T>().FirstOrDefault(c => c.Name == name);

        public void Click(Control control)
        {
            var center = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), Window)
                ?? throw new Xunit.Sdk.XunitException($"{control.Name}: not attached to the window's visual tree");
            Window.MouseDown(center, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Window.MouseUp(center, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
        }

        public void Dispose()
        {
            Window.Close();
            AppServices.KeyStore = null;
            Directory.Delete(_tempKeyDir, recursive: true);
        }
    }
}
