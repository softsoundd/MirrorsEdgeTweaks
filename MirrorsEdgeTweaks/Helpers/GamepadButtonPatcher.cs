using System.IO;
using UELib;
using UELib.Core;

namespace MirrorsEdgeTweaks.Helpers
{
    // Swaps the in-game gamepad button prompts between Xbox and PS3 by byte-patching the
    // localized Ts_LOC_*.upk packages and swapping the PC/PS3 controller image-path name indices
    // in TdGame.u. The Ts_LOC_*.upk files must already be decompressed before calling
    // ApplyButtonPatches.
    public static class GamepadButtonPatcher
    {
        // Patches the (already-decompressed) Ts_LOC_*.upk localization packages and swaps the
        // controller image paths in TdGame.u for the given button type ("xbox" / "ps3").
        // All locale packages are patched in memory first and only written once every candidate
        // has been processed, so a failure mid-way cannot leave a mix of patched and unpatched
        // locales on disk.
        public static void ApplyButtonPatches(string cookedPcPath, string buttonType)
        {
            string[] tsLocFiles = Directory.GetFiles(cookedPcPath, "Ts_LOC_*.upk");

            if (tsLocFiles.Length == 0)
            {
                throw new FileNotFoundException("No Ts_LOC_*.upk files found in CookedPC directory.");
            }

            // Verified before patching locales: failing here after locale writes would leave a
            // partially applied prompt swap.
            string tdGamePackagePath = Path.Combine(cookedPcPath, "TdGame.u");
            if (!File.Exists(tdGamePackagePath))
            {
                throw new FileNotFoundException($"TdGame.u not found at: {tdGamePackagePath}");
            }

            // todo: utilise UELib instead of hardcoded byte patterns - works for now though
            byte[] gamepadPatternHeader = new byte[] { 0x00, 0x00, 0x00, 0x09, 0x00, 0x00, 0x00, 0x0E, 0x00, 0x00, 0x00, 0x02, 0x04, 0x00, 0x00, 0x00 };

            var pendingWrites = new List<(string Path, byte[] Data)>();

            foreach (string tsLocFilePath in tsLocFiles)
            {
                string fileName = Path.GetFileName(tsLocFilePath);
                string countryCode = fileName.Split('_').Last().Substring(0, 3).ToUpper();

                byte[]? replacement = GetGamepadReplacement(countryCode, buttonType);
                byte[]? otherVariant = GetGamepadReplacement(countryCode, buttonType == "ps3" ? "xbox" : "ps3");

                if (replacement == null || otherVariant == null)
                {
                    continue;
                }

                byte[] data = File.ReadAllBytes(tsLocFilePath);

                int startIndex = 0;
                bool patternFound = true;
                for (int occurrence = 0; occurrence < 2; occurrence++)
                {
                    startIndex = FindBytePattern(data, gamepadPatternHeader, startIndex);
                    if (startIndex == -1)
                    {
                        patternFound = false;
                        break;
                    }
                    startIndex += 1;
                }

                if (!patternFound)
                {
                    continue;
                }

                int replaceIndex = startIndex + 43;

                if (replaceIndex + replacement.Length > data.Length)
                {
                    continue;
                }

                // Only patch a site whose current bytes are one of the two known variants; anything
                // else means the layout is not what this patcher expects, and writing would corrupt
                // the package.
                var current = data.AsSpan(replaceIndex, replacement.Length);
                if (current.SequenceEqual(replacement))
                {
                    continue;
                }
                if (!current.SequenceEqual(otherVariant))
                {
                    continue;
                }

                byte[] modifiedData = (byte[])data.Clone();
                Array.Copy(replacement, 0, modifiedData, replaceIndex, replacement.Length);

                pendingWrites.Add((tsLocFilePath, modifiedData));
            }

            foreach (var (path, modified) in pendingWrites)
            {
                PatchUtility.WritePreservingAttributes(path, modified);
            }

            ApplyControllerImagePathSwap(tdGamePackagePath, buttonType);
        }

