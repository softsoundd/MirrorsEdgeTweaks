namespace MirrorsEdgeTweaks.Helpers
{
    // Single source of truth for the selectable TdGame.u variants and the mapping between a
    // version's display name and its combo index. The order must match the ComboBox items in
    // the Game Tweaks tab. Kept as a small static catalog so the index/name mapping is unit
    // testable without constructing the (WPF-bound) TdGameVersionViewModel.
    public static class TdGameVersionCatalog
    {
        public static readonly IReadOnlyList<string> Names = new[]
        {
            "Original",                          // 0
            "TdGame Fix (by Keku)",              // 1
            "Time Trials Timer Fix (by Nulaft)", // 2
            "TdGame Fix + Time Trials Timer Fix" // 3
        };

        // Returns the combo index for an exact version name, or -1 if not recognised.
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

        // Returns the version name for a combo index, or null if out of range.
        public static string? NameAt(int index) =>
            index >= 0 && index < Names.Count ? Names[index] : null;
    }
}
