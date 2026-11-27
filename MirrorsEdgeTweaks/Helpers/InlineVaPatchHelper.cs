using MirrorsEdgeTweaks.Services;
using System.IO;

namespace MirrorsEdgeTweaks.Helpers
{
    public enum InlinePatchState
    {
        Unknown,
        Unpatched,
        Patched
    }

    // Applies the simple byte patches that each version's address table lists under "inline_patches",
    // such as the multi-instance and ambiguous-package bypasses.
    public static class InlineVaPatchHelper
    {
        public const string MultiInstanceKey = "multiInstance_bypass";
        public const string AmbiguousPackageKey = "ambiguousPackage_bypass";

        // Makes the game skip the registry Locale value, so it reads its language from TdEngine.ini.
        public const string LanguageRegistryBypassKey = "languageRegistry_bypass";

        private const uint ImageBase = 0x00400000;

        public static InlinePatchState GetPatchState(string exePath, string patchKey)
        {
            ExeImage exe = GameFileReadScope.ReadExe(exePath);
            string? version = exe.Version;
            if (version == null) return InlinePatchState.Unknown;

            byte[] data = exe.Raw;
            if (version == "ea")
            {
                try { data = exe.GetDecrypted(); }
                catch { return InlinePatchState.Unknown; }
            }

            VersionAddressTable addrs;
            try { addrs = VersionAddressTable.Load(version); }
            catch { return InlinePatchState.Unknown; }

            if (!addrs.InlinePatches.TryGetValue(patchKey, out var patch))
                return InlinePatchState.Unknown;

            int offset = (int)(patch.Va - ImageBase);
            if (!FitsInImage(data, offset, patch))
                return InlinePatchState.Unknown;

            if (data.AsSpan(offset, patch.OldBytes.Length).SequenceEqual(patch.OldBytes))
                return InlinePatchState.Unpatched;

            if (patch.NewBytes.Length > 0 &&
                data.AsSpan(offset, patch.NewBytes.Length).SequenceEqual(patch.NewBytes))
                return InlinePatchState.Patched;

            return InlinePatchState.Unknown;
        }

        public static void ApplyPatch(string exePath, string patchKey)
        {
            byte[] data = File.ReadAllBytes(exePath);

            string? version = ExeVersionDetector.DetectVersion(data, exePath);
            if (version == null)
                throw new UserFacingException("This version of MirrorsEdge.exe is not supported.");

            bool isOoa = version == "ea";
            PatchUtility.OoaSession? ooa = isOoa ? PatchUtility.BeginOoa(data) : null;

            InlinePatchDef patch = LoadPatch(version, patchKey);
            int offset = (int)(patch.Va - ImageBase);
            if (!FitsInImage(data, offset, patch))
                throw new InvalidOperationException("Patch offset is out of bounds.");

            var site = data.AsSpan(offset, patch.OldBytes.Length);
            if (site.SequenceEqual(patch.NewBytes))
                return;
            if (!site.SequenceEqual(patch.OldBytes))
                throw new InvalidOperationException(
                    $"Unexpected bytes at patch site 0x{patch.Va:X8} - executable may be modified.");

            Buffer.BlockCopy(patch.NewBytes, 0, data, offset, patch.NewBytes.Length);

            if (isOoa)
                data = PatchUtility.FinishOoa(data, ooa!);

            PatchUtility.WritePreservingAttributes(exePath, data);
        }

        // Only restores a site that holds the patch, so bytes that another tool or a different version put there are never overwritten.
        public static void RemovePatch(string exePath, string patchKey)
        {
            byte[] data = File.ReadAllBytes(exePath);

            string? version = ExeVersionDetector.DetectVersion(data, exePath);
            if (version == null)
                throw new UserFacingException("This version of MirrorsEdge.exe is not supported.");

            bool isOoa = version == "ea";
            PatchUtility.OoaSession? ooa = isOoa ? PatchUtility.BeginOoa(data) : null;

            InlinePatchDef patch = LoadPatch(version, patchKey);
            int offset = (int)(patch.Va - ImageBase);
            if (!FitsInImage(data, offset, patch))
                throw new InvalidOperationException("Patch offset is out of bounds.");

            if (data.AsSpan(offset, patch.OldBytes.Length).SequenceEqual(patch.OldBytes))
                return;
            if (patch.NewBytes.Length == 0 || !data.AsSpan(offset, patch.NewBytes.Length).SequenceEqual(patch.NewBytes))
                throw new InvalidOperationException(
                    $"Unexpected bytes at patch site 0x{patch.Va:X8} - executable may be modified.");

            Buffer.BlockCopy(patch.OldBytes, 0, data, offset, patch.OldBytes.Length);

            if (isOoa)
                data = PatchUtility.FinishOoa(data, ooa!);

            PatchUtility.WritePreservingAttributes(exePath, data);
        }

        private static InlinePatchDef LoadPatch(string version, string patchKey) =>
            VersionAddressTable.Load(version).InlinePatches.TryGetValue(patchKey, out var patch)
                ? patch
                : throw new InvalidOperationException($"No {patchKey} patch defined for version '{version}'.");

        private static bool FitsInImage(byte[] data, int offset, InlinePatchDef patch) =>
            offset >= 0 && offset + Math.Max(patch.OldBytes.Length, patch.NewBytes.Length) <= data.Length;
    }
}
