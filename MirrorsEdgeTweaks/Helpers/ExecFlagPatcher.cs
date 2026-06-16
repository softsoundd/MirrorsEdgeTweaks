using System.IO;
using UELib;
using UELib.Core;

namespace MirrorsEdgeTweaks.Helpers
{
    // Adds the UE3 Exec function flag (0x200) to a UFunction in TdGame.u so it becomes callable via
    // keybinds/console commands. Used by the keybind apply flow and the TdGame version-swap snapshot
    // reapply.
    public static class ExecFlagPatcher
    {
        public static async Task AddExecFlag(string? gameDirectoryPath, string className, string functionName)
        {
            try
            {
                if (string.IsNullOrEmpty(gameDirectoryPath))
                    return;

                string tdGamePath = Path.Combine(gameDirectoryPath, "TdGame", "CookedPC", "TdGame.u");

                if (!File.Exists(tdGamePath))
                    return;

                await Task.Run(() =>
                {
                    UnrealPackage? package = null;
                    try
                    {
                        package = UnrealLoader.LoadPackage(tdGamePath, FileAccess.Read);
                        package?.InitializePackage();

                        if (package == null)
                            return;

                        var function = package.Objects
                            .OfType<UFunction>()
                            .FirstOrDefault(f => f.Name == functionName &&
                                                f.Outer != null && f.Outer.Name == className);

                        if (function == null)
                            return;

                        if (function.FunctionFlags.HasFlag(UELib.Flags.FunctionFlag.Exec))
                            return;

                        var functionExport = function.ExportTable;
                        if (functionExport == null)
                            return;

                        byte[] data = File.ReadAllBytes(tdGamePath);

                        ulong currentFlagsValue = function.FunctionFlags;
                        uint uelibFlags = (uint)currentFlagsValue;
                        byte[] uelibFlagsBytes = BitConverter.GetBytes(uelibFlags);

                        long serialStart = functionExport.SerialOffset;
                        long uelibFlagsOffset = -1;
                        long wideSearchStart = Math.Max(0, serialStart - 1000);
                        long wideSearchEnd = Math.Min(data.Length - 4, serialStart + functionExport.SerialSize + 1000);

                        for (long i = wideSearchStart; i <= wideSearchEnd - 4; i++)
                        {
                            bool match = true;
                            for (int j = 0; j < 4; j++)
                            {
                                if (data[i + j] != uelibFlagsBytes[j])
                                {
                                    match = false;
                                    break;
                                }
                            }

                            if (match)
                            {
                                long relativeOffset = i - serialStart;

                                if (relativeOffset >= 0 && relativeOffset < functionExport.SerialSize)
                                {
                                    uelibFlagsOffset = i;
                                }
                            }
                        }

                        if (uelibFlagsOffset == -1)
                            return;

                        const uint UE3_EXEC_FLAG = 0x00000200;
                        uint newUelibFlags = uelibFlags | UE3_EXEC_FLAG;
                        byte[] newUelibFlagsBytes = BitConverter.GetBytes(newUelibFlags);
                        Array.Copy(newUelibFlagsBytes, 0, data, uelibFlagsOffset, 4);

                        package.Dispose();
                        package = null;

                        FileAttributes attributes = File.GetAttributes(tdGamePath);
                        bool wasReadOnly = (attributes & FileAttributes.ReadOnly) == FileAttributes.ReadOnly;

                        if (wasReadOnly)
                        {
                            File.SetAttributes(tdGamePath, attributes & ~FileAttributes.ReadOnly);
                        }

                        File.WriteAllBytes(tdGamePath, data);

                        if (wasReadOnly)
                        {
                            File.SetAttributes(tdGamePath, attributes);
                        }
                    }
                    finally
                    {
                        package?.Dispose();
                    }
                });
            }
            catch (Exception)
            {
            }
        }
    }
}
