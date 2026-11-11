using MirrorsEdgeTweaks.Services;

namespace MirrorsEdgeTweaks.Helpers
{
    public sealed class ExeRestoreResult
    {
        public List<string> Removed { get; } = new();

        // Each name completes the sentence "Could not remove {Name}."
        public List<(string Name, Exception Error)> Failures { get; } = new();

        // Names the patches whose state is unclear in an exe of a known version.
        public List<string> Undetermined { get; } = new();
    }

    // Removes every patch that Tweaks makes to MirrorsEdge.exe, apart from Unlocked Configs,
    // which is only safe to remove once the config files are stock.
    public static class ExeStockRestorer
    {
        // State returns true while the patch is applied.
        // It returns false while that part of the exe is stock.
        // It returns null when it cannot tell, such as for an exe of an unknown version.
        private sealed record Patch(string Name, Func<string, bool?> State, Action<string> Remove);

        private static readonly Patch[] Patches =
        {
            new("the logging patch",
                exe => StateOf(LoggingPatchHelper.GetPatchState(exe), LoggingPatchState.Patched, LoggingPatchState.Unpatched),
                LoggingPatchHelper.RemovePatch),
            new("reversed depth",
                exe => StateOf(ReversedDepthPatcher.GetState(exe), ReversedDepthState.Patched, ReversedDepthState.Unpatched),
                ReversedDepthPatcher.Remove),
            new("supersampling",
                exe => StateOf(SupersamplePatchHelper.GetPatchState(exe), SupersamplePatchState.Patched, SupersamplePatchState.Unpatched),
                SupersamplePatchHelper.RemovePatch),
            new("the high-res UI fix for subtitles", SubtitleRegionPatcher.IsApplied, SubtitleRegionPatcher.Remove),
            new("the high-res UI fix",
                exe => StateOf(MultiFontScalePatcher.DetectState(exe), MultiFontScaleState.Patched, MultiFontScaleState.Unpatched),
                MultiFontScalePatcher.Remove),
            new("the FOV and render resolution fixes", RenderTargetPatcher.IsApplied, RenderTargetPatcher.Remove),
            new("the developer console patch", SetCommandPatchHelper.IsPatched, SetCommandPatchHelper.Remove),
            new("the Multi-instance patch",
                exe => StateOf(InlineVaPatchHelper.GetPatchState(exe, InlineVaPatchHelper.MultiInstanceKey),
                    InlinePatchState.Patched, InlinePatchState.Unpatched),
                exe => InlineVaPatchHelper.RemovePatch(exe, InlineVaPatchHelper.MultiInstanceKey)),
            new("the Ambiguous Bypass patch",
                exe => StateOf(InlineVaPatchHelper.GetPatchState(exe, InlineVaPatchHelper.AmbiguousPackageKey),
                    InlinePatchState.Patched, InlinePatchState.Unpatched),
                exe => InlineVaPatchHelper.RemovePatch(exe, InlineVaPatchHelper.AmbiguousPackageKey)),
            new("the Launch Arguments patch",
                exe => CommandLineUnlockHelper.GetUnlockMode(exe) == CommandLineUnlockMode.PersistentFilePatch
                    && CommandLineUnlockHelper.IsUnlocked(exe),
                exe => CommandLineUnlockHelper.RestoreStock(exe)),
        };

        // Emptying a cave that a patch still jumps into would crash the game,
        // so the caves are only emptied when resetCaves is true and every patch is known to be removed.
        public static ExeRestoreResult Restore(string exePath, bool resetCaves)
        {
            var result = new ExeRestoreResult();
            bool allStock = true;
            using (GameFileReadScope.Begin())
            {
                bool knownVersion;
                try
                {
                    ExeImage exe = GameFileReadScope.ReadExe(exePath);
                    if (OoaService.HasOoaSection(exe.Raw) && OoaService.FindLicensePath(exe.Raw) == null)
                        throw new OoaLicenseNotFoundException(OoaService.GetExpectedLicensePath(exe.Raw));

                    // Most patches need a known version's address table, so an unknown exe's undetermined states are expected.
                    knownVersion = exe.Version != null;
                }
                catch (Exception ex)
                {
                    result.Failures.Add(("the patches in MirrorsEdge.exe", ex));
                    return result;
                }

                foreach (Patch patch in Patches)
                {
                    try
                    {
                        bool? applied = patch.State(exePath);
                        if (applied == true)
                        {
                            patch.Remove(exePath);
                            applied = patch.State(exePath);
                            if (applied != false)
                                throw new InvalidOperationException($"{patch.Name} was still applied after it was removed.");
                            result.Removed.Add(patch.Name);
                        }
                        else if (applied == null && knownVersion)
                        {
                            result.Undetermined.Add(patch.Name);
                        }
                        allStock &= applied == false;
                    }
                    catch (Exception ex)
                    {
                        allStock = false;
                        result.Failures.Add((patch.Name, ex));
                    }
                }
            }

            if (resetCaves && allStock)
            {
                try
                {
                    PatchUtility.RewriteExe(exePath, CaveSection.ResetExistingCaves);
                }
                catch (Exception ex)
                {
                    result.Failures.Add(("the leftover patch code in MirrorsEdge.exe", ex));
                }
            }

            return result;
        }

        private static bool? StateOf<T>(T state, T patched, T unpatched) where T : struct, Enum =>
            EqualityComparer<T>.Default.Equals(state, patched) ? true
            : EqualityComparer<T>.Default.Equals(state, unpatched) ? false
            : null;
    }
}
