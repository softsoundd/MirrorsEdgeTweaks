using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace MirrorsEdgeTweaks.Helpers
{
    // Holds stock copies of the config files that Tweaks edits, taken from a clean install.
    // Every supported MirrorsEdge.exe stores the same config file hashes, so one set fits every game version.
    public static class StockConfigFiles
    {
        internal const string GameResourcePrefix = "MirrorsEdgeTweaks.StockConfig.Game.";
        internal const string UserResourcePrefix = "MirrorsEdgeTweaks.StockConfig.User.";

        // Tweaks edits these files in TdGame\Config.
        public static readonly IReadOnlyList<string> EditedGameConfigNames = new[]
        {
            "DefaultGame.ini",
            "DefaultHudEffects.ini",
            "DefaultUI.ini",
        };

        // Holds the SHA-256 of every stock TdGame\Config file, each of which the exe's config check covers.
        internal static readonly IReadOnlyDictionary<string, string> StockGameConfigHashes =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["DefaultAchievements.ini"] = "74590BAFDC353FE8A6C9BA377555C1C81C1B174A3E94EAAA67D59EAF4D354DB1",
                ["DefaultAI.ini"] = "59DB83672AFA9B1DBD4AAD22D41A5350225C2B75D57812B8E2CFD6C52873BBA8",
                ["DefaultAIMeleeAttacks.ini"] = "E5706031738A6CF229D983D4A4E81997A2CFF985596D4538E701D73897237006",
                ["DefaultAITemplates.ini"] = "016FAE6B92A601EF1DFBF40C5628CD1F3746F4FF2E5CDE2084E0C67D203BDEC7",
                ["DefaultAnimation.ini"] = "0E06463CC0BDD08FDCE48DF0A1E1293B6B8A290F1BE9AD9D428983C3FA7A0CAD",
                ["DefaultCamera.ini"] = "56638614FA95C8A51E2BB87609D183B500EA88099DA47B43C67105EBD356C01A",
                ["DefaultCompat.ini"] = "B58772B174EDC9CE3815AD463AE2B7EAE6EEFFC4B7906EACCF73DEA55A6E2BB7",
                ["DefaultEditor.ini"] = "6A3967BAB5AB2098A6F1C6B4B70E05D82A3E81677806D0515181D950332D5F3F",
                ["DefaultEditorUserSettings.ini"] = "1A52C4D14AAF5EA214DD8221696E43DE8AB252056F6469F0A8F23B40129B56E0",
                ["DefaultEngine.ini"] = "DEA2E1BE00A62C9DEFDED5AD3DD241956B3D380841E53B0A2ED8CDC616D5A181",
                ["DefaultGame.ini"] = "AA1A25C5B526698E71C79428E9F05D9A40810C7F51D7B1D0635A69BB54837C3A",
                ["DefaultGameTypeSettings.ini"] = "EC17C572182872BCA9178D554F309E235FF683FF5126CFC3C4C6957F54F8742F",
                ["DefaultHudEffects.ini"] = "1031AE527C93FD2C7F857E0EDAECC727B689F92947036D53200F2F93EBE05B50",
                ["DefaultInput.ini"] = "76BED0D001FD77FBA3C731FBB81E0D360028B315833D2252E27E651C4B9505B7",
                ["DefaultJuice.ini"] = "6DA7CD9E6416BE9249826F21CE30522A784C7F1650CAA4ACEA03FC2F688BA5AC",
                ["DefaultLOI.ini"] = "A8D585CB21B32C24E02DD71541A8BFE8B6224EF749FB11897C30171C6E31536F",
                ["DefaultPathfindingCosts.ini"] = "4469C53DBD85F6710447338BF4D8FC579093F33D0A391A692BACC4B8AE4222FA",
                ["DefaultPawnMovement.ini"] = "6386225C89B045597B91CE8AF04F067A79F0AA67C13E80D034DF59DAFE71FCC8",
                ["DefaultPresence.ini"] = "3149C19F96AAAEC4D351BE5E545FA86ABFE96A74B865ACE2D94D8C4E70EA720F",
                ["DefaultRankingStats.ini"] = "DCDA35CBCB2774FAC84EEFA8978CB97F7B002A552182FC6B0053F4F429E2ADCC",
                ["DefaultScoring.ini"] = "9CD823CEF44839019D502C06E19D6A33F44F2710AB3B44D794514CE9C9860A2D",
                ["DefaultUI.ini"] = "534B83116AE3D7C59A6AC5EB5ABBEBB69114E7B9D25BD5A987771B3B1F6E4E45",
                ["DefaultWeapons.ini"] = "41EF3BE140B2C7A74D7560CF76CEF3BA65C29B127B1FBF7C9D4E93354B9CEADE",
            };

        private static readonly Regex LanguageCode = new(@"^[A-Za-z]{2,8}\z");
        private static readonly Regex IniEntry = new(@"^([^\[;=\r\n][^=\r\n]*)=([^\r\n]*)", RegexOptions.Multiline);
        private static readonly Regex CompatLevel = new(@"^CompatLevelComposite=(\d+)\r?$", RegexOptions.Multiline | RegexOptions.IgnoreCase);

        // Returns the names of the files that were rewritten.
        public static List<string> RestoreGameConfigs(string gameDirectoryPath)
        {
            string configDirectory = GetGameConfigDirectory(gameDirectoryPath);
            var restored = new List<string>();
            foreach (string name in EditedGameConfigNames)
            {
                if (WriteStock(Path.Combine(configDirectory, name), ReadResource(GameResourcePrefix + name)))
                    restored.Add(name);
            }
            return restored;
        }

        // Returns the names of the covered files that are missing or differ from stock.
        public static List<string> FindNonStockGameConfigs(string gameDirectoryPath)
        {
            string configDirectory = GetGameConfigDirectory(gameDirectoryPath);
            return StockGameConfigHashes
                .Where(stock => !HasHash(Path.Combine(configDirectory, stock.Key), stock.Value))
                .Select(stock => stock.Key)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        // Only rewrites the files that exist, because the game creates missing ones when it next starts.
        // Copies the [IniVersion] and [AppCompat] sections from the current files.
        // Without a matching [IniVersion], the game rebuilds the file from its defaults.
        // Without [AppCompat], the game reruns its hardware detection, which overwrites the graphics settings and resolution.
        // Returns the names of the files that were rewritten.
        public static List<string> RestoreUserConfigs(string gameDirectoryPath, string tdEngineIniPath, string tdInputIniPath,
            string? languageCode, ResolutionHelper.Resolution? resolution)
        {
            var restored = new List<string>();
            if (File.Exists(tdEngineIniPath))
            {
                string current = File.ReadAllText(tdEngineIniPath, Encoding.Latin1);
                string appCompat = CopySection(current, "AppCompat");
                string content = BuildTdEngineIni(languageCode, resolution, ReadCompatBucket(gameDirectoryPath, appCompat))
                    + CopySection(current, "IniVersion") + appCompat;
                if (WriteStock(tdEngineIniPath, Encoding.Latin1.GetBytes(content)))
                    restored.Add(Path.GetFileName(tdEngineIniPath));
            }
            if (File.Exists(tdInputIniPath))
            {
                string content = Encoding.Latin1.GetString(ReadResource(UserResourcePrefix + "TdInput.ini"))
                    + CopySection(File.ReadAllText(tdInputIniPath, Encoding.Latin1), "IniVersion");
                if (WriteStock(tdInputIniPath, Encoding.Latin1.GetBytes(content)))
                    restored.Add(Path.GetFileName(tdInputIniPath));
            }
            return restored;
        }

        // Keeps the game language, because an install may only hold that language's packages.
        internal static string BuildTdEngineIni(string? languageCode, ResolutionHelper.Resolution? resolution,
            IReadOnlyDictionary<string, string>? compatBucket = null)
        {
            string text = Encoding.Latin1.GetString(ReadResource(UserResourcePrefix + "TdEngine.ini"));
            if (languageCode != null && LanguageCode.IsMatch(languageCode))
                text = ReplaceStockLine(text, "Language=int", "Language=" + languageCode);

            var settings = new Dictionary<string, string>(compatBucket ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
            if (resolution is { Width: > 0, Height: > 0 })
            {
                settings["ResX"] = resolution.Width.ToString(CultureInfo.InvariantCulture);
                settings["ResY"] = resolution.Height.ToString(CultureInfo.InvariantCulture);
            }
            return SetSystemSettings(text, settings);
        }

        // Returns the settings of the hardware bucket that the [AppCompat] section names, from the game's DefaultCompat.ini.
        // The game only applies them when it runs its hardware detection, which keeping the section prevents.
        // Returns null when the section or the bucket is missing.
        private static Dictionary<string, string>? ReadCompatBucket(string gameDirectoryPath, string appCompatSection)
        {
            Match level = CompatLevel.Match(appCompatSection);
            string compatIniPath = Path.Combine(GetGameConfigDirectory(gameDirectoryPath), "DefaultCompat.ini");
            if (!level.Success || !File.Exists(compatIniPath))
                return null;

            string bucket = CopySection(File.ReadAllText(compatIniPath, Encoding.Latin1), "AppCompatBucket" + level.Groups[1].Value);
            var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match entry in IniEntry.Matches(bucket))
                settings[entry.Groups[1].Value] = entry.Groups[2].Value;
            return settings.Count > 0 ? settings : null;
        }

        // Writes each value in the format of the value it replaces, such as True or 1.000000, which is how the game writes them.
        private static string SetSystemSettings(string text, IReadOnlyDictionary<string, string> settings)
        {
            Match section = FindSection(text, "SystemSettings");
            if (!section.Success)
                return text;

            string updated = IniEntry.Replace(section.Value, entry =>
                settings.TryGetValue(entry.Groups[1].Value, out string? value)
                    ? entry.Groups[1].Value + "=" + FormatLike(entry.Groups[2].Value, value)
                    : entry.Value);
            return text[..section.Index] + updated + text[(section.Index + section.Length)..];
        }

        private static string FormatLike(string current, string value)
        {
            if (bool.TryParse(current, out _) && bool.TryParse(value, out bool flag))
                return flag ? "True" : "False";
            if (current.Contains('.') && double.TryParse(current, NumberStyles.Float, CultureInfo.InvariantCulture, out _)
                && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number))
                return number.ToString("F6", CultureInfo.InvariantCulture);
            return value;
        }

        // Returns the section's header and entries, followed by the empty line that the game writes after every section.
        // Returns an empty string when the text has no such section.
        private static string CopySection(string text, string name)
        {
            Match section = FindSection(text, name);
            if (!section.Success)
                return "";

            var lines = section.Value.Split('\n').Select(line => line.TrimEnd('\r')).Where(line => line.Length > 0);
            return string.Join("\r\n", lines) + "\r\n\r\n";
        }

        // Matches from the section's header up to the next header.
        private static Match FindSection(string text, string name) =>
            Regex.Match(text, $@"^\[{Regex.Escape(name)}\]\r?$.*?(?=^\[|\z)",
                RegexOptions.Multiline | RegexOptions.Singleline | RegexOptions.IgnoreCase);

        // Replaces the first line that equals stockLine.
        private static string ReplaceStockLine(string text, string stockLine, string line) =>
            new Regex($@"^{Regex.Escape(stockLine)}(?=\r?$)", RegexOptions.Multiline).Replace(text, _ => line, 1);

        internal static byte[] ReadResource(string resourceName)
        {
            using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException($"The stock config file '{resourceName}' is missing from Mirror's Edge Tweaks.");
            using var copy = new MemoryStream();
            stream.CopyTo(copy);
            return copy.ToArray();
        }

        private static string GetGameConfigDirectory(string gameDirectoryPath) =>
            Path.Combine(gameDirectoryPath, "TdGame", "Config");

        // Clears the read-only attribute that Tweaks sets on edited config files, because stock files are writable.
        // Returns false when the file already held the content and was writable.
        private static bool WriteStock(string path, byte[] content)
        {
            if (File.Exists(path))
            {
                FileAttributes attributes = File.GetAttributes(path);
                bool wasReadOnly = (attributes & FileAttributes.ReadOnly) != 0;
                if (wasReadOnly)
                    File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
                if (File.ReadAllBytes(path).AsSpan().SequenceEqual(content))
                    return wasReadOnly;
            }

            File.WriteAllBytes(path, content);
            return true;
        }

        private static bool HasHash(string path, string sha256) =>
            File.Exists(path)
            && Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).Equals(sha256, StringComparison.OrdinalIgnoreCase);
    }
}
