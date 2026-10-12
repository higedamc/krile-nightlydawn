using System.Buffers.Binary;
using System.IO.Compression;
using Avalonia.Headless.XUnit;
using NightlyDawn.App.Avatars;
using NightlyDawn.App.RenderTests.Fakes;
using Xunit;

namespace NightlyDawn.App.RenderTests;

/// <summary>
/// L6b-2 (plan §27): the promise of <see cref="AvatarImageCache.Decode"/> and <see cref="AvatarImageCache.GetAsync"/>
/// -- null, never an exception, never a multi-gigabyte allocation, for any bytes the remote end chooses --
/// measured against eight files that all pass the loader's checks (scheme, address, 2 MiB, 12-byte container
/// signature) and go wrong only past that point. The files are written here from crafted headers, not read
/// from disk or from a fixture directory, so the test is the whole specification.
///
/// <para>The review found the shipped <c>Decode</c> throwing bare <see cref="Exception"/> on a 794-byte GIF
/// declaring 65535 x 65535 ("Unable to allocate pixels"), and allocating 2 GB of working set for a 194 KB PNG
/// declaring 40000 x 40000 (§27.2-27.3). A named catch list cannot fix either: the decoder's exception types
/// for hostile input were measured at three (§27.4). The fix is a header gate (<c>SKCodec</c>, no pixels
/// allocated) before the decoder plus a total catch around the decoder calls alone. These tests assert the
/// promise, not the exception types.</para>
///
/// <para>Mutation run by hand before commit (result in the PR body): removing the
/// <c>HeaderFitsAnAvatar</c> call from <c>Decode</c> turns <c>bomb.png</c> and <c>bomb2.png</c> red in
/// <see cref="Decode_ReturnsNull_ForEveryHostileFixture_WithoutGrowingTheWorkingSet"/> (the bombs decode,
/// gigabytes later, to 96 x 96 bitmaps instead of null, and the first one to run grows the working set by
/// about 2 GB) and the same two red in <see cref="GetAsync_ReturnsNull_ForEveryHostileFixture"/>.</para>
/// </summary>
public sealed class AvatarDecodeFixtureTests
{
    /// <summary>Written once per process; a few hundred KB under the temp directory, removed at exit.</summary>
    private static readonly Lazy<Fixtures> Shared = new(Fixtures.Write);

    public static TheoryData<string> HostileFixtureNames => new(Fixtures.HostileNames);

    [AvaloniaFact]
    public void Decode_OfTheControl_ReturnsTheScaledBitmap()
    {
        // Positive control: proves the decoder actually ran in this process. Without it every "null" below
        // could be the total catch hiding a missing render platform.
        using var bitmap = AvatarImageCache.Decode(Shared.Value.Path("ok.png"));

        Assert.NotNull(bitmap);
        Assert.Equal(AvatarImageCache.DecodeWidth, bitmap!.PixelSize.Width);
        Assert.Equal(AvatarImageCache.DecodeWidth, bitmap.PixelSize.Height);
    }

    [AvaloniaTheory]
    [MemberData(nameof(HostileFixtureNames))]
    public void Decode_ReturnsNull_ForEveryHostileFixture_WithoutGrowingTheWorkingSet(string name)
    {
        // §27.3 measured bomb2 at +2,036 MB of working set in the shipped code. The gate reads the header and
        // stops; the budget is noise room for the runtime, an order of magnitude under the smallest bomb. The
        // working set is process-wide, so only the first bomb decoded in a run measures a true delta -- which
        // is why the assertion lives in every case rather than in one test whose place in the order would
        // decide whether it can see anything.
        const long Budget = 128L * 1024 * 1024;

        var before = Environment.WorkingSet;
        var bitmap = AvatarImageCache.Decode(Shared.Value.Path(name));
        var after = Environment.WorkingSet;

        Assert.True(bitmap is null, $"{name} ({Shared.Value.Describe(name)}) decoded to a {bitmap?.PixelSize.Width}x{bitmap?.PixelSize.Height} bitmap; the header gate should have refused it");
        Assert.True(after - before < Budget,
            $"{name} ({Shared.Value.Describe(name)}) grew the working set by {(after - before) / (1024 * 1024)} MB; the decoder was reached");
    }

