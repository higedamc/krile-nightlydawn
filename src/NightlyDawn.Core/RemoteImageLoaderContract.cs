namespace NightlyDawn.Core;

/// <summary>
/// The only sanctioned path from a relay-supplied URL (a kind:0 profile's <c>picture</c>/<c>banner</c>) to
/// bytes on disk. Relay content is adversarial input (the same convention as every other B-series boundary in
/// this codebase): nothing upstream vets that URL, so <see cref="NightlyDawn.App"/> must never grow an ad hoc
/// <c>HttpClient</c> to fetch it directly (plan §1 item C). An implementation of this interface MUST enforce,
/// not merely document:
/// <list type="bullet">
/// <item><description>http(s) scheme only, including after a redirect -- no <c>file://</c>, no redirect to a
/// non-http(s) target.</description></item>
/// <item><description>A maximum response byte count enforced while streaming, not after a full buffered
/// download: a decompression bomb must not be allowed to fully materialize first.</description></item>
/// <item><description>A timeout on the whole fetch.</description></item>
/// </list>
/// <para>Declaration only (0d leaf brief): no implementation here. L2 (author display names) ships without
/// avatars; this interface, its implementation, and wiring it into the row view are L6.</para>
/// </summary>
public interface IRemoteImageLoader
{
    /// <summary>Downloads and disk-caches the image at <paramref name="url"/>, returning the local file path
    /// to read it from. Returns null -- never throws for a content-level failure -- when the URL is rejected,
    /// the response exceeds the byte cap, the fetch times out, or the content does not decode as an image: an
    /// avatar is decoration, and a failure to load one must not surface as an error to the user.</summary>
    Task<string?> GetOrFetchAsync(string url, CancellationToken cancellationToken = default);
}
