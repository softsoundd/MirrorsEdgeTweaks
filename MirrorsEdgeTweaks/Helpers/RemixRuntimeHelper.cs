using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace MirrorsEdgeTweaks.Helpers
{
    // Identifies the Direct3D 9 runtime that Binaries\d3d9.dll provides.
    public enum D3D9Runtime
    {
        // Binaries holds no d3d9.dll, so the game uses Windows' Direct3D 9.
        Stock,
        Dxvk,
        // This DXVK predates 2.0, which added d3d9.useD32forD24.
        DxvkOutdated,
        RemixNgx,
        // This NGX passthrough runtime predates its reversed depth support.
        RemixNgxOutdated,
        // This RTX Remix build path traces, and comes from NVIDIA or the fork's mirrors-edge branch.
        RemixPathTraced,
        // The d3d9.dll belongs to another mod, such as ReShade.
        OtherWrapper
    }

    // Detects the runtime beside MirrorsEdge.exe, and installs and removes RTX Remix NGX.
    // Its d3d9.dll is a 32-bit bridge client that runs the 64-bit runtime in .trex through NvRemixBridge.exe.
    public static class RemixRuntimeHelper
    {
        public const string AssetFileName = "dxvk-remix-d3d9.zip";

        private const string BridgeProductPrefix = "bridge-remix";
        private const string RuntimeDirectoryName = ".trex";

        // Holds the download's HTTP validator, because the version resources cannot identify a build.
        // The file is deleted along with .trex, so it always describes the runtime beside it.
        private const string InstalledVersionFileName = "MirrorsEdgeTweaks-asset.txt";

        private static readonly byte[] NgxPassthroughBytes = Encoding.ASCII.GetBytes("rtx.ngxPassthrough");
        private static readonly TimeSpan ExitTimeout = TimeSpan.FromSeconds(10);

        public static D3D9Runtime Detect(string binariesDirectory)
        {
            string dll = Path.Combine(binariesDirectory, "d3d9.dll");
            if (!File.Exists(dll))
                return D3D9Runtime.Stock;

            string? productName = FileVersionInfo.GetVersionInfo(dll).ProductName;
            if (DxvkHelper.IsDxvkProduct(productName))
                return DxvkHelper.HasFloatDepthOption(File.ReadAllBytes(dll)) ? D3D9Runtime.Dxvk : D3D9Runtime.DxvkOutdated;
            if (!IsBridgeProduct(productName))
                return D3D9Runtime.OtherWrapper;

            string runtime = Path.Combine(binariesDirectory, RuntimeDirectoryName, "d3d9.dll");
            return ClassifyRemix(File.Exists(runtime) ? File.ReadAllBytes(runtime) : Array.Empty<byte>());
        }

        // The bracketed build tag differs between builds, so only the start of the product name is compared.
        internal static bool IsBridgeProduct(string? productName) =>
            productName?.Trim().StartsWith(BridgeProductPrefix, StringComparison.OrdinalIgnoreCase) == true;

        // NVIDIA's builds carry the same version resources, so only the runtime's option names identify the NGX passthrough runtime.
        // A bridge without its runtime counts as path traced, so it is only replaced after a confirmation.
        internal static D3D9Runtime ClassifyRemix(ReadOnlySpan<byte> runtimeDll)
        {
            if (runtimeDll.IndexOf(NgxPassthroughBytes) < 0)
                return D3D9Runtime.RemixPathTraced;
            return DxvkHelper.HasFloatDepthOption(runtimeDll) ? D3D9Runtime.RemixNgx : D3D9Runtime.RemixNgxOutdated;
        }

        // The asset's entries start with Binaries/, so it extracts into the game directory.
        // The bridge client only works with a runtime from a compatible build, so both are always replaced.
        public static void Install(string zipPath, string gameDirectory) =>
            Install(zipPath, gameDirectory, Detect(Path.Combine(gameDirectory, "Binaries")));

        internal static void Install(string zipPath, string gameDirectory, D3D9Runtime current)
        {
            using (ZipArchive archive = ZipFile.OpenRead(zipPath))
            {
                if (!HasEntry(archive, "Binaries/d3d9.dll") || !HasEntry(archive, "Binaries/.trex/d3d9.dll"))
                    throw new UserFacingException("The RTX Remix NGX download is incomplete. Please try again later.");
            }

            string binaries = Path.Combine(gameDirectory, "Binaries");
            RemoveRuntime(binaries, current);

            // A .trex folder can outlast its bridge client, and files from another Remix build must not linger.
            DeleteRuntimeDirectory(binaries);

            ZipExtractHelper.ExtractOverwritingReadOnly(zipPath, gameDirectory);
        }

        public static void RemoveCurrentRuntime(string binariesDirectory) =>
            RemoveRuntime(binariesDirectory, Detect(binariesDirectory));

        // Keeps every config file, because the next runtime may read them.
        private static void RemoveRuntime(string binariesDirectory, D3D9Runtime runtime)
        {
            switch (runtime)
            {
                case D3D9Runtime.Stock:
                    return;
                case D3D9Runtime.Dxvk:
                case D3D9Runtime.DxvkOutdated:
                    DxvkHelper.RemoveDll(binariesDirectory);
                    return;
                case D3D9Runtime.OtherWrapper:
                    throw new UserFacingException(
                        "Another mod's d3d9.dll (for example ReShade) is in the game's Binaries folder. Please remove it and try again.");
                default:
                    Remove(binariesDirectory);
                    return;
            }
        }

        // Keeps dxvk.conf, the settings saved from the Remix menu in rtx.conf and user.conf, and the runtime's logs in rtx-remix.
        public static void Remove(string binariesDirectory)
        {
            DeleteFile(Path.Combine(binariesDirectory, "d3d9.dll"));
            DeleteFile(Path.Combine(binariesDirectory, "NvRemixLauncher32.exe"));
            DeleteRuntimeDirectory(binariesDirectory);
        }

        // rtx.conf outranks dxvk.conf, and the runtime stores depth as float unless one of them turns it off.
        public static bool IsFloatDepthOffInRtxConf(string binariesDirectory)
        {
            string conf = Path.Combine(binariesDirectory, "rtx.conf");
            return File.Exists(conf) && DxvkHelper.ReadFloatDepth(File.ReadAllLines(conf)) == false;
        }

        public static string? ReadInstalledVersion(string binariesDirectory)
        {
            string path = Path.Combine(binariesDirectory, RuntimeDirectoryName, InstalledVersionFileName);
            if (!File.Exists(path))
                return null;

            string version = File.ReadAllText(path).Trim();
            return version.Length > 0 ? version : null;
        }

        public static void WriteInstalledVersion(string binariesDirectory, string version) =>
            File.WriteAllText(Path.Combine(binariesDirectory, RuntimeDirectoryName, InstalledVersionFileName), version);

        public static bool IsGameOrBridgeRunning() => IsRunning("MirrorsEdge") || IsRunning("NvRemixBridge");

        // The bridge server can outlive the game, especially after a crash, and it keeps the .trex DLLs open.
        // Returns false when either one is still running after ExitTimeout.
        public static async Task<bool> WaitForGameExitAsync()
        {
            var elapsed = Stopwatch.StartNew();
            while (IsGameOrBridgeRunning())
            {
                if (elapsed.Elapsed >= ExitTimeout)
                    return false;
                await Task.Delay(500);
            }
            return true;
        }

        private static bool IsRunning(string processName)
        {
            Process[] processes = Process.GetProcessesByName(processName);
            try
            {
                return processes.Length > 0;
            }
            finally
            {
                foreach (Process process in processes)
                    process.Dispose();
            }
        }

        private static bool HasEntry(ZipArchive archive, string fullName) =>
            archive.Entries.Any(entry => entry.FullName.Replace('\\', '/').Equals(fullName, StringComparison.OrdinalIgnoreCase));

        private static void DeleteFile(string path)
        {
            var file = new FileInfo(path);
            if (!file.Exists)
                return;
            if (file.IsReadOnly)
                file.IsReadOnly = false;
            file.Delete();
        }

        // Directory.Delete cannot delete a read-only file or folder.
        private static void DeleteRuntimeDirectory(string binariesDirectory)
        {
            var directory = new DirectoryInfo(Path.Combine(binariesDirectory, RuntimeDirectoryName));
            if (!directory.Exists)
                return;

            foreach (FileSystemInfo entry in directory.EnumerateFileSystemInfos("*", SearchOption.AllDirectories).Append(directory))
            {
                if ((entry.Attributes & FileAttributes.ReadOnly) != 0)
                    entry.Attributes &= ~FileAttributes.ReadOnly;
            }
            directory.Delete(recursive: true);
        }
    }
}
