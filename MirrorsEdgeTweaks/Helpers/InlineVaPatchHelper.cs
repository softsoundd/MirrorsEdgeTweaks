using System.IO;

namespace MirrorsEdgeTweaks.Helpers
{
    public enum InlinePatchState
    {
        Unknown,
        Unpatched,
        Patched
    }

    // Shared implementation for the simple inline byte patches defined per game version in the
    // VersionAddressTable "inlinePatches" section (multi-instance bypass, ambiguous-package
    // bypass). Handles version detection, OOA (EA) transparency, pre-write byte verification and
    // attribute-preserving writes; the per-feature classes this replaces were ~98% identical.
    public static class InlineVaPatchHelper
    {
        public const string MultiInstanceKey = "multiInstance_bypass";
        public const string AmbiguousPackageKey = "ambiguousPackage_bypass";

        private const uint ImageBase = 0x00400000;

        public static InlinePatchState GetPatchState(string exePath, string patchKey)
        {
            byte[] data = File.ReadAllBytes(exePath);
            string? version = ExeVersionDetector.DetectVersion(data, exePath);
            if (version == null) return InlinePatchState.Unknown;

            if (version == "ea")
            {
                try { PatchUtility.DecryptOoaInPlace(data); }
                catch { return InlinePatchState.Unknown; }
            }

            VersionAddressTable addrs;
            try { addrs = VersionAddressTable.Load(version); }
            catch { return InlinePatchState.Unknown; }

            if (!addrs.InlinePatches.TryGetValue(patchKey, out var patch))
                return InlinePatchState.Unknown;

            int offset = (int)(patch.Va - ImageBase);
            if (offset < 0 || offset + patch.OldBytes.Length > data.Length)
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
                throw new InvalidOperationException("Unrecognized executable - cannot detect game version.");

            bool isOoa = version == "ea";
            PatchUtility.OoaSession? ooa = isOoa ? PatchUtility.BeginOoa(data) : null;

            var addrs = VersionAddressTable.Load(version);
            if (!addrs.InlinePatches.TryGetValue(patchKey, out var patch))
                throw new InvalidOperationException($"No {patchKey} patch defined for version '{version}'.");

            int offset = (int)(patch.Va - ImageBase);
            if (offset < 0 || offset + patch.OldBytes.Length > data.Length)
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

        public static void RemovePatch(string exePath, string patchKey)
        {
            byte[] data = File.ReadAllBytes(exePath);

            string? version = ExeVersionDetector.DetectVersion(data, exePath);
            if (version == null) return;

            bool isOoa = version == "ea";
            PatchUtility.OoaSession? ooa = null;
            if (isOoa)
            {
                try { ooa = PatchUtility.BeginOoa(data); }
                catch { return; }
            }

            VersionAddressTable addrs;
            try { addrs = VersionAddressTable.Load(version); }
            catch { return; }

            if (!addrs.InlinePatches.TryGetValue(patchKey, out var patch))
                return;

            int offset = (int)(patch.Va - ImageBase);
            if (offset < 0 || offset + patch.NewBytes.Length > data.Length)
                return;

            if (data.AsSpan(offset, patch.OldBytes.Length).SequenceEqual(patch.OldBytes))
                return;

            Buffer.BlockCopy(patch.OldBytes, 0, data, offset, patch.OldBytes.Length);

            if (isOoa && ooa != null)
                data = PatchUtility.FinishOoa(data, ooa);

            PatchUtility.WritePreservingAttributes(exePath, data);
        }
    }
}
