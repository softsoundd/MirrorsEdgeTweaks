using System.IO;

namespace MirrorsEdgeTweaks.Services
{
    // Decompresses LZO1X data, the format of Markus F. X. J. Oberhumer's LZO library that UE3 packages use.
    // The instruction layout follows the Linux kernel's description at https://docs.kernel.org/staging/lzo.html.
    internal static class Lzo1xDecoder
    {
        // The count that stands for four or more literals.
        private const int LongLiteralRun = 4;

        // No valid length is longer, and the limit keeps the length arithmetic inside an int.
        private const int MaxEncodedLength = 0x7FFF0000;

        public static byte[] Decompress(ReadOnlySpan<byte> source, int maxLength)
        {
            byte[] output = new byte[maxLength];
            int inputPosition = 0;
            int outputPosition = 0;
            int literalsBefore = 0;

            if (source.Length > 0 && source[0] > 17)
            {
                int count = source[0] - 17;
                inputPosition = 1;
                CopyLiterals(source, ref inputPosition, output, ref outputPosition, count);
                literalsBefore = Math.Min(count, LongLiteralRun);
            }

            while (true)
            {
                int instruction = ReadByte(source, ref inputPosition);
                int matchLength;
                int distance;
                int literalsAfter;

                if (instruction >= 64)
                {
                    matchLength = (instruction >> 5) + 1;
                    distance = (ReadByte(source, ref inputPosition) << 3) + ((instruction >> 2) & 7) + 1;
                    literalsAfter = instruction & 3;
                }
                else if (instruction >= 32)
                {
                    matchLength = ReadLength(source, ref inputPosition, instruction & 31, 31) + 2;
                    int operand = ReadUInt16(source, ref inputPosition);
                    distance = (operand >> 2) + 1;
                    literalsAfter = operand & 3;
                }
                else if (instruction >= 16)
                {
                    matchLength = ReadLength(source, ref inputPosition, instruction & 7, 7) + 2;
                    int operand = ReadUInt16(source, ref inputPosition);
                    distance = ((instruction & 8) << 11) + (operand >> 2);
                    if (distance == 0)
                        break;

                    distance += 0x4000;
                    literalsAfter = operand & 3;
                }
                else if (literalsBefore == 0)
                {
                    int count = ReadLength(source, ref inputPosition, instruction, 15) + 3;
                    CopyLiterals(source, ref inputPosition, output, ref outputPosition, count);
                    literalsBefore = LongLiteralRun;
                    continue;
                }
                else
                {
                    bool afterLongRun = literalsBefore == LongLiteralRun;
                    matchLength = afterLongRun ? 3 : 2;
                    distance = (ReadByte(source, ref inputPosition) << 2) + (instruction >> 2) + (afterLongRun ? 2049 : 1);
                    literalsAfter = instruction & 3;
                }

                CopyMatch(output, ref outputPosition, distance, matchLength);
                CopyLiterals(source, ref inputPosition, output, ref outputPosition, literalsAfter);
                literalsBefore = literalsAfter;
            }

            if (inputPosition != source.Length)
                throw new InvalidDataException("The LZO data continues after its end marker.");

            if (outputPosition != output.Length)
                Array.Resize(ref output, outputPosition);
            return output;
        }

        private static int ReadByte(ReadOnlySpan<byte> source, ref int position)
        {
            if (position >= source.Length)
                throw new InvalidDataException("The LZO data ends before its end marker.");

            return source[position++];
        }

        private static int ReadUInt16(ReadOnlySpan<byte> source, ref int position)
        {
            if (source.Length - position < 2)
                throw new InvalidDataException("The LZO data ends before its end marker.");

            int value = source[position] | (source[position + 1] << 8);
            position += 2;
            return value;
        }

        private static int ReadLength(ReadOnlySpan<byte> source, ref int position, int bits, int largestInBits)
        {
            if (bits != 0)
                return bits;

            int length = largestInBits;
            int next;
            while ((next = ReadByte(source, ref position)) == 0)
            {
                length += 255;
                if (length > MaxEncodedLength)
                    throw new InvalidDataException("The LZO data holds a length that is too long.");
            }

            return length + next;
        }

        private static void CopyLiterals(ReadOnlySpan<byte> source, ref int inputPosition, byte[] output, ref int outputPosition, int count)
        {
            if (count > source.Length - inputPosition)
                throw new InvalidDataException("The LZO data ends before its end marker.");
            if (count > output.Length - outputPosition)
                throw new InvalidDataException("The LZO data decompresses to more than the expected size.");

            source.Slice(inputPosition, count).CopyTo(output.AsSpan(outputPosition));
            inputPosition += count;
            outputPosition += count;
        }

        private static void CopyMatch(byte[] output, ref int outputPosition, int distance, int length)
        {
            if (distance > outputPosition)
                throw new InvalidDataException("The LZO data copies from before the start of its output.");
            if (length > output.Length - outputPosition)
                throw new InvalidDataException("The LZO data decompresses to more than the expected size.");

            int matchPosition = outputPosition - distance;
            if (distance >= length)
            {
                output.AsSpan(matchPosition, length).CopyTo(output.AsSpan(outputPosition));
            }
            else
            {
                // The match runs into the bytes it writes, which repeats the last `distance` bytes.
                for (int i = 0; i < length; i++)
                    output[outputPosition + i] = output[matchPosition + i];
            }

            outputPosition += length;
        }
    }
}
