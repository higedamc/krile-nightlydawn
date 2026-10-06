using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using NightlyDawn.Core;
using NightlyDawn.Nostr.Relay;

namespace NightlyDawn.Nostr.Timelines;

/// <summary>
/// <see cref="ITimelineSource"/> over the relay pool: one live subscription for the compiled filter, mapped to
/// <see cref="Note"/>s. <see cref="InitialLoadComplete"/> is emitted once every relay that was connected when the
/// stream started has sent EOSE (B6: never just the fastest relay), or when <c>initialLoadTimeout</c> elapses
/// first — a relay that never sends EOSE must not keep the column in its loading state forever. Events the mapper
/// rejects are skipped (and counted by the mapper, B10); the same event seen on several relays is yielded once (B7).
/// </summary>
internal sealed class NostrTimelineSource(
    NostrBackend backend,
    IEventMapper mapper,
    CompiledFilter filter,
    Func<CancellationToken, Task> ensureConnected,
    TimeSpan initialLoadTimeout,
    ILogger logger) : ITimelineSource
{
    public async IAsyncEnumerable<TimelineUpdate> StreamAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await ensureConnected(cancellationToken).ConfigureAwait(false);

        var pendingEose = backend.ConnectedRelays.Select(r => r.Value).ToHashSet(StringComparer.Ordinal);
        if (pendingEose.Count == 0)
        {
            // Offline is a connectivity failure, not an empty timeline (same stance as FetchAsync / PublishAsync).
            throw new RelayConnectionException("(pool)", "No connected relays to subscribe on");
        }

        logger.LogInformation("Timeline: subscribing on {Count} relays", pendingEose.Count);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var loaded = false;

        // The inner subscription gets its own linked token: when the consumer stops enumerating while a MoveNextAsync is
        // still pending (e.g. right after the timeout-driven InitialLoadComplete on a silent relay), .NET refuses to
        // dispose an async iterator mid-MoveNext. Cancelling the inner token and draining that pending call first makes
        // the disposal legal and also closes the relay subscriptions promptly.
        using var inner = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var messages = backend.SubscribeAsync(filter.RelayFilter, inner.Token).GetAsyncEnumerator(inner.Token);
        var next = messages.MoveNextAsync().AsTask();
        try
        {
            var timeout = Task.Delay(initialLoadTimeout, inner.Token);
            while (true)
            {
                if (!loaded && await Task.WhenAny(next, timeout).ConfigureAwait(false) == timeout)
                {
                    await timeout.ConfigureAwait(false); // rethrows if the wait ended because we were cancelled
                    logger.LogWarning("Timeline: {Pending} relays did not send EOSE within {Timeout}s; leaving the loading state", pendingEose.Count, initialLoadTimeout.TotalSeconds);
                    loaded = true;
                    yield return new InitialLoadComplete();
                    continue;
                }

                if (!await next.ConfigureAwait(false))
                {
                    break;
                }

                switch (messages.Current)
                {
                    case EventReceived received:
                        var note = TryMap(received.Event);
                        if (note is not null && seen.Add(note.Id) && (filter.LocalPredicate?.Invoke(note) ?? true))
                        {
                            // First relay to deliver the event wins the SeenOnRelays slot; the duplicates from other relays are dropped above.
                            yield return new NoteArrived(note with { SeenOnRelays = [received.RelayUrl.Value] });
                        }

                        break;

                    case EndOfStoredEvents eose:
                        if (!loaded && pendingEose.Remove(eose.RelayUrl.Value) && pendingEose.Count == 0)
                        {
                            loaded = true;
                            yield return new InitialLoadComplete();
                        }

                        break;

                    case SubscriptionClosed:
                        // Every relay closed the subscription; the backend ends the stream after this message.
                        if (!loaded)
                        {
                            loaded = true;
                            yield return new InitialLoadComplete();
                        }

                        break;
                }

                next = messages.MoveNextAsync().AsTask();
            }

            if (!loaded)
            {
                yield return new InitialLoadComplete();
            }
        }
        finally
        {
            inner.Cancel();
            try
            {
                await next.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Pending MoveNextAsync ends with OperationCanceledException (or whatever the relay loop was failing with); the stream is over either way.
            }

            await messages.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Pages strictly older than <paramref name="beforeCreatedAt"/> with a one-shot fetch (<c>until = before - 1</c>); relays that ignore <c>until</c> are corrected client-side.</summary>
    public async Task<IReadOnlyList<Note>> LoadOlderAsync(long beforeCreatedAt, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        if (beforeCreatedAt <= 0)
        {
            return [];
        }

        await ensureConnected(cancellationToken).ConfigureAwait(false);

        var page = filter.RelayFilter with { Until = beforeCreatedAt - 1, Limit = limit };
        var events = await backend.FetchAsync(page, cancellationToken).ConfigureAwait(false); // already newest-first and deduplicated

        var notes = new List<Note>(Math.Min(limit, events.Count));
        foreach (var e in events)
        {
            if (e.CreatedAt >= beforeCreatedAt)
            {
                continue;
            }

            var note = TryMap(e);
            if (note is null || !(filter.LocalPredicate?.Invoke(note) ?? true))
            {
                continue;
            }

            notes.Add(note);
            if (notes.Count == limit)
            {
                break;
            }
        }

        return notes;
    }

    private Note? TryMap(NostrEvent e)
    {
        try
        {
            return mapper.ToNote(e);
        }
        catch (EventMappingException)
        {
            return null; // counted and logged (without payload) by the mapper
        }
    }
}
