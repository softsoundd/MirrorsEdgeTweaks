using MirrorsEdgeTweaks.Services;
using MirrorsEdgeTweaks.Tests.TestSupport;
using System.Security.Cryptography;

namespace MirrorsEdgeTweaks.Tests
{
    // The decompressor of LZO 2.10 returns the same output for every stream that these tests decompress.
    public class Lzo1xDecoderTests
    {
        private static readonly byte[] EndMarker = { 0x11, 0x00, 0x00 };

        public static TheoryData<string> SampleNames() => new(Lzo1xSamples.All.Select(sample => sample.Name));

        [Theory]
        [MemberData(nameof(SampleNames))]
        public void Decompress_RestoresWhatTheLzoLibraryCompressed(string name)
        {
            Lzo1xSample sample = Lzo1xSamples.Get(name);

            byte[] decompressed = Lzo1xDecoder.Decompress(sample.Compressed, sample.Length);

            Assert.Equal(sample.Length, decompressed.Length);
            Assert.Equal(sample.Sha256, Convert.ToHexString(SHA256.HashData(decompressed)));
        }

        [Fact]
        public void Decompress_ReturnsOnlyTheBytesTheDataHolds()
        {
            Lzo1xSample sample = Lzo1xSamples.All[0];

            byte[] decompressed = Lzo1xDecoder.Decompress(sample.Compressed, sample.Length + 1000);

            Assert.Equal(sample.Length, decompressed.Length);
        }

        [Theory]
        [InlineData("110000", "")]
        [InlineData("14414243110000", "414243")]
        [InlineData("0248454C4C4F110000", "48454C4C4F")]
        [InlineData("1241C000110000", "4141414141414141")]
        public void Decompress_ReadsHandWrittenData(string compressedHex, string expectedHex)
        {
            byte[] expected = Convert.FromHexString(expectedHex);

            Assert.Equal(expected, Lzo1xDecoder.Decompress(Convert.FromHexString(compressedHex), expected.Length));
        }

        [Fact]
        public void Decompress_ReadsALiteralRunWithAMultiByteLength()
        {
            byte[] literals = RandomBytes(1000, seed: 1);
            var stream = new List<byte>();
            AppendLiteralRun(stream, literals);
            stream.AddRange(EndMarker);

            // 1000 literals are stored as 0, then 255 + 255 + 255 as three zero bytes, then the remaining 217.
            Assert.Equal(new byte[] { 0x00, 0x00, 0x00, 0x00, 217 }, stream.Take(5));
            Assert.Equal(literals, Lzo1xDecoder.Decompress(stream.ToArray(), literals.Length));
        }

        [Fact]
        public void Decompress_CopiesTwoBytes_WhenAnInstructionBelow16FollowsOneToThreeLiterals()
        {
            // 0x14 is three literals, then 0000DDSS with D = 1 and S = 2 copies from (H << 2) + D + 1 = 2 bytes back.
            byte[] stream = { 0x14, 0xA1, 0xA2, 0xA3, 0b0000_01_10, 0x00, 0xB1, 0xB2, 0x11, 0x00, 0x00 };

            Assert.Equal(new byte[] { 0xA1, 0xA2, 0xA3, 0xA2, 0xA3, 0xB1, 0xB2 }, Lzo1xDecoder.Decompress(stream, 7));
        }

        [Fact]
        public void Decompress_CopiesThreeBytesFromOver2KbBack_WhenAnInstructionBelow16FollowsALiteralRun()
        {
            byte[] literals = RandomBytes(3000, seed: 2);
            var stream = new List<byte>();
            AppendLiteralRun(stream, literals);

            // 0000DDSS with D = 3 and S = 1, then H = 100, copies from (H << 2) + D + 2049 = 2452 bytes back.
            stream.Add(0b0000_11_01);
            stream.Add(100);
            stream.Add(0xC1);
            stream.AddRange(EndMarker);

            byte[] expected = literals
                .Concat(literals.Skip(3000 - 2452).Take(3))
                .Append((byte)0xC1)
                .ToArray();
            Assert.Equal(expected, Lzo1xDecoder.Decompress(stream.ToArray(), expected.Length));
        }

        [Fact]
        public void Decompress_CopiesALongMatchFromUnder16KbBack()
        {
            byte[] literals = RandomBytes(10000, seed: 3);
            var stream = new List<byte>();
            AppendLiteralRun(stream, literals);

            // 001LLLLL with L = 0 takes its length from the bytes that follow: 2 + 31 + 255 + 44 = 332.
            const int distance = 9000;
            stream.AddRange(new byte[] { 0b001_00000, 0x00, 44 });
            AppendUInt16(stream, ((distance - 1) << 2) | 3);
            stream.AddRange(new byte[] { 0xD1, 0xD2, 0xD3 });
            stream.AddRange(EndMarker);

            byte[] expected = literals
                .Concat(literals.Skip(10000 - distance).Take(332))
                .Concat(new byte[] { 0xD1, 0xD2, 0xD3 })
                .ToArray();
            Assert.Equal(expected, Lzo1xDecoder.Decompress(stream.ToArray(), expected.Length));
        }

