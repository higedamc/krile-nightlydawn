using System.Security.Cryptography;
using NightlyDawn.Core;
using NightlyDawn.Keys;
using Xunit;

namespace NightlyDawn.Keys.Tests;

/// <summary>Covers the two IKeyStore members added for the Keys panel (2026-10-07): HasStoredKeyAsync and UnlockStoredKeyAsync.</summary>
public class LocalKeyStoreUnlockTests
{
    [Fact]
    public async Task HasStoredKey_NeedsNoPassphrase_AndIsFalseWhenNothingIsStored()
    {
        var file = new MemoryKeyFileStore();
        var store = new LocalKeyStore(file);

        Assert.False(await store.HasStoredKeyAsync());
        await store.GenerateLocalKeyAsync("correct horse".AsMemory());
        Assert.True(await store.HasStoredKeyAsync());

        var freshSession = new LocalKeyStore(file); // same file, nothing unlocked yet
        Assert.True(await freshSession.HasStoredKeyAsync());
        await Assert.ThrowsAsync<SignerUnavailableException>(() => freshSession.GetActiveSignerAsync());
    }

    [Fact]
    public async Task Unlock_ActivatesTheStoredKey_WithoutRewritingTheFile()
    {
        var file = new MemoryKeyFileStore();
        var generated = await new LocalKeyStore(file).GenerateLocalKeyAsync("correct horse".AsMemory());
        var storedBefore = file.Content;
        var writesBefore = file.Writes;

        var session = new LocalKeyStore(file);
        var unlocked = await session.UnlockStoredKeyAsync("correct horse".AsMemory());

        Assert.Equal(generated.Pubkey, unlocked.Pubkey);
        Assert.Equal(SignerKind.Nip49Local, unlocked.Kind);
        Assert.Equal(unlocked.Pubkey, (await session.GetActiveSignerAsync()).Pubkey);
        Assert.Equal(storedBefore, file.Content);
        Assert.Equal(writesBefore, file.Writes); // unlock is read-only against the only copy
    }

    [Fact]
    public async Task Unlock_WithTheWrongPassphrase_Throws_AndLeavesTheStoreLocked()
    {
        var file = new MemoryKeyFileStore();
        await new LocalKeyStore(file).GenerateLocalKeyAsync("correct horse".AsMemory());
        var session = new LocalKeyStore(file);

        await Assert.ThrowsAsync<CryptographicException>(() => session.UnlockStoredKeyAsync("wrong horse".AsMemory()));
        await Assert.ThrowsAsync<SignerUnavailableException>(() => session.GetActiveSignerAsync());
    }

    [Fact]
    public async Task Lock_EndsTheSession_WithoutTouchingTheFile_AndUnlockBringsItBack()
    {
        var file = new MemoryKeyFileStore();
        var store = new LocalKeyStore(file);
        var generated = await store.GenerateLocalKeyAsync("correct horse".AsMemory());
        var writesBefore = file.Writes;

        await store.LockAsync();

        await Assert.ThrowsAsync<SignerUnavailableException>(() => store.GetActiveSignerAsync());
        Assert.True(await store.HasStoredKeyAsync());
        Assert.NotNull(file.Content);
        Assert.Equal(writesBefore, file.Writes);

        var again = await store.UnlockStoredKeyAsync("correct horse".AsMemory());
        Assert.Equal(generated.Pubkey, again.Pubkey);

        await store.LockAsync();
        await store.LockAsync(); // idempotent, never throws
    }

    [Fact]
    public async Task SignOut_DeletesTheStoredKey_UnlikeLock()
    {
        var file = new MemoryKeyFileStore();
        var store = new LocalKeyStore(file);
        await store.GenerateLocalKeyAsync("correct horse".AsMemory());

        await store.SignOutAsync();

        Assert.False(await store.HasStoredKeyAsync());
        Assert.Null(file.Content);
    }

    [Fact]
    public async Task Unlock_WithNothingStored_IsSignerUnavailable()
    {
        var store = new LocalKeyStore(new MemoryKeyFileStore());

        await Assert.ThrowsAsync<SignerUnavailableException>(() => store.UnlockStoredKeyAsync("anything".AsMemory()));
    }

    private sealed class MemoryKeyFileStore : IKeyFileStore
    {
        public string? Content { get; private set; }
        public int Writes { get; private set; }

        public Task<string?> ReadAsync(CancellationToken cancellationToken = default) => Task.FromResult(Content);

        public Task WriteAsync(string ncryptsec, CancellationToken cancellationToken = default)
        {
            Writes++;
            Content = ncryptsec;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(CancellationToken cancellationToken = default)
        {
            Content = null;
            return Task.CompletedTask;
        }
    }
}
