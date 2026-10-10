using System.Text;

namespace NightlyDawn.Core;

/// <summary>
/// The single place a timeline row turns a kind:0 profile -- attacker-controlled text that a human uses as a
/// claim of identity -- into a label (plan §8.2). <c>NoteRow.AuthorLabel</c> is the only intended caller; nothing
/// else should format a display name out of a <see cref="Profile"/> on its own, or the sanitization here would
/// need to be remembered at every call site instead of once.
/// </summary>
public static class AuthorLabel
{
    /// <summary>Display-name characters kept after sanitization, before the "…" suffix is added.</summary>
    public const int MaxDisplayNameChars = 48;

    private const int PubkeyPrefixChars = 8;

    // Bidirectional control characters. These can reorder the characters around them on screen -- the attack
    // this function exists to stop: a name that *looks* like someone else's because its characters were
    // rendered in a different order than they were typed (plan §8.2).
    private static readonly HashSet<int> BidiControls =
    [
        0x202A, 0x202B, 0x202C, 0x202D, 0x202E, // LRE, RLE, PDF, LRO, RLO
        0x2066, 0x2067, 0x2068, 0x2069, // LRI, RLI, FSI, PDI
        0x200E, 0x200F, // LRM, RLM
        0x061C, // ALM
    ];

    // Invisible characters that pad a name without displaying anything. Deliberately excludes U+200C (ZWNJ)
    // and U+200D (ZWJ): removing those breaks emoji ZWJ sequences and Indic/Arabic orthography -- verified with
    // real runes in AuthorLabelTests, not inferred (plan §8.2).
    private static readonly HashSet<int> InvisiblePadding =
    [
        0x200B, // ZWSP
        0x2060, 0x2061, 0x2062, 0x2063, 0x2064, // word joiner, invisible times/plus/separator
        0xFEFF, // BOM / ZWNBSP
        0xFFF9, 0xFFFA, 0xFFFB, // interlinear annotation anchor/separator/terminator
        0x180E, // Mongolian vowel separator
    ];

    /// <summary>
    /// The label a row shows for <paramref name="pubkeyHex"/>, given whatever profile is cached for it (or null
    /// if none is -- unresolved and confirmed-absent look the same to a caller that only renders, per
    /// <see cref="IProfileStore.TryGet"/>). Always includes the pubkey prefix: a display name alone cannot prove
    /// who sent a note, since any pubkey can claim any name (plan §8.2). Never returns an empty string.
    /// </summary>
    public static string For(Profile? profile, string pubkeyHex)
    {
        var prefix = PubkeyPrefix(pubkeyHex);
        var name = Sanitize(profile?.DisplayName) ?? Sanitize(profile?.Name);
        return name is null ? prefix : $"{name} ({prefix})";
    }

    private static string PubkeyPrefix(string pubkeyHex) =>
        pubkeyHex.Length > PubkeyPrefixChars ? string.Concat(pubkeyHex.AsSpan(0, PubkeyPrefixChars), "…") : pubkeyHex;

    /// <summary>
    /// Null in, null out. Otherwise: strips bidi/padding control characters outright, collapses every other
    /// run of whitespace or control characters (newlines, tabs, ...) to a single space, trims, and truncates to
    /// <see cref="MaxDisplayNameChars"/>. Returns null if nothing printable survives, so the caller falls back
    /// to the pubkey alone instead of showing an empty label (plan §8.2).
    /// </summary>
    private static string? Sanitize(string? raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return null;
        }

        var builder = new StringBuilder(raw.Length);
        var pendingSpace = false;

        foreach (var ch in raw)
        {
            if (BidiControls.Contains(ch) || InvisiblePadding.Contains(ch))
            {
                continue; // Removed outright -- these are invisible, so there is nothing to replace with a space.
            }

            if (char.IsControl(ch) || char.IsWhiteSpace(ch))
            {
                pendingSpace = builder.Length > 0; // A run at the very start is dropped by the trim below.
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(ch);
        }

        var collapsed = builder.ToString().Trim();
        if (collapsed.Length == 0)
        {
            return null;
        }

        return collapsed.Length > MaxDisplayNameChars
            ? TruncateWithoutSplittingASurrogatePair(collapsed, MaxDisplayNameChars) + "…"
            : collapsed;
    }

    private static string TruncateWithoutSplittingASurrogatePair(string value, int maxChars)
    {
        var cut = maxChars;
        if (cut > 0 && char.IsHighSurrogate(value[cut - 1]))
        {
            cut--;
        }

        return value[..cut];
    }
}
