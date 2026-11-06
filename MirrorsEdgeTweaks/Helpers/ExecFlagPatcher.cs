using MirrorsEdgeTweaks.Services;
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

                    ulong currentFlagsValue = function.FunctionFlags;
                    uint uelibFlags = (uint)currentFlagsValue;
                    byte[] uelibFlagsBytes = BitConverter.GetBytes(uelibFlags);

                    long flagsOffset = LocateFlagsOffset(tdGamePath, package, functionExport, functionName, uelibFlagsBytes);

                    const uint UE3_EXEC_FLAG = 0x00000200;
                    uint newUelibFlags = uelibFlags | UE3_EXEC_FLAG;
                    byte[] newUelibFlagsBytes = BitConverter.GetBytes(newUelibFlags);

                    package.Dispose();
                    package = null;

                    PatchUtility.PatchPreservingAttributes(tdGamePath, [(flagsOffset, newUelibFlagsBytes)]);
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
        private static long LocateFlagsOffset(string tdGamePath, UnrealPackage package, UELib.UExportTableItem functionExport, string functionName, byte[] currentFlagsBytes)
        {
            long fileLength = new FileInfo(tdGamePath).Length;
            long serialStart = functionExport.SerialOffset;
            long serialEnd = Math.Min(fileLength - 4, serialStart + functionExport.SerialSize);

            // The FriendlyName check reads up to 20 bytes past the export, so the window includes them.
            long windowEnd = Math.Min(fileLength, serialStart + functionExport.SerialSize + 20);
            byte[] window = UePackageLocator.ReadRange(tdGamePath, serialStart, (int)(windowEnd - serialStart));

            var matches = new List<int>();
            for (int i = 0; i <= serialEnd - 4 - serialStart; i++)
            {
                if (window.AsSpan(i, 4).SequenceEqual(currentFlagsBytes))
                    matches.Add(i);
            }

            if (matches.Count == 0)
                throw new InvalidOperationException($"Current function flags for {functionName} not found within its export range.");

            if (matches.Count == 1)
                return serialStart + matches[0];

            int nameIndex = package.Names.FindIndex(n => n.ToString() == functionName);
            if (nameIndex != -1)
            {
                byte[] friendlyNameBytes = BitConverter.GetBytes((long)nameIndex);
                var validated = matches.Where(m => FollowedByName(window, m, friendlyNameBytes)).ToList();
                if (validated.Count == 1)
                    return serialStart + validated[0];
            }

            throw new InvalidOperationException(
                $"Ambiguous function-flags location for {functionName} ({matches.Count} candidates); refusing to patch.");
        }

        private static bool FollowedByName(byte[] window, int matchOffset, byte[] friendlyNameBytes)
        {
            for (int k = 4; k <= 16; k++)
            {
                int p = matchOffset + k;
                if (p + friendlyNameBytes.Length > window.Length)
                    break;

                if (window.AsSpan(p, friendlyNameBytes.Length).SequenceEqual(friendlyNameBytes))
                    return true;
            }

            return false;
        }
    }
}
