using System.IO;
using UELib;
using UELib.Core;

namespace MirrorsEdgeTweaks.Helpers
{
    // Patches and reads the PhysX cloth-simulation timing in Engine.u. The simulation TimeStep
    // (PhysicsTimings / CompartmentTimingCloth) and the skeletal-mesh ClothIterations are written
    // directly as bytes within the WorldInfo / SkeletalMesh default-object export ranges.
    public static class PhysXTimingPatcher
    {
        private static string GetEnginePackagePath(string gameDirectoryPath) =>
            Path.Combine(gameDirectoryPath, "TdGame", "CookedPC", "Engine.u");

        // Applies the given PhysX FPS by writing the corresponding cloth TimeStep and a derived
        // ClothIterations value into Engine.u. Throws on missing file or unlocatable properties.
        public static void Apply(string gameDirectoryPath, int physxFps)
        {
            string enginePackagePath = GetEnginePackagePath(gameDirectoryPath);

            if (!File.Exists(enginePackagePath))
            {
                throw new FileNotFoundException("Engine.u file not found. Please ensure your game directory is correct.");
            }

            float physxTimestep = 1.0f / physxFps;

            // linear equation for skeletal mesh PhysX iterations - keeps cloth sim somewhat consistent
            int physxIterations = (int)(-0.016 * physxFps + 5.8);

            using var package = UnrealLoader.LoadPackage(enginePackagePath, FileAccess.Read);
            package?.InitializePackage();

            if (package == null)
            {
                throw new InvalidOperationException("Failed to load Engine.u package");
            }

            bool timestepModified = false;
            bool iterationsModified = false;

            var worldInfoClass = package.FindObject<UClass>("WorldInfo");
            if (worldInfoClass != null)
            {
                if (worldInfoClass.Default is UObject defaultObject)
                {
                    defaultObject.Load<UObjectRecordStream>();

                    if (defaultObject.ExportTable != null)
                    {
                        timestepModified = ModifyPhysicsTimingsTimeStep(enginePackagePath, defaultObject, physxTimestep);
                    }
                }
                else
                {
                    string defaultObjectName = "Default__WorldInfo";
                    var defaultObjectAlt = package.Objects.FirstOrDefault(o => o.Name == defaultObjectName);

                    if (defaultObjectAlt is UObject uObj && uObj.ExportTable != null)
                    {
                        uObj.Load<UObjectRecordStream>();
                        timestepModified = ModifyPhysicsTimingsTimeStep(enginePackagePath, uObj, physxTimestep);
                    }
                }
            }

            var skeletalMeshClass = package.FindObject<UClass>("SkeletalMesh");
            if (skeletalMeshClass != null)
            {
                if (skeletalMeshClass.Default is UObject defaultObject)
                {
                    defaultObject.Load<UObjectRecordStream>();

                    if (defaultObject.ExportTable != null)
                    {
                        iterationsModified = ModifyClothIterations(enginePackagePath, defaultObject, package, physxIterations);
                    }
                }
                else
                {
                    string defaultObjectName = "Default__SkeletalMesh";
                    var defaultObjectAlt = package.Objects.FirstOrDefault(o => o.Name == defaultObjectName);

                    if (defaultObjectAlt is UObject uObj && uObj.ExportTable != null)
                    {
                        uObj.Load<UObjectRecordStream>();
                        iterationsModified = ModifyClothIterations(enginePackagePath, uObj, package, physxIterations);
                    }
                }
            }

            if (!timestepModified)
            {
                throw new InvalidOperationException("Failed to locate PhysicsTimings CompartmentTimingCloth TimeStep in Engine.u");
            }

            if (!iterationsModified)
            {
                throw new InvalidOperationException("Failed to locate ClothIterations property in Engine.u");
            }
        }

        // Reads the current PhysX FPS from Engine.u (derived from the cloth TimeStep), or null if it
        // cannot be determined.
        public static int? Read(string? gameDirectoryPath)
        {
            if (string.IsNullOrEmpty(gameDirectoryPath))
                return null;

            try
            {
                string enginePackagePath = GetEnginePackagePath(gameDirectoryPath);

                if (!File.Exists(enginePackagePath))
                    return null;

                using var package = UnrealLoader.LoadPackage(enginePackagePath, FileAccess.Read);
                package?.InitializePackage();

                if (package == null)
                    return null;

                var worldInfoClass = package.FindObject<UClass>("WorldInfo");
                if (worldInfoClass?.Default is UObject defaultObject)
                {
                    defaultObject.Load<UObjectRecordStream>();

                    if (defaultObject.ExportTable != null)
                    {
                        float? timestep = ReadPhysicsTimingsTimeStep(enginePackagePath, defaultObject);
                        if (timestep.HasValue && timestep.Value > 0)
                        {
                            return (int)Math.Round(1.0f / timestep.Value);
                        }
                    }
                }
            }
            catch
            {
            }

            return null;
        }