        [Theory]
        [InlineData(20000, 0b0001_0_000)]
        [InlineData(40000, 0b0001_1_000)]
        public void Decompress_CopiesALongMatchFromOver16KbBack(int distance, int instruction)
        {
            byte[] literals = RandomBytes(45000, seed: 4);
            var stream = new List<byte>();
            AppendLiteralRun(stream, literals);

            // 0001HLLL with L = 0 takes its length from the bytes that follow: 2 + 7 + 91 = 100.
            // H and the 16-bit operand hold the distance less 16 KB.
            stream.AddRange(new byte[] { (byte)instruction, 91 });
            AppendUInt16(stream, ((distance - 0x4000) & 0x3FFF) << 2);
            stream.AddRange(EndMarker);

            byte[] expected = literals
                .Concat(literals.Skip(45000 - distance).Take(100))
                .ToArray();
            Assert.Equal(expected, Lzo1xDecoder.Decompress(stream.ToArray(), expected.Length));
        }

        [Fact]
        public void Decompress_RepeatsAPattern_WhenAMatchRunsIntoTheBytesItWrites()
        {
            // 0x14 is three literals, then 1LLDDDSS with L = 3 and D = 2 copies 8 bytes from 3 bytes back.
            byte[] stream = { 0x14, 0x01, 0x02, 0x03, 0b1_11_010_00, 0x00, 0x11, 0x00, 0x00 };

            Assert.Equal(new byte[] { 1, 2, 3, 1, 2, 3, 1, 2, 3, 1, 2 }, Lzo1xDecoder.Decompress(stream, 11));
        }

        [Fact]
        public void Decompress_RejectsEmptyData()
        {
            Assert.Throws<InvalidDataException>(() => Lzo1xDecoder.Decompress(ReadOnlySpan<byte>.Empty, 16));
        }

        [Theory]
        [MemberData(nameof(SampleNames))]
        public void Decompress_RejectsDataThatIsCutShort(string name)
        {
            Lzo1xSample sample = Lzo1xSamples.Get(name);
            byte[] compressed = sample.Compressed;

            for (int length = 0; length < compressed.Length; length++)
            {
                byte[] cut = compressed[..length];
                Assert.Throws<InvalidDataException>(() => Lzo1xDecoder.Decompress(cut, sample.Length));
            }
        }

        [Fact]
        public void Decompress_RejectsBytesAfterTheEndMarker()
        {
            byte[] stream = { 0x14, 0x41, 0x42, 0x43, 0x11, 0x00, 0x00, 0x00 };

            Assert.Throws<InvalidDataException>(() => Lzo1xDecoder.Decompress(stream, 16));
        }

        [Theory]
        [MemberData(nameof(SampleNames))]
        public void Decompress_RejectsDataThatHoldsMoreThanTheMaximum(string name)
        {
            Lzo1xSample sample = Lzo1xSamples.Get(name);

            Assert.Throws<InvalidDataException>(() => Lzo1xDecoder.Decompress(sample.Compressed, sample.Length - 1));
        }

        [Fact]
        public void Decompress_RejectsAMatchFromBeforeTheStartOfTheOutput()
        {
            // 0x12 is one literal, then 01LDDDSS copies 3 bytes from 2 bytes back, where only 1 byte exists.
            byte[] stream = { 0x12, 0xA1, 0b01_0_001_00, 0x00, 0x11, 0x00, 0x00 };

            Assert.Throws<InvalidDataException>(() => Lzo1xDecoder.Decompress(stream, 16));
        }

        [Fact]
        public void Decompress_RejectsALengthThatWouldOverflow()
        {
            // Each zero byte after a 001LLLLL instruction adds 255 to its length.
            // 16,843,009 of them add one less than 2^32, which would wrap a 32-bit length around to a match of 33 bytes.
            const int zeroBytes = 16_843_009;
            byte[] start = { 0x14, 0xA1, 0xA2, 0xA3, 0b001_00000 };
            byte[] end = { 0x01, 0x00, 0x00, 0x11, 0x00, 0x00 };
            byte[] stream = new byte[start.Length + zeroBytes + end.Length];
            start.CopyTo(stream, 0);
            end.CopyTo(stream, start.Length + zeroBytes);

            Assert.Throws<InvalidDataException>(() => Lzo1xDecoder.Decompress(stream, 1000));
        }

        // A changed byte may still leave a valid stream, so only the kind of failure is checked.
        [Theory]
        [MemberData(nameof(SampleNames))]
        public void Decompress_ThrowsOnlyInvalidDataException_WhenAByteIsChanged(string name)
        {
            Lzo1xSample sample = Lzo1xSamples.Get(name);
            byte[] damaged = sample.Compressed;

            for (int index = 0; index < damaged.Length; index++)
            {
                byte original = damaged[index];
                foreach (byte value in new byte[] { 0x00, 0xFF, (byte)(original ^ 0x55) })
                {
                    damaged[index] = value;
                    Exception? failure = Record.Exception(() => Lzo1xDecoder.Decompress(damaged, sample.Length));
                    Assert.True(failure is null or InvalidDataException, $"Byte {index} set to 0x{value:X2} threw {failure}");
                }

                damaged[index] = original;
            }
        }

        private static void AppendLiteralRun(List<byte> stream, byte[] literals)
        {
            int length = literals.Length - 3;
            if (length <= 15)
            {
                stream.Add((byte)length);
            }
            else
            {
                stream.Add(0x00);
                length -= 15;
                for (; length > 255; length -= 255)
                    stream.Add(0x00);
                stream.Add((byte)length);
            }

            stream.AddRange(literals);
        }

        private static void AppendUInt16(List<byte> stream, int value)
        {
            stream.Add((byte)value);
            stream.Add((byte)(value >> 8));
        }

        private static byte[] RandomBytes(int length, int seed)
        {
            byte[] bytes = new byte[length];
            new Random(seed).NextBytes(bytes);
            return bytes;
        }
    }
}
