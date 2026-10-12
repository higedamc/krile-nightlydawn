using NightlyDawn.Core;
using NightlyDawn.Filters;
using NightlyDawn.Nostr.Tests.Fakes;
using NightlyDawn.Nostr.Timelines;
using Xunit;

namespace NightlyDawn.Nostr.Tests.Timelines;

/// <summary>Plan §13.2 items 1, 2, 4, 5: the adapter wiring real KQL (<see cref="FilterCompiler"/>) to
/// <see cref="IContactListSource"/>. Parsing and the account check happen synchronously inside <c>Compile</c>
/// itself (so a bad query or a missing account surfaces before any relay traffic); only <c>from home</c>'s
/// kind:3 fetch is deferred to the returned delegate.</summary>
public class NostrFilterQueryCompilerTests
{
    private static readonly string AccountPubkey = new TestSigner(9).PubkeyHex;

    [Fact]
    public void Compile_FromHome_WithNoAccount_ThrowsAccountRequired_Synchronously_WithoutTouchingTheContactListSource()
    {
        var contactList = new FakeContactListSource();
        var sut = new NostrFilterQueryCompiler(new FilterCompiler(), contactList);

        // Not wrapped in the returned delegate: thrown directly by the Compile() call.
        Assert.Throws<AccountRequiredException>(() => sut.Compile("from home where ()", accountPubkey: null));
    }

    [Fact]
    public void Compile_FromMentions_WithNoAccount_ThrowsAccountRequired_Synchronously()
    {
        var sut = new NostrFilterQueryCompiler(new FilterCompiler(), new FakeContactListSource());

        Assert.Throws<AccountRequiredException>(() => sut.Compile("from mentions where ()", accountPubkey: null));
    }

    [Fact]
    public void Compile_BadQuery_ThrowsFilterParseException_Synchronously_BeforeAnyAccountCheck()
    {
        var sut = new NostrFilterQueryCompiler(new FilterCompiler(), new FakeContactListSource());

        Assert.Throws<FilterParseException>(() => sut.Compile("not a kql query", accountPubkey: null));
    }

    [Fact]
    public async Task Compile_FromHome_DefersTheContactListFetch_UntilTheReturnedDelegateRuns()
    {
        var contactList = new FakeContactListSource { Follows = ["bob"] };
        var sut = new NostrFilterQueryCompiler(new FilterCompiler(), contactList);

        // Mutating Compile to call the contact list source eagerly would make this fail: the fetch has not
        // happened yet when Compile returns, only once the delegate below is awaited.
        var deferred = sut.Compile("from home where ()", AccountPubkey);
        Assert.True(deferred.IsAccountBound);
        Assert.Equal(0, contactList.FetchCalls);

        var compiled = await deferred.ResolveAsync(CancellationToken.None);

        Assert.Equal(1, contactList.FetchCalls);
        Assert.Equal(["bob"], compiled.RelayFilter.Authors);
    }

    [Fact]
    public async Task Compile_FromHome_NoFollows_CompilesToAnAuthorsListThatMatchesNothing_NotEveryone()
    {
        var sut = new NostrFilterQueryCompiler(new FilterCompiler(), new FakeContactListSource());

        var compiled = await sut.Compile("from home where ()", AccountPubkey).ResolveAsync(CancellationToken.None);

        // Mutating `Authors: []` to `Authors: null` would turn "follows nobody yet" into "match any author".
        Assert.NotNull(compiled.RelayFilter.Authors);
        Assert.Empty(compiled.RelayFilter.Authors!);
    }

    [Fact]
    public void Compile_FromMentions_WithAccount_CompilesWithNoIO()
    {
        var contactList = new FakeContactListSource();
        var sut = new NostrFilterQueryCompiler(new FilterCompiler(), contactList);

        var deferred = sut.Compile("from mentions where ()", AccountPubkey);

        Assert.True(deferred.IsAccountBound);
        Assert.Equal(0, contactList.FetchCalls);
    }

    /// <summary>Plan §18.5 (L5k): a source that never needs the account must report it, synchronously, same as
    /// the home/mentions cases above report true -- this is what <see cref="NostrTimelineSourceFactory"/> stamps
    /// onto <see cref="ITimelineSource.IsAccountBound"/> regardless of whether the caller happened to supply an
    /// account at all.</summary>
    [Fact]
    public void Compile_FromKind_IsNotAccountBound_WhetherOrNotAnAccountWasSupplied()
    {
        var sut = new NostrFilterQueryCompiler(new FilterCompiler(), new FakeContactListSource());

        Assert.False(sut.Compile("from kind(\"1\") where ()", accountPubkey: null).IsAccountBound);
        Assert.False(sut.Compile("from kind(\"1\") where ()", AccountPubkey).IsAccountBound);
    }
}
