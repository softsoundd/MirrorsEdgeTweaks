using MirrorsEdgeTweaks.Services;
using System.Globalization;
using System.IO;
using System.Text;
using UELib;

namespace MirrorsEdgeTweaks.Helpers
{
    public enum LegacyFile { Exe, Engine, TdGame, Localisation }

    // Tweaks that a finding shows were enabled, where the current state detection cannot tell.
    [Flags]
    public enum LegacyIntent
    {
        None = 0,
        HighResFix = 1,
        Fov = 2,
        FovAgnosticSens = 4,
        CompensatedClip = 8,
    }

    public sealed record LegacyChange(LegacyFile File, string FileName, string Description,
        LegacyIntent Intent = LegacyIntent.None);

    public sealed class LegacyChangeReport
    {
        public LegacyChangeReport(IReadOnlyList<LegacyChange> changes) => Changes = changes;

        public IReadOnlyList<LegacyChange> Changes { get; }

        public bool IsEmpty => Changes.Count == 0;

        public LegacyIntent Intent => Changes.Aggregate(LegacyIntent.None, (all, change) => all | change.Intent);

        public bool Affects(LegacyFile file) => Changes.Any(change => change.File == file);
    }

    // Finds edits by older Tweaks releases that the current patchers cannot recognise or undo.
    public static class LegacyChangeDetector
    {
        private const string ExeName = "MirrorsEdge.exe";
        private const string EngineName = "Engine.u";
        private const string TdGameName = "TdGame.u";

        private const string BetaHighRes = "High-res UI fix from the 4.5.0 betas";
        private const string StaticHighRes = "High-res UI fix from 4.4.2 or earlier";
        private const string StaticFov = "FOV patch from 4.3.0 or earlier";

        // The betas' high-res blobs run these console commands, which the current patches never use.
        private static readonly byte[][] BetaHighResCommands =
        {
            BytecodeBuilder.StrConst("set MultiFont ResolutionTestTable (480,720,"),
            BytecodeBuilder.StrConst("set UIStyle_Text Scale (X="),
            BytecodeBuilder.StrConst("set TdUIScene bRefreshWidgetStyles true"),
        };

        // The betas found each hook site from a prefix that is unique in .text,
        // and wrote an E9 detour at the offset.
        // The subtitle spacing prefix is masked, because its FADD operand differs between builds.
        private static readonly (BytePattern Prefix, int HookOffset)[] BetaHighResHooks =
        {
            (BytePattern.Parse("8B 06 8B 90 18 01 00 00 51 8B CE D9 1C 24 FF D2"), 16),
            (BytePattern.Parse("D9 44 24 40 89 44 24 38 8B 07 8B 90 18 01 00 00 51 8B CF D9 1C 24 FF D2"), 24),
            (BytePattern.Parse("D9 E8 D9 54 24 04 D9 1C 24 D9 44 24 30 50"), 14),
            (BytePattern.Parse("89 44 24 78 8B 01 8B 90 18 01 00 00 51 D9 1C 24 FF D2"), 18),
            (BytePattern.Parse("51 89 44 24 28 D9 1C 24 8B 82 18 01 00 00 8B CF FF D0"), 18),
            (BytePattern.Parse("D8 05 ?? ?? ?? ?? 8B 07 8B 90 18 01 00 00 51 8B CF D9 1C 24 FF D2"), 22),
            (BytePattern.Parse("D9 E8 51 8D 4C 24 24 51 8D 54 24 24 52 83 EC 08 D9 54 24 04 D9 1C 24 50"), 29),
        };

        // 4.0.0 and 4.1.0 renamed the FlybyFlight switch to CmdLineArgs,
        // and wrote the user's arguments into the map URL after it.
        private static readonly byte[] CmdLineArgsHijack = Encoding.Unicode.GetBytes("CmdLineArgs\0tdmainmenu?");

        // 4.0.0 and 4.1.0 unlocked the configs by making this MOV store 0 instead of 1.
        private static readonly byte[][] LegacyConfigUnlocks =
        {
            Convert.FromHexString("0100006898110502C705A01305020000"),
            Convert.FromHexString("01000068D8800302C705E08203020000"),
        };

