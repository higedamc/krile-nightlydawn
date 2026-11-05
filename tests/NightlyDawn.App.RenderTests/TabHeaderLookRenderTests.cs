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
using NightlyDawn.App.Views;
using NightlyDawn.Core;
using NightlyDawn.Keys;
using Xunit;

namespace NightlyDawn.App.RenderTests;

/// <summary>
/// L18 (plan §68): the tab headers in the fork's shape (StarryEyes <c>MainArea.xaml:23-81</c>): flat text, three colour
/// roles that are two conditions. Everything here reads what was <em>realised</em> (<c>Button.Foreground</c> on the live
/// header, a pixel of the captured frame for the background), never the XAML source text.
///
/// <para>Negative controls run while writing this (each reverted afterwards, application counts printed): dropping the
/// column's <c>IsFocused</c> input from the MultiBinding turns <see cref="TheThreeRoles_RealiseThreeDifferentForegrounds_AndTheFocusedOneIsNotTheBackground"/>
/// and <see cref="TheRolesFollowBothTheSelectedTabAndTheFocusedColumn"/> red; making the focused role the fork's literal
/// <c>#FF000000</c> turns the Dark case of the first red on its background assertion; restoring the old bordered
/// <c>Border Padding="8,4"</c> chrome turns <see cref="TheHeadersCarryNoChrome_AndTheCloseButtonStays"/> red.</para>
/// </summary>
public sealed class TabHeaderLookRenderTests
{
    public static TheoryData<AppTheme> Themes() => new() { AppTheme.Dark, AppTheme.Light };

    [AvaloniaTheory]
    [MemberData(nameof(Themes))]
    public void TheThreeRoles_RealiseThreeDifferentForegrounds_AndTheFocusedOneIsNotTheBackground(AppTheme theme)
    {
        using var s = new Session(theme);

        // column 0: A (idle) + B (selected, column NOT focused); column 1: C (selected, column focused).
        Assert.Equal(TabHeaderRole.Idle, s.Role(s.A));
        Assert.Equal(TabHeaderRole.Selected, s.Role(s.B));
        Assert.Equal(TabHeaderRole.Focused, s.Role(s.C));

        var idle = s.Foreground(s.A);
        var selected = s.Foreground(s.B);
        var focused = s.Foreground(s.C);

        Assert.NotEqual(idle, selected);
        Assert.NotEqual(selected, focused);
        Assert.NotEqual(idle, focused);

        // The measured background behind a header: a pixel of the real frame, in the 4 DIP margin above the label.
        var frame = s.Capture();
        foreach (var (name, tab, colour) in new[] { ("idle", s.A, idle), ("selected", s.B, selected), ("focused", s.C, focused) })
        {
            var background = s.BackgroundBehind(frame, tab);
            Assert.False(IsClose(colour, background),
                $"{theme}: the {name} tab's foreground {colour} is the same as the background {background} it is drawn on - the label would vanish.");

            // "Not equal" alone passes a near-invisible pairing (the fork's literal #FF000000 on this fork's dark purple
            // window is not *equal* to it, and still unreadable), so also require WCAG's 3:1 floor for UI text.
            var contrast = ContrastRatio(colour, background);
            Assert.True(contrast >= 3.0,
                $"{theme}: the {name} tab's foreground {colour} on background {background} has contrast {contrast:0.##}:1, below the 3:1 floor.");
        }
    }

    [AvaloniaFact]
    public void TheRolesFollowBothTheSelectedTabAndTheFocusedColumn()
    {
        using var s = new Session(AppTheme.Dark);

        s.Workspace.SelectedColumn = s.Col0; // focus moves: B becomes Focused, C drops to Selected
        s.Pump();
        Assert.Equal(TabHeaderRole.Idle, s.Role(s.A));
        Assert.Equal(TabHeaderRole.Focused, s.Role(s.B));
        Assert.Equal(TabHeaderRole.Selected, s.Role(s.C));

        s.Col0.SelectedTab = s.A; // selection moves inside the focused column: A Focused, B Idle
        s.Pump();
        Assert.Equal(TabHeaderRole.Focused, s.Role(s.A));
        Assert.Equal(TabHeaderRole.Idle, s.Role(s.B));
        Assert.Equal(TabHeaderRole.Selected, s.Role(s.C));
    }

    [AvaloniaFact]
    public void TheHeadersCarryNoChrome_AndTheCloseButtonStays()
    {
        using var s = new Session(AppTheme.Dark);

        foreach (var tab in new[] { s.A, s.B, s.C })
        {
            var header = s.Header(tab);
            Assert.Equal(new Thickness(0), header.BorderThickness);
            Assert.Equal(16, header.FontSize);
            Assert.True(IsTransparent(header.Background), "header background must be transparent");

            // Nothing between the label and the strip draws a frame: no border, no radius, no padding (the fork's
            // MainArea.xaml:23-66 item is Border Margin=0,4,0,4 > StackPanel > text).
            foreach (var border in header.GetVisualAncestors().TakeWhile(a => a is not ItemsControl).OfType<Border>())
            {
                Assert.Equal(new Thickness(0), border.BorderThickness);
                Assert.Equal(new CornerRadius(0), border.CornerRadius);
                Assert.Equal(new Thickness(0), border.Padding);
            }

            // The x is kept (this port has no context menu) with its chrome gone.
            var close = s.Close(tab);
            Assert.Equal("×", close.Content);
            Assert.Equal(new Thickness(0), close.BorderThickness);
            Assert.True(IsTransparent(close.Background), "close button background must be transparent");
            Assert.True(close.Bounds.Width > 0 && close.Bounds.Height > 0, "close button must still be on screen");
        }
    }

