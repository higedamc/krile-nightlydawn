using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using NightlyDawn.Core;

namespace NightlyDawn.App.Composing;

public enum ComposeMode
{
    Post,
    Reply,
    Quote,
}

/// <summary>
/// State behind the shared compose box (plan §10, goal ③'s posting half). Talks only to Core's
/// <see cref="INotePublisher"/> -- reached through <c>publisher()</c>, a <c>Func</c> rather than a stored
/// reference, so it keeps working if the host wires <see cref="AppServices.NotePublisher"/> after this view
/// model is constructed (null today: this leaf does not depend on L3a's implementation). <c>sink()</c> is the
/// same kind of indirection for <see cref="IPublishedNoteSink"/>, needed because <c>MainWindow</c> builds this
/// view model before the timeline column it reports into exists (plan §10.1-2's row-construction-time
/// injection has the same ordering problem one level up).
///
/// <para><b>Reply/Quote mode is entered externally</b> (<see cref="BeginReply"/>/<see cref="BeginQuote"/>; a
/// row's Reply/Quote button calls these through <see cref="NoteRowActions"/>) and the next successful
/// <see cref="PostAsync"/> sends that action instead of a plain note.</para>
///
/// <para><b>No sanitization or truncation here</b> (plan §10.2): the publisher is the one place content is
/// judged, and a validation failure's <see cref="ArgumentException.Message"/> is surfaced as
/// <see cref="Status"/> verbatim rather than rewriting what the user typed. On success the box empties only
/// if <see cref="Content"/> still holds exactly what was sent -- the box stays enabled during the round trip
/// (plan §10.4 non-blocking ③), so text typed while a prior post is in flight must survive it, not be erased
/// by a success continuation that assumes nothing changed underneath it. On any failure it never empties, so
/// a rejected post is never lost to a retype either.</para>
///
/// <para><b>Double-submit is stopped structurally</b>, not just by a disabled button binding:
/// <see cref="PostAsync"/> itself refuses a second call while <see cref="IsBusy"/> is set (plan §10.1-5) --
/// that flag flips to true on the synchronous part of the call, before the first <c>await</c>, so a second
/// call arriving before the UI thread repaints the disabled button still sees it set.</para>
/// </summary>
public sealed class ComposeViewModel(Func<INotePublisher?> publisher, Func<IPublishedNoteSink?> sink, Action<Action> postToUi) : INotifyPropertyChanged
{
    private string _content = string.Empty;
    private string _status = string.Empty;
    private bool _isBusy;
    private ComposeMode _mode = ComposeMode.Post;
    private Note? _target;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Content
    {
        get => _content;
        set => SetField(ref _content, value ?? string.Empty);
    }

