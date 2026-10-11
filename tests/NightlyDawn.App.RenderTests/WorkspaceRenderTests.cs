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
/// confined to the <c>Workspace</c> element's own box, never leaking into the header/compose/keys/footer chrome
/// around it, and (b) a multi-tab/multi-column layout renders a non-blank frame.
/// </summary>
public sealed class WorkspaceRenderTests
{
    private static readonly TimeSpan PumpTimeout = TimeSpan.FromSeconds(5);

    public static readonly string OutputDirectory = Path.Combine(AppContext.BaseDirectory, "RenderFrames");

    /// <summary>
    /// Plan §11.4's required containment check. <c>MainWindow.axaml</c>'s diff for this whole leaf is a single
    /// element swap at one DockPanel slot -- <c>TimelineColumnView x:Name="Column"</c> became
    /// <c>WorkspaceView x:Name="Workspace"</c>, same margin, same position among undocked/docked siblings
    /// (verified by reading the diff, not assumed). Nothing about the header text, <c>ComposeBox</c>,
    /// <c>KeysExpander</c>, or the footer text's own docking changed, so if their boxes stay disjoint from
    /// <c>Workspace</c>'s box, nothing this leaf added (the tab strip, the extra columns) could have reached
    /// them -- the only control actually new to the tree, <c>TabStrip</c>, is asserted to sit inside
    /// <c>Workspace</c>'s own box, at its top.
    /// </summary>
    [AvaloniaFact]
    public void DefaultLayout_NewChrome_IsContainedInsideTheWorkspaceRegion()
    {
        using var session = new RenderSession(900, 600, 1.0);
        var window = session.Window;

        var workspace = FindByName<Control>(window, "Workspace")!;
        var workspaceBounds = BoundsInWindow(workspace, window);

        var tabStrip = FindByName<Control>(window, "TabStrip")!;
        var tabStripBounds = BoundsInWindow(tabStrip, window);

        AssertContains(workspaceBounds, tabStripBounds, "TabStrip");
        Assert.True(Math.Abs(tabStripBounds.Y - workspaceBounds.Y) < 1.0,
            $"TabStrip should sit at the top of Workspace's box; TabStrip.Y={tabStripBounds.Y:0.#}, Workspace.Y={workspaceBounds.Y:0.#}");

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

    /// <summary>Visual record only (plan §11.4: "2 タブ・3 列のフレームを 1 枚焼く") -- not asserted for pixel
    /// equality against anything, just that Skia actually drew a non-blank frame with this many tabs/columns
    /// open at once. Saved next to <see cref="MainWindowRenderTests"/>'s frames for a human to look at.</summary>
    [AvaloniaFact]
    public void MultiTabMultiColumn_RendersANonBlankFrame()
    {
        using var session = new RenderSession(1280, 800, 1.0);
        var window = session.Window;
        var workspace = (WorkspaceViewModel)window.DataContext!;

        // Home tab: the default column plus one more (2 columns). A second tab with its own column (1 more).
        // 2 tabs, 3 columns total, matching the plan's "2タブ・3列" ask.
        workspace.Tabs[0].AddColumn();
        var secondTab = workspace.AddTab();
        secondTab.SelectedColumn ??= secondTab.Columns.FirstOrDefault();
        workspace.SelectedTab = workspace.Tabs[0];
        Dispatcher.UIThread.RunJobs();

        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        Directory.CreateDirectory(OutputDirectory);
        frame!.Save(Path.Combine(OutputDirectory, "workspace_2tabs_3columns.png"), new PngBitmapEncoderOptions());

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

    private static void AssertContains(Rect outer, Rect inner, string innerName) =>
        Assert.True(
            inner.X >= outer.X - 0.5 && inner.Y >= outer.Y - 0.5 &&
            inner.Right <= outer.Right + 0.5 && inner.Bottom <= outer.Bottom + 0.5,
            $"{innerName} {inner} is not contained inside Workspace {outer}");

    private static void AssertDisjoint(Rect a, Rect b, string bName) =>
        Assert.True(!a.Intersects(b), $"Workspace {a} overlaps {bName} {b}, expected disjoint boxes");
}
