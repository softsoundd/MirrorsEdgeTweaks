using System;
using System.IO;
using MirrorsEdgeTweaks.Services;

namespace MirrorsEdgeTweaks.Helpers
{
    public enum MultiInstancePatchState
    {
        Unknown,
        Unpatched,
        Patched
    }

    public static class MultiInstancePatchHelper
    {
        private const uint ImageBase = 0x00400000;
        private const string PatchKey = "multiInstance_bypass";

        public static MultiInstancePatchState GetPatchState(string exePath)
        {
            byte[] data = File.ReadAllBytes(exePath);
            string? version = ExeVersionDetector.DetectVersion(data, exePath);
            if (version == null) return MultiInstancePatchState.Unknown;

            if (version == "ea")
            {
                try { PatchUtility.DecryptOoaInPlace(data); }
                catch { return MultiInstancePatchState.Unknown; }
            }

            VersionAddressTable addrs;
            try { addrs = VersionAddressTable.Load(version); }
            catch { return MultiInstancePatchState.Unknown; }

            if (!addrs.InlinePatches.TryGetValue(PatchKey, out var patch))
                return MultiInstancePatchState.Unknown;

            int offset = (int)(patch.Va - ImageBase);
            if (offset < 0 || offset + patch.OldBytes.Length > data.Length)
                return MultiInstancePatchState.Unknown;

            if (data.AsSpan(offset, patch.OldBytes.Length).SequenceEqual(patch.OldBytes))
                return MultiInstancePatchState.Unpatched;

            if (patch.NewBytes.Length > 0 &&
                data.AsSpan(offset, patch.NewBytes.Length).SequenceEqual(patch.NewBytes))
                return MultiInstancePatchState.Patched;

            return MultiInstancePatchState.Unknown;
        }

        public static void ApplyPatch(string exePath)
        {
            byte[] data = File.ReadAllBytes(exePath);

            string? version = ExeVersionDetector.DetectVersion(data, exePath);
            if (version == null)
                throw new InvalidOperationException("Unrecognized executable -- cannot detect game version.");

            bool isOoa = version == "ea";
            OoaContext? ooaCtx = null;
            byte[]? ooaKey = null;

            if (isOoa)
            {
                string? dlfPath = OoaService.FindLicensePath(data);
                if (dlfPath == null)
                    throw new OoaLicenseNotFoundException(OoaService.GetExpectedLicensePath(data));
                ooaKey = OoaService.DecryptDlf(File.ReadAllBytes(dlfPath));
                OoaService.StripAuthenticode(data);
                ooaCtx = OoaService.DecryptSections(data, ooaKey);
            }

            var addrs = VersionAddressTable.Load(version);
            if (!addrs.InlinePatches.TryGetValue(PatchKey, out var patch))
                throw new InvalidOperationException($"No {PatchKey} patch defined for version '{version}'.");

            int offset = (int)(patch.Va - ImageBase);
            if (offset < 0 || offset + patch.OldBytes.Length > data.Length)
                throw new InvalidOperationException("Patch offset is out of bounds.");

            var site = data.AsSpan(offset, patch.OldBytes.Length);
            if (site.SequenceEqual(patch.NewBytes))
                return;
            if (!site.SequenceEqual(patch.OldBytes))
                throw new InvalidOperationException(
                    $"Unexpected bytes at patch site 0x{patch.Va:X8} -- executable may be modified.");

            Buffer.BlockCopy(patch.NewBytes, 0, data, offset, patch.NewBytes.Length);

            if (isOoa)
            {
                OoaService.UpdateEncBlockCrcs(data, ooaCtx!);
                OoaService.ReencryptSections(data, ooaKey!, ooaCtx!);
                data = OoaService.TruncateOverlay(data, ooaCtx!);
            }

            PatchUtility.WritePreservingAttributes(exePath, data);
        }

        public static void RemovePatch(string exePath)
        {
            byte[] data = File.ReadAllBytes(exePath);

            string? version = ExeVersionDetector.DetectVersion(data, exePath);
            if (version == null) return;

            bool isOoa = version == "ea";
            OoaContext? ooaCtx = null;
            byte[]? ooaKey = null;

            if (isOoa)
            {
                try
                {
                    string? dlfPath = OoaService.FindLicensePath(data);
                    if (dlfPath == null) return;
                    ooaKey = OoaService.DecryptDlf(File.ReadAllBytes(dlfPath));
                    OoaService.StripAuthenticode(data);
                    ooaCtx = OoaService.DecryptSections(data, ooaKey);
                }
                catch { return; }
            }

            VersionAddressTable addrs;
            try { addrs = VersionAddressTable.Load(version); }
            catch { return; }

            if (!addrs.InlinePatches.TryGetValue(PatchKey, out var patch))
                return;

            int offset = (int)(patch.Va - ImageBase);
            if (offset < 0 || offset + patch.NewBytes.Length > data.Length)
                return;

            if (data.AsSpan(offset, patch.OldBytes.Length).SequenceEqual(patch.OldBytes))
                return;

            Buffer.BlockCopy(patch.OldBytes, 0, data, offset, patch.OldBytes.Length);

            if (isOoa && ooaCtx != null && ooaKey != null)
            {
                OoaService.UpdateEncBlockCrcs(data, ooaCtx);
                OoaService.ReencryptSections(data, ooaKey, ooaCtx);
                data = OoaService.TruncateOverlay(data, ooaCtx);
            }

            PatchUtility.WritePreservingAttributes(exePath, data);
        }
    }
}
