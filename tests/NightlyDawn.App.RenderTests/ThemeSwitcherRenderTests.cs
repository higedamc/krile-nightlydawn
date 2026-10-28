using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NightlyDawn.App.RenderTests.Fakes;
using NightlyDawn.App.Theming;
using NightlyDawn.App.Timelines;
using NightlyDawn.Core;
using Xunit;

namespace NightlyDawn.App.RenderTests;

/// <summary>
/// The in-app theme switch (L8), driven through the real <see cref="ComboBox"/> in the real
/// <see cref="MainWindow"/> header. <see cref="ThemeRenderTests"/> already proves the palette reaches the pixels
/// when something applies a variant; what is unproven until here is that <i>the control the user touches</i> is
/// wired to that path at all, and that putting it in the header did not cost the timeline any height.
///
/// <para>Separate class per leaf (plan §3), and the frames land in the same <c>RenderFrames</c> artifact
/// directory, so a reviewer gets a light and a dark screenshot that were produced by using the drop-down rather
/// than by the harness setting the variant itself.</para>
/// </summary>
public sealed class ThemeSwitcherRenderTests
{
    private const double Width = 900;
    private const double Height = 600;
    private const double Epsilon = 0.5;
    private static readonly TimeSpan PumpTimeout = TimeSpan.FromSeconds(5);

    /// <summary>The floor <see cref="MainWindowRenderTests"/> pins for the timeline, repeated here on purpose:
    /// this leaf adds chrome to the header row, and the failure mode it has to rule out is exactly the one PR #30
    /// shipped and had sent back -- new chrome eating the timeline's vertical budget (measured then: 95 -> 19 DIP
    /// on macOS, 0 on Linux). 60 rather than 90 because Linux's default font metrics render the same tree 22 DIP
    /// shorter than macOS does (plan §11.7).</summary>
    private const double TimelineFloorWidth = 600;
    private const double TimelineFloorHeight = 60;

    /// <summary>
    /// The switch is in the compose row beside the compose box (not above the timeline), does not overlap the
    /// workspace, and the timeline still clears its floor. A control docked into a new row would pass the first
    /// two assertions and fail the last.
    /// <para>Rewritten for plan §26.5 (L4c ②): the header title row this used to check beside is gone -- the
    /// compose box is the window's top row now, and the switch still shares that row with it (plan §26.5 /
    /// L4c's MainWindow.axaml keeps ThemeSwitcher and ComposeBox in the same DockPanel).</para>
    /// </summary>
    [AvaloniaFact]
    public void TheSwitchSitsInTheComposeRowAndCostsTheTimelineNoHeight()
    {
        using var session = new SwitcherSession(AppTheme.Light);
        var window = session.Window;

        var selector = session.Selector;
        var selectorBounds = session.BoundsInWindow(selector);

        var composeBox = session.Find<Control>("ComposeBox") ?? throw new Xunit.Sdk.XunitException("ComposeBox not found");
        var composeBoxBounds = session.BoundsInWindow(composeBox);

        Assert.True(Math.Abs(selectorBounds.Y - composeBoxBounds.Y) < 20.0,
            $"the theme switch should sit beside the compose box, not in a row of its own; ThemeSelector.Y={selectorBounds.Y:0.#}, ComposeBox.Y={composeBoxBounds.Y:0.#}");

        var tabStrip = session.Find<Control>("TabStrip") ?? throw new Xunit.Sdk.XunitException("TabStrip not found");
        var tabStripBounds = session.BoundsInWindow(tabStrip);
        Assert.True(selectorBounds.X >= tabStripBounds.Right - Epsilon,
            $"the theme switch should be the rightmost item in the header row; ThemeSelector.X={selectorBounds.X:0.#}, TabStrip.Right={tabStripBounds.Right:0.#}");

        var workspace = session.Find<Control>("Workspace") ?? throw new Xunit.Sdk.XunitException("Workspace not found");
        var workspaceBounds = session.BoundsInWindow(workspace);
        Assert.False(workspaceBounds.Intersects(selectorBounds),
            $"the theme switch {selectorBounds} overlaps the workspace {workspaceBounds}, expected disjoint boxes");

        Assert.True(selectorBounds.Right <= window.Bounds.Width + Epsilon,
            $"the theme switch overflows the window: right edge {selectorBounds.Right:0.#} vs window width {window.Bounds.Width:0.#}");

        var timeline = session.Find<Control>("TimelineList") ?? throw new Xunit.Sdk.XunitException("TimelineList not found");
        var timelineBounds = session.BoundsInWindow(timeline);
        Assert.True(timelineBounds.Width >= TimelineFloorWidth && timelineBounds.Height >= TimelineFloorHeight,
            $"TimelineList: {timelineBounds.Width:0.#}x{timelineBounds.Height:0.#} is below the floor ({TimelineFloorWidth:0.#}x{TimelineFloorHeight:0.#}) with the theme switch added");
    }

