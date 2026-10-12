using System.Runtime.InteropServices;
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
/// New render-test class for L4 (plan §11.4: kept separate from <see cref="MainWindowRenderTests"/>, which
/// only ever exercises the default one-tab/one-column layout). Frame byte/pixel equality against the pre-L4
/// layout is explicitly not the bar here (plan §11.4: the tab strip is new chrome, so the picture is supposed
/// to change) -- these tests show instead that (a) whatever this leaf could have broken is structurally
/// confined to known boxes, never leaking into the header/compose/keys/footer chrome around it, and (b) a
/// multi-tab/multi-column layout renders a non-blank frame.
/// </summary>
public sealed class WorkspaceRenderTests
{
    private static readonly TimeSpan PumpTimeout = TimeSpan.FromSeconds(5);

    public static readonly string OutputDirectory = Path.Combine(AppContext.BaseDirectory, "RenderFrames");

    /// <summary>
    /// Plan §21.2 (L4b) replaces the PR #30 placement this test used to assert: <c>TabStrip</c> moved back
    /// out of <c>MainWindow</c>'s header row and into each column, inside <c>WorkspaceView</c> -- the fork's
    /// structure (StarryEyes <c>MainArea.xaml</c>) nests tabs inside columns, so the strip that switches
    /// between a column's tabs belongs with that column, not with the window title. This asserts the
    /// opposite of the pre-L4b version on purpose: <c>TabStrip</c> must now be <em>inside</em>
    /// <c>Workspace</c>'s box, not disjoint from it and sitting beside the header. <c>Workspace</c> itself
    /// must still stay disjoint from the compose/keys/footer/header chrome around it, same as before.
    /// </summary>
    [AvaloniaFact]
    public void DefaultLayout_PerColumnTabStrip_LivesInsideWorkspace_NotTheHeader()
    {
        using var session = new RenderSession(900, 600, 1.0);
        var window = session.Window;

        var workspace = FindByName<Control>(window, "Workspace")!;
        var workspaceBounds = BoundsInWindow(workspace, window);

        var tabStrip = FindByName<Control>(window, "TabStrip")!;
        var tabStripBounds = BoundsInWindow(tabStrip, window);

        Assert.True(workspaceBounds.Intersects(tabStripBounds),
            $"TabStrip {tabStripBounds} should now live inside Workspace {workspaceBounds} (plan §21.2's inversion moved it out of the header)");

        // "PlatformInfo" is deliberately not in this list: it lives inside the collapsed-by-default "Platform
        // check" Expander, which does not realize its content into the visual tree while collapsed -- neither
        // MainWindowRenderTests asserts on it.
        foreach (var name in new[] { "ComposeBox", "KeysExpander" })
        {
            var sibling = FindByName<Control>(window, name) ?? throw new Xunit.Sdk.XunitException($"{name}: not found in the visual tree");
            AssertDisjoint(workspaceBounds, BoundsInWindow(sibling, window), name);
        }

        var header = window.GetVisualDescendants().OfType<TextBlock>()
            .FirstOrDefault(tb => tb.Text == "Krile NightlyDawn")
            ?? throw new Xunit.Sdk.XunitException("header title text block not found");
        AssertDisjoint(workspaceBounds, BoundsInWindow(header, window), "header title");

        var footer = window.GetVisualDescendants().OfType<TextBlock>()
            .FirstOrDefault(tb => tb.Text?.Contains("Krile NightlyDawn contributors", StringComparison.Ordinal) == true)
            ?? throw new Xunit.Sdk.XunitException("footer text block not found");
        AssertDisjoint(workspaceBounds, BoundsInWindow(footer, window), "footer");
    }

