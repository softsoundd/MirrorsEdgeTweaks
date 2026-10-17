namespace MirrorsEdgeTweaks.Helpers
{
    // Matches bytes with optional wildcards, so a locator can skip per-build absolute addresses
    // and the bytes its own detour overwrites.
    // A plain byte[] converts to a pattern without wildcards.
    internal readonly struct BytePattern
    {
        private readonly byte[] _bytes;
        // False marks a wildcard position.
        private readonly bool[] _fixed;

        private BytePattern(byte[] bytes, bool[] fixedMask)
        {
            _bytes = bytes;
            _fixed = fixedMask;
        }

        public int Length => _bytes.Length;

        public static implicit operator BytePattern(byte[] bytes)
        {
            var fixedMask = new bool[bytes.Length];
            Array.Fill(fixedMask, true);
            return new BytePattern(bytes, fixedMask);
        }

        // Parses whitespace-separated hex bytes such as "8B 06 ?? FF", where "??" is a wildcard.
        public static BytePattern Parse(string spec)
        {
            string[] tokens = spec.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            var bytes = new byte[tokens.Length];
            var fixedMask = new bool[tokens.Length];
            for (int i = 0; i < tokens.Length; i++)
            {
                if (tokens[i] == "??")
                    continue;
                bytes[i] = Convert.ToByte(tokens[i], 16);
                fixedMask[i] = true;
            }
            return new BytePattern(bytes, fixedMask);
        }

        public bool MatchesAt(byte[] data, int at)
        {
            for (int j = 0; j < _bytes.Length; j++)
                if (_fixed[j] && data[at + j] != _bytes[j]) return false;
            return true;
        }

        public int IndexOf(byte[] data, int start, int end)
        {
            int limit = end - Length;
            for (int i = start; i <= limit; i++)
                if (MatchesAt(data, i)) return i;
            return -1;
        }

        // Returns the file offset of the only match in the image's .text section.
        // Returns -1 when the pattern is missing or occurs more than once.
        public int FindUniqueInText(byte[] data, PeImageLayout pe)
        {
            var text = pe.FindSectionByName(".text");
            if (text == null) return -1;
            int start = (int)text.PointerToRawData;
            int end = Math.Min(start + (int)text.SizeOfRawData, data.Length);

            int p = IndexOf(data, start, end);
            if (p < 0 || IndexOf(data, p + 1, end) >= 0) return -1;
            return p;
        }
    }
}
