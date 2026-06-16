using System.IO;
using UELib;
using UELib.Core;
using static UELib.Core.UStruct.UByteCodeDecompiler;

namespace MirrorsEdgeTweaks.Helpers
{
    // Patches and reads the "uniform sensitivity" rotation-speed modifier float inside
    // TdPlayerController.UpdateRotation in TdGame.u (16384 = default/disabled, 66536 = uniform/enabled).
    public static class UniformSensitivityPatcher
    {
        public const float EnabledValue = 66536f;
        public const float DisabledValue = 16384f;

        // Writes the given rotation-speed modifier value (EnabledValue or DisabledValue) into
        // TdGame.u. Throws on failure.
        public static void Apply(string tdGamePackagePath, float targetValue)
        {
            FileAttributes attributes = File.GetAttributes(tdGamePackagePath);
            bool wasReadOnly = (attributes & FileAttributes.ReadOnly) == FileAttributes.ReadOnly;

            if (wasReadOnly)
            {
                File.SetAttributes(tdGamePackagePath, attributes & ~FileAttributes.ReadOnly);
            }

            try
            {
                using var package = UnrealLoader.LoadPackage(tdGamePackagePath, FileAccess.Read);
                package?.InitializePackage();

                if (package == null)
                    throw new Exception("Failed to load TdGame.u package.");

                var tdPlayerController = package.FindObject<UClass>("TdPlayerController");
                if (tdPlayerController == null)
                    throw new Exception("TdPlayerController class not found in TdGame.u");

                var updateRotationFunc = tdPlayerController.EnumerateFields<UFunction>().FirstOrDefault(f => f.Name == "UpdateRotation");
                if (updateRotationFunc == null)
                    throw new Exception("UpdateRotation function not found in TdPlayerController class.");

                updateRotationFunc.Load<UObjectRecordStream>();

                long floatOffset = FindRotSpeedModFloatOffset(updateRotationFunc);

                if (floatOffset == -1)
                    throw new Exception("Could not locate the rotation speed modifier float in UpdateRotation function.");

                byte[] data = File.ReadAllBytes(tdGamePackagePath);

                float currentValue = BitConverter.ToSingle(data, (int)floatOffset);
                if (Math.Abs(currentValue - DisabledValue) > 0.1f && Math.Abs(currentValue - EnabledValue) > 0.1f)
                {
                    throw new Exception($"Unexpected current value at offset {floatOffset}: {currentValue}. Expected either 16384 or 66536.");
                }

                byte[] newValueBytes = BitConverter.GetBytes(targetValue);
                Array.Copy(newValueBytes, 0, data, floatOffset, 4);

                File.WriteAllBytes(tdGamePackagePath, data);
            }
            finally
            {
                if (wasReadOnly)
                {
                    File.SetAttributes(tdGamePackagePath, attributes);
                }
            }
        }

        // Reads the current state: true if uniform sensitivity is enabled (66536), false if disabled,
        // or null if it cannot be determined (leave the UI unchanged).
        public static bool? ReadIsEnabled(string? tdGamePackagePath)
        {
            if (string.IsNullOrEmpty(tdGamePackagePath) || !File.Exists(tdGamePackagePath))
                return null;

            try
            {
                using var package = UnrealLoader.LoadPackage(tdGamePackagePath, FileAccess.Read);
                package?.InitializePackage();

                if (package == null)
                    return null;

                var tdPlayerController = package.FindObject<UClass>("TdPlayerController");
                if (tdPlayerController == null)
                    return null;

                var updateRotationFunc = tdPlayerController.EnumerateFields<UFunction>().FirstOrDefault(f => f.Name == "UpdateRotation");
                if (updateRotationFunc == null)
                    return null;

                updateRotationFunc.Load<UObjectRecordStream>();
                long floatOffset = FindRotSpeedModFloatOffset(updateRotationFunc);

                if (floatOffset == -1)
                    return null;

                byte[] data = File.ReadAllBytes(tdGamePackagePath);
                float currentValue = BitConverter.ToSingle(data, (int)floatOffset);

                return Math.Abs(currentValue - EnabledValue) < 0.1f;
            }
            catch
            {
                return null;
            }
        }

        private static long FindRotSpeedModFloatOffset(UFunction function)
        {
            if (function.ByteCodeManager == null || function.ExportTable == null)
                return -1;

            function.ByteCodeManager.Deserialize();
            var tokens = function.ByteCodeManager.DeserializedTokens;

            // look for the 2nd last float in the expression
            // for reference: RotSpeedMod = FMax(0.4, 1 - float(Min(1, int(Abs(float(Normalize(Rotation - myPawn.Rotation).Pitch) / 16384) + 0.3))));
            for (int i = 0; i < tokens.Count; i++)
            {
                if (tokens[i] is FloatConstToken floatToken)
                {
                    float value = floatToken.Value;
                    if (Math.Abs(value - DisabledValue) < 0.1f || Math.Abs(value - EnabledValue) < 0.1f)
                    {
                        return function.ExportTable.SerialOffset + function.ScriptOffset + floatToken.StoragePosition + 1;
                    }
                }
            }

            return -1;
        }
    }
}
