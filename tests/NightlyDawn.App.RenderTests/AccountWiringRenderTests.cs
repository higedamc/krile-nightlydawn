using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NightlyDawn.App.RenderTests.Fakes;
using NightlyDawn.App.Timelines;
using NightlyDawn.Core;
using NightlyDawn.Keys;
using Xunit;

namespace NightlyDawn.App.RenderTests;

/// <summary>
/// L5j (plan §18.2 item 4), end to end through the real <see cref="MainWindow"/>: a <c>from home</c> column
/// fails with the account's own wording before any key exists, and -- without anyone pressing Subscribe again --
/// starts streaming once a key is generated through the real Keys panel controls. This is MainWindow's own glue
/// under test (<c>KeyPanelViewModel.PropertyChanged</c> -> <c>WorkspaceViewModel.NotifyAccountUnlocked</c>/
/// <c>NotifyAccountLocked</c>), not the view models in isolation -- those already have
/// <c>TimelineViewModelTests</c> and the mutation-sensitive race test in that class.
/// </summary>
public sealed class AccountWiringRenderTests
{
    private static readonly TimeSpan PumpTimeout = TimeSpan.FromSeconds(5);
    private const string Passphrase = "correct horse battery staple";

    [AvaloniaFact]
    public void FromHome_NeedsAnAccount_ThenResubscribesTheRealControlOnceOneIsGenerated()
    {
        using var session = new Session();
        var column = session.Column;

        column.Query = "from home";
        column.Subscribe();
        session.Pump(() => !column.IsLoading, "the anonymous attempt never finished failing");

        Assert.Equal("Could not open this timeline: 'from home' needs a signed-in account.", column.Status);
        Assert.True(column.NeedsAccountRetry);
        Assert.False(column.IsAccountBound);
        Assert.Empty(column.Notes);
        Assert.Empty(session.Factory.AccountCalls); // Negative control: nothing claims to be account-bound before a key exists.

        session.GenerateKey();
        session.Pump(() => !column.IsLoading, "the retried subscribe never finished");

        Assert.Single(session.Factory.AccountCalls); // The retry used Create(), not a second CreateAnonymous() call.
        Assert.Equal(["account note"], column.Notes.Select(n => n.DisplayContent));
        Assert.False(column.NeedsAccountRetry);
        Assert.True(column.IsAccountBound);
    }

    /// <summary>Negative control for the test above: a column whose query never needs an account must not be
    /// touched by generating one -- <see cref="WorkspaceViewModel.NotifyAccountUnlocked"/> only retries columns
    /// with <see cref="TimelineViewModel.NeedsAccountRetry"/> set, not every column in the workspace. A
    /// version of that method that resubscribed unconditionally would pass the test above and fail only this one.</summary>
    [AvaloniaFact]
    public void GeneratingAKey_DoesNotTouchAColumnThatNeverNeededAnAccount()
    {
        using var session = new Session();
        var column = session.Column;

        column.Query = "from kind(\"1\")";
        column.Subscribe();
        session.Pump(() => !column.IsLoading, "the anonymous subscribe never finished");
        Assert.Empty(column.Notes); // AccountGatedTimelineSourceFactory.CreateAnonymous returns no notes for anything but `from home`.
        Assert.False(column.NeedsAccountRetry);

        session.GenerateKey();
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(session.Factory.AccountCalls); // Unlocking never called Create() for a column that was never waiting on one.
        Assert.False(column.IsAccountBound);
    }

    [AvaloniaFact]
    public void LockingAgain_RevertsAnAccountBoundColumnToAnonymous()
    {
        using var session = new Session();
        session.GenerateKey();

        var column = session.Column;
        column.Query = "from home";
        column.Subscribe();
        session.Pump(() => !column.IsLoading, "the account-bound subscribe never finished");
        Assert.True(column.IsAccountBound);

        session.Click(session.FindByContent<Button>("Lock"));
        session.Pump(() => !column.IsAccountBound, "locking never reverted the column to anonymous");

        Assert.Equal("Could not open this timeline: 'from home' needs a signed-in account.", column.Status);
        Assert.True(column.NeedsAccountRetry);
    }

