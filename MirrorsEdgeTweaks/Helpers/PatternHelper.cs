using System.IO;

namespace MirrorsEdgeTweaks.Helpers
{
    // The other FindPattern helpers delegate here.
    // The search uses span IndexOf, which the runtime vectorises.
    public static class PatternHelper
    {
        // An endExclusive of -1 searches to the end of data.
        // The bounds are clamped, so callers may pass windows that run past the data.
        public static int FindPattern(byte[] data, ReadOnlySpan<byte> pattern, int start = 0, int endExclusive = -1)
        {
            if (pattern.Length == 0)
                return -1;

            if (endExclusive < 0 || endExclusive > data.Length)
                endExclusive = data.Length;
            if (start < 0)
                start = 0;
            if (start + pattern.Length > endExclusive)
                return -1;

            int idx = data.AsSpan(start, endExclusive - start).IndexOf(pattern);
            return idx < 0 ? -1 : start + idx;
        }

        // Searches the file in chunks, so a large package never has to fit in one array.
        // Each chunk starts with the previous chunk's last bytes, so a match across a chunk boundary is still found.
        public static bool FileContainsAny(string path, IReadOnlyList<byte[]> patterns, int chunkSize = 64 * 1024)
        {
            if (!patterns.Any(pattern => pattern.Length > 0))
                return false;

            int overlap = patterns.Max(pattern => pattern.Length) - 1;
            byte[] buffer = new byte[overlap + chunkSize];
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 0, FileOptions.SequentialScan);

            int carried = 0;
            while (true)
            {
                int read = stream.ReadAtLeast(buffer.AsSpan(carried, chunkSize), chunkSize, throwOnEndOfStream: false);
                int filled = carried + read;
                ReadOnlySpan<byte> window = buffer.AsSpan(0, filled);
                foreach (byte[] pattern in patterns)
                {
                    if (pattern.Length > 0 && window.IndexOf(pattern) >= 0)
                        return true;
                }

                if (read < chunkSize)
                    return false;

                carried = Math.Min(overlap, filled);
                buffer.AsSpan(filled - carried, carried).CopyTo(buffer);
            }
        }

        // Matches may overlap.
        public static IEnumerable<int> FindAll(byte[] data, byte[] pattern, int start = 0, int endExclusive = -1)
        {
            int pos = start;
            while (true)
            {
                pos = FindPattern(data, pattern, pos, endExclusive);
                if (pos < 0)
                    yield break;
                yield return pos;
                pos++;
            }
        }

        // Returns the offset of the only match.
        // Returns -1 when there is no match.
        // Throws when there is more than one match, because patching an ambiguous site could corrupt the file.
        public static int FindUnique(byte[] data, byte[] pattern, int start = 0, int endExclusive = -1)
        {
            int first = FindPattern(data, pattern, start, endExclusive);
            if (first < 0)
                return -1;

            int second = FindPattern(data, pattern, first + 1, endExclusive);
            if (second >= 0)
                throw new InvalidOperationException("Byte pattern is ambiguous (found more than once) in the search window.");

            return first;
        }
    }
}
