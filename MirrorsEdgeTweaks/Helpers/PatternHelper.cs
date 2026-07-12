namespace MirrorsEdgeTweaks.Helpers
{
    // Canonical byte-pattern scanner for the whole patching layer. All FindPattern-style helpers
    // delegate here; the search itself uses span IndexOf, which is vectorised by the runtime.
    public static class PatternHelper
    {
        // Returns the index of the first occurrence of pattern within data[start..endExclusive),
        // or -1 when absent. endExclusive of -1 means "to the end of data". Bounds are clamped,
        // so callers may pass generous windows.
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

        // Enumerates every (possibly overlapping) occurrence of pattern within
        // data[start..endExclusive), in ascending order.
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

        // Requires the pattern to occur exactly once in the window: returns its index, -1 when
        // absent, and throws when ambiguous (patching an ambiguous site risks corrupting the file).
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
