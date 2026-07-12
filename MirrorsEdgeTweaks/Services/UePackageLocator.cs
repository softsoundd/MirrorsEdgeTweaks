using System.IO;
using UELib;
using UELib.Core;
using static UELib.Core.UStruct.UByteCodeDecompiler;

namespace MirrorsEdgeTweaks.Services
{
    internal sealed class UeFunctionInfo
    {
        public int SerialOffset;
        public int ExportIndex;    // 1-based package export index (objref)
        public IList<Token> Tokens = Array.Empty<Token>();
    }

    internal static class UePackageLocator
    {
        // Loads the package summary plus the name/import/export tables, but doesn't
        // construct or deserialise the objects (that is what InitializePackage does).
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
                ExportIndex = (int)func.PackageIndex,
                Tokens = func.ByteCodeManager.DeserializedTokens,
            };
        }

        // Returns the negative object reference of an import. outerName disambiguates same-named
        // properties on different classes (e.g. Texture2D.SizeX vs HUD.SizeX vs Canvas.SizeX).
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

        // Uses the export table directly so it finds exports even when UELib did not construct
        // a UObject for them
        public static int FindExportIndex(UnrealPackage package, string name, string outerName)
        {
            var export = package.Exports
                .FirstOrDefault(e => e.ObjectName?.ToString() == name
                                     && e.Outer?.ObjectName?.ToString() == outerName);
            return export == null ? 0 : package.Exports.IndexOf(export) + 1;
        }

        // Works against the export table alone (does not require InitializePackage).
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

        // Three-level match - disambiguates e.g. PlayerController.ConsoleCommand.ReturnValue from
        // other ConsoleCommand.ReturnValue properties belonging to different classes.
        public static UObject? FindExportObject(UnrealPackage package, string name, string outerName, string grandOuterName)
        {
            return package.Objects.FirstOrDefault(o => (int)o.PackageIndex > 0
                && o.Name?.ToString() == name
                && o.Outer?.Name?.ToString() == outerName
                && o.Outer?.Outer?.Name?.ToString() == grandOuterName);
        }

        public static int ObjRef(UObject? o) => o == null ? 0 : (int)o.PackageIndex;

        public static int FindNameIndex(UnrealPackage package, string name)
        {
            return package.Names.FindIndex(n => n.ToString() == name);
        }

        public static int Pos(Token t) => t.StoragePosition;

        public static byte[] Harvest(byte[] bc, Token t, int length)
        {
            var blob = new byte[length];
            Buffer.BlockCopy(bc, t.StoragePosition, blob, 0, length);
            return blob;
        }
    }
}
