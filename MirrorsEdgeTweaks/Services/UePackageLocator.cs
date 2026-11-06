using System.Globalization;
using System.IO;
using UELib;
using UELib.Core;
using static UELib.Core.UStruct.UByteCodeDecompiler;

namespace MirrorsEdgeTweaks.Services
{
    internal sealed class UeFunctionInfo
    {
        public int SerialOffset;
        public int SerialSize;

        // Holds the 1-based export index, which is also the export's object reference.
        public int ExportIndex;

        public IList<Token> Tokens = Array.Empty<Token>();
    }

    internal static class UePackageLocator
    {
        // Loads the summary and the name, import and export tables, but not the objects, which InitializePackage constructs.
        public static UnrealPackage LoadHeader(string path)
        {
            return UnrealLoader.LoadPackage(path, FileAccess.Read)
                ?? throw new InvalidOperationException($"UELib failed to load package: {path}");
        }

        public static UnrealPackage Load(string path)
        {
            var package = LoadHeader(path);
            try
            {
                package.InitializePackage();
            }
            catch
            {
                package.Dispose();
                throw;
            }
            return package;
        }

        public static UeFunctionInfo? FindFunction(UnrealPackage package, string className, string funcName)
        {
            var func = package.Objects
                .OfType<UFunction>()
                .FirstOrDefault(f => f.Name?.ToString() == funcName
                                     && f.Outer?.Name?.ToString() == className);
            if (func?.ExportTable == null || func.ByteCodeManager == null) return null;

            func.ByteCodeManager.Deserialize();
            return new UeFunctionInfo
            {
                SerialOffset = (int)func.ExportTable.SerialOffset,
                SerialSize = func.ExportTable.SerialSize,
                ExportIndex = (int)func.PackageIndex,
                Tokens = func.ByteCodeManager.DeserializedTokens,
            };
        }

        // Returns the negative object reference of an import.
        // outerName tells apart properties with the same name on different classes, such as Texture2D.SizeX and HUD.SizeX.
        public static int FindImportObjRef(UnrealPackage package, string name, string? className = null,
            string? outerName = null)
        {
            for (int i = 0; i < package.Imports.Count; i++)
            {
                var imp = package.Imports[i];
                if (imp.ObjectName?.ToString() != name) continue;
                if (className != null && imp.ClassName?.ToString() != className) continue;
                if (outerName != null && imp.Outer?.ObjectName?.ToString() != outerName) continue;
                return -(i + 1);
            }
            return 0;
        }

        // Searches the export table, so it also finds exports that UELib did not construct a UObject for.
        public static int FindExportIndex(UnrealPackage package, string name, string outerName)
        {
            var export = package.Exports
                .FirstOrDefault(e => e.ObjectName?.ToString() == name
                                     && e.Outer?.ObjectName?.ToString() == outerName);
            return export == null ? 0 : package.Exports.IndexOf(export) + 1;
        }

        // Works without InitializePackage, because it only reads the export table.
        public static int FindExportSerialOffset(UnrealPackage package, string className, string name)
        {
            var export = package.Exports
                .FirstOrDefault(e => e.ObjectName?.ToString() == name
                                     && e.Outer?.ObjectName?.ToString() == className);
            return export == null ? -1 : (int)export.SerialOffset;
        }

        public static UObject? FindExportObject(UnrealPackage package, string name, string outerName)
        {
            return package.Objects.FirstOrDefault(o => (int)o.PackageIndex > 0
                && o.Name?.ToString() == name
                && o.Outer?.Name?.ToString() == outerName);
        }

        // grandOuterName tells PlayerController.ConsoleCommand.ReturnValue apart from the ConsoleCommand.ReturnValue of other classes.
        public static UObject? FindExportObject(UnrealPackage package, string name, string outerName, string grandOuterName)
        {
            return package.Objects.FirstOrDefault(o => (int)o.PackageIndex > 0
                && o.Name?.ToString() == name
                && o.Outer?.Name?.ToString() == outerName
                && o.Outer?.Outer?.Name?.ToString() == grandOuterName);
        }

        public static int ObjRef(UObject? o) => o == null ? 0 : (int)o.PackageIndex;

        // Opens its own handle, so it works while a loaded package still holds the file open.
        public static byte[] ReadRange(string path, long offset, int count)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            stream.Position = offset;
            byte[] bytes = new byte[count];
            stream.ReadExactly(bytes);
            return bytes;
        }

        public static int FindNameIndex(UnrealPackage package, string name)
        {
            return package.Names.FindIndex(n => n.ToString() == name);
        }

        // Returns the property's value in the class default object, as UELib formats it.
        // Returns null when the class or the property is missing.
        public static string? ReadDefault(UnrealPackage package, string className, string propertyName)
        {
            if (package.FindObject<UClass>(className)?.Default is not UObject cdo)
                return null;
            cdo.Load<UObjectRecordStream>();
            return cdo.Properties?.FirstOrDefault(p => p.Name == propertyName)?.Value;
        }

        public static float? ReadFloatDefault(UnrealPackage package, string className, string propertyName) =>
            float.TryParse(ReadDefault(package, className, propertyName), NumberStyles.Float, CultureInfo.InvariantCulture,
                out float value) ? value : null;

        // In Mirror's Edge, a token's Position equals its StoragePosition,
        // so this offset is both a jump target and an offset into the serialized script.
        public static int Pos(Token t) => t.StoragePosition;

        public static byte[] Harvest(byte[] bc, Token t, int length)
        {
            var blob = new byte[length];
            Buffer.BlockCopy(bc, t.StoragePosition, blob, 0, length);
            return blob;
        }

        // Returns the offsets of the Jump, JumpIfNot and Case tokens.
        // Token positions avoid a byte scan, which could mistake a data byte, such as a 0x06 in an ObjectConst, for a jump.
        public static List<int> ExtractJumpPositions(IList<Token> tokens)
        {
            var positions = new List<int>();
            foreach (var t in tokens)
                if (t is JumpToken || t is JumpIfNotToken || t is CaseToken)
                    positions.Add(Pos(t));
            return positions;
        }
    }
}
