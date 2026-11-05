using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NightlyDawn.App.RenderTests.Fakes;
using NightlyDawn.Core;
using NightlyDawn.Keys;
using Xunit;

namespace NightlyDawn.App.RenderTests;

/// <summary>
/// L20: the window's bottom is one Auto-height band, as in the fork (StarryEyes <c>MainWindow.xaml:315-322</c>).
/// Everything reads realised bounds in the visual tree, not the XAML text.
///
/// <para>Negative controls run while writing this (each reverted afterwards): stacking the two cells in a vertical
/// <c>StackPanel</c> turns <see cref="TheBandIsOneRow_NotTwoStackedLines"/> red at both sizes.</para>
/// </summary>
public sealed class StatusBandRenderTests
{
    [AvaloniaTheory]
    [InlineData(900, 600)]
    [InlineData(1280, 800)]
    public void TheBandIsOneRow_NotTwoStackedLines(double width, double height)
    {
        using var s = new Session(width, height);
        var band = s.Find<Grid>("StatusBand");
        var status = s.Find<TextBlock>("StatusLine");
        var copyright = s.Find<TextBlock>("Copyright");

        Assert.Same(band, status.GetVisualParent());
        Assert.Same(band, copyright.GetVisualParent());

        var st = s.Origin(status);
        var cp = s.Origin(copyright);

        // Side by side: the status cell is left of the copyright cell and their vertical extents overlap.
        Assert.True(st.X + status.Bounds.Width <= cp.X + 0.5, $"status ends at {st.X + status.Bounds.Width}, copyright starts at {cp.X}");
        Assert.True(st.Y < cp.Y + copyright.Bounds.Height && cp.Y < st.Y + status.Bounds.Height, "the two cells do not share a row");

        // One line's worth of height: the band is as tall as its tallest cell, not the sum of both (stacked).
        Assert.Equal(Math.Max(status.Bounds.Height, copyright.Bounds.Height), band.Bounds.Height, precision: 1);
        Assert.True(band.Bounds.Height < status.Bounds.Height + copyright.Bounds.Height - 1);

        // The licence notice is whole, not trimmed away.
        Assert.Equal(TextTrimming.None, copyright.TextTrimming);
        Assert.Contains("MIT", copyright.Text);
    }

    private sealed class Session : IDisposable
    {
        private readonly string _tempKeyDir;

        public MainWindow Window { get; }

        public Session(double width, double height)
        {
            _tempKeyDir = Directory.CreateTempSubdirectory("nightlydawn-status-band-tests").FullName;
            AppServices.KeyStore = new LocalKeyStore(new FileKeyFileStore(Path.Combine(_tempKeyDir, "key.ncryptsec")));
            AppServices.TimelineSourceFactory = new StaticTimelineSourceFactory([]);
            AppServices.ScreenshotPath = null;

            Window = new MainWindow { Width = width, Height = height };
            Window.Show();
            HeadlessWindowExtensions.SetRenderScaling(Window, 1.0);
            Dispatcher.UIThread.RunJobs();
        }

        public T Find<T>(string name) where T : Control =>
            Window.GetVisualDescendants().OfType<T>().FirstOrDefault(c => c.Name == name)
            ?? throw new Xunit.Sdk.XunitException($"{name} not found");

        public Point Origin(Control control) =>
            control.TranslatePoint(new Point(0, 0), Window) ?? throw new Xunit.Sdk.XunitException($"{control.Name}: not attached");

        public void Dispose()
        {
            Window.Close();
            AppServices.KeyStore = null;
            Directory.Delete(_tempKeyDir, recursive: true);
        }
    }
}