        private static readonly (string Class, string Property, float Stock)[] EngineFovDefaults =
        {
            ("PlayerController", "DefaultFOV", 85f),
            ("PlayerController", "DesiredFOV", 85f),
            ("PlayerController", "FOVAngle", 85f),
            ("Camera", "DefaultFOV", 90f),
            ("CameraActor", "FOVAngle", 90f),
        };

        private static readonly (string Name, int Stock)[] Crosshairs =
        {
            ("CrossHair_Reaction", 16),
            ("CrossHair_Standard", 32),
            ("CrossHair_Unarmed", 16),
            ("CrossHair_Weapon", 64),
        };

        private const float StockCutsceneFov = 70f;
        private const float StockVertigoFov = 84f;
        private const float StockResolutionTest = 1080f;

        // Checks that fail, for example on a file that is locked or unreadable, report nothing for that file.
        public static LegacyChangeReport Detect(string gameDirectoryPath, string? languageCode)
        {
            var changes = new List<LegacyChange>();
            string cookedPc = Path.Combine(gameDirectoryPath, "TdGame", "CookedPC");

            Collect(changes, () => DetectExe(Path.Combine(gameDirectoryPath, "Binaries", ExeName)));
            Collect(changes, () => DetectEngine(Path.Combine(cookedPc, EngineName)));
            Collect(changes, () => DetectTdGame(Path.Combine(cookedPc, TdGameName)));
            foreach (string package in LocalisationPackages(cookedPc, languageCode))
                Collect(changes, () => DetectLocalisation(package));

            return new LegacyChangeReport(changes);
        }

        public static IEnumerable<string> LocalisationPackages(string cookedPcPath, string? languageCode)
        {
            if (string.IsNullOrEmpty(languageCode))
                yield break;
            foreach (string prefix in new[] { "Ts_LOC_", "Startup_" })
            {
                string path = Path.Combine(cookedPcPath, prefix + languageCode.ToUpperInvariant() + ".upk");
                if (File.Exists(path))
                    yield return path;
            }
        }

        private static void Collect(List<LegacyChange> changes, Func<List<LegacyChange>> check)
        {
            try { changes.AddRange(check()); }
            catch { }
        }

        internal static List<LegacyChange> DetectExe(string exePath)
        {
            if (!File.Exists(exePath))
                return new List<LegacyChange>();

            byte[] data = File.ReadAllBytes(exePath);
            string? version = ExeVersionDetector.DetectVersion(data, exePath);

            // Without the licence the code stays encrypted, so the exe is not checked.
            if (OoaService.HasOoaSection(data))
            {
                try { PatchUtility.DecryptOoaInPlace(data); }
                catch { return new List<LegacyChange>(); }
            }

            return DetectExeImage(data, version);
        }

        // The image must already be decrypted.
        internal static List<LegacyChange> DetectExeImage(byte[] data, string? version)
        {
            var found = new List<LegacyChange>();

            if (Check(() => HasBetaHighResHook(data)))
                found.Add(new(LegacyFile.Exe, ExeName, BetaHighRes, LegacyIntent.HighResFix));
            if (PatternHelper.FindPattern(data, CmdLineArgsHijack) != -1)
                found.Add(new(LegacyFile.Exe, ExeName, "Launch arguments patch from 4.1.0 or earlier"));
            if (LegacyConfigUnlocks.Any(pattern => PatternHelper.FindPattern(data, pattern) != -1))
                found.Add(new(LegacyFile.Exe, ExeName, "Unlocked configs patch from 4.1.0 or earlier"));
            if (version != null && Check(() => LoggingPatchHelper.HasUnsupportedLeftovers(data, version)))
                found.Add(new(LegacyFile.Exe, ExeName, "Logging patch from 4.4.0 to 4.4.2"));

            return found;
        }

        // Patchers throw these instead of editing a file whose older changes they would mishandle.
        internal static InvalidOperationException PackageRestoreRequired(string fileName) => new(
            $"{fileName} has changes from an older version of Mirror's Edge Tweaks that this patch cannot handle. " +
            "Restart Mirror's Edge Tweaks and accept the prompt to restore the file.");

