using MirrorsEdgeTweaks.Services;
using System.IO;
using UELib;
using UELib.Core;

namespace MirrorsEdgeTweaks.Helpers
{
    // Scales the UI mouse cursor, the "Arrow" Texture2D, by writing its SizeX and SizeY into the Startup_* packages.
    // UGameUISceneClient draws the cursor at its native pixel size through CalculateExtent(), so it shrinks at higher resolutions.
    // Bytecode cannot scale it at runtime, because its native draw has no script hook,
    // and the texture lives in UISkin.CursorMap, a native TMap with no script getter.
    public static class CursorScalePatcher
    {
        const int ArrowBaseSize = 32;
        const double AuthoredHeight = 1080.0;

        public static void Apply(string cookedPcPath, int height)
        {
            int target = (int)Math.Round(ArrowBaseSize * Math.Max(1.0, height / AuthoredHeight));
            SetAllStartups(cookedPcPath, target);
        }

        public static void Remove(string cookedPcPath) => SetAllStartups(cookedPcPath, ArrowBaseSize);

        static void SetAllStartups(string cookedPcPath, int newSize)
        {
            if (!Directory.Exists(cookedPcPath)) return;
            foreach (string file in Directory.GetFiles(cookedPcPath, "Startup_*"))
            {
                try { SetArrowSize(file, newSize); }
                catch { }
            }
        }

        static void SetArrowSize(string filePath, int newSize)
        {
            var patches = new List<(long Offset, byte[] Bytes)>();

            using (var pkg = UnrealLoader.LoadPackage(filePath, FileAccess.Read))
            {
                pkg.InitializePackage();
                var arrow = pkg.Objects.FirstOrDefault(o => (int)o.PackageIndex > 0
                    && o.Name?.ToString() == "Arrow"
                    && o.Class?.Name?.ToString() == "Texture2D");
                if (arrow?.ExportTable == null) return;

                arrow.Load<UObjectRecordStream>();
                if (arrow.Properties == null) return;

                long start = arrow.ExportTable.SerialOffset;
                byte[] export = UePackageLocator.ReadRange(filePath, start, arrow.ExportTable.SerialSize);

                foreach (string propName in new[] { "SizeX", "SizeY" })
                {
                    var prop = arrow.Properties.OfType<UDefaultProperty>()
                        .FirstOrDefault(p => p.Name?.ToString() == propName);
                    if (prop == null || !int.TryParse(prop.Value, out int cur) || cur == newSize) continue;

                    int nameIdx = pkg.Names.FindIndex(n => n.ToString() == propName);
                    if (nameIdx < 0) continue;

                    int valueOffset = FindIntPropertyValue(export, nameIdx, cur);
                    if (valueOffset >= 0)
                        patches.Add((start + valueOffset, BitConverter.GetBytes(newSize)));
                }
            }

            if (patches.Count > 0)
                PatchUtility.PatchPreservingAttributes(filePath, patches);
        }

        // A property tag is <NameIndex:8><TypeName:8><Size:4><ArrayIndex:4><Value>, so an int value sits 24 bytes after the name.
        // The tag is found by its name index and confirmed by its current value.
        // Returns the value's offset in the export.
        // Returns -1 when the export has no such tag.
        static int FindIntPropertyValue(byte[] export, int nameIdx, int curValue)
        {
            byte[] nameBytes = BitConverter.GetBytes((long)nameIdx);
            for (int i = 0; i < export.Length - 28; i++)
            {
                if (export.AsSpan(i, 8).SequenceEqual(nameBytes) && BitConverter.ToInt32(export, i + 24) == curValue)
                    return i + 24;
            }
            return -1;
        }
    }
}