        public static bool? ReadIsPs3(string? gameDirectoryPath)
        {
            try
            {
                if (string.IsNullOrEmpty(gameDirectoryPath))
                    return null;

                string cookedPcPath = Path.Combine(gameDirectoryPath, "TdGame", "CookedPC");
                if (!Directory.Exists(cookedPcPath))
                    return null;

                string tdGamePackagePath = Path.Combine(cookedPcPath, "TdGame.u");
                if (!File.Exists(tdGamePackagePath))
                    return null;

                using var package = UnrealLoader.LoadPackage(tdGamePackagePath, FileAccess.Read);
                package?.InitializePackage();

                if (package == null)
                    return null;

                var controlsSettingsClass = package.FindObject<UClass>("TdUIScene_ControlsSettings");
                if (controlsSettingsClass == null)
                {
                    var allClasses = package.Objects.OfType<UClass>().ToList();
                    controlsSettingsClass = allClasses.FirstOrDefault(c => c.Name.ToString().Contains("ControlsSettings", StringComparison.OrdinalIgnoreCase));
                }

                if (controlsSettingsClass == null)
                    return null;

                string defaultObjectName = $"Default__{controlsSettingsClass.Name}";
                var defaultObject = package.Objects.FirstOrDefault(o => o.Name == defaultObjectName);

                if (defaultObject is UObject uObject)
                {
                    uObject.Load<UObjectRecordStream>();

                    if (uObject.Properties != null)
                    {
                        var pcControllerImagePathProp = uObject.Properties
                            .OfType<UDefaultProperty>()
                            .FirstOrDefault(p => p.Name?.ToString() == "PCControllerImagePath");

                        if (pcControllerImagePathProp != null)
                        {
                            string pcPathValue = pcControllerImagePathProp.Value;
                            return pcPathValue.Contains("PS3", StringComparison.OrdinalIgnoreCase);
                        }
                    }
                }

                return false;
            }
            catch
            {
                return null;
            }
        }

        public static void ApplyControllerImagePathSwap(string tdGamePackagePath, string buttonType)
        {
            FileAttributes attributes = File.GetAttributes(tdGamePackagePath);
            bool wasReadOnly = (attributes & FileAttributes.ReadOnly) == FileAttributes.ReadOnly;

            if (wasReadOnly)
            {
                File.SetAttributes(tdGamePackagePath, attributes & ~FileAttributes.ReadOnly);
            }

            try
            {
                long searchStart;
                long searchEnd;
                byte[] pcNameIndexBytes;
                byte[] ps3NameIndexBytes;

                // The package reader must be fully disposed before the file is rewritten below:
                // the safe-write pipeline replaces the file via rename, which Windows refuses
                // while another handle is open on it.
                using (var package = UnrealLoader.LoadPackage(tdGamePackagePath, FileAccess.Read))
                {
                    package?.InitializePackage();

                    if (package == null)
                        throw new Exception("Failed to load TdGame.u package.");

                    var controlsSettingsClass = package.FindObject<UClass>("TdUIScene_ControlsSettings");

                    if (controlsSettingsClass == null)
                    {
                        throw new Exception("TdUIScene_ControlsSettings class not found in TdGame.u");
                    }

                    string defaultObjectName = $"Default__{controlsSettingsClass.Name}";

                    var defaultObject = package.Objects
                        .FirstOrDefault(o => o.Name == defaultObjectName);

                    if (defaultObject == null || !(defaultObject is UObject uObject))
                    {
                        throw new Exception($"Default object not found for class {controlsSettingsClass.Name}");
                    }

                    uObject.Load<UObjectRecordStream>();

                    if (uObject.Properties == null)
                    {
                        throw new Exception("Failed to load properties for Default__TdUIScene_ControlsSettings");
                    }

                    var pcControllerImagePathProp = uObject.Properties
                        .OfType<UDefaultProperty>()
                        .FirstOrDefault(p => p.Name?.ToString() == "PCControllerImagePath");

                    var ps3ControllerImagePathProp = uObject.Properties
                        .OfType<UDefaultProperty>()
                        .FirstOrDefault(p => p.Name?.ToString() == "PS3ControllerImagePath");

                    if (pcControllerImagePathProp == null || ps3ControllerImagePathProp == null)
                    {
                        throw new Exception("Required controller image path properties not found");
                    }

                    string currentPcPath = pcControllerImagePathProp.Value.Trim('"');

                    bool shouldSwap = (buttonType == "ps3" && currentPcPath.Contains("Xbox", StringComparison.OrdinalIgnoreCase)) ||
                                      (buttonType == "xbox" && currentPcPath.Contains("PS3", StringComparison.OrdinalIgnoreCase));

                    if (!shouldSwap)
                    {
                        return;
                    }

                    int pcNameIndex = package.Names.FindIndex(n => n.ToString() == "PCControllerImagePath");
                    int ps3NameIndex = package.Names.FindIndex(n => n.ToString() == "PS3ControllerImagePath");

                    if (pcNameIndex == -1 || ps3NameIndex == -1)
                    {
                        throw new Exception("Could not find property name indices");
                    }

                    var exportTable = uObject.ExportTable;
                    if (exportTable == null)
                    {
                        throw new Exception("Export table not found for Default__TdUIScene_ControlsSettings");
                    }

                    searchStart = exportTable.SerialOffset;
                    searchEnd = exportTable.SerialOffset + exportTable.SerialSize;

                    pcNameIndexBytes = BitConverter.GetBytes((long)pcNameIndex);
                    ps3NameIndexBytes = BitConverter.GetBytes((long)ps3NameIndex);
                }

                byte[] data = File.ReadAllBytes(tdGamePackagePath);

                long pcPropertyOffset = -1;
                long ps3PropertyOffset = -1;

                for (long i = searchStart; i < searchEnd - 8; i++)
                {
                    bool matchesPc = true;
                    for (int j = 0; j < 8; j++)
                    {
                        if (data[i + j] != pcNameIndexBytes[j])
                        {
                            matchesPc = false;
                            break;
                        }
                    }

                    if (matchesPc && pcPropertyOffset == -1)
                    {
                        pcPropertyOffset = i;
                    }

                    bool matchesPs3 = true;
                    for (int j = 0; j < 8; j++)
                    {
                        if (data[i + j] != ps3NameIndexBytes[j])
                        {
                            matchesPs3 = false;
                            break;
                        }
                    }

                    if (matchesPs3 && ps3PropertyOffset == -1)
                    {
                        ps3PropertyOffset = i;
                    }

                    if (pcPropertyOffset != -1 && ps3PropertyOffset != -1)
                    {
                        break;
                    }
                }

                if (pcPropertyOffset == -1 || ps3PropertyOffset == -1)
                {
                    throw new Exception("Could not find property name index offsets");
                }

                Array.Copy(ps3NameIndexBytes, 0, data, pcPropertyOffset, 8);
                Array.Copy(pcNameIndexBytes, 0, data, ps3PropertyOffset, 8);

                PatchUtility.WritePreservingAttributes(tdGamePackagePath, data);
            }
            finally
            {
                if (wasReadOnly)
                {
                    File.SetAttributes(tdGamePackagePath, File.GetAttributes(tdGamePackagePath) | FileAttributes.ReadOnly);
                }
            }
        }

