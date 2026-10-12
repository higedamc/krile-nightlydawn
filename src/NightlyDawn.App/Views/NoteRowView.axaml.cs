using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using NightlyDawn.App.Avatars;
using NightlyDawn.App.Timelines;

namespace NightlyDawn.App.Views;

public sealed partial class NoteRowView : UserControl
{
    private readonly Image _avatarImage;

    /// <summary>The load in progress for the current <see cref="DataContext"/>, or null. Replaced (and the old
    /// one cancelled) whenever the row is given a different <see cref="NoteRow"/> or leaves the tree -- a
    /// ListBox recycles this control for other rows, so a bitmap that arrives late must not land on whichever
    /// note the control shows by then. The identity of this object is the generation check: a continuation
    /// whose source is no longer the current one is stale and does nothing.</summary>
    private CancellationTokenSource? _avatarLoad;

    public NoteRowView()
    {
        AvaloniaXamlLoader.Load(this);
        _avatarImage = this.FindControl<Image>("AvatarImage")
            ?? throw new InvalidOperationException("NoteRowView.axaml no longer has an Image named AvatarImage");
    }

    private NoteRow? Row => DataContext as NoteRow;

    // Reply/Quote only hand the target to the shared compose box (NoteRowActions.BeginReply/BeginQuote are
    // synchronous) -- the publish itself happens later, when that box's own Post is pressed.
    private void OnReplyClick(object? sender, RoutedEventArgs e) => Row?.Actions?.BeginReply();

    private void OnQuoteClick(object? sender, RoutedEventArgs e) => Row?.Actions?.BeginQuote();

    private async void OnRepostClick(object? sender, RoutedEventArgs e) => await (Row?.Actions?.RepostAsync() ?? Task.CompletedTask);

    private async void OnReactClick(object? sender, RoutedEventArgs e) => await (Row?.Actions?.ReactAsync() ?? Task.CompletedTask);

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        RestartAvatarLoad();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_avatarLoad is null && _avatarImage.Source is null)
        {
            // Detaching cancelled the load and cleared the slot (below); a re-attached row needs its image
            // back. A load already running (DataContext was set before the attach, the usual order for a
            // ListBox container) is left alone rather than restarted.
            RestartAvatarLoad();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        CancelAvatarLoad();
        _avatarImage.Source = null;
    }

    /// <summary>Cancels whatever was loading and, when the current row names a picture, starts loading it.
    /// Must run on the UI thread (every caller is a control lifecycle override).</summary>
    private void RestartAvatarLoad()
    {
        CancelAvatarLoad();
        _avatarImage.Source = null;

        var url = Row?.AvatarUrl;
        if (url is null)
        {
            return;
        }

        // The one line that switches avatars off (plan §26.4): with no loader there is nothing to ask, and the
        // cache is never consulted, so the decision "fetch for everyone" is reversible from the host alone.
        if (AppServices.RemoteImageLoader is null)
        {
            return;
        }

        var load = new CancellationTokenSource();
        _avatarLoad = load;
        LoadAvatarAsync(url, load);
    }

    private void CancelAvatarLoad()
    {
        var load = _avatarLoad;
        _avatarLoad = null;
        if (load is null)
        {
            return;
        }

        load.Cancel();
        load.Dispose();
    }

    /// <summary>Waits for the cache off the UI thread and hands the result back on it. The result is applied
    /// only if <paramref name="load"/> is still the current load: the row may have been recycled (new
    /// DataContext), detached, or both while the fetch was running (up to the loader's 10 s budget).
    /// Anything other than cancellation is a bug in the cache or the loader -- both promise null for every
    /// remote-controlled failure -- and is allowed to surface rather than be swallowed here.</summary>
    private async void LoadAvatarAsync(string url, CancellationTokenSource load)
    {
        Bitmap? bitmap;
        try
        {
            bitmap = await AvatarImageCache.Shared.GetAsync(url, load.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return; // Row moved on; nothing to show and nobody to show it to.
        }

        if (bitmap is null)
        {
            return; // The slot is already empty; no UI-thread hop needed.
        }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (!ReferenceEquals(_avatarLoad, load) || load.IsCancellationRequested)
            {
                return;
            }

            _avatarImage.Source = bitmap;
        });
    }
}
