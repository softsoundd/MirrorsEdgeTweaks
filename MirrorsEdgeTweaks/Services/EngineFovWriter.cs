using MirrorsEdgeTweaks.Models;
using System.IO;

namespace MirrorsEdgeTweaks.Services
{
    // Writes the FOV into the Engine.u defaults that GameDataService located.
    // A splice into Engine.u moves these defaults, so the offsets must come from a load after the last splice.
    internal static class EngineFovWriter
    {
        public static void Write(string enginePath, PackageOffsets offsets, float fov)
        {
            byte[] value = BitConverter.GetBytes(fov);
            using var stream = new FileStream(enginePath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
            foreach (long offset in new[]
            {
                offsets.PlayerControllerDefaultFovOffset,
                offsets.PlayerControllerDesiredFovOffset,
                offsets.PlayerControllerFovAngleOffset,
                offsets.CameraFovOffset,
                offsets.CameraActorFovAngleOffset,
            })
            {
                if (offset == -1)
                    continue;
                stream.Position = offset;
                stream.Write(value, 0, value.Length);
            }
        }
    }
}
