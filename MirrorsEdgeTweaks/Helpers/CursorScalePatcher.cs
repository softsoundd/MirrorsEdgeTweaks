using System.IO;
using UELib;
using UELib.Core;

namespace MirrorsEdgeTweaks.Helpers
{
    // The UI mouse cursor ("Arrow" Texture2D) renders at its native pixel size - UGameUISceneClient
    // draws it via CalculateExtent() with no scaling so it shrinks at higher resolutions. The cursor cannot be made
    // bytecode dynamic as its native render is unanchorable + the texture lives in UISkin.CursorMap (a
    // native TMap with no script getter). So we scale the Arrow texture's SizeX/SizeY directly in the Startup_* packages
    public static class CursorScalePatcher
    {
        const int ArrowBaseSize = 32;
        const double AuthoredHeight = 1080.0;

        // Scale to base * max(1, height/1080) (clamped so <=1080p stays stock).
        public static void Apply(string cookedPcPath, int height)
        {
            int target = (int)Math.Round(ArrowBaseSize * Math.Max(1.0, height / AuthoredHeight));
            SetAllStartups(cookedPcPath, target);
        }

        // Restore the stock cursor size.
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
            byte[] data = File.ReadAllBytes(filePath);
            bool changed = false;

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
                long end = start + arrow.ExportTable.SerialSize;

                foreach (string propName in new[] { "SizeX", "SizeY" })
                {
                    var prop = arrow.Properties.OfType<UDefaultProperty>()
                        .FirstOrDefault(p => p.Name?.ToString() == propName);
                    if (prop == null || !int.TryParse(prop.Value, out int cur) || cur == newSize) continue;

                    int nameIdx = pkg.Names.FindIndex(n => n.ToString() == propName);
                    if (nameIdx < 0) continue;

                    if (WriteIntPropertyValue(data, nameIdx, cur, newSize, start, end))
                        changed = true;
                }
            }

            if (changed)
                PatchUtility.WritePreservingAttributes(filePath, data);
        }

        // Property tag layout: <NameIndex:8><TypeName:8><Size:4><ArrayIndex:4><Value...> so the int
        // value sits at name+24. Locating by the property's name index and verifying the value equals the
        // expected current value pins it to the correct tag
        static bool WriteIntPropertyValue(byte[] data, int nameIdx, int curValue, int newValue, long start, long end)
        {
            byte[] nameBytes = BitConverter.GetBytes((long)nameIdx);
            long limit = Math.Min(end, data.Length) - 28;
            for (long i = start; i < limit; i++)
            {
                bool match = true;
                for (int j = 0; j < 8; j++)
                    if (data[i + j] != nameBytes[j]) { match = false; break; }
                if (!match) continue;

                long valueOff = i + 24;
                if (valueOff + 4 > data.Length) continue;
                if (BitConverter.ToInt32(data, (int)valueOff) != curValue) continue;

                BitConverter.GetBytes(newValue).CopyTo(data, (int)valueOff);
                return true;
            }
            return false;
        }
    }
}
