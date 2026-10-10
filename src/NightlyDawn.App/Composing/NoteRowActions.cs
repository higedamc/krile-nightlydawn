using System.Linq;
using NightlyDawn.Core;

namespace NightlyDawn.App.Composing;

/// <summary>
/// One row's publish actions (plan §10.1-2), injected into <see cref="Timelines.NoteRow"/> at construction
/// time instead of looked up via visual-tree ancestry -- a <c>DataTemplate</c>'s <c>DataContext</c> is the
/// <see cref="Timelines.NoteRow"/> itself, and walking up to <c>$parent[ListBox].DataContext</c> from
/// <c>NoteRowView</c>'s code-behind would make this untestable without a live visual tree.
///
/// <para><see cref="BeginReply"/>/<see cref="BeginQuote"/> only hand the target off to the shared compose box
/// -- replying or quoting needs the user to type something, so there is nothing this class can send on its
/// own. <see cref="RepostAsync"/>/<see cref="ReactAsync"/> publish immediately: no text, nothing to type.</para>
///
/// <para>Every publisher call is caught here (plan §10.1-3: <see cref="ArgumentException"/> and
/// <see cref="EventPublishException"/> are the documented failure modes of <see cref="INotePublisher"/>), so
/// the Avalonia <c>async void</c> click handler that calls into this class never needs its own try/catch --
/// nothing it could observe would otherwise escape and take the process down.</para>
/// </summary>
public sealed class NoteRowActions(
    Note note,
    Func<INotePublisher?> publisher,
    ComposeViewModel compose,
    IPublishedNoteSink sink,
    Action<string> setStatus)
{
    private bool _repostBusy;
    private bool _reactBusy;

    public void BeginReply() => compose.BeginReply(note);

    public void BeginQuote() => compose.BeginQuote(note);

    public async Task RepostAsync(CancellationToken cancellationToken = default)
    {
        if (_repostBusy)
        {
            return; // Structural guard (plan §10.1-5): a different created_at makes a second repost a distinct event, not deduplicated by any relay.
        }

        var active = publisher();
        if (active is null)
        {
            setStatus("No publisher available yet.");
            return;
        }

        _repostBusy = true;
        try
        {
            var published = await active.RepostAsync(note, cancellationToken).ConfigureAwait(false);
            sink.NotePublished(published.Note, published.PublishResult);
            var accepted = published.PublishResult.Outcomes.Count(o => o.Accepted);
            setStatus($"Reposted · {accepted}/{published.PublishResult.Outcomes.Count} relays accepted.");
        }
        catch (ArgumentException ex)
        {
            setStatus(ex.Message);
        }
        catch (EventPublishException ex)
        {
            setStatus($"Repost failed: 0 of {ex.Result.Outcomes.Count} relays accepted.");
        }
        finally
        {
            _repostBusy = false;
        }
    }

    public async Task ReactAsync(CancellationToken cancellationToken = default)
    {
        if (_reactBusy)
        {
            return; // Structural guard (plan §10.1-5), same reasoning as RepostAsync.
        }

        var active = publisher();
        if (active is null)
        {
            setStatus("No publisher available yet.");
            return;
        }

        _reactBusy = true;
        try
        {
            // Fixed "+" only (plan §10.2): v1 opens no arbitrary-string reaction input from this UI, even
            // though INotePublisher.ReactAsync's own validation is a second line of defense either way.
            //
            // ReactAsync still returns Task<PublishedNote> on this leaf's base (origin/master, pre-#23): L3a
            // (unmerged at the time this leaf was written) changes that to Task<PublishResult> directly,
            // since a reaction has no Note to return (plan §9.1). The only change this call needs once that
            // lands and this branch rebases is dropping ".PublishResult" below.
            var published = await active.ReactAsync(note, "+", cancellationToken).ConfigureAwait(false);
            var accepted = published.PublishResult.Outcomes.Count(o => o.Accepted);
            setStatus($"Reacted · {accepted}/{published.PublishResult.Outcomes.Count} relays accepted.");
        }
        catch (ArgumentException ex)
        {
            setStatus(ex.Message);
        }
        catch (EventPublishException ex)
        {
            setStatus($"Reaction failed: 0 of {ex.Result.Outcomes.Count} relays accepted.");
        }
        finally
        {
            _reactBusy = false;
        }
    }
}