    /// <summary>Visual record only (plan §11.4's original "2 タブ・3 列のフレームを 1 枚焼く", reshaped for
    /// §21.2's inversion since which axis is "tabs" and which is "columns" has swapped) -- not asserted for
    /// pixel equality against anything, just that Skia actually drew a non-blank frame with several
    /// tabs/columns open at once. Saved next to <see cref="MainWindowRenderTests"/>'s frames for a human to
    /// look at.</summary>
    [AvaloniaFact]
    public void MultiTabMultiColumn_RendersANonBlankFrame()
    {
        using var session = new RenderSession(1280, 800, 1.0);
        var window = session.Window;
        var workspace = (WorkspaceViewModel)window.DataContext!;

        // Column 1: the default tab plus one more (2 tabs). A second, independent column with its own tab
        // (1 more). 2 columns, 3 tabs total -- the inverted-axis equivalent of the plan's "2タブ・3列" ask.
        workspace.Columns[0].AddTab();
        workspace.AddColumn();
        workspace.SelectedColumn = workspace.Columns[0];
        Dispatcher.UIThread.RunJobs();

        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        Directory.CreateDirectory(OutputDirectory);
        frame!.Save(Path.Combine(OutputDirectory, "workspace_2columns_3tabs.png"), new PngBitmapEncoderOptions());

        AssertFrameIsNotUniform(frame);
    }

    /// <summary>Owns one headless window plus the fakes it was built with, same composition as
    /// <see cref="MainWindowRenderTests.RenderSession"/>.</summary>
    private sealed class RenderSession : IDisposable
    {
        private readonly string _tempKeyDir;

        public MainWindow Window { get; }

        public RenderSession(double width, double height, double scale)
        {
            _tempKeyDir = Directory.CreateTempSubdirectory("nightlydawn-workspace-render-tests").FullName;

            AppServices.KeyStore = new LocalKeyStore(new FileKeyFileStore(Path.Combine(_tempKeyDir, "key.ncryptsec")));
            AppServices.TimelineSourceFactory = new StaticTimelineSourceFactory(SampleNotes());
            AppServices.ScreenshotPath = null;

            Window = new MainWindow { Width = width, Height = height };
            Window.Show();
            HeadlessWindowExtensions.SetRenderScaling(Window, scale);
            Dispatcher.UIThread.RunJobs();
        }

        public void Dispose()
        {
            Window.Close();
            AppServices.KeyStore = null;
            Directory.Delete(_tempKeyDir, recursive: true);
        }

        private static IReadOnlyList<Note> SampleNotes() =>
        [
            MakeNote(1, "Landed in Omarchy; Hyprland + Wayland feels snappy so far."),
            MakeNote(2, "[render-test] a second note so the list has some scroll height."),
        ];

        private static Note MakeNote(int n, string content) => new(
            Id: n.ToString("x64"),
            AuthorPubkey: n.ToString("x64"),
            CreatedAt: 1_700_000_000 - n * 60,
            Kind: NoteKind.Text,
            Content: content,
            Tags: []);
    }

    private static void AssertFrameIsNotUniform(WriteableBitmap frame)
    {
        using var buffer = frame.Lock();
        var byteCount = buffer.RowBytes * buffer.Size.Height;
        var bytes = new byte[byteCount];
        Marshal.Copy(buffer.Address, bytes, 0, byteCount);

        var distinctPixels = new HashSet<int>();
        for (var i = 0; i + 4 <= bytes.Length; i += 4 * 7)
        {
            distinctPixels.Add(BitConverter.ToInt32(bytes, i));
        }

        Assert.True(distinctPixels.Count >= 16,
            $"frame has only {distinctPixels.Count} distinct sampled pixel value(s) ({buffer.Size.Width}x{buffer.Size.Height}); it looks blank rather than rendered.");
    }

    /// <summary>Finds a control by <c>Name</c> anywhere in <paramref name="root"/>'s visual tree, crossing
    /// NameScope boundaries (same reasoning/negative control as <see cref="MainWindowRenderTests"/>'s copy:
    /// <c>TabStrip</c> and <c>Workspace</c> each own a NameScope <c>Window.FindControl</c> cannot cross).</summary>
    private static T? FindByName<T>(Visual root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().FirstOrDefault(c => c.Name == name);

    private static Rect BoundsInWindow(Visual control, Visual window)
    {
        var topLeft = control.TranslatePoint(new Point(0, 0), window)
            ?? throw new Xunit.Sdk.XunitException($"{control}: not attached to the window's visual tree");
        return new Rect(topLeft, control.Bounds.Size);
    }

    private static void AssertDisjoint(Rect a, Rect b, string bName) =>
        Assert.True(!a.Intersects(b), $"Workspace {a} overlaps {bName} {b}, expected disjoint boxes");
}
