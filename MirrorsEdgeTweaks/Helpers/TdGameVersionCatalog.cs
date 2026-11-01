namespace MirrorsEdgeTweaks.Helpers
{
    // The order must match the ComboBox items in the Game Tweaks tab.
    public static class TdGameVersionCatalog
    {
        public static readonly IReadOnlyList<string> Names = new[]
        {
            "Original",
            "TdGame Fix (by Keku)",
            "Time Trials Timer Fix (by Nulaft)",
            "TdGame Fix + Time Trials Timer Fix"
        };

        public static int IndexOf(string? versionName)
        {
            if (string.IsNullOrEmpty(versionName))
                return -1;

            for (int i = 0; i < Names.Count; i++)
            {
                if (Names[i].Equals(versionName, StringComparison.Ordinal))
                    return i;
            }
            return -1;
        }

        public static string? NameAt(int index) =>
            index >= 0 && index < Names.Count ? Names[index] : null;
    }
}
