using System.IO;
using UELib;
using UELib.Core;

namespace MirrorsEdgeTweaks.Helpers
{
    // Adds the UE3 Exec function flag (0x200) to a TdGame.u function, so keybinds and console commands can call it.
    // Throws on failure, so callers can report which patch did not apply.
    public static class ExecFlagPatcher
    {
        public static async Task AddExecFlag(string? gameDirectoryPath, string className, string functionName)
        {
            if (string.IsNullOrEmpty(gameDirectoryPath))
                throw new InvalidOperationException("Game directory is not set; cannot apply the exec-flag patch.");

            string tdGamePath = Path.Combine(gameDirectoryPath, "TdGame", "CookedPC", "TdGame.u");

            if (!File.Exists(tdGamePath))
                throw new FileNotFoundException("TdGame.u was not found.", tdGamePath);

            await Task.Run(() =>
            {
                UnrealPackage? package = null;
                try
                {
                    package = UnrealLoader.LoadPackage(tdGamePath, FileAccess.Read);
                    package?.InitializePackage();

                    if (package == null)
                        throw new InvalidOperationException("Failed to load TdGame.u.");

                    var function = package.Objects
                        .OfType<UFunction>()
                        .FirstOrDefault(f => f.Name == functionName &&
                                            f.Outer != null && f.Outer.Name == className);

                    if (function == null)
                        throw new InvalidOperationException($"Function {className}.{functionName} not found in TdGame.u.");

                    if (function.FunctionFlags.HasFlag(UELib.Flags.FunctionFlag.Exec))
                        return;

                    var functionExport = function.ExportTable;
                    if (functionExport == null)
                        throw new InvalidOperationException($"Export table entry missing for {className}.{functionName}.");

                    byte[] data = File.ReadAllBytes(tdGamePath);

                    ulong currentFlagsValue = function.FunctionFlags;
                    uint uelibFlags = (uint)currentFlagsValue;
                    byte[] uelibFlagsBytes = BitConverter.GetBytes(uelibFlags);

                    long flagsOffset = LocateFlagsOffset(data, package, functionExport, functionName, uelibFlagsBytes);

                    const uint UE3_EXEC_FLAG = 0x00000200;
                    uint newUelibFlags = uelibFlags | UE3_EXEC_FLAG;
                    byte[] newUelibFlagsBytes = BitConverter.GetBytes(newUelibFlags);
                    Array.Copy(newUelibFlagsBytes, 0, data, flagsOffset, 4);

                    package.Dispose();
                    package = null;

                    PatchUtility.WritePreservingAttributes(tdGamePath, data);
                }
                finally
                {
                    package?.Dispose();
                }
            });
        }

        // Returns the file offset of the function's serialized FunctionFlags DWORD.
        // The current flags value is searched for within the export's serialized range.
        // When it occurs more than once, the real field is the one followed within 16 bytes by the function's FriendlyName name index.
        // Anything still ambiguous throws, because patching the wrong DWORD corrupts the package.
        private static long LocateFlagsOffset(byte[] data, UnrealPackage package, UELib.UExportTableItem functionExport, string functionName, byte[] currentFlagsBytes)
        {
            long serialStart = functionExport.SerialOffset;
            long serialEnd = Math.Min((long)data.Length - 4, serialStart + functionExport.SerialSize);

            var matches = new List<long>();
            for (long i = serialStart; i <= serialEnd - 4; i++)
            {
                bool match = true;
                for (int j = 0; j < 4; j++)
                {
                    if (data[i + j] != currentFlagsBytes[j])
                    {
                        match = false;
                        break;
                    }
                }

                if (match)
                    matches.Add(i);
            }

            if (matches.Count == 0)
                throw new InvalidOperationException($"Current function flags for {functionName} not found within its export range.");

            if (matches.Count == 1)
                return matches[0];

            int nameIndex = package.Names.FindIndex(n => n.ToString() == functionName);
            if (nameIndex != -1)
            {
                byte[] friendlyNameBytes = BitConverter.GetBytes((long)nameIndex);
                var validated = matches.Where(m => FollowedByName(data, m, friendlyNameBytes)).ToList();
                if (validated.Count == 1)
                    return validated[0];
            }

            throw new InvalidOperationException(
                $"Ambiguous function-flags location for {functionName} ({matches.Count} candidates); refusing to patch.");
        }

        private static bool FollowedByName(byte[] data, long matchOffset, byte[] friendlyNameBytes)
        {
            for (int k = 4; k <= 16; k++)
            {
                long p = matchOffset + k;
                if (p + friendlyNameBytes.Length > data.Length)
                    break;

                bool equal = true;
                for (int j = 0; j < friendlyNameBytes.Length; j++)
                {
                    if (data[p + j] != friendlyNameBytes[j])
                    {
                        equal = false;
                        break;
                    }
                }

                if (equal)
                    return true;
            }

            return false;
        }
    }
}
