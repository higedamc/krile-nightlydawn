using System.Reflection;
using Xunit;

namespace NightlyDawn.Core.Tests;

public class ContractTests
{
    [Fact]
    public void FilterAst_RoundTripsThroughRecordEquality()
    {
        var ast = new FilterAst(
            new FilterSource(FilterSourceKind.User, "npub1example"),
            new FilterAnd(
                new FilterComparison("user.name", FilterComparisonOperator.Contains, "x"),
                new FilterNot(new FilterComparison("reply", FilterComparisonOperator.Equals, "true"))));

        var sameShape = new FilterAst(
            new FilterSource(FilterSourceKind.User, "npub1example"),
            new FilterAnd(
                new FilterComparison("user.name", FilterComparisonOperator.Contains, "x"),
                new FilterNot(new FilterComparison("reply", FilterComparisonOperator.Equals, "true"))));

        Assert.Equal(ast, sameShape);
    }

    [Fact]
    public void CompiledFilter_AllowsNoLocalPredicate_WhenRelayFilterIsSufficient()
    {
        var compiled = new CompiledFilter(new NostrFilter(Kinds: [1], Authors: ["abc"]));

        Assert.Null(compiled.LocalPredicate);
        Assert.Equal(1, compiled.RelayFilter.Kinds?.Single());
    }

    [Fact]
    public void PublishResult_AnyAccepted_IsTrue_WhenAtLeastOneRelayAccepted()
    {
        var result = new PublishResult([
            new RelayPublishOutcome(RelayUrl.Parse("wss://a.example"), Accepted: false, Reason: "rate-limited"),
            new RelayPublishOutcome(RelayUrl.Parse("wss://b.example"), Accepted: true),
        ]);

        Assert.True(result.AnyAccepted);
    }

    [Fact]
    public void PublishResult_AnyAccepted_IsFalse_WhenZeroRelaysAccepted()
    {
        var result = new PublishResult([
            new RelayPublishOutcome(RelayUrl.Parse("wss://a.example"), Accepted: false, Reason: "rate-limited"),
        ]);

        Assert.False(result.AnyAccepted);

        var exception = new EventPublishException(result);
        Assert.Same(result, exception.Result);
    }

    [Fact]
    public void RelayUrl_RejectsPlaintextWebsocket_ByDefault()
    {
        Assert.Throws<ArgumentException>(() => RelayUrl.Parse("ws://relay.example"));
    }

    [Fact]
    public void RelayUrl_AllowsPlaintextWebsocket_WhenDevelopmentOptInIsSet()
    {
        var relay = RelayUrl.Parse("ws://localhost:4869", allowInsecureForDevelopment: true);

        Assert.Equal("ws://localhost:4869/", relay.ToString());
    }

    [Fact]
    public void RelayUrl_RejectsEmbeddedControlCharacters()
    {
        // A bare StartsWith("wss://") check would have let this through (B12).
        Assert.Throws<ArgumentException>(() => RelayUrl.Parse("wss://relay.example\r\nSec-Fetch: fake"));
    }

    [Fact]
    public void RelayUrl_NormalizesSurroundingWhitespaceAndCase_SoEquivalentRelaysCompareEqual()
    {
        // B14: Value must be uri.AbsoluteUri, not the caller's raw string — otherwise
        // incidental whitespace reaches the WebSocket client, and two inputs denoting
        // the same relay (trailing slash, scheme case) would compare unequal, which
        // breaks B6's "EOSE from every connected relay" and B7's FirstSeenOnRelay.
        var padded = RelayUrl.Parse("  wss://a.example\t");
        var upperScheme = RelayUrl.Parse("WSS://A.Example/");
        var canonical = RelayUrl.Parse("wss://a.example/");

        Assert.Equal("wss://a.example/", padded.ToString());
        Assert.Equal(canonical, upperScheme);
        Assert.Equal(canonical, padded);
    }

    [Fact]
    public void RelayUrl_HasNoPublicParameterlessConstructor()
    {
        var publicConstructors = typeof(RelayUrl).GetConstructors();

        Assert.Empty(publicConstructors);
    }

    /// <summary>Plan §32.1 (L9): columns are on the outside and tabs on the inside, matching the running UI
    /// since §21.2. The 0d-era <c>Tab(Id, Title, Columns)</c> this used to construct no longer exists.</summary>
    [Fact]
    public void Column_HoldsOrderedTabs_AndNamesItsSelectedOne()
    {
        var home = new Timeline("home", "Home", "from home");
        var mentions = new Timeline("mentions", "Mentions", "from mentions");

        var column = new Column("left", [home, mentions], SelectedTabId: "mentions");

        Assert.Equal(["home", "mentions"], column.Tabs.Select(t => t.Id));
        Assert.Equal("mentions", column.SelectedTabId);
    }

    /// <summary>The reshape kept <see cref="AppState.SchemaVersion"/> at 1 on purpose (no 0d-shaped file was
    /// ever written, so there is nothing to migrate -- the record's doc says so). This pins both that number and
    /// the "empty columns, no active column, default settings" meaning of <see cref="AppState.Default"/>, which
    /// the app relies on to mean "build the built-in layout and do not save it".</summary>
    /// <summary>Plan §32.3's "write no secret" starts here: the record a store serializes has exactly these four
    /// members, so there is no field through which key material, a passphrase or a signer handle could reach the
    /// file. A fifth public property on <see cref="AppState"/> should be a deliberate, reviewed change.</summary>
    [Fact]
    public void AppState_CarriesExactlyTheFourLayoutFields()
    {
        var names = typeof(AppState).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();

        Assert.Equal(["ActiveColumnId", "Columns", "SchemaVersion", "Settings"], names);
        Assert.Equal(["Id", "SelectedTabId", "Tabs"], typeof(Column).GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal));
        Assert.Equal(["Id", "KqlQuery", "Title"], typeof(Timeline).GetProperties(BindingFlags.Public | BindingFlags.Instance).Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal));
    }

    [Fact]
    public void AppStateDefault_IsSchemaVersion1_WithNoColumns()
    {
        var state = AppState.Default;

        Assert.Equal(1, state.SchemaVersion);
        Assert.Equal(AppState.CurrentSchemaVersion, state.SchemaVersion);
        Assert.Empty(state.Columns);
        Assert.Null(state.ActiveColumnId);
        Assert.Equal(new Settings(), state.Settings);
    }
}