        private static byte[]? GetGamepadReplacement(string countryCode, string buttonType)
        {
            var mappings = new Dictionary<string, (string ps3, string xbox)>
            {
                { "INT", ("410000004200000043000000", "450000004600000044000000") },
                { "CZE", ("540000005500000056000000", "580000005900000057000000") },
                { "HUN", ("540000005500000056000000", "580000005900000057000000") },
                { "DEU", ("78000000790000007A000000", "7C0000007D0000007B000000") },
                { "ESN", ("78000000790000007A000000", "7C0000007D0000007B000000") },
                { "FRA", ("78000000790000007A000000", "7C0000007D0000007B000000") },
                { "ITA", ("78000000790000007A000000", "7C0000007D0000007B000000") },
                { "POL", ("8B0000008C0000008D000000", "8F000000900000008E000000") },
                { "RUS", ("7E0000007F00000080000000", "820000008300000081000000") },
                { "KOR", ("2E0100002F01000030010000", "320100003301000031010000") },
                { "JPN", ("390100003A0100003B010000", "3D0100003E0100003C010000") },
                { "CHS", ("810100008201000083010000", "850100008601000084010000") },
                { "CHT", ("7F0100008001000081010000", "830100008401000082010000") }
            };

            if (mappings.TryGetValue(countryCode, out var mapping))
            {
                string hexString = buttonType == "ps3" ? mapping.ps3 : mapping.xbox;
                return HexStringToByteArray(hexString);
            }

            return null;
        }

        private static byte[] HexStringToByteArray(string hex)
        {
            int length = hex.Length;
            byte[] bytes = new byte[length / 2];
            for (int i = 0; i < length; i += 2)
            {
                bytes[i / 2] = Convert.ToByte(hex.Substring(i, 2), 16);
            }
            return bytes;
        }

        private static int FindBytePattern(byte[] data, byte[] pattern, int startIndex)
            => PatternHelper.FindPattern(data, pattern, startIndex);
    }
}