    /// <summary>Plan §18.5 (L5k) -- the wart L5j left behind. Opening a column while unlocked always goes
    /// through <see cref="ITimelineSourceFactory.Create"/>, even for a query like <c>from kind("1")</c> that
    /// never touches the account; before this leaf <see cref="TimelineViewModel.IsAccountBound"/> was
    /// <c>account is not null</c>, so it came back true regardless, and <see cref="WorkspaceViewModel.NotifyAccountLocked"/>
    /// resubscribed (and cleared) a column that had nothing to do with the account that just locked. The
    /// negative control is the sibling test above (and the second column here): an actually account-bound
    /// <c>from home</c> column must still flip back to anonymous on the very same Lock click.</summary>
    [AvaloniaFact]
    public void Locking_DoesNotTouchAColumnWhoseQueryNeverBoundToTheAccount()
    {
        using var session = new Session();
        session.GenerateKey(); // Unlocked before either column opens, so both open through Create().

        var indifferentColumn = session.Column;
        indifferentColumn.Query = "from kind(\"1\")";
        indifferentColumn.Subscribe();
        session.Pump(() => !indifferentColumn.IsLoading, "the kind(1) subscribe never finished");
        Assert.False(indifferentColumn.IsAccountBound); // Create() ran, but the compiled query never needed the account.
        Assert.NotEmpty(indifferentColumn.Notes);
        var notesBeforeLock = indifferentColumn.Notes.ToList();

        var homeColumn = session.AddColumn();
        homeColumn.Query = "from home";
        homeColumn.Subscribe();
        session.Pump(() => !homeColumn.IsLoading, "the home subscribe never finished");
        Assert.True(homeColumn.IsAccountBound); // Negative control: a genuinely account-bound column, same session.

        session.Click(session.FindByContent<Button>("Lock"));
        session.Pump(() => !homeColumn.IsAccountBound, "locking never reverted the account-bound column");

        // The account-indifferent column must not have been touched at all: no resubscribe, no clear-and-reload.
        Assert.True(indifferentColumn.IsAccountBound == false);
        Assert.Equal(notesBeforeLock, indifferentColumn.Notes.ToList());
        Assert.False(indifferentColumn.NeedsAccountRetry);

        // The account-bound column, meanwhile, behaved exactly like the single-column test above.
        Assert.Equal("Could not open this timeline: 'from home' needs a signed-in account.", homeColumn.Status);
        Assert.True(homeColumn.NeedsAccountRetry);
    }

    /// <summary>Owns one headless window, a temp-dir-backed real <see cref="LocalKeyStore"/> (same construction
    /// as <c>MainWindowRenderTests</c>'s session -- the host builds a real one too), and the account-gated fake
    /// factory. Generates the key through the real <c>KeysPanelView</c> controls rather than calling
    /// <c>KeyPanelViewModel.GenerateAsync</c> directly, since the thing under test is <see cref="MainWindow"/>'s
    /// own wiring, not the view model in isolation.</summary>
    private sealed class Session : IDisposable
    {
        private readonly string _tempKeyDir;

        public MainWindow Window { get; }

        public AccountGatedTimelineSourceFactory Factory { get; }

        public TimelineViewModel Column { get; }

        public Session()
        {
            _tempKeyDir = Directory.CreateTempSubdirectory("nightlydawn-account-wiring-render-tests").FullName;

            AppServices.KeyStore = new LocalKeyStore(new FileKeyFileStore(Path.Combine(_tempKeyDir, "key.ncryptsec")));
            Factory = new AccountGatedTimelineSourceFactory([MakeNote(1, "account note")]);
            AppServices.TimelineSourceFactory = Factory;
            AppServices.ScreenshotPath = null;

            Window = new MainWindow { Width = 900, Height = 600 };
            Window.Show();
            HeadlessWindowExtensions.SetRenderScaling(Window, 1.0);
            Dispatcher.UIThread.RunJobs();

            var keysExpander = FindByName<Expander>(Window, "KeysExpander")!;
            keysExpander.IsExpanded = true;
            Pump(() => FindByName<Control>(Window, "GenerateBox")?.IsVisible == true,
                "the Keys panel never reached the NoKey/Generate state");

            Column = ((WorkspaceViewModel)Window.DataContext!).Columns[0].Tabs[0];
        }

        /// <summary>A second, independent column in the same workspace -- for a test that needs one column
        /// whose query never binds the account alongside one that does, both reacting to the same Lock.</summary>
        public TimelineViewModel AddColumn() => ((WorkspaceViewModel)Window.DataContext!).AddColumn().Tabs[0];

        /// <summary>Drives the real Generate button -- the passphrase text boxes and the click -- the same way
        /// a user does, and waits for the panel to actually reach Unlocked before returning.</summary>
        public void GenerateKey()
        {
            FindByName<TextBox>(Window, "GeneratePassphrase")!.Text = Passphrase;
            FindByName<TextBox>(Window, "GenerateConfirm")!.Text = Passphrase;
            Click(FindByContent<Button>("Generate key"));
            Pump(() => FindByName<Control>(Window, "UnlockedBox")?.IsVisible == true, "generating a key never reached Unlocked");
        }

        public void Click(Button button)
        {
            button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
        }

        public T FindByContent<T>(string content) where T : Control =>
            Window.GetVisualDescendants().OfType<T>().FirstOrDefault(c => (c as ContentControl)?.Content as string == content)
                ?? throw new Xunit.Sdk.XunitException($"no {typeof(T).Name} with Content \"{content}\" found in the visual tree");

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
            Window.Close();
            AppServices.KeyStore = null;
            Directory.Delete(_tempKeyDir, recursive: true);
        }

        private static Note MakeNote(int n, string content) => new(
            Id: n.ToString("x64"),
            AuthorPubkey: n.ToString("x64"),
            CreatedAt: 1_700_000_000 - (n * 60),
            Kind: NoteKind.Text,
            Content: content,
            Tags: []);
    }

    private static T? FindByName<T>(Visual root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().FirstOrDefault(c => c.Name == name);
}
