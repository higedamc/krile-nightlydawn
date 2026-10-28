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
/// Renders the real <see cref="MainWindow"/> once per theme variant and asserts on the pixels (L1). A theme is the
/// one kind of change whose entire effect is what reaches the screen, so "the dictionary loaded" is not evidence:
/// every assertion here samples the captured frame or compares two frames.
///
/// <para>Both frames are written to the same <c>RenderFrames</c> directory the other render tests use, so CI's
/// artifact carries a light and a dark screenshot of every PR; that is the "theme switch for the harness" half of
/// this leaf's requirement (plan §1 item 7).</para>
/// </summary>
public sealed class ThemeRenderTests
{
    private const double Width = 1280;
    private const double Height = 800;
    private static readonly TimeSpan PumpTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Where the window background is plain: the shell's root DockPanel has a 16 DIP margin, so a few
    /// pixels in from the corner is the window's own brush and nothing else.</summary>
    private static readonly PixelPoint BackgroundProbe = new(4, 4);

    public static TheoryData<AppTheme, string> Variants() => new()
    {
        { AppTheme.Dark, "#FF1C1822" },
        { AppTheme.Light, "#FFF4F1F8" },
    };

    /// <summary>The palette has to be visible, not merely loaded: the window's background pixel must be the colour
    /// this variant's dictionary declares. This is also what pins down <i>which</i> Fluent key the Window template
    /// actually consumes, which is why the expectation is a literal colour rather than a resource lookup.</summary>
    [AvaloniaTheory]
    [MemberData(nameof(Variants))]
    public void TheWindowBackgroundIsThisVariantsKrileColor(AppTheme theme, string expectedHex)
    {
        using var session = new ThemeSession(theme);

        var frame = session.Capture();
        SaveFrame(frame, theme);

        var actual = PixelAt(frame, BackgroundProbe);
        var expected = Color.Parse(expectedHex);

        Assert.True(IsClose(actual, expected),
            $"{theme}: window background at {BackgroundProbe.X},{BackgroundProbe.Y} is {actual} but the {theme} dictionary declares {expected}. " +
            "Either the dictionary is not reaching the Window template, or the key it binds is not the one this leaf overrides.");
    }

    /// <summary>Negative control for the switch itself: if applying the variant did nothing (a theme pinned at
    /// build time, a dictionary without ThemeDictionaries, a switch that never reaches the running Application),
    /// both captures would be the same picture. Comparing mean luminance rather than bytes keeps this independent
    /// of the frame non-determinism tracked in plan §6.3.</summary>
    [AvaloniaFact]
    public void SwitchingTheVariantActuallyChangesThePicture()
    {
        double dark;
        double light;

        using (var session = new ThemeSession(AppTheme.Dark))
        {
            dark = MeanLuminance(session.Capture());
        }

        using (var session = new ThemeSession(AppTheme.Light))
        {
            light = MeanLuminance(session.Capture());
        }

        Assert.True(light - dark > 40,
            $"light mean luminance {light:0.#} vs dark {dark:0.#}: the two variants rendered too similarly for the switch to be doing anything.");
    }

    /// <summary>"Follow the OS" must stay distinguishable from a pinned variant all the way to the Application, or
    /// a later leaf persisting the setting cannot tell "never chosen" from "chose light"
    /// (<c>AppStateContract.cs</c> makes the same point about the stored state).</summary>
    [AvaloniaFact]
    public void SystemLeavesTheVariantToTheOperatingSystem()
    {
        var application = Application.Current!;

        ThemeController.Apply(application, AppTheme.Dark);
        Assert.Equal(ThemeVariant.Dark, application.RequestedThemeVariant);

        ThemeController.Apply(application, AppTheme.System);
        Assert.Equal(ThemeVariant.Default, application.RequestedThemeVariant);
    }