        internal static InvalidOperationException ExeVerificationRequired() => new(
            "MirrorsEdge.exe has changes from an older version of Mirror's Edge Tweaks that this patch cannot handle. " +
            "Verify your game files to restore it.");

        // The image must already be decrypted.
        internal static bool HasBetaHighResHook(byte[] data)
        {
            var pe = PeImageLayout.Parse(data);
            foreach (var (prefix, hookOffset) in BetaHighResHooks)
            {
                int site = prefix.FindUniqueInText(data, pe);
                if (site != -1 && site + hookOffset < data.Length && data[site + hookOffset] == 0xE9)
                    return true;
            }
            return false;
        }

        internal static List<LegacyChange> DetectEngine(string enginePath)
        {
            var found = new List<LegacyChange>();
            if (!File.Exists(enginePath))
                return found;

            byte[] data = File.ReadAllBytes(enginePath);
            if (ContainsBetaHighResCommand(data))
                found.Add(new(LegacyFile.Engine, EngineName, BetaHighRes, LegacyIntent.HighResFix));

            using var pkg = TryLoad(enginePath);
            if (pkg == null)
                return found;

            if (Check(() => UePackageLocator.ReadDefault(pkg, "UIStyle_Text", "Scale") is { } scale
                && !IsUnitScale(scale)))
                found.Add(new(LegacyFile.Engine, EngineName, StaticHighRes, LegacyIntent.HighResFix));

            // The current FOV patch writes these defaults only after it has applied the dynamic FOV code.
            bool fovChanged = Check(() => EngineFovDefaults.Any(d =>
                UePackageLocator.ReadFloatDefault(pkg, d.Class, d.Property) is { } value && !Near(value, d.Stock)));
            if (fovChanged && Check(() => EnginePatcher.DetectState(data, pkg) == EnginePatchState.Unpatched))
                found.Add(new(LegacyFile.Engine, EngineName, StaticFov, LegacyIntent.Fov));

            return found;
        }

        internal static List<LegacyChange> DetectTdGame(string tdGamePath)
        {
            var found = new List<LegacyChange>();
            if (!File.Exists(tdGamePath))
                return found;

            byte[] data = File.ReadAllBytes(tdGamePath);
            if (ContainsBetaHighResCommand(data))
                found.Add(new(LegacyFile.TdGame, TdGameName, BetaHighRes, LegacyIntent.HighResFix));

            using var pkg = TryLoad(tdGamePath);
            if (pkg == null)
                return found;

            LegacyTdGameChanges changes;
            try { changes = TdGamePatcher.FindLegacyChanges(data, pkg); }
            catch { changes = LegacyTdGameChanges.None; }
            if (changes.HasFlag(LegacyTdGameChanges.NearClipBlock))
                found.Add(new(LegacyFile.TdGame, TdGameName,
                    "Sensitivity or near-clip patch from 4.4.0 to the 4.5.0 betas"));
            if (changes.HasFlag(LegacyTdGameChanges.UnzoomDelay))
                found.Add(new(LegacyFile.TdGame, TdGameName, "Unzoom patch from 4.4.1"));

            // The core patch replaces the vertigo's use of ZoomFOV, but nothing replaces the cutscene NewFOV.
            bool cutsceneChanged = Check(() =>
                UePackageLocator.ReadFloatDefault(pkg, "SeqAct_TdCameraFOV", "NewFOV") is { } fov
                && !Near(fov, StockCutsceneFov));
            bool vertigoChanged = Check(() => !TdGamePatcher.DetectState(data, pkg).CoreApplied
                && UePackageLocator.ReadFloatDefault(pkg, "TdMove_Vertigo", "ZoomFOV") is { } fov
                && !Near(fov, StockVertigoFov));

            var fovIntent = LegacyIntent.None;
            if (changes.HasFlag(LegacyTdGameChanges.StaticFovScale))
                fovIntent |= LegacyIntent.Fov | LegacyIntent.FovAgnosticSens;
            if (changes.HasFlag(LegacyTdGameChanges.StaticNearClip))
                fovIntent |= LegacyIntent.Fov | LegacyIntent.CompensatedClip;
            if (changes.HasFlag(LegacyTdGameChanges.StaticUnzoomRate) || cutsceneChanged || vertigoChanged)
                fovIntent |= LegacyIntent.Fov;
            if (fovIntent != LegacyIntent.None)
                found.Add(new(LegacyFile.TdGame, TdGameName, StaticFov, fovIntent));

            // The TdGame Fix package does not contain the crosshair textures.
            if (Check(() => Crosshairs.Any(c => ReadTextureSize(pkg, c.Name) is { } size && size != c.Stock)))
                found.Add(new(LegacyFile.TdGame, TdGameName, StaticHighRes, LegacyIntent.HighResFix));

            return found;
        }

