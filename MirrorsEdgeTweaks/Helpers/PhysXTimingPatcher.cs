using MirrorsEdgeTweaks.Services;
using System.IO;
using UELib;
using UELib.Core;

namespace MirrorsEdgeTweaks.Helpers
{
    // Reads and patches the PhysX cloth simulation timing in Engine.u.
    // The TimeStep of WorldInfo's CompartmentTimingCloth and SkeletalMesh's ClothIterations are written as bytes in their default objects.
    public static class PhysXTimingPatcher
    {
        private static string GetEnginePackagePath(string gameDirectoryPath) =>
            Path.Combine(gameDirectoryPath, "TdGame", "CookedPC", "Engine.u");

        // Both value offsets are found before the single write, so a failure cannot leave Engine.u with only one of the two values.
        public static void Apply(string gameDirectoryPath, int physxFps)
        {
            string enginePackagePath = GetEnginePackagePath(gameDirectoryPath);

            if (!File.Exists(enginePackagePath))
            {
                throw new FileNotFoundException("Engine.u file not found. Please ensure your game directory is correct.");
            }

            float physxTimestep = 1.0f / physxFps;

            // Fewer iterations at higher rates keep the cloth simulation roughly consistent.
            int physxIterations = (int)(-0.016 * physxFps + 5.8);

            long timeStepValueOffset;
            long clothIterationsValueOffset;

            // The package reader must be disposed before the file is rewritten,
            // because Windows refuses to replace a file while another handle is open on it.
            using (var package = UnrealLoader.LoadPackage(enginePackagePath, FileAccess.Read))
            {
                package?.InitializePackage();

                if (package == null)
                {
                    throw new InvalidOperationException("Failed to load Engine.u package");
                }

                UObject? worldInfoDefault = FindDefaultObject(package, "WorldInfo");
                UObject? skeletalMeshDefault = FindDefaultObject(package, "SkeletalMesh");

                // The cloth compartment is the 4th TimeStep property in the WorldInfo default object.
                timeStepValueOffset = worldInfoDefault != null
                    ? LocateValueOffset(enginePackagePath, package, worldInfoDefault, "TimeStep", occurrence: 4)
                    : -1;
                clothIterationsValueOffset = skeletalMeshDefault != null
                    ? LocateValueOffset(enginePackagePath, package, skeletalMeshDefault, "ClothIterations", occurrence: 1)
                    : -1;
            }

            if (timeStepValueOffset < 0)
            {
                throw new InvalidOperationException("Failed to locate PhysicsTimings CompartmentTimingCloth TimeStep in Engine.u");
            }

            if (clothIterationsValueOffset < 0)
            {
                throw new InvalidOperationException("Failed to locate ClothIterations property in Engine.u");
            }

            PatchUtility.PatchPreservingAttributes(enginePackagePath,
            [
                (timeStepValueOffset, BitConverter.GetBytes(physxTimestep)),
                (clothIterationsValueOffset, BitConverter.GetBytes(physxIterations)),
            ]);
        }

        public static int? Read(string? gameDirectoryPath)
        {
            if (string.IsNullOrEmpty(gameDirectoryPath))
                return null;

            try
            {
                string enginePackagePath = GetEnginePackagePath(gameDirectoryPath);

                if (!File.Exists(enginePackagePath))
                    return null;

                using var lease = GameFileReadScope.OpenPackage(enginePackagePath);
                var package = lease.Package;

                UObject? worldInfoDefault = FindDefaultObject(package, "WorldInfo");
                if (worldInfoDefault == null)
                    return null;

                long timeStepValueOffset = LocateValueOffset(enginePackagePath, package, worldInfoDefault, "TimeStep", occurrence: 4);
                if (timeStepValueOffset < 0)
                    return null;

                float timestep = BitConverter.ToSingle(UePackageLocator.ReadRange(enginePackagePath, timeStepValueOffset, sizeof(float)));
                if (timestep > 0)
                    return (int)Math.Round(1.0f / timestep);
            }
            catch
            {
            }

            return null;
        }

        // Returns the class's default object.
        // Returns the object named Default__<class> in package layouts where the class has no linked default.
        private static UObject? FindDefaultObject(UnrealPackage package, string className)
        {
            var uClass = package.FindObject<UClass>(className);
            if (uClass == null)
                return null;

            if (uClass.Default is UObject defaultObject)
            {
                defaultObject.Load<UObjectRecordStream>();
                return defaultObject.ExportTable != null ? defaultObject : null;
            }

            var defaultObjectAlt = package.Objects.FirstOrDefault(o => o.Name == $"Default__{className}");
            if (defaultObjectAlt is UObject uObj && uObj.ExportTable != null)
            {
                uObj.Load<UObjectRecordStream>();
                return uObj;
            }

            return null;
        }

        // Returns the file offset of the value of the property's nth tag in the default object, 24 bytes after its name.
        // Returns -1 when the default object has no nth tag for the property.
        private static long LocateValueOffset(string packagePath, UnrealPackage package, UObject defaultObject, string propertyName, int occurrence)
        {
            int nameIndex = package.Names.FindIndex(n => n.ToString() == propertyName);
            if (nameIndex == -1)
                return -1;

            byte[] nameIndexBytes = BitConverter.GetBytes((long)nameIndex);

            var exportTable = defaultObject.ExportTable;
            if (exportTable == null)
                return -1;

            byte[] export = UePackageLocator.ReadRange(packagePath, exportTable.SerialOffset, exportTable.SerialSize);
            int occurrences = 0;
            for (int i = 0; i < export.Length - 28; i++)
            {
                if (export.AsSpan(i, 8).SequenceEqual(nameIndexBytes) && ++occurrences == occurrence)
                    return exportTable.SerialOffset + i + 24;
            }

            return -1;
        }
    }
}
