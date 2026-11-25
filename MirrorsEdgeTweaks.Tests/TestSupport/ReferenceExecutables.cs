namespace MirrorsEdgeTweaks.Tests.TestSupport
{
    // Finds the stock executables in the gitignored ref folder.
    public static class ReferenceExecutables
    {
        // Returns null when the ref folder or the executable is missing.
        public static string? Find(string relativePath)
        {
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
            {
                string candidate = Path.Combine(directory.FullName, "ref", "Mirrors-Edge-Version-KB", relativePath);
                if (File.Exists(candidate))
                    return candidate;
            }
            return null;
        }
    }
}