    /// <summary>The accent is what makes the window read as Krile rather than as stock Fluent, and controls reach it
    /// through the Fluent key, not through ours. The variant is made live before the lookup rather than only passed to
    /// it: a resource request carrying a variant the Application is not in resolves against the theme's own
    /// dictionaries, which made an earlier version of this test depend on whatever variant the previous test left
    /// behind. A dictionary that defines <c>KrileAccentBrush</c> but forgets to override <c>SystemAccentColor</c>
    /// still fails here.</summary>
    [AvaloniaTheory]
    [InlineData("Dark", "#FF9B7BE0")]
    [InlineData("Light", "#FF5B3FA8")]
    public void TheFluentAccentKeyIsTheKrilePurple(string variantName, string expectedHex)
    {
        var variant = variantName == "Dark" ? ThemeVariant.Dark : ThemeVariant.Light;
        var application = Application.Current!;
        var previous = application.RequestedThemeVariant;
        application.RequestedThemeVariant = variant;

        try
        {
            Assert.True(application.TryGetResource("SystemAccentColor", variant, out var accent),
                "SystemAccentColor did not resolve at all; Fluent derives its accent brushes from it.");
            Assert.Equal(Color.Parse(expectedHex), Assert.IsType<Color>(accent));

            Assert.True(application.TryGetResource("KrileAccentBrush", variant, out var brush), "KrileAccentBrush did not resolve.");
            Assert.Equal(Color.Parse(expectedHex), Assert.IsType<SolidColorBrush>(brush).Color);
        }
        finally
        {
            application.RequestedThemeVariant = previous;
        }
    }

    /// <summary>The production wiring: the running app has to read <see cref="AppServices.Theme"/> at startup. Every
    /// other test here applies the variant itself, so deleting the call in <c>App.Initialize</c> would leave them all
    /// green while <c>NIGHTLYDAWN_THEME</c> silently stopped working (measured: that mutation passes 17/17 without
    /// this test). A second <see cref="App"/> instance is initialised for this, separate from the one hosting the
    /// window.</summary>
    [AvaloniaFact]
    public void InitializeAppliesTheConfiguredTheme()
    {
        var previous = AppServices.Theme;

        try
        {
            AppServices.Theme = AppTheme.Dark;
            var app = new App();
            app.Initialize();
            Assert.Equal(ThemeVariant.Dark, app.RequestedThemeVariant);

            AppServices.Theme = AppTheme.Light;
            var light = new App();
            light.Initialize();
            Assert.Equal(ThemeVariant.Light, light.RequestedThemeVariant);
        }
        finally
        {
            AppServices.Theme = previous;
        }
    }

    /// <summary>The accent has to be on the screen, not just in a dictionary. The primary action (Subscribe, keyed
    /// off <c>IsDefault</c>) is sampled a few pixels inside its own bounds, away from the glyphs, so a theme that
    /// resolves the right colour but never paints a control still fails.</summary>
    [AvaloniaTheory]
    [InlineData(AppTheme.Dark, "#FF9B7BE0")]
    [InlineData(AppTheme.Light, "#FF5B3FA8")]
    public void ThePrimaryButtonIsFilledWithTheAccent(AppTheme theme, string expectedHex)
    {
        using var session = new ThemeSession(theme);

        var button = session.Find<Button>("SubscribeButton") ?? throw new Xunit.Sdk.XunitException("SubscribeButton not found");
        var topLeft = button.TranslatePoint(new Point(0, 0), session.Root) ?? throw new Xunit.Sdk.XunitException("SubscribeButton is not attached");
        var probe = new PixelPoint((int)Math.Round(topLeft.X + 4), (int)Math.Round(topLeft.Y + (button.Bounds.Height / 2)));

        var actual = PixelAt(session.Capture(), probe);
        var expected = Color.Parse(expectedHex);

        Assert.True(IsClose(actual, expected),
            $"{theme}: the default button's fill at {probe.X},{probe.Y} is {actual}, not the accent {expected}. " +
            "The accent resolves as a resource but is not reaching the control.");
    }

    /// <summary>One window with the variant applied, the timeline streamed in, and the frame ready to capture.</summary>
    private sealed class ThemeSession : IDisposable
    {
        private readonly AppTheme _previousTheme;
        private readonly ThemeVariant? _previousVariant;

        private MainWindow Window { get; }

        public Visual Root => Window;

        public T? Find<T>(string name) where T : Control =>
            Window.GetVisualDescendants().OfType<T>().FirstOrDefault(c => c.Name == name);

