using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NightlyDawn.App.Composing;
using NightlyDawn.App.RenderTests.Fakes;
using NightlyDawn.App.Timelines;
using NightlyDawn.App.Views;
using NightlyDawn.Core;
using NightlyDawn.Keys;
using Xunit;

namespace NightlyDawn.App.RenderTests;

/// <summary>
/// Plan §10.3: a leaf-specific render frame (not appended to <see cref="MainWindowRenderTests"/>) with three
/// things in it -- a compose box holding typed text, a kind:1 row with every publish button enabled, and a
/// kind:6 (repost) row with Reply/Repost disabled -- proving §10.1-6's UI-side guard actually renders that
/// way, not just that <see cref="NoteRow.CanReplyOrRepost"/> returns the right bool in isolation.
/// </summary>
public sealed class ComposeRenderTests
{
    public static readonly string OutputDirectory = Path.Combine(AppContext.BaseDirectory, "RenderFrames");

    private static readonly TimeSpan PumpTimeout = TimeSpan.FromSeconds(5);

    private const string ComposedText = "Hello from the compose box render test.";
    private const string TextAuthor = "4444444444444444444444444444444444444444444444444444444444444444";
    private const string RepostAuthor = "5555555555555555555555555555555555555555555555555555555555555555";

    [AvaloniaFact]
    public void ComposeBoxAndRowButtons_RenderWithTheRightEnabledState()
    {
        using var session = new RenderSession();
        var window = session.Window;

        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        SaveFrame(frame!);
        AssertFrameIsNotUniform(frame!);

        // ① The compose box shows what was typed into it -- not a copy somewhere else, the actual bound TextBox.
        var composeText = window.GetVisualDescendants().OfType<TextBox>().First(tb => tb.Name == "ComposeText");
        Assert.Equal(ComposedText, composeText.Text);

        // ② A plain kind:1 row: every publish button enabled.
        AssertButton(window, "ReplyButton", session.TextNoteId, enabled: true);
        AssertButton(window, "RepostButton", session.TextNoteId, enabled: true);
        AssertButton(window, "QuoteButton", session.TextNoteId, enabled: true);
        AssertButton(window, "ReactButton", session.TextNoteId, enabled: true);

        // ③ A kind:6 (repost) row: Reply/Repost disabled (plan §10.1-6); Quote/React stay enabled -- neither
        // NoteEventBuilder.Quote nor .Reaction restricts the target's kind.
        AssertButton(window, "ReplyButton", session.RepostNoteId, enabled: false);
        AssertButton(window, "RepostButton", session.RepostNoteId, enabled: false);
        AssertButton(window, "QuoteButton", session.RepostNoteId, enabled: true);
        AssertButton(window, "ReactButton", session.RepostNoteId, enabled: true);
    }

    private static void AssertButton(MainWindow window, string buttonName, string noteId, bool enabled)
    {
        var button = window.GetVisualDescendants().OfType<Button>()
            .FirstOrDefault(b => b.Name == buttonName && (b.DataContext as NoteRow)?.Id == noteId);
        Assert.NotNull(button);
        Assert.Equal(enabled, button!.IsEnabled);
    }

    private sealed class RenderSession : IDisposable
    {
        private readonly string _tempKeyDir;

        public MainWindow Window { get; }

        public string TextNoteId { get; }

        public string RepostNoteId { get; }

        public RenderSession()
        {
            _tempKeyDir = Directory.CreateTempSubdirectory("nightlydawn-compose-render-tests").FullName;

            var textNote = MakeNote(1, TextAuthor, "A plain note anyone can reply to, repost, quote, or react to.", NoteKind.Text);
            var repostNote = MakeNote(2, RepostAuthor, string.Empty, NoteKind.Repost);
            TextNoteId = textNote.Id;
            RepostNoteId = repostNote.Id;

            AppServices.KeyStore = new LocalKeyStore(new FileKeyFileStore(Path.Combine(_tempKeyDir, "key.ncryptsec")));
            AppServices.TimelineSourceFactory = new StaticTimelineSourceFactory([textNote, repostNote]);
            AppServices.ProfileStore = null;
            AppServices.NotePublisher = null; // Null-tolerant (plan §10): this frame never clicks a button, only checks bound state.
            AppServices.ScreenshotPath = null;

            Window = new MainWindow { Width = 900, Height = 700 };
            Window.Show();
            Dispatcher.UIThread.RunJobs();

            var timeline = (TimelineColumnViewModel)Window.DataContext!;
            timeline.Query = "from home";
            timeline.Subscribe();
            Pump(() => !timeline.IsLoading, "the timeline stream never finished");
            Dispatcher.UIThread.RunJobs();

            var compose = (ComposeViewModel)Window.FindControl<ComposeBoxView>("ComposeBox")!.DataContext!;
            compose.Content = ComposedText;
            Dispatcher.UIThread.RunJobs();
        }

        public void Dispose()
        {
            Window.Close();
            AppServices.KeyStore = null;
            AppServices.ProfileStore = null;
            AppServices.NotePublisher = null;
            Directory.Delete(_tempKeyDir, recursive: true);
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

        private static Note MakeNote(int n, string authorPubkey, string content, NoteKind kind) => new(
            Id: n.ToString("x64"),
            AuthorPubkey: authorPubkey,
            CreatedAt: 1_700_000_000 - n * 60,
            Kind: kind,
            Content: content,
            Tags: []);
    }

    private static void SaveFrame(WriteableBitmap frame)
    {
        Directory.CreateDirectory(OutputDirectory);
        frame.Save(Path.Combine(OutputDirectory, "compose_900x700.png"), new PngBitmapEncoderOptions());
    }

    /// <summary>Same pixel-variety check as <c>MainWindowRenderTests.AssertFrameIsNotUniform</c>: proof Skia
    /// actually drew something, not just that a correctly-sized bitmap exists.</summary>
    private static void AssertFrameIsNotUniform(WriteableBitmap frame)
    {
        using var buffer = frame.Lock();
        var byteCount = buffer.RowBytes * buffer.Size.Height;
        var bytes = new byte[byteCount];
        System.Runtime.InteropServices.Marshal.Copy(buffer.Address, bytes, 0, byteCount);

        var distinctPixels = new HashSet<int>();
        for (var i = 0; i + 4 <= bytes.Length; i += 4 * 7)
        {
            distinctPixels.Add(BitConverter.ToInt32(bytes, i));
        }

        Assert.True(distinctPixels.Count >= 16,
            $"frame has only {distinctPixels.Count} distinct sampled pixel value(s) ({buffer.Size.Width}x{buffer.Size.Height}); it looks blank rather than rendered.");
    }
}
