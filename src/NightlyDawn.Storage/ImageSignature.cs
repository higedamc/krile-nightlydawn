namespace NightlyDawn.Storage;

/// <summary>
/// Recognises the container signature ("magic bytes") of the image formats this client is willing to cache.
///
/// <para><b>What this is and is not.</b> It is an allowlist of container types, checked against the first bytes
/// of a response so that a relay-supplied URL serving HTML, a script, an executable or a zip never lands in the
/// avatar cache. It is <b>not</b> a decode: nothing here parses pixel data, so it cannot tell a valid PNG from a
/// PNG with a corrupt IDAT chunk, and it is not a defence against a decoder bug. Decoding happens later, in the
/// App layer, with Avalonia's decoder; that call must tolerate failure too (see
/// <see cref="RemoteImageLoader"/>'s remarks).</para>
///
/// <para>Deliberately narrow: no SVG. SVG is XML with scripting and external-reference semantics, which makes it
/// an attack surface rather than an image, and Avalonia's <c>Bitmap</c> cannot decode it anyway.</para>
/// </summary>
public static class ImageSignature
{
    /// <summary>Longest prefix any check below needs. A response shorter than this cannot be identified and is
    /// rejected -- 12 bytes of "image" is not an image.</summary>
    public const int MaximumPrefixLength = 12;

    /// <summary>True when <paramref name="prefix"/> starts with the signature of a format we accept.</summary>
    public static bool IsRecognised(ReadOnlySpan<byte> prefix) =>
        IsPng(prefix) || IsJpeg(prefix) || IsGif(prefix) || IsWebp(prefix);

    // PNG: the 8-byte signature from the spec. The 0x89 and the CR/LF/EOF bytes are there to catch transport
    // mangling, so checking all eight is cheap and stricter than checking "PNG".
    private static bool IsPng(ReadOnlySpan<byte> p) =>
        p.Length >= 8 && p[0] == 0x89 && p[1] == 0x50 && p[2] == 0x4E && p[3] == 0x47
                      && p[4] == 0x0D && p[5] == 0x0A && p[6] == 0x1A && p[7] == 0x0A;

    // JPEG: SOI marker, then any marker byte. JFIF/Exif/raw all share this start.
    private static bool IsJpeg(ReadOnlySpan<byte> p) =>
        p.Length >= 3 && p[0] == 0xFF && p[1] == 0xD8 && p[2] == 0xFF;

    // GIF: "GIF87a" or "GIF89a".
    private static bool IsGif(ReadOnlySpan<byte> p) =>
        p.Length >= 6 && p[0] == (byte)'G' && p[1] == (byte)'I' && p[2] == (byte)'F' && p[3] == (byte)'8'
                      && (p[4] == (byte)'7' || p[4] == (byte)'9') && p[5] == (byte)'a';

    // WebP: a RIFF container whose form type is "WEBP" at offset 8. The 4 length bytes in between are not
    // checked against the body length -- that is the decoder's job, and a mismatch is a corrupt image, not a
    // different container.
    private static bool IsWebp(ReadOnlySpan<byte> p) =>
        p.Length >= 12 && p[0] == (byte)'R' && p[1] == (byte)'I' && p[2] == (byte)'F' && p[3] == (byte)'F'
                       && p[8] == (byte)'W' && p[9] == (byte)'E' && p[10] == (byte)'B' && p[11] == (byte)'P';
}
