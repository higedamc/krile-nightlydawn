using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using NightlyDawn.Core;

namespace NightlyDawn.Nostr.Wire;

/// <summary>NIP-01 wire format: canonical event serialization (for the id), client→relay messages, relay→client message parsing.</summary>
internal static class NostrJson
{
    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Indented = false,
    };

    /// <summary>
    /// The exact bytes whose SHA-256 is the event id. Delegates to
    /// <see cref="NostrEventCanonicalization"/> in Core — the serialization is the NIP-01
    /// protocol contract itself, shared with NightlyDawn.Keys's event signing so the two
    /// can never silently diverge (see that type's doc comment for why this matters).
    /// </summary>
    public static byte[] CanonicalEventBytes(string pubkey, long createdAt, int kind, IReadOnlyList<IReadOnlyList<string>> tags, string content) =>
        NostrEventCanonicalization.CanonicalEventBytes(pubkey, createdAt, kind, tags, content);

    public static string EventMessage(NostrEvent e)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer, WriterOptions))
        {
            w.WriteStartArray();
            w.WriteStringValue("EVENT");
            WriteEvent(w, e);
            w.WriteEndArray();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    public static string ReqMessage(string subscriptionId, NostrFilter filter)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer, WriterOptions))
        {
            w.WriteStartArray();
            w.WriteStringValue("REQ");
            w.WriteStringValue(subscriptionId);
            WriteFilter(w, filter);
            w.WriteEndArray();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    public static string CloseMessage(string subscriptionId)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var w = new Utf8JsonWriter(buffer, WriterOptions))
        {
            w.WriteStartArray();
            w.WriteStringValue("CLOSE");
            w.WriteStringValue(subscriptionId);
            w.WriteEndArray();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void WriteEvent(Utf8JsonWriter w, NostrEvent e)
    {
        w.WriteStartObject();
        w.WriteString("id", e.Id);
        w.WriteString("pubkey", e.Pubkey);
        w.WriteNumber("created_at", e.CreatedAt);
        w.WriteNumber("kind", e.Kind);
        w.WritePropertyName("tags");
        w.WriteStartArray();
        foreach (var tag in e.Tags)
        {
            w.WriteStartArray();
            foreach (var item in tag)
            {
                w.WriteStringValue(item);
            }

            w.WriteEndArray();
        }

        w.WriteEndArray();
        w.WriteString("content", e.Content);
        w.WriteString("sig", e.Sig);
        w.WriteEndObject();
    }

    private static void WriteFilter(Utf8JsonWriter w, NostrFilter f)
    {
        w.WriteStartObject();
        WriteStringArray(w, "ids", f.Ids);
        WriteStringArray(w, "authors", f.Authors);
        if (f.Kinds is not null)
        {
            w.WritePropertyName("kinds");
            w.WriteStartArray();
            foreach (var k in f.Kinds)
            {
                w.WriteNumberValue(k);
            }

            w.WriteEndArray();
        }

        if (f.TagFilters is not null)
        {
            foreach (var (tag, values) in f.TagFilters)
            {
                // Tag filters are "#<single letter>"; accept either form from the caller.
                WriteStringArray(w, tag.StartsWith('#') ? tag : "#" + tag, values);
            }
        }

        if (f.Since is not null)
        {
            w.WriteNumber("since", f.Since.Value);
        }

        if (f.Until is not null)
        {
            w.WriteNumber("until", f.Until.Value);
        }

        if (f.Limit is not null)
        {
            w.WriteNumber("limit", f.Limit.Value);
        }

        if (f.Search is not null)
        {
            w.WriteString("search", f.Search);
        }

        w.WriteEndObject();
    }

    private static void WriteStringArray(Utf8JsonWriter w, string name, IReadOnlyList<string>? values)
    {
        if (values is null)
        {
            return;
        }

        w.WritePropertyName(name);
        w.WriteStartArray();
        foreach (var v in values)
        {
            w.WriteStringValue(v);
        }

        w.WriteEndArray();
    }

    /// <summary>Parses one relay→client message. Returns null for anything that is not a well-formed NIP-01 array message (counted by the caller as malformed).</summary>
    public static RelayMessage? ParseRelayMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Array || root.GetArrayLength() < 2 || root[0].ValueKind != JsonValueKind.String)
            {
                return null;
            }

            switch (root[0].GetString())
            {
                case "EVENT" when root.GetArrayLength() >= 3 && root[1].ValueKind == JsonValueKind.String:
                    var e = ParseEvent(root[2]);
                    return e is null ? null : new RelayMessage.Event(root[1].GetString()!, e);
                case "EOSE" when root[1].ValueKind == JsonValueKind.String:
                    return new RelayMessage.Eose(root[1].GetString()!);
                case "OK" when root.GetArrayLength() >= 3 && root[1].ValueKind == JsonValueKind.String && root[2].ValueKind is JsonValueKind.True or JsonValueKind.False:
                    var reason = root.GetArrayLength() >= 4 && root[3].ValueKind == JsonValueKind.String ? root[3].GetString() : null;
                    return new RelayMessage.Ok(root[1].GetString()!, root[2].GetBoolean(), reason);
                case "CLOSED" when root[1].ValueKind == JsonValueKind.String:
                    var closedReason = root.GetArrayLength() >= 3 && root[2].ValueKind == JsonValueKind.String ? root[2].GetString() : null;
                    return new RelayMessage.Closed(root[1].GetString()!, closedReason);
                case "NOTICE":
                    return new RelayMessage.Notice(root[1].ValueKind == JsonValueKind.String ? root[1].GetString()!.Length : 0);
                case "AUTH":
                    return new RelayMessage.Auth();
                default:
                    return null;
            }
        }
        catch (JsonException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            // JsonDocument.Parse accepts a lone UTF-16 surrogate escape such as "\uD800"; GetString() then throws
            // InvalidOperationException. Treat it like any other malformed message instead of letting it escape.
            return null;
        }
    }

    private static NostrEvent? ParseEvent(JsonElement obj)
    {
        if (obj.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        if (!TryGetString(obj, "id", out var id) || !TryGetString(obj, "pubkey", out var pubkey) ||
            !TryGetString(obj, "content", out var content) || !TryGetString(obj, "sig", out var sig) ||
            !obj.TryGetProperty("created_at", out var createdAtEl) || createdAtEl.ValueKind != JsonValueKind.Number || !createdAtEl.TryGetInt64(out var createdAt) ||
            !obj.TryGetProperty("kind", out var kindEl) || kindEl.ValueKind != JsonValueKind.Number || !kindEl.TryGetInt32(out var kind) ||
            !obj.TryGetProperty("tags", out var tagsEl) || tagsEl.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var tags = new List<IReadOnlyList<string>>(tagsEl.GetArrayLength());
        foreach (var tagEl in tagsEl.EnumerateArray())
        {
            if (tagEl.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var tag = new List<string>(tagEl.GetArrayLength());
            foreach (var item in tagEl.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                {
                    return null;
                }

                tag.Add(item.GetString()!);
            }

            tags.Add(tag);
        }

        return new NostrEvent(id, pubkey, createdAt, kind, tags, content, sig);
    }

    private static bool TryGetString(JsonElement obj, string name, out string value)
    {
        if (obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String)
        {
            value = el.GetString()!;
            return true;
        }

        value = string.Empty;
        return false;
    }
}

internal abstract record RelayMessage
{
    public sealed record Event(string SubscriptionId, NostrEvent Payload) : RelayMessage;

    public sealed record Eose(string SubscriptionId) : RelayMessage;

    public sealed record Ok(string EventId, bool Accepted, string? Reason) : RelayMessage;

    public sealed record Closed(string SubscriptionId, string? Reason) : RelayMessage;

    /// <summary>Only the length is kept: NOTICE text is relay-authored and must not be logged verbatim.</summary>
    public sealed record Notice(int Length) : RelayMessage;

    public sealed record Auth : RelayMessage;
}