    /// <summary>
    /// The feature itself, end to end: selecting Dark in the drop-down has to change what Skia draws. This is the
    /// assertion the required negative control targets -- deleting the one <c>_apply(...)</c> call in
    /// <see cref="ThemeSwitcherViewModel.Selected"/>'s setter leaves every other test in this class green and
    /// fails only this one, because nothing else in the app depends on that line.
    ///
    /// <para>Mean luminance rather than a byte comparison: the frames are not deterministic (plan §6.3), and the
    /// claim is "the picture got darker", not "these exact bytes".</para>
    /// </summary>
    [AvaloniaFact]
    public void ChoosingDarkInTheDropDownDarkensTheRenderedWindow()
    {
        using var session = new SwitcherSession(AppTheme.Light);

        session.Choose(AppTheme.Light);
        var light = session.Capture();
        SaveFrame(light, "light");

        session.Choose(AppTheme.Dark);
        var dark = session.Capture();
        SaveFrame(dark, "dark");

        var lightLuminance = MeanLuminance(light);
        var darkLuminance = MeanLuminance(dark);

        Assert.True(lightLuminance - darkLuminance > 40,
            $"light mean luminance {lightLuminance:0.#} vs dark {darkLuminance:0.#} after using the drop-down: the control is not reaching the running Application, so the switch does nothing on screen.");
    }

    /// <summary>
    /// The drop-down opens on the variant the app is actually in. A control that always started at the first
    /// option would show "System" to someone who launched with <c>NIGHTLYDAWN_THEME=dark</c> -- the UI would be
    /// lying about the current state, and their first click would be a no-op.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(AppTheme.System)]
    [InlineData(AppTheme.Light)]
    [InlineData(AppTheme.Dark)]
    public void TheSwitchOpensOnTheVariantTheHostChose(AppTheme hostChoice)
    {
        using var session = new SwitcherSession(hostChoice);

        var selected = Assert.IsType<ThemeOption>(session.Selector.SelectedItem);
        Assert.Equal(hostChoice, selected.Value);
    }

    /// <summary>
    /// "Follow the OS" has to survive being an option in a drop-down: picking it must leave the Application at
    /// <see cref="ThemeVariant.Default"/>, not resolve to whichever variant the OS happens to be in. If this
    /// collapsed into Light, a later leaf persisting the choice (L9) could never store "never chosen"
    /// (<c>Core/AppStateContract.cs</c>).
    /// </summary>
    [AvaloniaFact]
    public void PickingSystemLeavesTheVariantToTheOperatingSystem()
    {
        using var session = new SwitcherSession(AppTheme.Light);

        session.Choose(AppTheme.Dark);
        Assert.Equal(ThemeVariant.Dark, Application.Current!.RequestedThemeVariant);

        session.Choose(AppTheme.System);
        Assert.Equal(ThemeVariant.Default, Application.Current!.RequestedThemeVariant);
    }

    /// <summary>
    /// Negative control for <see cref="SwitcherSession.Choose"/>: an option this control does not offer must not
    /// be selectable through it. Without this, a <c>Choose</c> that silently did nothing (a mis-typed name, an
    /// option list rebuilt into fresh instances the binding no longer matches) would make the tests above look
    /// green while driving nothing at all.
    /// </summary>
    [AvaloniaFact]
    public void ChoosingAnOptionTheControlDoesNotOfferIsRejected()
    {
        using var session = new SwitcherSession(AppTheme.Light);

        var foreign = new ThemeOption(AppTheme.Dark, "Dark");
        Assert.Throws<Xunit.Sdk.XunitException>(() => session.Select(foreign));
    }

    /// <summary>One window with the host's theme applied, the timeline streamed in, and the header's theme
    /// drop-down located. Mirrors <c>ThemeRenderTests.ThemeSession</c>; kept separate so this leaf does not edit
    /// another leaf's harness.</summary>
    private sealed class SwitcherSession : IDisposable
    {
        private readonly AppTheme _previousTheme;
        private readonly ThemeVariant? _previousVariant;

        public MainWindow Window { get; }

        public ComboBox Selector { get; }

        public SwitcherSession(AppTheme hostChoice)
        {
            _previousTheme = AppServices.Theme;
            _previousVariant = Application.Current!.RequestedThemeVariant;

            // Exactly what the host does before the window exists: put the choice in AppServices and apply it.
            // The switcher must read the same value, which is why it is set before construction, not after.
            AppServices.Theme = hostChoice;
            ThemeController.Apply(Application.Current!, hostChoice);

            AppServices.TimelineSourceFactory = new StaticTimelineSourceFactory(SampleNotes());
            AppServices.KeyStore = null;
            AppServices.ScreenshotPath = null;

            Window = new MainWindow { Width = Width, Height = Height };
            Window.Show();
            HeadlessWindowExtensions.SetRenderScaling(Window, 1.0);
            Dispatcher.UIThread.RunJobs();

            var timeline = ((WorkspaceViewModel)Window.DataContext!).Columns[0].Tabs[0];
            timeline.Query = "kind:1";
            timeline.Subscribe();
            // `!X.IsLoading` alone cannot tell "never subscribed" from "finished" (plan §46/§50;
            // WaitSoundnessTests demonstrates it) -- ANDing with `Status != IdleStatus` closes the
            // gap, since IdleStatus is the one value Subscribe() never leaves the timeline in.
            Pump(() => !timeline.IsLoading && timeline.Status != TimelineViewModel.IdleStatus, "the timeline stream never finished");
            Dispatcher.UIThread.RunJobs();

            Selector = Find<ComboBox>("ThemeSelector")
                ?? throw new Xunit.Sdk.XunitException("ThemeSelector not found in the visual tree");
        }

