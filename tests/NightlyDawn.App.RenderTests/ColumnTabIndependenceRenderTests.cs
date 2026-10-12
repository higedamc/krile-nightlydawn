using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NightlyDawn.App.Composing;
using NightlyDawn.App.RenderTests.Fakes;
using NightlyDawn.App.Timelines;
using NightlyDawn.Core;
using NightlyDawn.Keys;
using Xunit;

namespace NightlyDawn.App.RenderTests;

/// <summary>
/// The required test for the column/tab inversion itself (plan §21.2, L4b): before this leaf, a tab held
/// columns and there was no per-column selection to protect; now each <see cref="ColumnViewModel"/> owns its
/// own <see cref="ColumnViewModel.SelectedTab"/>, so switching tabs inside one column must leave every other
/// column's selection untouched. Drives the real <see cref="MainWindow"/> -- the per-column
/// <see cref="Views.TabStripView"/>'s own button wiring is what is under test, not
/// <see cref="ColumnViewModel.SelectedTab"/>'s setter in isolation.
///
/// <para>Mutation checks performed by hand while writing this class (both reverted after confirming red):
/// (1) changing <c>ColumnViewModel</c>'s <c>_selectedTab</c> field to <c>static</c> -- so every column shares
/// one selection, the workspace-wide shape this leaf replaces -- turns
/// <see cref="SwitchingOneColumnsTab_DoesNotChangeASiblingColumnsSelection"/> red (column B's selection moved
/// with column A's click). (2) hard-coding <see cref="WorkspaceViewModel.ActiveSink"/> to always read
/// <c>Columns[0].SelectedTab</c> turns <see cref="Post_LandsOnTheSelectedColumnsTab_NotAlwaysTheFirstColumn"/>
/// red (the posted note landed in column A instead of the selected column B).</para>
/// </summary>
public sealed class ColumnTabIndependenceRenderTests
{
    private static readonly TimeSpan PumpTimeout = TimeSpan.FromSeconds(5);

    [AvaloniaFact]
    public void SwitchingOneColumnsTab_DoesNotChangeASiblingColumnsSelection()
    {
        using var session = new Session();
        var workspace = (WorkspaceViewModel)session.Window.DataContext!;

        var columnA = workspace.Columns[0];
        var tabA1 = columnA.Tabs[0];
        var tabA2 = columnA.AddTab();
        session.Pump();
        Assert.Same(tabA2, columnA.SelectedTab); // AddTab selects the one it just built.

        var columnB = workspace.AddColumn();
        var tabB1 = columnB.Tabs[0];
        session.Pump();
        Assert.Same(tabB1, columnB.SelectedTab);

        // Switch column A back to its first tab through the real TabHeaderButton -- the thing under test is
        // TabStripView.OnSelectTabClick's wiring, not ColumnViewModel.SelectedTab's setter directly.
        session.Click(session.FindTabHeaderButton(tabA1));

        Assert.Same(tabA1, columnA.SelectedTab);

        // The required assertion: column B -- a sibling column whose strip was never clicked -- must not move.
        Assert.Same(tabB1, columnB.SelectedTab);
    }

    [AvaloniaFact]
    public async Task Post_LandsOnTheSelectedColumnsTab_NotAlwaysTheFirstColumn()
    {
        using var session = new Session();
        var workspace = (WorkspaceViewModel)session.Window.DataContext!;

        var columnA = workspace.Columns[0];
        var tabA = columnA.Tabs[0];
        var columnB = workspace.AddColumn();
        var tabB = columnB.Tabs[0];
        session.Pump();

        workspace.SelectedColumn = columnB;
        Assert.Same(tabB, workspace.ActiveSink); // Sanity check before driving the real compose box.

        session.ComposeText.Text = "Posted while column B is selected.";
        await session.ClickPostAndWaitAsync();

        Assert.Contains(tabB.Notes, n => n.DisplayContent == "Posted while column B is selected.");
        Assert.DoesNotContain(tabA.Notes, n => n.DisplayContent == "Posted while column B is selected.");
    }

    /// <summary>Owns one headless <see cref="MainWindow"/> wired to a <see cref="FakeNotePublisher"/> (so Post
    /// resolves without a signer or relay), same composition pattern as
    /// <see cref="MainWindowColumnRoutingTests.RoutingSession"/>.</summary>
    private sealed class Session : IDisposable
    {
        private readonly string _tempKeyDir;

        public MainWindow Window { get; }

        public TextBox ComposeText { get; }

        public Session()
        {
            _tempKeyDir = Directory.CreateTempSubdirectory("nightlydawn-column-tab-independence-tests").FullName;

            AppServices.KeyStore = new LocalKeyStore(new FileKeyFileStore(Path.Combine(_tempKeyDir, "key.ncryptsec")));
            AppServices.TimelineSourceFactory = new StaticTimelineSourceFactory([]);
            AppServices.NotePublisher = new FakeNotePublisher();
            AppServices.ScreenshotPath = null;

            Window = new MainWindow { Width = 1280, Height = 800 };
            Window.Show();
            HeadlessWindowExtensions.SetRenderScaling(Window, 1.0);
            Dispatcher.UIThread.RunJobs();

            ComposeText = FindByName<TextBox>(Window, "ComposeText")!;
        }

        public Button FindTabHeaderButton(TimelineViewModel tab) =>
            Window.GetVisualDescendants().OfType<Button>()
                .FirstOrDefault(b => b.Name == "TabHeaderButton" && ReferenceEquals(b.Tag, tab))
            ?? throw new Xunit.Sdk.XunitException("no TabHeaderButton found for the given tab");

        public void Click(Button button)
        {
            button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
        }

        public async Task ClickPostAndWaitAsync()
        {
            var postButton = FindByName<Button>(Window, "PostButton")!;
            var statusBefore = FindByName<TextBlock>(Window, "ComposeStatus")!.Text;
            postButton.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

            var deadline = DateTime.UtcNow + PumpTimeout;
            while (DateTime.UtcNow < deadline)
            {
                Dispatcher.UIThread.RunJobs();
                if (FindByName<TextBlock>(Window, "ComposeStatus")!.Text != statusBefore)
                {
                    return;
                }

                await Task.Delay(5);
            }

            throw new TimeoutException("the compose box's Post click never finished (ComposeStatus never changed)");
        }

        public void Pump() => Dispatcher.UIThread.RunJobs();

        public void Dispose()
        {
            Window.Close();
            AppServices.KeyStore = null;
            AppServices.NotePublisher = null;
            Directory.Delete(_tempKeyDir, recursive: true);
        }

        private static T? FindByName<T>(Avalonia.Visual root, string name) where T : Control =>
            root.GetVisualDescendants().OfType<T>().FirstOrDefault(c => c.Name == name);
    }
}
