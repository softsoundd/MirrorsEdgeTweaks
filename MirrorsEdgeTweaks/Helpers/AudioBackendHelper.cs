using System.IO;

namespace MirrorsEdgeTweaks.Helpers
{
    public static class AudioBackendHelper
    {
        public const string DefaultAssetFileName = "OpenAL.zip";
        public const string DefaultDeviceName = "Generic Hardware";
        public const string OpenAlSoftDeviceName = "OpenAL Soft";

        // OpenALSoftHRTF.zip puts these in Binaries beside its OpenAL32.dll proxy, and the other backends do not use them.
        private static readonly string[] HrtfFileNames = { "soft_oal.dll", "alsoft.ini", "openal-hrtf-proxy.ini" };

        public static bool IsHrtfInstalled(string gameDirectoryPath) =>
            File.Exists(Path.Combine(gameDirectoryPath, "Binaries", HrtfFileNames[0]));

        public static void DeleteHrtfFiles(string gameDirectoryPath)
        {
            foreach (string name in HrtfFileNames)
            {
                string path = Path.Combine(gameDirectoryPath, "Binaries", name);
                if (File.Exists(path))
                    File.Delete(path);
            }
        }

        // Returns the DeviceName= value from TdEngine.ini's [ALAudio.ALAudioDevice] section.
        // Returns null when the ini or the entry is missing.
        public static string? ReadDeviceName(string tdEngineIniPath)
        {
            if (!File.Exists(tdEngineIniPath))
                return null;

            bool inALAudioSection = false;
            foreach (string line in File.ReadLines(tdEngineIniPath))
            {
                string trimmedLine = line.Trim();
                if (trimmedLine.StartsWith("["))
                {
                    inALAudioSection = trimmedLine == "[ALAudio.ALAudioDevice]";
                    continue;
                }

                if (inALAudioSection && trimmedLine.StartsWith("DeviceName="))
                    return trimmedLine.Substring("DeviceName=".Length).Trim();
            }
            return null;
        }
    }
}
