using NightlyDawn.Core;
using NightlyDawn.Nostr.Timelines;
using Xunit;

namespace NightlyDawn.Nostr.Tests.Timelines;

public class SimpleTimelineQueryCompilerTests
{
    private const string HexA = "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789";
    private const string HexB = "0000000000000000000000000000000000000000000000000000000000000001";

    private readonly SimpleTimelineQueryCompiler _compiler = new();

    [Theory]
    [InlineData("")]
    [InlineData("   \t ")]
    public void EmptyQuery_IsKind1WithTheDefaultLimit(string query)
    {
        var compiled = _compiler.Compile(query);

        Assert.Equal([SimpleTimelineQueryCompiler.DefaultKind], compiled.RelayFilter.Kinds);
        Assert.Equal(_compiler.DefaultLimit, compiled.RelayFilter.Limit);
        Assert.Null(compiled.RelayFilter.Authors);
        Assert.Null(compiled.RelayFilter.TagFilters);
        Assert.Null(compiled.LocalPredicate); // everything is relay-side in this compiler
    }

    [Fact]
    public void Tokens_CompileToKindsAuthorsHashtagsAndLimit()
    {
        var compiled = _compiler.Compile($"  kinds:1,6,1  #Nostr t:bitcoin tags:#Nostr,lightning  author:{HexA.ToUpperInvariant()} authors:{HexB},{HexA}  limit:20 ");
        var f = compiled.RelayFilter;

        Assert.Equal([1, 6], f.Kinds);
        Assert.Equal([HexA, HexB], f.Authors); // lower-cased, deduplicated, first-seen order
        Assert.Equal(["nostr", "bitcoin", "lightning"], f.TagFilters!["t"]);
        Assert.Equal(20, f.Limit);
        Assert.Null(f.Since);
        Assert.Null(f.Until);
        Assert.Null(f.Search);
    }

    [Fact]
    public void Keys_AreCaseInsensitive()
    {
        var f = _compiler.Compile("KIND:7 LIMIT:3").RelayFilter;

        Assert.Equal([7], f.Kinds);
        Assert.Equal(3, f.Limit);
    }

    [Fact]
    public void Limit_IsClampedToMaxLimit()
    {
        var compiler = new SimpleTimelineQueryCompiler(defaultLimit: 10, maxLimit: 40);

        Assert.Equal(40, compiler.Compile("limit:999999").RelayFilter.Limit);
        Assert.Equal(10, compiler.Compile("kind:1").RelayFilter.Limit);
    }

    [Theory]
    [InlineData("kind:x")]
    [InlineData("kind:-1")]
    [InlineData("kind:70000")]
    [InlineData("kind:")]
    [InlineData("limit:0")]
    [InlineData("limit:abc")]
    [InlineData("limit:1,2")]
    [InlineData("author:zz")]
    [InlineData("author:abcdef")]
    [InlineData("#")]
    [InlineData("foo")]
    [InlineData(":bar")]
    [InlineData("colour:blue")]
    [InlineData("kind:1 banana")]
    public void Garbage_IsRejected_WithFilterParseException(string query)
    {
        Assert.Throws<FilterParseException>(() => _compiler.Compile(query));
    }

    [Fact]
    public void Kql_IsRejected_WithAPointerToLeaf1c()
    {
        var ex = Assert.Throws<FilterParseException>(() => _compiler.Compile("from home where text contains \"x\""));

        Assert.Contains("1c", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Npub_IsAccepted_AndDecodedToTheSameHexAsTheHexForm()
    {
        var npub = Bech32.Encode("npub", Convert.FromHexString(HexA));

        var fromNpub = _compiler.Compile($"author:{npub}").RelayFilter.Authors;
        var fromUpper = _compiler.Compile($"author:{npub.ToUpperInvariant()}").RelayFilter.Authors;
        var mixed = _compiler.Compile($"authors:{npub},{HexB},{HexA}").RelayFilter.Authors;

        Assert.Equal([HexA], fromNpub);
        Assert.Equal([HexA], fromUpper);
        Assert.Equal([HexA, HexB], mixed); // npub and its hex twin are the same author, deduplicated
    }

    [Fact]
    public void Npub_WithABadChecksumOrWrongLength_IsRejected()
    {
        var npub = Bech32.Encode("npub", Convert.FromHexString(HexA));
        var corrupted = npub[..^1] + (npub[^1] == 'q' ? 'p' : 'q');
        var tooShort = Bech32.Encode("npub", Convert.FromHexString(HexA)[..31]);

        Assert.Throws<FilterParseException>(() => _compiler.Compile($"author:{corrupted}"));
        Assert.Throws<FilterParseException>(() => _compiler.Compile($"author:{tooShort}"));
        Assert.Throws<FilterParseException>(() => _compiler.Compile("author:npub1qqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqqq"));
    }

    [Fact]
    public void ErrorMessages_DoNotEchoLongTokensVerbatim()
    {
        var blob = new string('a', 10_000);
        var ex = Assert.Throws<FilterParseException>(() => _compiler.Compile(blob));

        Assert.True(ex.Message.Length < 200, $"message was {ex.Message.Length} chars");
    }

    [Fact]
    public void OversizedHashtag_IsRejected()
    {
        Assert.Throws<FilterParseException>(() => _compiler.Compile("#" + new string('x', 101)));
        Assert.NotNull(_compiler.Compile("#" + new string('x', 100)).RelayFilter.TagFilters);
    }
}