        public T? Find<T>(string name) where T : Control =>
            Window.GetVisualDescendants().OfType<T>().FirstOrDefault(c => c.Name == name);

        public Rect BoundsInWindow(Visual control)
        {
            var topLeft = control.TranslatePoint(new Point(0, 0), Window)
                ?? throw new Xunit.Sdk.XunitException("control is not attached to the window");
            return new Rect(topLeft, control.Bounds.Size);
        }

        /// <summary>Picks the option for <paramref name="theme"/> the way the user does: through the control's
        /// own selection, which the XAML two-way binding carries into the view model.</summary>
        public void Choose(AppTheme theme)
        {
            var option = Selector.Items.OfType<ThemeOption>().FirstOrDefault(o => o.Value == theme)
                ?? throw new Xunit.Sdk.XunitException($"the drop-down offers no option for {theme}");
            Select(option);
        }

        /// <summary>Selects <paramref name="option"/> and asserts the control took it, so a selection the
        /// control rejects cannot pass for a successful one.</summary>
        public void Select(ThemeOption option)
        {
            Selector.SelectedItem = option;
            Dispatcher.UIThread.RunJobs();

            if (!ReferenceEquals(Selector.SelectedItem, option))
            {
                throw new Xunit.Sdk.XunitException(
                    $"the drop-down did not take the selection: SelectedItem is {Selector.SelectedItem ?? "(null)"} after selecting {option.Label}");
            }
        }

        public WriteableBitmap Capture() =>
            Window.CaptureRenderedFrame() ?? throw new Xunit.Sdk.XunitException("CaptureRenderedFrame returned null");

        public void Dispose()
        {
            Window.Close();
            AppServices.Theme = _previousTheme;
            Application.Current!.RequestedThemeVariant = _previousVariant;
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
            MakeNote(1, "Theme switch check: the drop-down in the header changes this window's variant."),
            MakeNote(2, "Second note so the list has rows to colour."),
        ];

        private static Note MakeNote(int n, string content) => new(
            Id: n.ToString("x64"),
            AuthorPubkey: n.ToString("x64"),
            CreatedAt: 1_700_000_000 - n * 60, // Fixed epoch (plan §6.2): no wall clock in the rendered labels.
            Kind: NoteKind.Text,
            Content: content,
            Tags: []);
    }

    private static void SaveFrame(WriteableBitmap frame, string variant)
    {
        Directory.CreateDirectory(MainWindowRenderTests.OutputDirectory);
        frame.Save(
            Path.Combine(MainWindowRenderTests.OutputDirectory, $"themeswitch_{variant}_{Width:0}x{Height:0}.png"),
            new PngBitmapEncoderOptions());
    }

    /// <summary>Stride-sampled brightness average. Channel order is read from the framebuffer's own format rather
    /// than assumed, for the reason <see cref="ThemeRenderTests"/> records: this headless Skia platform hands back
    /// Rgba8888, and a hand-rolled BGRA reader reports a wrong colour that looks like a wrong theme.</summary>
    private static double MeanLuminance(WriteableBitmap frame)
    {
        using var buffer = frame.Lock();
        var bytes = new byte[buffer.RowBytes * buffer.Size.Height];
        Marshal.Copy(buffer.Address, bytes, 0, bytes.Length);

        double sum = 0;
        var samples = 0;
        for (var i = 0; i + 4 <= bytes.Length; i += 4 * 13)
        {
            var pixel = ReadPixel(bytes, i, buffer.Format);
            sum += (0.2126 * pixel.R) + (0.7152 * pixel.G) + (0.0722 * pixel.B);
            samples++;
        }

        return sum / samples;
    }

    private static Color ReadPixel(byte[] bytes, int offset, PixelFormat? format)
    {
        var alpha = bytes[offset + 3];

        if (format == PixelFormat.Rgba8888)
        {
            return Color.FromArgb(alpha, bytes[offset], bytes[offset + 1], bytes[offset + 2]);
        }

        if (format == PixelFormat.Bgra8888)
        {
            return Color.FromArgb(alpha, bytes[offset + 2], bytes[offset + 1], bytes[offset]);
        }

        throw new Xunit.Sdk.XunitException($"unexpected framebuffer format {format?.ToString() ?? "(null)"}; this test reads channels by hand and has no mapping for it.");
    }
}
