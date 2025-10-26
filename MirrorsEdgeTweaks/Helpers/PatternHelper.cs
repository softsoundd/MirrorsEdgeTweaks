using System.Linq;

namespace MirrorsEdgeTweaks.Helpers
{
    public static class PatternHelper
    {
        public static int FindPattern(byte[] source, byte[] pattern)
        {
            for (int i = 0; i < source.Length - pattern.Length + 1; i++)
            {
                if (source.Skip(i).Take(pattern.Length).SequenceEqual(pattern))
                {
                    return i;
                }
            }
            return -1;
        }
    }
}