        private static UnrealPackage? TryLoad(string path)
        {
            try { return UePackageLocator.Load(path); }
            catch { return null; }
        }

        // Returns false when the condition throws, for example on an object UELib cannot read.
        private static bool Check(Func<bool> condition)
        {
            try { return condition(); }
            catch { return false; }
        }

        // 4.4.2 always decompressed a package before editing it, so compressed packages are stock.
        internal static List<LegacyChange> DetectLocalisation(string packagePath)
        {
            var found = new List<LegacyChange>();
            if (!File.Exists(packagePath) || DecompressionService.IsCompressed(packagePath))
                return found;

            int nameIndex;
            var exports = new List<(long Offset, long Size)>();
            using (var pkg = UePackageLocator.LoadHeader(packagePath))
            {
                nameIndex = UePackageLocator.FindNameIndex(pkg, "ResolutionTestTable");
                if (nameIndex < 0)
                    return found;
                foreach (var export in pkg.Exports.Where(e => e.Class?.ObjectName?.ToString() == "MultiFont"))
                    exports.Add((export.SerialOffset, export.SerialSize));
            }

            byte[] data = File.ReadAllBytes(packagePath);
            if (exports.Any(e => ReadLastResolutionTest(data, e.Offset, e.Size, nameIndex) is { } last
                && !Near(last, StockResolutionTest)))
                found.Add(new(LegacyFile.Localisation, Path.GetFileName(packagePath), StaticHighRes,
                    LegacyIntent.HighResFix));

            return found;
        }

        // Returns the last entry of the export's ResolutionTestTable.
        // Returns null when the table is missing or truncated.
        // The array value starts 24 bytes into the property tag, with the element count followed by the floats.
        internal static float? ReadLastResolutionTest(byte[] data, long serialOffset, long serialSize, int nameIndex)
        {
            byte[] name = BitConverter.GetBytes((long)nameIndex);
            int end = (int)Math.Min(serialOffset + serialSize, data.Length);
            int tag = PatternHelper.FindPattern(data, name, (int)serialOffset, end);
            if (tag == -1 || tag + 28 > end)
                return null;

            int count = BitConverter.ToInt32(data, tag + 24);
            int last = tag + 28 + (count - 1) * sizeof(float);
            if (count <= 0 || last + sizeof(float) > end)
                return null;
            return BitConverter.ToSingle(data, last);
        }

        internal static bool ContainsBetaHighResCommand(byte[] data) =>
            BetaHighResCommands.Any(command => PatternHelper.FindPattern(data, command) != -1);

        private static int? ReadTextureSize(UnrealPackage pkg, string textureName)
        {
            var texture = UePackageLocator.FindExportObject(pkg, textureName, "TdUIResources_InGame");
            if (texture == null)
                return null;
            texture.Load<UObjectRecordStream>();
            string? sizeX = texture.Properties?.FirstOrDefault(p => p.Name == "SizeX")?.Value;
            return int.TryParse(sizeX, NumberStyles.Integer, CultureInfo.InvariantCulture, out int size) ? size : null;
        }

        // UELib formats the vector as "(X=1.0000000,Y=1.0000000)".
        private static bool IsUnitScale(string scale)
        {
            foreach (string part in scale.Trim('(', ')').Split(','))
            {
                string[] pair = part.Split('=');
                if (pair.Length != 2
                    || !float.TryParse(pair[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float value)
                    || !Near(value, 1f))
                    return false;
            }
            return true;
        }

        private static bool Near(float value, float expected) => Math.Abs(value - expected) < 0.001f;
    }
}