    public string Status
    {
        get => _status;
        private set => SetField(ref _status, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetField(ref _isBusy, value))
            {
                Raise(nameof(CanPost));
            }
        }
    }

    /// <summary>Negated <see cref="IsBusy"/> for a direct <c>IsEnabled</c> binding (Avalonia has no built-in
    /// boolean-negation binding converter in this codebase yet).</summary>
    public bool CanPost => !IsBusy;

    public ComposeMode Mode
    {
        get => _mode;
        private set
        {
            if (SetField(ref _mode, value))
            {
                Raise(nameof(HasTarget));
                Raise(nameof(TargetDescription));
            }
        }
    }

    /// <summary>What <see cref="Mode"/> targets; null in <see cref="ComposeMode.Post"/>.</summary>
    public Note? Target
    {
        get => _target;
        private set => SetField(ref _target, value);
    }

    public bool HasTarget => Mode != ComposeMode.Post;

    public string? TargetDescription => Mode switch
    {
        ComposeMode.Reply => $"Replying to {Target?.AuthorPubkey[..8]}…",
        ComposeMode.Quote => $"Quoting {Target?.AuthorPubkey[..8]}…",
        _ => null,
    };

    /// <summary>Called by a row's Reply button (via <see cref="NoteRowActions.BeginReply"/>). Leaves
    /// <see cref="Content"/> untouched -- switching targets must not discard whatever the user had already typed.</summary>
    public void BeginReply(Note parent)
    {
        ArgumentNullException.ThrowIfNull(parent);
        Target = parent;
        Mode = ComposeMode.Reply;
    }

    public void BeginQuote(Note quoted)
    {
        ArgumentNullException.ThrowIfNull(quoted);
        Target = quoted;
        Mode = ComposeMode.Quote;
    }

    public void CancelTargeting()
    {
        Mode = ComposeMode.Post;
        Target = null;
    }

    public Task PostAsync(CancellationToken cancellationToken = default)
    {
        if (IsBusy)
        {
            return Task.CompletedTask; // Structural guard (plan §10.1-5): a second call before this one's first await must not publish twice.
        }

        var active = publisher();
        if (active is null)
        {
            Status = "No publisher available yet.";
            return Task.CompletedTask;
        }

        return RunAsync(active, cancellationToken);
    }

    private async Task RunAsync(INotePublisher active, CancellationToken cancellationToken)
    {
        IsBusy = true;
        var mode = Mode;
        var target = Target;
        var sentContent = Content; // Captured before the first await (plan §10.4 non-blocking ③): the box
        // stays enabled during the round trip, so the user can keep typing. Clearing on success must only
        // erase what was actually sent, not whatever Content happens to hold when the continuation runs.
        Status = mode switch
        {
            ComposeMode.Reply => "Replying…",
            ComposeMode.Quote => "Quoting…",
            _ => "Posting…",
        };

        try
        {
            var published = mode switch
            {
                ComposeMode.Reply => await active.ReplyToAsync(target!, sentContent, cancellationToken).ConfigureAwait(false),
                ComposeMode.Quote => await active.QuoteAsync(target!, sentContent, cancellationToken).ConfigureAwait(false),
                _ => await active.PostNoteAsync(sentContent, cancellationToken).ConfigureAwait(false),
            };

            Post(() =>
            {
                if (Content == sentContent)
                {
                    Content = string.Empty;
                }

                // Guarded the same way as Content above: Mode/Target are mutable fields this continuation does
                // not own exclusively. BeginReply/BeginQuote carry no IsBusy guard (a row's Reply/Quote button
                // only looks at CanReplyOrRepost, which is kind-only), so a row's click while this call is still
                // in flight can retarget the box before this runs. Resetting unconditionally would silently
                // drop that new target, and the next Post the user sends would publish as a plain note instead
                // of the reply/quote they just selected.
                if (Mode == mode && ReferenceEquals(Target, target))
                {
                    Mode = ComposeMode.Post;
                    Target = null;
                }

                var accepted = published.PublishResult.Outcomes.Count(o => o.Accepted);
                Status = $"Posted · {accepted}/{published.PublishResult.Outcomes.Count} relays accepted.";
                sink()?.NotePublished(published.Note, published.PublishResult);
            });
        }
        catch (ArgumentException ex)
        {
            // ex.Message is a validation reason the publisher wrote itself (byte count, emptiness) -- never
            // the content itself (plan §10.2: show why it was rejected, not a copy of what was typed).
            Post(() => Status = ex.Message);
        }
        catch (EventPublishException ex)
        {
            Post(() => Status = $"Publish failed: 0 of {ex.Result.Outcomes.Count} relays accepted.");
        }
        catch (Exception ex)
        {
            // Type name only (plan §10.4-2), same convention as TimelineColumnViewModel's status line -- e.g.
            // SignerUnavailableException before a key is unlocked, or RelayConnectionException on a socket
            // failure that never reaches an accept/reject outcome. Without this, Status is left stuck on the
            // "Posting…"/"Replying…"/"Quoting…" set above, every byte of Content survives (the catch below
            // never runs), and only the finally's IsBusy reset distinguishes "failed silently" from "still busy".
            Post(() => Status = $"Post failed: {ex.GetType().Name}.");
        }
        finally
        {
            Post(() => IsBusy = false);
        }
    }

    private void Post(Action action) => postToUi(action);

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        Raise(propertyName);
        return true;
    }

    private void Raise(string? propertyName) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