        public ThemeSession(AppTheme theme)
        {
            _previousTheme = AppServices.Theme;
            _previousVariant = Application.Current!.RequestedThemeVariant;

            AppServices.Theme = theme;
            ThemeController.Apply(Application.Current!, theme);

            AppServices.TimelineSourceFactory = new StaticTimelineSourceFactory(SampleNotes());
            AppServices.KeyStore = null;
            AppServices.ScreenshotPath = null;

            Window = new MainWindow { Width = Width, Height = Height };
            Window.Show();
            HeadlessWindowExtensions.SetRenderScaling(Window, 1.0);
            Dispatcher.UIThread.RunJobs();

            // SubscribeButton lives inside TabSettingsPanel, a Popup collapsed by default (plan §26.5 / L4c
            // ④, moved behind a Popup by §28.6 ②) -- open it through a real pointer click so the probe below
            // samples an actually-rendered button, not a collapsed one with stale/zero bounds.
            // RaiseEvent(Button.ClickEvent) would skip ToggleButton.OnClick() entirely (Lead's §28 review),
            // leaving IsChecked false and the Popup closed.
            var settingsToggle = Find<Avalonia.Controls.Primitives.ToggleButton>("SettingsToggle") ?? throw new Xunit.Sdk.XunitException("SettingsToggle not found");
            var toggleCenter = settingsToggle.TranslatePoint(new Point(settingsToggle.Bounds.Width / 2, settingsToggle.Bounds.Height / 2), Window)
                ?? throw new Xunit.Sdk.XunitException("SettingsToggle: not attached to the window's visual tree");
            Window.MouseDown(toggleCenter, Avalonia.Input.MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Window.MouseUp(toggleCenter, Avalonia.Input.MouseButton.Left);
            Dispatcher.UIThread.RunJobs();

            // L4 replaced MainWindow's DataContext with the workspace shell, so the timeline now lives one
            // tab and one column down. Mirror MainWindowRenderTests rather than casting the shell itself.
            var timeline = ((WorkspaceViewModel)Window.DataContext!).Columns[0].Tabs[0];
            timeline.Query = "from home";
            timeline.Subscribe();
            // `!X.IsLoading` alone cannot tell "never subscribed" from "finished" (plan §46/§50;
            // WaitSoundnessTests demonstrates it) -- ANDing with `Status != IdleStatus` closes the
            // gap, since IdleStatus is the one value Subscribe() never leaves the timeline in.
            Pump(() => !timeline.IsLoading && timeline.Status != TimelineViewModel.IdleStatus, "the timeline stream never finished");
            Dispatcher.UIThread.RunJobs();
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
            MakeNote(1, "Theme check: a dark-leaning purple, with a light variant of the same hue."),
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

    private static void SaveFrame(WriteableBitmap frame, AppTheme theme)
    {
        Directory.CreateDirectory(MainWindowRenderTests.OutputDirectory);
        frame.Save(Path.Combine(MainWindowRenderTests.OutputDirectory, $"theme_{theme.ToString().ToLowerInvariant()}_{Width:0}x{Height:0}.png"),
            new PngBitmapEncoderOptions());
    }

    private static Color PixelAt(WriteableBitmap frame, PixelPoint point)
    {
        using var buffer = frame.Lock();
        var bytes = new byte[buffer.RowBytes * buffer.Size.Height];
        Marshal.Copy(buffer.Address, bytes, 0, bytes.Length);

        return ReadPixel(bytes, (point.Y * buffer.RowBytes) + (point.X * 4), buffer.Format);
    }

    /// <summary>Reads one pixel, honouring the framebuffer's channel order instead of assuming one. This headless
    /// Skia platform hands back <see cref="PixelFormat.Rgba8888"/>, not the BGRA an "everyone uses BGRA" assumption
    /// would reach for: the first version of this test read the channels reversed and reported #ff22181c for a
    /// #ff1c1822 fill, which looks exactly like a wrong colour rather than a wrong reader. An unexpected format
    /// throws rather than guessing, so the same mistake cannot come back silently.</summary>
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

    private static double MeanLuminance(WriteableBitmap frame)
    {
        using var buffer = frame.Lock();
        var bytes = new byte[buffer.RowBytes * buffer.Size.Height];
        Marshal.Copy(buffer.Address, bytes, 0, bytes.Length);

        double sum = 0;
        var samples = 0;
        for (var i = 0; i + 4 <= bytes.Length; i += 4 * 13) // stride-sample; a brightness average, not a diff.
        {
            var pixel = ReadPixel(bytes, i, buffer.Format);
            sum += (0.2126 * pixel.R) + (0.7152 * pixel.G) + (0.0722 * pixel.B);
            samples++;
        }

        return sum / samples;
    }

    /// <summary>Skia's premultiplied blend of an opaque fill is exact in principle; a tolerance of 2 per channel
    /// keeps the assertion about the colour rather than about rounding.</summary>
    private static bool IsClose(Color actual, Color expected) =>
        Math.Abs(actual.R - expected.R) <= 2 &&
        Math.Abs(actual.G - expected.G) <= 2 &&
        Math.Abs(actual.B - expected.B) <= 2 &&
        Math.Abs(actual.A - expected.A) <= 2;
}
