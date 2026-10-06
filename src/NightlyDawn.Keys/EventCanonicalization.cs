using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace NightlyDawn.Keys;

/// <summary>
/// NIP-01 event <c>id</c> computation: <c>sha256(serialize([0, pubkey, created_at, kind,
/// tags, content]))</c>, with the spec's exact escaping — only <c>"</c>, <c>\</c>, and the
/// named control characters (<c>\n \r \t \b \f</c>) are escaped; everything else (including
/// non-ASCII text) is emitted as raw UTF-8, not <c>\uXXXX</c>-escaped. This intentionally
/// does not use <see cref="System.Text.Json"/>, whose default encoder escapes a broader
/// set of characters than the spec calls for and would produce a different <c>id</c> than
/// every other Nostr implementation.
///
/// This duplicates canonicalization logic that also exists in NightlyDawn.Nostr (phase
/// 1a's event verifier needs the same serialization to check an incoming <c>id</c>). The
/// duplication is deliberate for leaf isolation (Lead's instruction: 1b must not touch
/// src/NightlyDawn.Nostr while 1a is in flight) rather than an oversight — both
/// implementations are tested against the same NIP-01 rules independently, and the
/// duplication is a candidate to fold into NightlyDawn.Core once both leaves land.
/// </summary>
internal static class EventCanonicalization
{
    public static string Serialize(string pubkey, long createdAt, int kind, IReadOnlyList<IReadOnlyList<string>> tags, string content)
    {
        var builder = new StringBuilder();
        builder.Append("[0,");
        AppendEscapedString(builder, pubkey);
        builder.Append(',').Append(createdAt.ToString(CultureInfo.InvariantCulture));
        builder.Append(',').Append(kind.ToString(CultureInfo.InvariantCulture));
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

    /// <exception cref="ArgumentException">A string contains an unpaired UTF-16 surrogate. Signing fails closed here rather than silently substituting U+FFFD, matching 1a's verifier-side behavior for the same malformation.</exception>
    public static byte[] ComputeId(string pubkey, long createdAt, int kind, IReadOnlyList<IReadOnlyList<string>> tags, string content)
    {
        var canonical = Serialize(pubkey, createdAt, kind, tags, content);
        return SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
    }

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

        // Any other control character must still be escaped to remain valid JSON (RFC 8259),
        // even though NIP-01 only names the seven cases above explicitly.
        if (c < 0x20)
        {
            builder.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
            return;
        }

        builder.Append(c);
    }
}
