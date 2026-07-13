using System.IO;
using System.Text;

namespace MirrorsEdgeTweaks.Helpers
{
    internal enum SteamInstallScriptPatchStatus
    {
        Patched,
        AlreadyClean,
        Failed
    }

    internal readonly struct SteamInstallScriptPatchFileResult
    {
        public string Path { get; init; }
        public SteamInstallScriptPatchStatus Status { get; init; }
        public string? Error { get; init; }
    }

    internal static class SteamInstallScriptPatcher
    {
        internal const string MirrorsEdgeAppId = "17410";

        private static readonly HashSet<string> LanguageKeys = new(StringComparer.OrdinalIgnoreCase)
        {
            "english",    // INT
            "german",     // DEU
            "spanish",    // ESN
            "french",     // FRA
            "italian",    // ITA
            "czech",      // CZE
            "hungarian",  // HUN
            "polish",     // POL
            "portuguese", // POR
            "russian",    // RUS
            "koreana",    // KOR
            "tchinese",   // CHT
            "japanese",   // JPN
            "schinese",   // CHS
        };

        private static readonly string[] InstallScriptFileNames =
        {
            "installscript.vdf",
            "17410_install.vdf",
        };

        public static bool NeedsPatch(string vdfContent) =>
            !string.Equals(vdfContent, Patch(vdfContent), StringComparison.Ordinal);

        public static string Patch(string vdfContent)
        {
            bool useCrLf = vdfContent.Contains("\r\n", StringComparison.Ordinal);
            string[] lines = vdfContent.Replace("\r\n", "\n").Split('\n');
            var output = new List<string>(lines.Length);

            int depth = 0;
            int registryDepth = -1;
            int stringDepth = -1;
            int skipUntilDepth = -1;

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                string trimmed = line.TrimEnd('\r').Trim();

                if (skipUntilDepth >= 0)
                {
                    UpdateDepth(line, ref depth);
                    if (depth < skipUntilDepth)
                        skipUntilDepth = -1;
                    continue;
                }

                if (TryGetQuotedKey(trimmed, out string? keyName))
                {
                    if (keyName.Equals("registry", StringComparison.OrdinalIgnoreCase)
                        && LineOpensBlock(trimmed, lines, i))
                    {
                        output.Add(line);
                        registryDepth = depth + 1;
                        UpdateDepth(line, ref depth);
                        continue;
                    }

                    if (registryDepth >= 0 && depth >= registryDepth
                        && keyName.Equals("string", StringComparison.OrdinalIgnoreCase)
                        && LineOpensBlock(trimmed, lines, i))
                    {
                        output.Add(line);
                        stringDepth = depth + 1;
                        UpdateDepth(line, ref depth);
                        continue;
                    }

                    if (stringDepth >= 0 && depth == stringDepth
                        && LanguageKeys.Contains(keyName)
                        && LineOpensBlock(trimmed, lines, i))
                    {
                        skipUntilDepth = depth + 1;
                        UpdateDepth(line, ref depth);
                        continue;
                    }
                }

                output.Add(line);
                UpdateDepth(line, ref depth);

                if (trimmed == "}")
                {
                    if (stringDepth >= 0 && depth < stringDepth)
                        stringDepth = -1;
                    if (registryDepth >= 0 && depth < registryDepth)
                    {
                        registryDepth = -1;
                        stringDepth = -1;
                    }
                }
            }

            return string.Join(useCrLf ? "\r\n" : "\n", output);
        }

        public static SteamInstallScriptPatchFileResult TryPatchFile(string path)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return new SteamInstallScriptPatchFileResult
                    {
                        Path = path,
                        Status = SteamInstallScriptPatchStatus.Failed,
                        Error = "File not found.",
                    };
                }

                string original = File.ReadAllText(path);
                string patched = Patch(original);
                if (string.Equals(original, patched, StringComparison.Ordinal))
                {
                    return new SteamInstallScriptPatchFileResult
                    {
                        Path = path,
                        Status = SteamInstallScriptPatchStatus.AlreadyClean,
                    };
                }

                PatchUtility.WritePreservingAttributes(path, Encoding.UTF8.GetBytes(patched));

                return new SteamInstallScriptPatchFileResult
                {
                    Path = path,
                    Status = SteamInstallScriptPatchStatus.Patched,
                };
            }
            catch (Exception ex)
            {
                return new SteamInstallScriptPatchFileResult
                {
                    Path = path,
                    Status = SteamInstallScriptPatchStatus.Failed,
                    Error = ex.Message,
                };
            }
        }

        public static IReadOnlyList<string> FindInstallScriptPaths(string gameDirectory, string? steamInstallPath)
        {
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string fileName in InstallScriptFileNames)
            {
                string candidate = Path.Combine(gameDirectory, fileName);
                if (File.Exists(candidate))
                    paths.Add(candidate);
            }

            if (!string.IsNullOrWhiteSpace(steamInstallPath))
            {
                string steamAppsScript = Path.Combine(
                    steamInstallPath,
                    "steamapps",
                    $"{MirrorsEdgeAppId}_install.vdf");

                if (File.Exists(steamAppsScript))
                    paths.Add(steamAppsScript);
            }

            return paths.ToList();
        }

        private static bool LineOpensBlock(string trimmedLine, string[] lines, int index)
        {
            if (trimmedLine.Contains('{'))
                return true;

            if (index + 1 < lines.Length && lines[index + 1].Trim() == "{")
                return true;

            return false;
        }

        private static void UpdateDepth(string line, ref int depth)
        {
            foreach (char c in line)
            {
                if (c == '{') depth++;
                else if (c == '}') depth--;
            }
        }

        private static bool TryGetQuotedKey(string trimmedLine, out string keyName)
        {
            keyName = string.Empty;
            if (!trimmedLine.StartsWith('"'))
                return false;

            int endQuote = trimmedLine.IndexOf('"', 1);
            if (endQuote <= 1)
                return false;

            keyName = trimmedLine.Substring(1, endQuote - 1);
            return true;
        }
    }
}
