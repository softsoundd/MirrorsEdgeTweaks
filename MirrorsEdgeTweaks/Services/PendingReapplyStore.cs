using MirrorsEdgeTweaks.Helpers;
using System.IO;
using System.Text.Json;

namespace MirrorsEdgeTweaks.Services
{
    // Keeps each game directory's tweak snapshot while the user verifies their game files.
    public class PendingReapplyStore
    {
        private readonly string _path;

        public PendingReapplyStore()
            : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MirrorsEdgeTweaks", "pending-reapply.json"))
        {
        }

        internal PendingReapplyStore(string path) => _path = path;

        public TweakSnapshot? Load(string gameDirectoryPath) =>
            ReadAll().TryGetValue(Key(gameDirectoryPath), out TweakSnapshot? snapshot) ? snapshot : null;

        public void Save(string gameDirectoryPath, TweakSnapshot snapshot)
        {
            var all = ReadAll();
            all[Key(gameDirectoryPath)] = snapshot;
            WriteAll(all);
        }

        public void Clear(string gameDirectoryPath)
        {
            var all = ReadAll();
            if (all.Remove(Key(gameDirectoryPath)))
                WriteAll(all);
        }

        // An unreadable file counts as empty, so it never blocks startup.
        private Dictionary<string, TweakSnapshot> ReadAll()
        {
            try
            {
                if (File.Exists(_path)
                    && JsonSerializer.Deserialize<Dictionary<string, TweakSnapshot>>(File.ReadAllText(_path)) is { } all)
                    return new Dictionary<string, TweakSnapshot>(all, StringComparer.OrdinalIgnoreCase);
            }
            catch { }
            return new Dictionary<string, TweakSnapshot>(StringComparer.OrdinalIgnoreCase);
        }

        private void WriteAll(Dictionary<string, TweakSnapshot> all)
        {
            if (all.Count == 0)
            {
                if (File.Exists(_path))
                    File.Delete(_path);
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(all));
        }

        private static string Key(string gameDirectoryPath) =>
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(gameDirectoryPath));
    }
}