        private static bool ModifyPhysicsTimingsTimeStep(string filePath, UObject defaultObject, float timestep)
        {
            using var package = UnrealLoader.LoadPackage(filePath, FileAccess.Read);
            package?.InitializePackage();

            if (package == null)
                return false;

            int timeStepNameIndex = package.Names.FindIndex(n => n.ToString() == "TimeStep");
            if (timeStepNameIndex == -1)
                return false;

            byte[] timeStepNameBytes = BitConverter.GetBytes((long)timeStepNameIndex);
            byte[] timestepBytes = BitConverter.GetBytes(timestep);

            var exportTable = defaultObject.ExportTable;
            if (exportTable == null)
                return false;

            byte[] data = File.ReadAllBytes(filePath);

            long searchStart = exportTable.SerialOffset;
            long searchEnd = searchStart + exportTable.SerialSize;

            // looking for 4th occurrence of the TimeStep property (cloth)
            int occurrences = 0;

            for (long i = searchStart; i < searchEnd - 28; i++)
            {
                bool nameMatch = true;
                for (int j = 0; j < 8; j++)
                {
                    if (data[i + j] != timeStepNameBytes[j])
                    {
                        nameMatch = false;
                        break;
                    }
                }

                if (nameMatch)
                {
                    occurrences++;

                    if (occurrences == 4)
                    {
                        Array.Copy(timestepBytes, 0, data, i + 24, 4);

                        File.WriteAllBytes(filePath, data);
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool ModifyClothIterations(string filePath, UObject defaultObject, UnrealPackage package, int iterations)
        {
            int nameIndex = package.Names.FindIndex(n => n.ToString() == "ClothIterations");
            if (nameIndex == -1)
                return false;

            byte[] nameIndexBytes = BitConverter.GetBytes((long)nameIndex);

            var exportTable = defaultObject.ExportTable;
            if (exportTable == null)
                return false;

            byte[] data = File.ReadAllBytes(filePath);

            long searchStart = exportTable.SerialOffset;
            long searchEnd = searchStart + exportTable.SerialSize;

            for (long i = searchStart; i < searchEnd - 28; i++)
            {
                bool nameMatch = true;
                for (int j = 0; j < 8; j++)
                {
                    if (data[i + j] != nameIndexBytes[j])
                    {
                        nameMatch = false;
                        break;
                    }
                }

                if (nameMatch)
                {
                    byte[] iterationsBytes = BitConverter.GetBytes(iterations);
                    Array.Copy(iterationsBytes, 0, data, i + 24, 4);

                    File.WriteAllBytes(filePath, data);
                    return true;
                }
            }

            return false;
        }

        private static float? ReadPhysicsTimingsTimeStep(string filePath, UObject defaultObject)
        {
            try
            {
                using var package = UnrealLoader.LoadPackage(filePath, FileAccess.Read);
                package?.InitializePackage();

                if (package == null)
                    return null;

                int timeStepNameIndex = package.Names.FindIndex(n => n.ToString() == "TimeStep");
                if (timeStepNameIndex == -1)
                    return null;

                byte[] timeStepNameBytes = BitConverter.GetBytes((long)timeStepNameIndex);

                var exportTable = defaultObject.ExportTable;
                if (exportTable == null)
                    return null;

                byte[] data = File.ReadAllBytes(filePath);

                long searchStart = exportTable.SerialOffset;
                long searchEnd = searchStart + exportTable.SerialSize;

                int occurrences = 0;

                for (long i = searchStart; i < searchEnd - 28; i++)
                {
                    bool nameMatch = true;
                    for (int j = 0; j < 8; j++)
                    {
                        if (data[i + j] != timeStepNameBytes[j])
                        {
                            nameMatch = false;
                            break;
                        }
                    }

                    if (nameMatch)
                    {
                        occurrences++;

                        if (occurrences == 4)
                        {
                            float timestep = BitConverter.ToSingle(data, (int)(i + 24));
                            return timestep;
                        }
                    }
                }

                return null;
            }
            catch
            {
                return null;
            }
        }
    }
}
