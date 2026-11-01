using System.IO;

namespace MirrorsEdgeTweaks.Helpers
{
    // Softimer runs each line of TweaksScriptsSettings as a console command, in order.
    public static class TweaksScriptsSettingsHelper
    {
        public static string GetPath(string gameDirectoryPath) =>
            Path.Combine(gameDirectoryPath, "Binaries", "TweaksScriptsSettings");

        public static Dictionary<string, string> ReadValues(IEnumerable<string> lines)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string line in lines)
            {
                if (TryParseLine(line, out string key, out string value))
                    values[key] = value;
            }

            return values;
        }

        // Rewrites every line for the key, because only the last one takes effect.
        public static List<string> SetValue(IEnumerable<string> lines, string key, string value)
        {
            string newLine = FormatLine(key, value);
            var result = new List<string>();
            bool found = false;

            foreach (string line in lines)
            {
                if (TryParseLine(line, out string lineKey, out _) && lineKey.Equals(key, StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(newLine);
                    found = true;
                }
                else
                {
                    result.Add(line);
                }
            }

            if (!found)
                result.Add(newLine);

            return result;
        }

        // Copies the values in previousLines onto matching keys in lines.
        // Drops keys that only previousLines has.
        public static List<string> MergeValues(IEnumerable<string> lines, IEnumerable<string> previousLines)
        {
            Dictionary<string, string> previousValues = ReadValues(previousLines);
            return lines
                .Select(line => TryParseLine(line, out string key, out _) && previousValues.TryGetValue(key, out string? value)
                    ? FormatLine(key, value)
                    : line)
                .ToList();
        }

        private static string FormatLine(string key, string value) =>
            value.Length == 0 ? key : $"{key} {value}";

        private static bool TryParseLine(string line, out string key, out string value)
        {
            string[] parts = line.Trim().Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0 || parts[0].StartsWith("//", StringComparison.Ordinal))
            {
                key = value = string.Empty;
                return false;
            }

            key = parts[0];
            value = parts.Length > 1 ? parts[1].Trim() : string.Empty;
            return true;
        }
    }
}
