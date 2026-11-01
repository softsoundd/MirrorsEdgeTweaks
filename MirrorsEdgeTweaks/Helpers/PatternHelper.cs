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
