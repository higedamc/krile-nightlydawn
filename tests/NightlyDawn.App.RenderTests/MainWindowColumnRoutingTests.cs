using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using NightlyDawn.App.RenderTests.Fakes;
using NightlyDawn.App.Timelines;
using NightlyDawn.Core;
using NightlyDawn.Keys;
using Xunit;

namespace NightlyDawn.App.RenderTests;

/// <summary>
/// Required test for plan §11.1: with two columns open, a row action fired from column B must land only on
/// column B (its <see cref="TimelineViewModel.Status"/> and <see cref="TimelineViewModel.Notes"/>),
/// never on column A. Drives the real <see cref="MainWindow"/> (not a hand-rolled stand-in) so the test
/// exercises <see cref="MainWindow.CreateColumn"/> itself -- the one place the per-column closure this guards
/// against actually lives.
///
/// <para>Mutation check performed by hand while writing this test (plan §11.1's required negative control):
/// changing <c>CreateColumn</c>'s <c>actionsFactory</c> to close over the tab's first column instead of its own
/// <c>column</c> local turned <see cref="RepostFromColumnB_LandsOnlyOnColumnB"/> red (column A's status/notes
/// changed instead of column B's). Reverted after confirming -- see the leaf's progress report for the exact
/// diff and the failure message.</para>
/// </summary>
public sealed class MainWindowColumnRoutingTests
{
    private static readonly TimeSpan PumpTimeout = TimeSpan.FromSeconds(5);

    [AvaloniaFact]
    public async Task RepostFromColumnB_LandsOnlyOnColumnB()
    {
        using var session = new RoutingSession();
        var workspace = (WorkspaceViewModel)session.Window.DataContext!;

        var columnA = workspace.Columns[0].Tabs[0];
        SubscribeAndWaitForLoad(columnA);
        var columnB = workspace.AddColumn().Tabs[0];
        SubscribeAndWaitForLoad(columnB);

        var statusABefore = columnA.Status;
        var notesCountABefore = columnA.Notes.Count;

        var rowB = columnB.Notes[0];
        var actionsB = rowB.Actions ?? throw new InvalidOperationException("column B's row has no actions wired -- actionsFactory returned null");

        await actionsB.RepostAsync();
        Pump(() => columnB.Status.StartsWith("Reposted", StringComparison.Ordinal),
            "column B's own Status never reflected the repost");

        Assert.StartsWith("Reposted", columnB.Status);
        Assert.Contains(columnB.Notes, n => n.DisplayContent.StartsWith("(repost)", StringComparison.Ordinal));

        // The required assertion: column A -- a sibling column the action was never invoked from -- must not
        // move. A shared/misrouted sink would update column A's Status and/or insert the reposted note there
        // instead of (or as well as) column B.
        Assert.Equal(statusABefore, columnA.Status);
        Assert.Equal(notesCountABefore, columnA.Notes.Count);
    }

    private static void SubscribeAndWaitForLoad(TimelineViewModel column)
    {
        column.Query = "from home";
        column.Subscribe();
        Pump(() => !column.IsLoading, "a column's timeline stream never finished loading");
    }

    private static void Pump(Func<bool> condition, string timeoutMessage)
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

    /// <summary>Owns one headless <see cref="MainWindow"/> wired to a <see cref="FakeNotePublisher"/> (so
    /// Repost/React resolve without a signer or relay) and a small deterministic timeline, same composition
    /// pattern as <see cref="MainWindowRenderTests.RenderSession"/> minus the frame capture this test does not need.</summary>
    private sealed class RoutingSession : IDisposable
    {
        private readonly string _tempKeyDir;

        public MainWindow Window { get; }

        public RoutingSession()
        {
            _tempKeyDir = Directory.CreateTempSubdirectory("nightlydawn-routing-tests").FullName;

            AppServices.KeyStore = new LocalKeyStore(new FileKeyFileStore(Path.Combine(_tempKeyDir, "key.ncryptsec")));
            AppServices.TimelineSourceFactory = new StaticTimelineSourceFactory(SampleNotes());
            AppServices.NotePublisher = new FakeNotePublisher();
            AppServices.ScreenshotPath = null;

            Window = new MainWindow { Width = 1280, Height = 800 };
            Window.Show();
            HeadlessWindowExtensions.SetRenderScaling(Window, 1.0);
            Dispatcher.UIThread.RunJobs();
        }

        public void Dispose()
        {
            Window.Close();
            AppServices.KeyStore = null;
            AppServices.NotePublisher = null;
            Directory.Delete(_tempKeyDir, recursive: true);
        }

        private static IReadOnlyList<Note> SampleNotes() =>
        [
            MakeNote(1, "Landed in Omarchy; Hyprland + Wayland feels snappy so far."),
            MakeNote(2, "Checking whether the IME preedit survives a relay round trip."),
        ];

        private static Note MakeNote(int n, string content) => new(
            Id: n.ToString("x64"),
            AuthorPubkey: n.ToString("x64"),
            CreatedAt: 1_700_000_000 - n * 60,
            Kind: NoteKind.Text,
            Content: content,
            Tags: []);
    }
}
