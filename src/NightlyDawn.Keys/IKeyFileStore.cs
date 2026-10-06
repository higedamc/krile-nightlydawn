namespace NightlyDawn.Keys;

/// <summary>Where <see cref="LocalKeyStore"/> persists the <c>ncryptsec1...</c> blob. Never sees plaintext key material — only the already-NIP-49-encrypted string.</summary>
public interface IKeyFileStore
{
    Task<string?> ReadAsync(CancellationToken cancellationToken = default);

    Task WriteAsync(string ncryptsec, CancellationToken cancellationToken = default);

    Task DeleteAsync(CancellationToken cancellationToken = default);
}
