using System.Text.Json;
using Microsoft.Extensions.Logging;
using NightlyDawn.Core;
using NightlyDawn.Nostr.Wire;

namespace NightlyDawn.Nostr.Mapping;

/// <summary>
/// Wire event → domain. Malformed input raises <see cref="EventMappingException"/> (B10); the caller skips and logs.
/// Individual bad tags (a non-hex id, a <c>ws://</c> relay) are dropped rather than failing the whole event,
/// because relays carry plenty of sloppy but otherwise usable events.
/// </summary>
public sealed class EventMapper(NostrBackendDiagnostics? diagnostics = null, ILogger? logger = null) : IEventMapper
{
    private static readonly string[] ReplyMarkers = ["root", "reply", "mention"];

    public Note ToNote(NostrEvent nostrEvent)
    {
        if (nostrEvent.Kind is not (1 or 6 or 16))
        {
            throw Fail(nostrEvent, $"kind {nostrEvent.Kind} is not a note kind (1/6/16)");
        }

        string? rootId = null, replyId = null, quotedId = null, repostedId = null;
        var mentions = new List<string>();
        var hashtags = new List<string>();
        var eTags = new List<(string Id, string? Marker)>();

        foreach (var tag in nostrEvent.Tags)
        {
            if (tag.Count < 2)
            {
                continue;
            }

            switch (tag[0])
            {
                case "e" when EventVerifier.IsLowerHex(tag[1], 64):
                    eTags.Add((tag[1], tag.Count >= 4 && ReplyMarkers.Contains(tag[3]) ? tag[3] : null));
                    break;
                case "p" when EventVerifier.IsLowerHex(tag[1], 64):
                    if (!mentions.Contains(tag[1]))
                    {
                        mentions.Add(tag[1]);
                    }

                    break;
                case "q" when EventVerifier.IsLowerHex(tag[1], 64):
                    quotedId ??= tag[1];
                    break;
                case "t" when tag[1].Length > 0:
                    var hashtag = tag[1].ToLowerInvariant();
                    if (!hashtags.Contains(hashtag))
                    {
                        hashtags.Add(hashtag);
                    }

                    break;
            }
        }

        if (nostrEvent.Kind is 6 or 16)
        {
            // Repost: the first e tag is the reposted event (NIP-18). No thread semantics.
            repostedId = eTags.FirstOrDefault().Id;
        }
        else if (eTags.Count > 0)
        {
            // NIP-10: prefer markers; fall back to positional (first = root, last = reply).
            var marked = eTags.Where(t => t.Marker is not null).ToList();
            if (marked.Count > 0)
            {
                rootId = marked.FirstOrDefault(t => t.Marker == "root").Id;
                replyId = marked.FirstOrDefault(t => t.Marker == "reply").Id ?? rootId;
                rootId ??= replyId;
            }
            else
            {
                rootId = eTags[0].Id;
                replyId = eTags[^1].Id;
            }
        }

        return new Note(
            nostrEvent.Id,
            nostrEvent.Pubkey,
            nostrEvent.CreatedAt,
            (NoteKind)nostrEvent.Kind,
            nostrEvent.Content,
            nostrEvent.Tags,
            RootId: rootId,
            ReplyId: replyId,
            QuotedNoteId: quotedId,
            RepostedNoteId: repostedId,
            MentionedPubkeys: mentions.Count > 0 ? mentions : null,
            Hashtags: hashtags.Count > 0 ? hashtags : null);
    }

    public Profile ToProfile(NostrEvent nostrEvent)
    {
        if (nostrEvent.Kind != 0)
        {
            throw Fail(nostrEvent, $"kind {nostrEvent.Kind} is not a profile (kind 0)");
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(nostrEvent.Content);
        }
        catch (JsonException ex)
        {
            throw Fail(nostrEvent, $"profile content is not JSON: {ex.GetType().Name}");
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw Fail(nostrEvent, "profile content is not a JSON object");
            }

            return new Profile(
                nostrEvent.Pubkey,
                Name: GetString(root, "name"),
                DisplayName: GetString(root, "display_name") ?? GetString(root, "displayName"),
                About: GetString(root, "about"),
                Picture: GetHttpUrl(root, "picture"),
                Banner: GetHttpUrl(root, "banner"),
                Nip05: GetString(root, "nip05"),
                Lud16: GetString(root, "lud16"),
                UpdatedAt: nostrEvent.CreatedAt);
        }
    }

    public RelayListEntry ToRelayList(NostrEvent nostrEvent)
    {
        if (nostrEvent.Kind != 10002)
        {
            throw Fail(nostrEvent, $"kind {nostrEvent.Kind} is not a relay list (kind 10002)");
        }

        var read = new List<RelayUrl>();
        var write = new List<RelayUrl>();
        foreach (var tag in nostrEvent.Tags)
        {
            if (tag.Count < 2 || tag[0] != "r")
            {
                continue;
            }

            RelayUrl url;
            try
            {
                if (!Uri.TryCreate(tag[1], UriKind.Absolute, out var uri) || uri.Scheme != "wss" || string.IsNullOrEmpty(uri.Host))
                {
                    continue; // ws:// and junk entries are dropped, not fatal
                }

                url = RelayUrl.Parse(uri.ToString().TrimEnd('/'));
            }
            catch (ArgumentException)
            {
                continue;
            }

            var marker = tag.Count >= 3 ? tag[2] : null;
            if (marker is null or "read")
            {
                AddUnique(read, url);
            }

            if (marker is null or "write")
            {
                AddUnique(write, url);
            }
        }

        return new RelayListEntry(nostrEvent.Pubkey, read, write);
    }

    private static void AddUnique(List<RelayUrl> list, RelayUrl url)
    {
        if (!list.Contains(url))
        {
            list.Add(url);
        }
    }

    private static string? GetString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;

    /// <summary>Picture/banner must be http(s) URLs; anything else (javascript:, data:, file:) is dropped before it reaches a UI image loader.</summary>
    private static string? GetHttpUrl(JsonElement obj, string name)
    {
        var value = GetString(obj, name);
        return value is not null && Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" ? value : null;
    }

    private EventMappingException Fail(NostrEvent e, string reason)
    {
        diagnostics?.CountMappingFailure();
        logger?.LogDebug("Event {EventId} kind {Kind}: mapping failed — {Reason}", e.Id.Length >= 8 ? e.Id[..8] : e.Id, e.Kind, reason);
        return new EventMappingException($"event {(e.Id.Length >= 8 ? e.Id[..8] : e.Id)}: {reason}");
    }
}
