namespace MirrorsEdgeTweaks.Helpers
{
    // Canonical byte-pattern scanner for the whole patching layer. All FindPattern-style helpers
    // delegate here; the search itself uses span IndexOf, which is vectorised by the runtime.
    public static class PatternHelper
    {
        // endExclusive of -1 means "to the end of data". Bounds are clamped, so callers may pass
        // generous windows.
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

        // Matches may overlap (each match advances the scan by one byte, not by pattern length).
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

        // Throws when the pattern occurs more than once: patching an ambiguous site risks
        // corrupting the file. Returns -1 when absent.
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
