using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace MirrorsEdgeTweaks.Helpers
{
    public enum DxvkState
    {
        // Binaries holds no d3d9.dll.
        Missing,
        OtherWrapper,
        // The DXVK predates 2.0, which added d3d9.useD32forD24.
        Outdated,
        Supported
    }

    // Detects, installs and removes DXVK beside MirrorsEdge.exe, and turns on its float depth.
    // DXVK's d3d9.useD32forD24 stores D24S8 surfaces as D32_SFLOAT_S8_UINT, which reversed depth needs.
    public static class DxvkHelper
    {
        public const string AssetFileName = "dxvk-d3d9.zip";

        private const string FloatDepthKey = "d3d9.useD32forD24";
        private const string FloatDepthLine = FloatDepthKey + " = True";
        private const string GameSection = "mirrorsedge.exe";
        private static readonly byte[] FloatDepthKeyBytes = Encoding.ASCII.GetBytes(FloatDepthKey);

        // DXVK reports the version of Windows' d3d9.dll, so a DXVK with d3d9.useD32forD24 is recognised by the option's name in the DLL.
        public static DxvkState GetState(string binariesDirectory)
        {
            string dll = Path.Combine(binariesDirectory, "d3d9.dll");
            if (!File.Exists(dll))
                return DxvkState.Missing;
            if (!IsDxvkProduct(FileVersionInfo.GetVersionInfo(dll).ProductName))
                return DxvkState.OtherWrapper;
            return HasFloatDepthOption(File.ReadAllBytes(dll)) ? DxvkState.Supported : DxvkState.Outdated;
        }

        internal static bool IsDxvkProduct(string? productName) =>
            string.Equals(productName?.Trim(), "DXVK", StringComparison.OrdinalIgnoreCase);

        internal static bool HasFloatDepthOption(ReadOnlySpan<byte> dll) => dll.IndexOf(FloatDepthKeyBytes) >= 0;

        // Installs only the asset's d3d9.dll, and throws rather than replace an existing one.
        public static void ExtractAssetDll(string zipPath, string binariesDirectory)
        {
            using ZipArchive archive = ZipFile.OpenRead(zipPath);
            ZipArchiveEntry entry = archive.Entries.FirstOrDefault(e => e.Name.Equals("d3d9.dll", StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException("The DXVK download does not contain d3d9.dll.");
            entry.ExtractToFile(Path.Combine(binariesDirectory, "d3d9.dll"), overwrite: false);
        }

        // Deletes d3d9.dll, DXVK's log, and a dxvk.conf that holds nothing but the line EnableFloatDepth writes.
        public static void RemoveDxvk(string binariesDirectory)
        {
            File.Delete(Path.Combine(binariesDirectory, "d3d9.dll"));
            File.Delete(Path.Combine(binariesDirectory, "MirrorsEdge_d3d9.log"));

            string conf = Path.Combine(binariesDirectory, "dxvk.conf");
            if (File.Exists(conf) && File.ReadAllLines(conf).Select(line => line.Trim()).Where(line => line.Length > 0)
                    .SequenceEqual(new[] { FloatDepthLine }))
                File.Delete(conf);
        }

        public static bool IsFloatDepthEnabled(string binariesDirectory)
        {
            string conf = Path.Combine(binariesDirectory, "dxvk.conf");
            return File.Exists(conf) && IsFloatDepthEnabled(File.ReadAllLines(conf));
        }

        // DXVK applies the global options, then the [MirrorsEdge.exe] section.
        // A later line overrides an earlier one.
        internal static bool IsFloatDepthEnabled(IEnumerable<string> lines)
        {
            bool? global = null, game = null;
            string? section = null;
            foreach (string line in lines)
            {
                if (TryReadSection(line, out string? name))
                {
                    section = name;
                    continue;
                }
                if (!TryReadFloatDepth(line, out bool value))
                    continue;
                if (section == null) global = value;
                else if (section == GameSection) game = value;
            }
            return game ?? global ?? false;
        }

        public static void EnableFloatDepth(string binariesDirectory)
        {
            string conf = Path.Combine(binariesDirectory, "dxvk.conf");
            string[] lines = File.Exists(conf) ? File.ReadAllLines(conf) : Array.Empty<string>();
            File.WriteAllLines(conf, WithFloatDepth(lines));
        }

        // Sets every global and [MirrorsEdge.exe] setting to True.
        // Without a global setting, it uncomments the line from DXVK's template.
        // Without that line either, it adds one above the first section.
        internal static List<string> WithFloatDepth(IReadOnlyList<string> lines)
        {
            var result = new List<string>(lines);
            string? section = null;
            int firstSection = -1, commented = -1;
            bool hasGlobal = false;

            for (int i = 0; i < result.Count; i++)
            {
                if (TryReadSection(result[i], out string? name))
                {
                    section = name;
                    if (firstSection < 0) firstSection = i;
                    continue;
                }
                if (TryReadFloatDepth(result[i], out _))
                {
                    if (section == null || section == GameSection)
                        result[i] = Indent(result[i]) + FloatDepthLine;
                    hasGlobal |= section == null;
                }
                else if (section == null && commented < 0 && IsCommentedFloatDepth(result[i]))
                {
                    commented = i;
                }
            }

            if (!hasGlobal)
            {
                if (commented >= 0)
                    result[commented] = FloatDepthLine;
                else
                    result.Insert(firstSection >= 0 ? firstSection : result.Count, FloatDepthLine);
            }
            return result;
        }

        private static bool TryReadSection(string line, out string? name)
        {
            string trimmed = line.Trim();
            name = trimmed.Length > 2 && trimmed[0] == '[' && trimmed[^1] == ']'
                ? trimmed[1..^1].Trim().ToLowerInvariant()
                : null;
            return name != null;
        }

        private static bool TryReadFloatDepth(string line, out bool value)
        {
            value = false;
            string trimmed = line.Trim();
            if (trimmed.StartsWith('#'))
                return false;
            int eq = trimmed.IndexOf('=');
            if (eq < 0 || !trimmed[..eq].Trim().Equals(FloatDepthKey, StringComparison.Ordinal))
                return false;
            value = trimmed[(eq + 1)..].Trim().Equals("True", StringComparison.OrdinalIgnoreCase);
            return true;
        }

        private static bool IsCommentedFloatDepth(string line)
        {
            string trimmed = line.Trim();
            return trimmed.StartsWith('#') && TryReadFloatDepth(trimmed.TrimStart('#'), out _);
        }

        private static string Indent(string line) => line[..(line.Length - line.TrimStart().Length)];
    }
}
