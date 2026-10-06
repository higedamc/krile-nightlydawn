using System.Security.Cryptography;
using System.Text;

namespace NightlyDawn.Core;

/// <summary>
/// NIP-01 event <c>id</c> computation: <c>sha256(serialize([0, pubkey, created_at, kind,
/// tags, content]))</c>, with the spec's exact escaping — only <c>"</c>, <c>\</c>, and the
/// named control characters (<c>\n \r \t \b \f</c>) get named escapes, other control
/// characters get <c>\uXXXX</c>, and everything else (including non-ASCII text) is
/// emitted verbatim, not <c>\uXXXX</c>-escaped. This intentionally does not use
/// <see cref="System.Text.Json"/>, whose default encoder escapes a broader set of
/// characters than the spec calls for and would produce a different <c>id</c> than every
/// other Nostr implementation.
///
/// Lives in Core (not NightlyDawn.Nostr or NightlyDawn.Keys) because it is the NIP-01
/// protocol contract itself, not an implementation detail of either leaf: both the relay
/// adapter (verifying incoming event ids) and the local key store (computing the id of an
/// event being signed) need byte-for-byte the same serialization, or a divergence between
/// two copies — invisible on ASCII content, surfacing only on surrogates, control
/// characters, or a future spec addendum — would show up as "my own client rejects an
/// event I signed" or "a valid event fails verification," with no obvious cause. (This
/// consolidates what was briefly two independent implementations in leaf 1a and leaf 1b,
/// written in parallel while the two leaves were isolated from each other; Lead's call once
/// isolation was no longer needed.)
/// </summary>
public static class NostrEventCanonicalization
{
    public static string Serialize(string pubkey, long createdAt, int kind, IReadOnlyList<IReadOnlyList<string>> tags, string content)
    {
        var builder = new StringBuilder();
        builder.Append("[0,");
        AppendEscapedString(builder, pubkey);
        builder.Append(',').Append(createdAt);
        builder.Append(',').Append(kind);
        builder.Append(",[");

        for (var i = 0; i < tags.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }

            builder.Append('[');
            var tag = tags[i];
            for (var j = 0; j < tag.Count; j++)
            {
                if (j > 0)
                {
                    builder.Append(',');
                }

                AppendEscapedString(builder, tag[j]);
            }

            builder.Append(']');
        }

        builder.Append("],");
        AppendEscapedString(builder, content);
        builder.Append(']');

        return builder.ToString();
    }

    public static byte[] CanonicalEventBytes(string pubkey, long createdAt, int kind, IReadOnlyList<IReadOnlyList<string>> tags, string content) =>
        Encoding.UTF8.GetBytes(Serialize(pubkey, createdAt, kind, tags, content));

    /// <exception cref="ArgumentException">A string contains an unpaired UTF-16 surrogate; it has no UTF-8 form, so no canonical bytes or id exist for it. Callers that verify untrusted events should map this to a rejection rather than letting it propagate; callers signing their own event should let it fail closed.</exception>
    public static byte[] ComputeId(string pubkey, long createdAt, int kind, IReadOnlyList<IReadOnlyList<string>> tags, string content) =>
        SHA256.HashData(CanonicalEventBytes(pubkey, createdAt, kind, tags, content));

    private static void AppendEscapedString(StringBuilder builder, string value)
    {
        builder.Append('"');

        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            switch (c)
            {
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                case '\b':
                    builder.Append("\\b");
                    break;
                case '\f':
                    builder.Append("\\f");
                    break;
                default:
                    AppendDefault(builder, value, ref i, c);
                    break;
            }
        }

        builder.Append('"');
    }

    private static void AppendDefault(StringBuilder builder, string value, ref int i, char c)
    {
        if (char.IsHighSurrogate(c))
        {
            if (i + 1 < value.Length && char.IsLowSurrogate(value[i + 1]))
            {
                builder.Append(c).Append(value[i + 1]);
                i++;
                return;
            }

            throw new ArgumentException("String contains an unpaired high surrogate.", nameof(value));
        }

        if (char.IsLowSurrogate(c))
        {
            throw new ArgumentException("String contains an unpaired low surrogate.", nameof(value));
        }

        if (c < 0x20)
        {
            builder.Append("\\u").Append(((int)c).ToString("x4"));
            return;
        }

        builder.Append(c);
    }
}
