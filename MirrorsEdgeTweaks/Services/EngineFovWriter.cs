using MirrorsEdgeTweaks.Helpers;
using MirrorsEdgeTweaks.Models;

namespace MirrorsEdgeTweaks.Services
{
    // Writes the FOV into the Engine.u defaults that GameDataService located.
    // A splice into Engine.u moves these defaults, so the offsets must come from a load after the last splice.
    internal static class EngineFovWriter
    {
        public static void Write(string enginePath, PackageOffsets offsets, float fov)
        {
            byte[] value = BitConverter.GetBytes(fov);
            var patches = new List<(long Offset, byte[] Bytes)>();
            foreach (long offset in new[]
            {
                offsets.PlayerControllerDefaultFovOffset,
                offsets.PlayerControllerDesiredFovOffset,
                offsets.PlayerControllerFovAngleOffset,
                offsets.CameraFovOffset,
                offsets.CameraActorFovAngleOffset,
            })
            {
                if (offset != -1)
                    patches.Add((offset, value));
            }

            if (patches.Count > 0)
                PatchUtility.PatchPreservingAttributes(enginePath, patches);
        }
    }
}