    [AvaloniaTheory]
    [MemberData(nameof(HostileFixtureNames))]
    public async Task GetAsync_ReturnsNull_ForEveryHostileFixture(string name)
    {
        // The public promise ("never throws for anything the remote end controls"), through the real cache
        // with a loader that hands back the fixture's path -- exactly what the disk cache does in production.
        var loader = new CountingRemoteImageLoader { Resolve = _ => Shared.Value.Path(name) };
        var cache = new AvatarImageCache(() => loader);

        var bitmap = await cache.GetAsync("https://cdn.example/" + name);

        Assert.Null(bitmap);
        Assert.Equal(0, cache.InFlightCount);
        Assert.Equal(1, cache.Count); // settled as "no avatar", so the next row does not ask again
    }

    [AvaloniaFact]
    public async Task GetAsync_OfTheControl_ReturnsTheBitmap()
    {
        var loader = new CountingRemoteImageLoader { Resolve = _ => Shared.Value.Path("ok.png") };
        var cache = new AvatarImageCache(() => loader);

        var bitmap = await cache.GetAsync("https://cdn.example/ok.png");

        Assert.NotNull(bitmap);
    }

    [Fact]
    public void FitsAnAvatar_IsTheShapeRule()
    {
        Assert.True(AvatarImageCache.FitsAnAvatar(8, 8));
        Assert.True(AvatarImageCache.FitsAnAvatar(AvatarImageCache.MaximumSourceDimension, AvatarImageCache.MaximumSourceDimension));
        Assert.False(AvatarImageCache.FitsAnAvatar(AvatarImageCache.MaximumSourceDimension + 1, 8));
        Assert.False(AvatarImageCache.FitsAnAvatar(8, AvatarImageCache.MaximumSourceDimension + 1));
        Assert.False(AvatarImageCache.FitsAnAvatar(0, 8));
        Assert.False(AvatarImageCache.FitsAnAvatar(8, 0));
        Assert.False(AvatarImageCache.FitsAnAvatar(2, 30)); // 1:15 portrait: scaled height 1440 > 384
        Assert.False(AvatarImageCache.FitsAnAvatar(3000, 2)); // scaled height rounds to 0
        Assert.True(AvatarImageCache.FitsAnAvatar(96, 384)); // exactly the tallest retained shape
    }

    [Fact]
    public void TheFixtures_PassTheLoadersContainerCheck()
    {
        // Each file starts with a signature the real loader accepts; otherwise the loader would have refused
        // it and these tests would be measuring a path production never takes.
        foreach (var name in Fixtures.HostileNames.Append("ok.png"))
        {
            var bytes = File.ReadAllBytes(Shared.Value.Path(name));
            Assert.True(bytes.Length >= Storage.ImageSignature.MaximumPrefixLength, name);
            Assert.True(Storage.ImageSignature.IsRecognised(bytes.AsSpan(0, Storage.ImageSignature.MaximumPrefixLength)), $"{name} would not pass the loader's signature check");
            Assert.True(bytes.Length <= 2 * 1024 * 1024, $"{name} is over the loader's 2 MiB cap");
        }
    }

    /// <summary>The eight files of plan §27.2, written from the specification of each container: a real
    /// PNG encoder (IHDR/IDAT/IEND with correct CRCs and a valid zlib stream of black pixels) for the PNGs, a
    /// minimal GIF89a (logical screen + one 1x1 frame) for the GIFs. The "bombs" are valid images: a decoder
    /// that reaches them really does allocate width x height x 4 bytes.</summary>
    private sealed class Fixtures
    {
        public static readonly string[] HostileNames =
        [
            "wide.png", // 30000 x 2: within no dimension limit of the old code, but not an avatar's shape
            "tall.png", // 2 x 30000
            "corrupt.png", // IHDR declaring 823703999 x 3502751793, then junk: SKCodec.Create returns null
            "bomb.png", // 20000 x 20000, 1-bit grey: 1.5 GB of pixels if decoded
            "bomb2.png", // 40000 x 40000, 1-bit grey: 6.4 GB of pixels if decoded (§27.3: +2 GB working set)
            "corrupt.gif", // 45925 x 14044 logical screen: the decoder threw bare System.Exception
            "hostile.gif", // 65535 x 65535 logical screen: 16 GB; the decoder threw bare System.Exception
        ];

        private readonly string _directory;
        private readonly Dictionary<string, string> _descriptions = new(StringComparer.Ordinal);

        private Fixtures(string directory)
        {
            _directory = directory;
        }

        public string Path(string name) => System.IO.Path.Combine(_directory, name);

        public string Describe(string name) => _descriptions[name];