    [AvaloniaFact]
    public void TheCloseButtonStillClosesItsTab()
    {
        using var s = new Session(AppTheme.Dark);
        Assert.Contains(s.B, s.Col0.Tabs);

        s.Close(s.B).RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        s.Pump();

        Assert.DoesNotContain(s.B, s.Col0.Tabs);
    }

    private static bool IsTransparent(IBrush? brush) => brush is null || (brush is ISolidColorBrush { Color.A: 0 });

    private static double ContrastRatio(Color a, Color b)
    {
        var (hi, lo) = (RelativeLuminance(a), RelativeLuminance(b));
        if (hi < lo)
        {
            (hi, lo) = (lo, hi);
        }

        return (hi + 0.05) / (lo + 0.05);
    }

    private static double RelativeLuminance(Color c)
    {
        static double Channel(byte v)
        {
            var x = v / 255.0;
            return x <= 0.03928 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4);
        }

        return (0.2126 * Channel(c.R)) + (0.7152 * Channel(c.G)) + (0.0722 * Channel(c.B));
    }

    private static bool IsClose(Color a, Color b) =>
        Math.Abs(a.R - b.R) <= 2 && Math.Abs(a.G - b.G) <= 2 && Math.Abs(a.B - b.B) <= 2;

    private sealed class Session : IDisposable
    {
        private readonly string _tempKeyDir;
        private readonly AppTheme _previousTheme;
        private readonly ThemeVariant? _previousVariant;

        public MainWindow Window { get; }
        public WorkspaceViewModel Workspace { get; }
        public ColumnViewModel Col0 { get; }
        public TimelineViewModel A { get; }
        public TimelineViewModel B { get; }
        public TimelineViewModel C { get; }

        public Session(AppTheme theme)
        {
            _previousTheme = AppServices.Theme;
            _previousVariant = Application.Current!.RequestedThemeVariant;
            AppServices.Theme = theme;
            ThemeController.Apply(Application.Current!, theme);

            _tempKeyDir = Directory.CreateTempSubdirectory("nightlydawn-tab-look-render-tests").FullName;
            AppServices.KeyStore = new LocalKeyStore(new FileKeyFileStore(Path.Combine(_tempKeyDir, "key.ncryptsec")));
            AppServices.TimelineSourceFactory = new StaticTimelineSourceFactory([]);
            AppServices.ScreenshotPath = null;

            Window = new MainWindow { Width = 1280, Height = 800 };
            Window.Show();
            HeadlessWindowExtensions.SetRenderScaling(Window, 1.0);
            Dispatcher.UIThread.RunJobs();

            Workspace = (WorkspaceViewModel)Window.DataContext!;
            Col0 = Workspace.Columns[0];
            A = Col0.Tabs[0];
            B = Col0.AddTab();                  // selects B; A is now idle
            var col1 = Workspace.AddColumn();   // selects (focuses) column 1
            C = col1.Tabs[0];
            Pump();
        }

        public void Pump() => Dispatcher.UIThread.RunJobs();

        public Button Header(TimelineViewModel tab) => Find("TabHeaderButton", tab);

        public Button Close(TimelineViewModel tab) => Find("CloseTabButton", tab);

        public TabHeaderRole Role(TimelineViewModel tab) => TabStripView.GetRole(Header(tab));

        public Color Foreground(TimelineViewModel tab) =>
            (Header(tab).Foreground as ISolidColorBrush)?.Color
            ?? throw new Xunit.Sdk.XunitException("header Foreground is not a solid colour");

        public WriteableBitmap Capture() =>
            Window.CaptureRenderedFrame() ?? throw new Xunit.Sdk.XunitException("CaptureRenderedFrame returned null");

        /// <summary>The pixel in the 4 DIP margin just above the label (the item Border's Margin="0,4,0,4"), which is
        /// whatever the column paints behind its tab strip.</summary>
        public Color BackgroundBehind(WriteableBitmap frame, TimelineViewModel tab)
        {
            var header = Header(tab);
            var topLeft = header.TranslatePoint(new Point(0, 0), Window)
                ?? throw new Xunit.Sdk.XunitException("header not attached to the window");
            var point = new PixelPoint((int)Math.Round(topLeft.X) + 2, (int)Math.Round(topLeft.Y) - 2);

            using var buffer = frame.Lock();
            var bytes = new byte[buffer.RowBytes * buffer.Size.Height];
            Marshal.Copy(buffer.Address, bytes, 0, bytes.Length);
            var offset = (point.Y * buffer.RowBytes) + (point.X * 4);

            if (buffer.Format == PixelFormat.Rgba8888)
            {
                return Color.FromArgb(bytes[offset + 3], bytes[offset], bytes[offset + 1], bytes[offset + 2]);
            }

            if (buffer.Format == PixelFormat.Bgra8888)
            {
                return Color.FromArgb(bytes[offset + 3], bytes[offset + 2], bytes[offset + 1], bytes[offset]);
            }

            throw new Xunit.Sdk.XunitException($"unexpected framebuffer format {buffer.Format}");
        }

        private Button Find(string name, TimelineViewModel tab) =>
            Window.GetVisualDescendants().OfType<Button>()
                .FirstOrDefault(b => b.Name == name && ReferenceEquals(b.Tag, tab))
            ?? throw new Xunit.Sdk.XunitException($"no {name} found for the given tab");

        public void Dispose()
        {
            Window.Close();
            AppServices.KeyStore = null;
            AppServices.Theme = _previousTheme;
            Application.Current!.RequestedThemeVariant = _previousVariant;
            Directory.Delete(_tempKeyDir, recursive: true);
        }
    }
}
