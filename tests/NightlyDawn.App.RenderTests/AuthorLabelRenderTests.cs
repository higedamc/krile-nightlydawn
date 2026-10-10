using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NightlyDawn.App.RenderTests.Fakes;
using NightlyDawn.App.Timelines;
using NightlyDawn.Core;
using NightlyDawn.Keys;
using Xunit;

namespace NightlyDawn.App.RenderTests;

/// <summary>
/// Plan §8.3: a leaf-specific render frame (not appended to <see cref="MainWindowRenderTests"/>, which stays
/// untouched) with three rows -- a resolved display name, a hex fallback (no kind:0), and an adversarial
/// bidi/zero-width name -- proving the third renders flat rather than reordered or hidden.
/// </summary>
public sealed class AuthorLabelRenderTests
{
    public static readonly string OutputDirectory = Path.Combine(AppContext.BaseDirectory, "RenderFrames");

    private static readonly TimeSpan PumpTimeout = TimeSpan.FromSeconds(5);

    private const string ResolvedPubkey = "1111111111111111111111111111111111111111111111111111111111111111";
    private const string FallbackPubkey = "2222222222222222222222222222222222222222222222222222222222222222";
    private const string AdversarialPubkey = "3333333333333333333333333333333333333333333333333333333333333333";

    // Several bidi overrides and zero-width characters packed around plain text -- the same shape of string
    // AuthorLabelTests.AdversarialBidiAndPaddingMix_RendersFlat exercises as a string; here it goes through
    // the real NoteRowView/TextBlock rendering path.
    private const string AdversarialDisplayName = "‮evil‬​mask﻿ed‏name";

    [AvaloniaFact]
    public void ResolvedFallbackAndAdversarialNames_AllRenderFlat()
    {
        using var session = new RenderSession();
        var window = session.Window;

        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        SaveFrame(frame!);

        AssertFrameIsNotUniform(frame!);
        AssertLabelIsNotClipped(window, $"Alice ({ResolvedPubkey[..8]}…)");
        AssertLabelIsNotClipped(window, $"{FallbackPubkey[..8]}…");

        // The adversarial name sanitizes down to this exact string (AuthorLabelTests pins the sanitizer
        // itself); finding it laid out flat here proves the picture matches the string, not just that some
        // TextBlock exists.
        AssertLabelIsNotClipped(window, $"evilmaskedname ({AdversarialPubkey[..8]}…)");
    }

    private sealed class RenderSession : IDisposable
    {
        private readonly string _tempKeyDir;

        public MainWindow Window { get; }

        public RenderSession()
        {
            _tempKeyDir = Directory.CreateTempSubdirectory("nightlydawn-authorlabel-render-tests").FullName;

            var profileStore = new StaticProfileStore();
            profileStore.Seed(ResolvedPubkey, new Profile(ResolvedPubkey, DisplayName: "Alice"));
            // FallbackPubkey is left unseeded: no kind:0, same as a pubkey that was asked for and found nothing.
            profileStore.Seed(AdversarialPubkey, new Profile(AdversarialPubkey, DisplayName: AdversarialDisplayName));

            AppServices.KeyStore = new LocalKeyStore(new FileKeyFileStore(Path.Combine(_tempKeyDir, "key.ncryptsec")));
            AppServices.TimelineSourceFactory = new StaticTimelineSourceFactory(SampleNotes());
            AppServices.ProfileStore = profileStore;
            AppServices.ScreenshotPath = null;

            Window = new MainWindow { Width = 900, Height = 700 };
            Window.Show();
            Dispatcher.UIThread.RunJobs();

            var timeline = (TimelineColumnViewModel)Window.DataContext!;
            timeline.Query = "from home";
            timeline.Subscribe();
            Pump(() => !timeline.IsLoading, "the timeline stream never finished");
            Dispatcher.UIThread.RunJobs();
        }

        public void Dispose()
        {
            Window.Close();
            AppServices.KeyStore = null;
            AppServices.ProfileStore = null;
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

        private static IReadOnlyList<Note> SampleNotes() =>
        [
            MakeNote(1, ResolvedPubkey, "Resolved display name."),
            MakeNote(2, FallbackPubkey, "No kind:0 for this author -- hex fallback."),
            MakeNote(3, AdversarialPubkey, AdversarialDisplayName),
        ];

        private static Note MakeNote(int n, string authorPubkey, string content) => new(
            Id: n.ToString("x64"),
            AuthorPubkey: authorPubkey,
            CreatedAt: 1_700_000_000 - n * 60, // Fixed, unlike MainWindowRenderTests' SampleNotes: a deterministic frame is not this leaf's concern to fix, but there is no reason to add more nondeterminism here.
            Kind: NoteKind.Text,
            Content: content,
            Tags: []);
    }

    /// <summary>Resolves synchronously from a seeded dictionary; no network, no delay -- this is a render
    /// test, not a <c>ProfileStore</c> behavior test (that is <c>ProfileStoreTests</c> in NightlyDawn.Nostr.Tests).</summary>
    private sealed class StaticProfileStore : IProfileStore
    {
        private readonly Dictionary<string, Profile?> _profiles = new(StringComparer.Ordinal);

        public void Seed(string pubkey, Profile profile) => _profiles[pubkey] = profile;

        public Profile? TryGet(string pubkey) => _profiles.TryGetValue(pubkey, out var profile) ? profile : null;

        public Task PrefetchAsync(IReadOnlyCollection<string> pubkeys, CancellationToken cancellationToken = default)
        {
            foreach (var pubkey in pubkeys)
            {
                _profiles.TryAdd(pubkey, null); // Negative-cache anything not seeded, same contract as the real store.
            }

            return Task.CompletedTask;
        }
    }

    private static void SaveFrame(WriteableBitmap frame)
    {
        Directory.CreateDirectory(OutputDirectory);
        frame.Save(Path.Combine(OutputDirectory, "authorlabel_900x700.png"), new PngBitmapEncoderOptions());
    }

    /// <summary>Same pixel-variety check as <c>MainWindowRenderTests.AssertFrameIsNotUniform</c>: proof Skia
    /// actually drew something, not just that a correctly-sized bitmap exists (2026-10-10's all-black-PNG lesson).</summary>
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

    /// <summary>Same unattached-clone measuring technique as <c>MainWindowRenderTests.AssertTextIsNotClipped</c>.</summary>
    private static void AssertLabelIsNotClipped(MainWindow window, string expectedLabel)
    {
        var textBlock = window.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(tb => tb.Text == expectedLabel)
            ?? throw new Xunit.Sdk.XunitException($"No TextBlock with text \"{expectedLabel}\" in the visual tree.");
        var topLeft = textBlock.TranslatePoint(new Point(0, 0), window)
            ?? throw new Xunit.Sdk.XunitException("Label is not attached to the window's visual tree.");

        var probe = new TextBlock
        {
            Text = textBlock.Text,
            FontFamily = textBlock.FontFamily,
            FontSize = textBlock.FontSize,
            FontWeight = textBlock.FontWeight,
        };
        probe.Measure(Size.Infinity);
        var naturalWidth = probe.DesiredSize.Width;
        var availableWidth = window.Bounds.Width - topLeft.X;

        Assert.True(naturalWidth <= availableWidth + 0.5,
            $"label needs {naturalWidth:0.#} but only {availableWidth:0.#} is available (\"{expectedLabel}\").");
    }
}
