using System.Text;
using Xunit;

namespace NightlyDawn.Storage.Tests;

public sealed class ImageSignatureTests
{
    [Theory]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })]                 // PNG
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 })]                                          // JPEG/JFIF
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE1 })]                                          // JPEG/Exif
    [InlineData(new byte[] { 0x47, 0x49, 0x46, 0x38, 0x37, 0x61 })]                              // GIF87a
    [InlineData(new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61 })]                              // GIF89a
    [InlineData(new byte[] { 0x52, 0x49, 0x46, 0x46, 1, 2, 3, 4, 0x57, 0x45, 0x42, 0x50 })]      // RIFF....WEBP
    public void RecognisesTheFormatsWeAccept(byte[] prefix) =>
        Assert.True(ImageSignature.IsRecognised(prefix));

    [Theory]
    [InlineData("<html><body>hi</body></html>")]
    [InlineData("<!DOCTYPE html>")]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>")] // SVG is XML with scripting, not an image we accept
    [InlineData("#!/bin/sh\nrm -rf /\n")]
    [InlineData("{\"not\":\"an image\"}")]
    public void RejectsTextualPayloads(string body) =>
        Assert.False(ImageSignature.IsRecognised(Encoding.UTF8.GetBytes(body)));

    [Theory]
    [InlineData(new byte[] { 0x7F, 0x45, 0x4C, 0x46 })]                            // ELF
    [InlineData(new byte[] { 0x4D, 0x5A })]                                        // PE/DOS
    [InlineData(new byte[] { 0x50, 0x4B, 0x03, 0x04 })]                            // ZIP (and every zip-based format)
    [InlineData(new byte[] { 0x1F, 0x8B, 0x08 })]                                  // gzip
    [InlineData(new byte[] { 0xCF, 0xFA, 0xED, 0xFE })]                            // Mach-O
    public void RejectsExecutablesAndArchives(byte[] prefix) =>
        Assert.False(ImageSignature.IsRecognised(prefix));

    /// <summary>A prefix shorter than a signature must be rejected, not treated as "could still turn out fine
    /// later": the loader decides on the prefix alone, so a lenient answer here would admit the whole body.</summary>
    [Theory]
    [InlineData(new byte[] { })]
    [InlineData(new byte[] { 0x89 })]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E })]                                   // PNG's first three bytes only
    [InlineData(new byte[] { 0xFF, 0xD8 })]                                         // JPEG SOI without the next marker
    [InlineData(new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39 })]                       // "GIF89" without the 'a'
    [InlineData(new byte[] { 0x52, 0x49, 0x46, 0x46, 1, 2, 3, 4, 0x57, 0x41, 0x56 })] // RIFF that is a WAV, not a WEBP
    public void RejectsAnythingItCannotFullyIdentify(byte[] prefix) =>
        Assert.False(ImageSignature.IsRecognised(prefix));

    /// <summary>The loader reads exactly this many bytes before deciding, so the longest signature must fit.</summary>
    [Fact]
    public void ThePrefixLengthCoversTheLongestSignature() =>
        Assert.True(ImageSignature.MaximumPrefixLength >= 12);
}