        public static Fixtures Write()
        {
            var directory = Directory.CreateTempSubdirectory("nightlydawn-avatar-fixtures").FullName;
            AppDomain.CurrentDomain.ProcessExit += (_, _) =>
            {
                try
                {
                    Directory.Delete(directory, recursive: true);
                }
                catch (IOException)
                {
                    // Temp directory; the OS reclaims it.
                }
            };

            var fixtures = new Fixtures(directory);
            fixtures.WritePng("ok.png", 8, 8);
            fixtures.WritePng("wide.png", 30000, 2);
            fixtures.WritePng("tall.png", 2, 30000);
            fixtures.WriteCorruptPng("corrupt.png", 823703999u, 3502751793u);
            fixtures.WritePng("bomb.png", 20000, 20000);
            fixtures.WritePng("bomb2.png", 40000, 40000);
            fixtures.WriteGif("corrupt.gif", 45925, 14044);
            fixtures.WriteGif("hostile.gif", 65535, 65535);
            return fixtures;
        }

        private void WritePng(string name, int width, int height)
        {
            using var file = File.Create(Path(name));
            file.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

            var ihdr = new byte[13];
            BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(0), (uint)width);
            BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), (uint)height);
            ihdr[8] = 1; // bit depth: 1 bit per pixel keeps the bombs well under the loader's 2 MiB cap
            ihdr[9] = 0; // colour type: greyscale
            WriteChunk(file, "IHDR", ihdr);

            using (var idat = new MemoryStream())
            {
                using (var zlib = new ZLibStream(idat, CompressionLevel.SmallestSize, leaveOpen: true))
                {
                    var row = new byte[1 + (width + 7) / 8]; // filter byte (None) + packed pixels, all black
                    for (var y = 0; y < height; y++)
                    {
                        zlib.Write(row);
                    }
                }

                WriteChunk(file, "IDAT", idat.ToArray());
            }

            WriteChunk(file, "IEND", []);
            file.Flush();
            _descriptions[name] = $"PNG {width}x{height}, {file.Length:N0} bytes";
        }

        private void WriteCorruptPng(string name, uint width, uint height)
        {
            using var file = File.Create(Path(name));
            file.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

            var ihdr = new byte[13];
            BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(0), width);
            BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), height);
            ihdr[8] = 8;
            ihdr[9] = 6;
            WriteChunk(file, "IHDR", ihdr);

            var junk = new byte[4096];
            new Random(27).NextBytes(junk); // deterministic: the same file on every run
            file.Write(junk);
            file.Flush();
            _descriptions[name] = $"PNG header {width}x{height} then junk, {file.Length:N0} bytes";
        }

        private void WriteGif(string name, int width, int height)
        {
            using var file = File.Create(Path(name));
            file.Write("GIF89a"u8);

            var screen = new byte[7];
            BinaryPrimitives.WriteUInt16LittleEndian(screen.AsSpan(0), (ushort)width);
            BinaryPrimitives.WriteUInt16LittleEndian(screen.AsSpan(2), (ushort)height);
            screen[4] = 0x80; // global colour table present, 2 entries
            file.Write(screen);
            file.Write([0x00, 0x00, 0x00, 0xFF, 0xFF, 0xFF]); // the two colours

            var image = new byte[10];
            image[0] = 0x2C;
            BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(5), 1); // one 1x1 frame at (0, 0)
            BinaryPrimitives.WriteUInt16LittleEndian(image.AsSpan(7), 1);
            file.Write(image);
            file.Write([0x02, 0x02, 0x44, 0x01, 0x00]); // LZW minimum code size 2; one sub-block: clear, pixel 0, end
            file.Write([0x3B]); // trailer
            file.Flush();
            _descriptions[name] = $"GIF logical screen {width}x{height}, {file.Length:N0} bytes";
        }

        private static void WriteChunk(Stream stream, string type, byte[] data)
        {
            var header = new byte[8];
            BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0), (uint)data.Length);
            for (var i = 0; i < 4; i++)
            {
                header[4 + i] = (byte)type[i];
            }

            stream.Write(header);
            stream.Write(data);

            var crc = Crc32(header.AsSpan(4), data);
            var trailer = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(trailer, crc);
            stream.Write(trailer);
        }

        private static readonly uint[] CrcTable = BuildCrcTable();

        private static uint[] BuildCrcTable()
        {
            var table = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                var c = n;
                for (var k = 0; k < 8; k++)
                {
                    c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                }

                table[n] = c;
            }

            return table;
        }

        private static uint Crc32(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second)
        {
            var c = 0xFFFFFFFFu;
            foreach (var b in first)
            {
                c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
            }

            foreach (var b in second)
            {
                c = CrcTable[(c ^ b) & 0xFF] ^ (c >> 8);
            }

            return c ^ 0xFFFFFFFFu;
        }
    }
}
