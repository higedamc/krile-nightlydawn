using Xunit;

namespace NightlyDawn.Core.Tests;

/// <summary>
/// Plan §8.3: the sanitizer is verified by mutation (remove one character class, see the assertion that
/// depends on it fail) rather than a per-character table, plus one test proving ZWJ/ZWNJ sequences survive
/// untouched.
/// </summary>
public class AuthorLabelTests
{
    private const string Pubkey = "a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2c3d4e5f6a1b2";
    private const string PubkeyPrefix = "a1b2c3d4…";

    [Fact]
    public void NoProfile_FallsBackToThePubkeyPrefixAlone()
    {
        Assert.Equal(PubkeyPrefix, AuthorLabel.For(null, Pubkey));
    }

    [Fact]
    public void ShortPubkey_IsNotEllipsized()
    {
        Assert.Equal("ab12", AuthorLabel.For(null, "ab12"));
    }

    [Fact]
    public void DisplayName_PreferredOverName()
    {
        var profile = new Profile(Pubkey, Name: "name-field", DisplayName: "Display Field");
        Assert.Equal($"Display Field ({PubkeyPrefix})", AuthorLabel.For(profile, Pubkey));
    }

    [Fact]
    public void DisplayNameBlank_FallsBackToName()
    {
        var profile = new Profile(Pubkey, Name: "fallback-name", DisplayName: "   ");
        Assert.Equal($"fallback-name ({PubkeyPrefix})", AuthorLabel.For(profile, Pubkey));
    }

    [Fact]
    public void BothNamesBlank_FallsBackToThePubkeyPrefixAlone()
    {
        var profile = new Profile(Pubkey, Name: " ", DisplayName: "");
        Assert.Equal(PubkeyPrefix, AuthorLabel.For(profile, Pubkey));
    }

    [Theory]
    // Bidi controls (plan §8.2): a mutation that stops stripping any one of these must be caught by this test.
    [InlineData("Eve‮evil")]
    [InlineData("Eve‭evil")]
    [InlineData("Eve⁦evil")]
    [InlineData("Eve⁩evil")]
    [InlineData("Eve‎evil")]
    [InlineData("Eve‏evil")]
    [InlineData("Eve؜evil")]
    public void BidiControlCharacters_AreRemoved(string poisoned)
    {
        var profile = new Profile(Pubkey, DisplayName: poisoned);
        Assert.Equal($"Eveevil ({PubkeyPrefix})", AuthorLabel.For(profile, Pubkey));
    }

    [Theory]
    // Invisible padding (plan §8.2): same shape of negative control as the bidi set above.
    [InlineData("Eve​evil")]
    [InlineData("Eve⁠evil")]
    [InlineData("Eve﻿evil")]
    [InlineData("Eve￹evil")]
    [InlineData("Eve᠎evil")]
    public void InvisiblePaddingCharacters_AreRemoved(string poisoned)
    {
        var profile = new Profile(Pubkey, DisplayName: poisoned);
        Assert.Equal($"Eveevil ({PubkeyPrefix})", AuthorLabel.For(profile, Pubkey));
    }

    [Fact]
    public void AdversarialBidiAndPaddingMix_RendersFlat()
    {
        // The kind of string the render test's third row uses: several bidi overrides and zero-width
        // characters packed around plain text. If sanitization regresses, this reads out of order or hides
        // text -- the render test checks the picture, this test checks the string it is built from.
        var poisoned = "‮evil‬​mask﻿ed‏name";
        var profile = new Profile(Pubkey, DisplayName: poisoned);

        Assert.Equal($"evilmaskedname ({PubkeyPrefix})", AuthorLabel.For(profile, Pubkey));
    }

    [Theory]
    [InlineData("line one\nline two", "line one line two")]
    [InlineData("tab\there", "tab here")]
    [InlineData("multi   space", "multi space")]
    [InlineData("  leading and trailing  ", "leading and trailing")]
    public void NewlinesTabsAndRuns_CollapseToASingleSpace(string input, string expected)
    {
        var profile = new Profile(Pubkey, DisplayName: input);
        Assert.Equal($"{expected} ({PubkeyPrefix})", AuthorLabel.For(profile, Pubkey));
    }

    [Fact]
    public void LongDisplayName_IsTruncatedWithEllipsis()
    {
        var longName = new string('x', AuthorLabel.MaxDisplayNameChars + 20);
        var profile = new Profile(Pubkey, DisplayName: longName);

        var label = AuthorLabel.For(profile, Pubkey);

        var expectedName = new string('x', AuthorLabel.MaxDisplayNameChars) + "…";
        Assert.Equal($"{expectedName} ({PubkeyPrefix})", label);
    }

    [Fact]
    public void NameThatIsOnlyControlCharacters_FallsBackToThePubkeyPrefixAlone()
    {
        var profile = new Profile(Pubkey, DisplayName: "‮​\n\t  ");
        Assert.Equal(PubkeyPrefix, AuthorLabel.For(profile, Pubkey));
    }

    [Fact]
    public void ZwjEmojiSequence_SurvivesUntouched()
    {
        // Family emoji: four people joined by ZWJ (U+200D). Stripping ZWJ would turn one glyph into four.
        const string family = "\U0001F468‍\U0001F469‍\U0001F467‍\U0001F466";
        var profile = new Profile(Pubkey, DisplayName: family);

        Assert.Equal($"{family} ({PubkeyPrefix})", AuthorLabel.For(profile, Pubkey));
    }

    [Fact]
    public void ZwnjInIndicText_SurvivesUntouched()
    {
        // Hindi "shabd" with an explicit ZWNJ (U+200C) controlling conjunct formation -- removing it changes
        // the rendered word, not just its appearance (plan §8.2).
        const string word = "क्‌ष";
        var profile = new Profile(Pubkey, DisplayName: word);

        Assert.Equal($"{word} ({PubkeyPrefix})", AuthorLabel.For(profile, Pubkey));
    }

    [Fact]
    public void SurrogatePairEmoji_IsNotSplitByTruncation()
    {
        // 48 'x' characters exactly fill the budget; appending a surrogate-pair emoji must either keep the
        // whole pair or drop it, never keep just one half (which would produce an unpaired surrogate).
        var name = new string('x', AuthorLabel.MaxDisplayNameChars - 1) + "\U0001F600"; // 47 'x' + 😀 (surrogate pair) = 49 UTF-16 code units
        var profile = new Profile(Pubkey, DisplayName: name);

        var label = AuthorLabel.For(profile, Pubkey);
        var namePart = label[..label.IndexOf(" (a1b2c3d4", StringComparison.Ordinal)];

        Assert.DoesNotContain('\uD83D', namePart); // lone high surrogate would mean the pair was split.
        Assert.DoesNotContain('\uDE00', namePart); // lone low surrogate, same reasoning.
    }
}
