using MirrorsEdgeTweaks.Helpers;
using MirrorsEdgeTweaks.Services;
using System.IO;

namespace MirrorsEdgeTweaks.ViewModels
{
    public sealed class RestoreSettingsReport
    {
        private readonly List<(string Summary, string? Explanation)> _failures = new();

        public List<string> Removed { get; } = new();
        public List<string> Restored { get; } = new();
        public List<string> FailureDetails { get; } = new();
        public List<string> Notes { get; } = new();

        // Holds how to verify the game files, when only a verification can finish restoring MirrorsEdge.exe.
        public string? VerifyNote { get; set; }

        public bool HasFailures => _failures.Count > 0;

        public void AddFailure(string message) => _failures.Add((message, null));

        // The summary is a sentence such as "Could not restore Engine.u.", which the explanation follows.
        public void AddFailure(string summary, Exception exception)
        {
            _failures.Add((summary, ErrorMessageHelper.Explain(exception)));
            if (ErrorMessageHelper.Details(exception) is { } details)
                FailureDetails.Add($"{summary} {details}");
        }

        // Failures with the same explanation share a paragraph, so a lost connection is only explained once.
        public IEnumerable<string> FailureParagraphs() => _failures
            .GroupBy(failure => (Explained: failure.Explanation != null, Text: failure.Explanation ?? failure.Summary))
            .Select(group => group.Key.Explained
                ? $"{string.Join(" ", group.Select(failure => failure.Summary))} {group.Key.Text}"
                : group.Key.Text);
    }

    // Puts every file and setting that Tweaks changes back to how a fresh install has it.
    public class RestoreSettingsViewModel : BusyViewModel
    {
        private const string ConfirmationMessage =
            "This undoes every change Mirror's Edge Tweaks has made, returning your game to how it was after a fresh install:\n\n" +
            "• All patches and tweaks are removed.\n\n" +
            "• The developer console, Tweaks Scripts packages, the Cinematic Faith Model, Faithful Luma tonemapper, " +
            "OpenAL Soft, DXVK and RTX Remix NGX are uninstalled.\n\n" +
            "• Your in-game settings and keybinds go back to their defaults.\n\n" +
            "Your game language and saves are kept. Some original game files are downloaded, so an internet connection is needed. " +
            "This cannot be undone. Do you want to continue?";

        private readonly IDialogService _dialogService;
        private readonly IDownloadService _download;
        private readonly IFileService _fileService;
        private readonly IAssetUrlProvider _assetUrls;
        private readonly IDecompressionService _decompressionService;
        private readonly PendingReapplyStore _pending;

        public RestoreSettingsViewModel(
            IDialogService dialogService,
            IDownloadService download,
            IFileService fileService,
            IAssetUrlProvider assetUrls,
            IDecompressionService decompressionService,
            PendingReapplyStore pending,
            GameSession session,
            GameStatusViewModel gameStatus,
            DownloadProgressViewModel downloadProgress)
            : base(session, gameStatus, downloadProgress)
        {
            _dialogService = dialogService;
            _download = download;
            _fileService = fileService;
            _assetUrls = assetUrls;
            _decompressionService = decompressionService;
            _pending = pending;
        }

        public Task<bool> ConfirmAsync() => _dialogService.ShowConfirmationAsync("Restore Settings", ConfirmationMessage);

        // The caller holds the apply gate, so the steps call the patchers directly.
        // Each step reports its own failure, and the others still run.
        // The caller must reload the game directory afterwards, because Engine.u and TdGame.u are left unloaded.
        public async Task<RestoreSettingsReport> RunAsync(string gameDirectoryPath)
        {
            var report = new RestoreSettingsReport();
            _gameStatus.IsUiEnabled = false;
            _gameStatus.IsGameTweaksEnabled = false;
            _session.PackagesLoaded = false;

            using var backupOperation = PatchUtility.BeginBackupOperation();

            ShowProgress("Checking your game files...", isIndeterminate: true);
            RestoreContext context = await Task.Run(() => CaptureContext(gameDirectoryPath));
            await _assetUrls.EnsureLoadedAsync();

            await RestoreExecutableAsync(context, report);
            await RestorePackagesAsync(context, report);
            await RestoreLocalisationAsync(context, report);
            await RemoveInstalledContentAsync(context, report);
            await RestoreGameConfigsAsync(context, report);
            await RestoreUserConfigsAsync(context, report);
            UpdatePendingReapply(context, report);

            if (!report.HasFailures)
                backupOperation.Complete();
            return report;
        }

        public Task ShowSummaryAsync(RestoreSettingsReport report)
        {
            var parts = report.FailureParagraphs().Concat(report.Notes).ToList();
            if (report.VerifyNote != null)
                parts.Add(report.VerifyNote);
            bool complete = parts.Count == 0;
            if (complete)
            {
                parts.Add(report.Removed.Count + report.Restored.Count > 0
                    ? "Your game is back to how it was after a fresh install."
                    : "Your game had no changes to undo.");
            }

            var details = new List<string>();
            if (report.Removed.Count > 0)
                details.Add($"Removed: {string.Join(", ", report.Removed)}.");
            if (report.Restored.Count > 0)
                details.Add($"Restored: {string.Join(", ", report.Restored)}.");
            details.AddRange(report.FailureDetails);

            return _dialogService.ShowMessageAsync(
                complete ? "Settings Restored" : "Settings Partly Restored",
                string.Join("\n\n", parts),
                complete ? DialogMessageType.Success : DialogMessageType.Warning,
                details.Count > 0 ? string.Join("\n", details) : null);
        }

        // Holds what the steps read before anything changes, because later steps rewrite the files these come from,
        // such as the language and audio device in TdEngine.ini.
        private sealed class RestoreContext
        {
            public required string GameDirectory { get; init; }
            public required string ExePath { get; init; }
            public required string CookedPc { get; init; }
            public required string Binaries { get; init; }
            public required string GameVersion { get; init; }
            public required string? LanguageCode { get; init; }
            public required string TdEngineIniPath { get; init; }
            public required string TdInputIniPath { get; init; }
            public required ResolutionHelper.Resolution? NativeResolution { get; init; }

            // Is null when the exe could not be checked.
            public required bool? HasLegacyExeChanges { get; init; }

            public required bool HasLegacyLocalisationChanges { get; init; }
            public required bool ConsoleInstalled { get; init; }
            public required bool TweaksScriptsUIPresent { get; init; }
            public required bool TweaksScriptsUIPublished { get; init; }
            public required TweaksScriptsUIPaths TweaksScriptsUIPaths { get; init; }
            public required string? TweaksScriptsUIStockZip { get; init; }
            public required string TweaksScriptsUIExtractDirectory { get; init; }
            public required bool CinematicFaithInstalled { get; init; }
            public required bool FaithfulLumaInstalled { get; init; }
            public required bool OpenAlSoftInstalled { get; init; }
            public required D3D9Runtime Runtime { get; init; }
        }

        private RestoreContext CaptureContext(string gameDirectoryPath)
        {
            var config = _session.Config;
            string exePath = Path.Combine(gameDirectoryPath, "Binaries", "MirrorsEdge.exe");
            string cookedPc = Path.Combine(gameDirectoryPath, "TdGame", "CookedPC");
            string binaries = Path.Combine(gameDirectoryPath, "Binaries");
            string tdEngineIniPath = UserTdGamePathHelper.GetTdEngineIniPath(config);
            string? languageCode = LanguageSettingsViewModel.ReadInstalledLanguageCode(config);
            TweaksScriptsUIPaths scriptsUIPaths = UserTdGamePathHelper.GetTweaksScriptsUIPaths(config);

            return new RestoreContext
            {
                GameDirectory = gameDirectoryPath,
                ExePath = exePath,
                CookedPc = cookedPc,
                Binaries = binaries,
                GameVersion = GameVersionHelper.GetGameVersion(gameDirectoryPath).Version ?? string.Empty,
                LanguageCode = languageCode,
                TdEngineIniPath = tdEngineIniPath,
                TdInputIniPath = UserTdGamePathHelper.GetTdInputIniPath(config),

                // The game switches the display mode when it starts fullscreen at a resolution other than the desktop's,
                // and the desktop normally runs at the native resolution.
                NativeResolution = NativeResolutionHelper.GetPrimaryNativeResolution(),

                HasLegacyExeChanges = FindLegacyChanges(() => LegacyChangeDetector.DetectExe(exePath)),
                HasLegacyLocalisationChanges = FindLegacyChanges(() => LegacyChangeDetector
                    .LocalisationPackages(cookedPc, languageCode)
                    .SelectMany(LegacyChangeDetector.DetectLocalisation)
                    .ToList()) == true,
                ConsoleInstalled = File.Exists(Path.Combine(cookedPc, "MirrorsEdgeConsole.u")),
                TweaksScriptsUIPresent = TweaksScriptsUIHelper.AnyFilesPresent(config, scriptsUIPaths),
                TweaksScriptsUIPublished = UserTdGamePathHelper.UsesPublishedLayout(config),
                TweaksScriptsUIPaths = scriptsUIPaths,
                TweaksScriptsUIStockZip = TweaksScriptsUIHelper.GetStockRestoreZipFileName(gameDirectoryPath),
                TweaksScriptsUIExtractDirectory = TweaksScriptsUIHelper.GetStockRestoreExtractDirectory(config),
                CinematicFaithInstalled = CinematicFaithHelper.IsCinematicInstalled(gameDirectoryPath) == true,
                FaithfulLumaInstalled = ToneMapperHelper.HasFaithfulLumaFiles(gameDirectoryPath, _fileService),
                OpenAlSoftInstalled = AudioBackendHelper.ReadDeviceName(tdEngineIniPath) == AudioBackendHelper.OpenAlSoftDeviceName
                    || AudioBackendHelper.IsHrtfInstalled(gameDirectoryPath),
                Runtime = RemixRuntimeHelper.Detect(binaries),
            };
        }

        // Returns null when the check throws, such as on a file that cannot be read.
        private static bool? FindLegacyChanges(Func<List<LegacyChange>> check)
        {
            try { return check().Count > 0; }
            catch { return null; }
        }

        private async Task RestoreExecutableAsync(RestoreContext context, RestoreSettingsReport report)
        {
            if (!File.Exists(context.ExePath))
                return;

            ShowProgress("Removing patches...", isIndeterminate: true);

            // Code from older Tweaks versions can jump into a cave, so the caves are only emptied when the exe is known to have none.
            ExeRestoreResult result = await Task.Run(() =>
                ExeStockRestorer.Restore(context.ExePath, resetCaves: context.HasLegacyExeChanges == false));

            report.Removed.AddRange(result.Removed);
            foreach (var (name, error) in result.Failures)
                report.AddFailure($"Could not remove {name}.", error);

            // Changes from older versions can leave patches undetermined, and the verify note already covers them.
            if (context.HasLegacyExeChanges == true)
            {
                SetVerifyNote(context, report,
                    "MirrorsEdge.exe still has changes from an older version of Mirror's Edge Tweaks. Verifying your game files will undo them.");
                return;
            }

            if (result.Undetermined.Count > 0)
                report.AddFailure("Some patches could not be checked, because MirrorsEdge.exe has unexpected changes: " +
                    $"{string.Join(", ", result.Undetermined)}.");
            if (result.Failures.Count > 0 || result.Undetermined.Count > 0)
                SetVerifyNote(context, report, "To finish restoring MirrorsEdge.exe, verify your game files.");
        }

        private async Task RestorePackagesAsync(RestoreContext context, RestoreSettingsReport report)
        {
            string? engineUrl = GameVersionHelper.GetEngineDownloadUrl(context.GameVersion, _assetUrls);
            string? tdGameUrl = GameVersionHelper.GetDownloadUrl(context.GameVersion, TdGameVersionCatalog.Names[0], _assetUrls);
            if (engineUrl == null || tdGameUrl == null)
            {
                report.AddFailure("Could not restore Engine.u and TdGame.u, because your game version was not recognised. " +
                    "Verify your game files to restore them.");
                return;
            }

            if (await DownloadAsync(report, engineUrl, context.GameDirectory, "Engine.u", Path.Combine(context.CookedPc, "Engine.u")))
                report.Restored.Add("Engine.u");
            if (await DownloadAsync(report, tdGameUrl, context.GameDirectory, "TdGame.u", Path.Combine(context.CookedPc, "TdGame.u")))
                report.Restored.Add("TdGame.u");
        }

        // The prompt and cursor patches edit every installed language, so they are undone in place rather than by download.
        // A language pack is only downloaded for edits from older Tweaks versions, which nothing else can undo.
        private async Task RestoreLocalisationAsync(RestoreContext context, RestoreSettingsReport report)
        {
            if (context.HasLegacyLocalisationChanges)
            {
                if (context.LanguageCode != null && LanguageSettingsViewModel.LanguagePackFor(context.LanguageCode) is { } pack)
                {
                    if (await DownloadAsync(report, _assetUrls.For(pack), context.GameDirectory, "the language files"))
                        report.Restored.Add("the language files");
                }
                else
                {
                    report.AddFailure("Could not restore the language files, because your game language was not recognised.");
                }
            }

            ShowProgress("Restoring the language files...", isIndeterminate: true);
            await RunStepAsync(report, "Could not restore the Xbox button prompts.",
                () => GamepadButtonPatcher.ApplyButtonPatches(context.CookedPc, "xbox"));
            await RunStepAsync(report, "Could not restore the mouse cursor size.",
                () => CursorScalePatcher.Remove(context.CookedPc));
        }

        private async Task RemoveInstalledContentAsync(RestoreContext context, RestoreSettingsReport report)
        {
            ShowProgress("Removing installed mods...", isIndeterminate: true);

            // Restoring Engine.u, TdEngine.ini and TdInput.ini undoes the console's other changes.
            if (context.ConsoleInstalled)
                await RemoveAsync(report, "the developer console",
                    () => _fileService.DeleteFile(Path.Combine(context.CookedPc, "MirrorsEdgeConsole.u")));

            try
            {
                if (await Task.Run(() => TweaksScriptsInstallHelper.DeleteFiles(context.GameDirectory, _fileService)) > 0)
                    report.Removed.Add("Tweaks Scripts");
            }
            catch (Exception ex)
            {
                report.AddFailure("Could not remove Tweaks Scripts.", ex);
            }

            if (context.TweaksScriptsUIPresent)
                await RemoveTweaksScriptsUIAsync(context, report);

            if (context.CinematicFaithInstalled
                && await DownloadAsync(report, _assetUrls.For(CinematicFaithHelper.OriginalAssetFileName), context.GameDirectory,
                    "the original Faith model"))
                report.Removed.Add("the Cinematic Faith Model");

            if (context.FaithfulLumaInstalled
                && await DownloadAsync(report, _assetUrls.For(ToneMapperHelper.OriginalAssetFileName), context.GameDirectory,
                    "the original tone mapper"))
                report.Removed.Add("Faithful Luma");

            // Restoring TdEngine.ini undoes OpenAL Soft's audio settings.
            if (context.OpenAlSoftInstalled
                && await DownloadAsync(report, _assetUrls.For(AudioBackendHelper.DefaultAssetFileName), context.GameDirectory,
                    "the original audio files"))
                await RemoveAsync(report, "OpenAL Soft", () => AudioBackendHelper.DeleteHrtfFiles(context.GameDirectory));

            if (context.Runtime is D3D9Runtime.Dxvk or D3D9Runtime.DxvkOutdated)
                await RemoveAsync(report, "DXVK", () => DxvkHelper.RemoveDxvk(context.Binaries));

            // A path-traced RTX Remix build stays, because Tweaks does not install one.
            if (context.Runtime is D3D9Runtime.RemixNgx or D3D9Runtime.RemixNgxOutdated)
            {
                await RemixRuntimeHelper.WaitForGameExitAsync();
                await RemoveAsync(report, "RTX Remix NGX", () =>
                {
                    RemixRuntimeHelper.Remove(context.Binaries);
                    DxvkHelper.DeleteFloatDepthOnlyConf(context.Binaries);
                });
            }
        }

        // Without the Published folder, the mod replaces stock menu files, which only a download can put back.
        private async Task RemoveTweaksScriptsUIAsync(RestoreContext context, RestoreSettingsReport report)
        {
            if (context.TweaksScriptsUIPublished)
            {
                await RemoveAsync(report, "Tweaks Scripts UI",
                    () => TweaksScriptsUIHelper.DeleteAllFiles(context.TweaksScriptsUIPaths, _fileService));
            }
            else if (context.TweaksScriptsUIStockZip is { } zipName)
            {
                if (await DownloadAsync(report, _assetUrls.For(zipName), context.TweaksScriptsUIExtractDirectory, "the original menu files"))
                    await RemoveAsync(report, "Tweaks Scripts UI",
                        () => TweaksScriptsUIHelper.DeleteModOnlyFiles(context.TweaksScriptsUIPaths, _fileService));
            }
            else
            {
                report.AddFailure("Could not remove Tweaks Scripts UI, because your game version was not recognised.");
            }
        }

        private async Task RestoreGameConfigsAsync(RestoreContext context, RestoreSettingsReport report)
        {
            ShowProgress("Restoring config files...", isIndeterminate: true);
            try
            {
                report.Restored.AddRange(await Task.Run(() => StockConfigFiles.RestoreGameConfigs(context.GameDirectory)));
            }
            catch (Exception ex)
            {
                // Unlocked Configs stays applied, because the game would not start with the config files left changed.
                report.AddFailure("Could not restore the game's config files.", ex);
                return;
            }

            if (!File.Exists(context.ExePath))
                return;

            try
            {
                var (removed, changedConfigs) = await Task.Run(() => RemoveConfigUnlock(context));
                if (removed)
                    report.Removed.Add("Unlocked Configs");
                else if (changedConfigs.Count > 0)
                    report.Notes.Add("Unlocked Configs was kept, because the game will not start without it while these config files " +
                        $"have changes from outside Mirror's Edge Tweaks: {string.Join(", ", changedConfigs)}. " +
                        "To remove it, verify your game files, then select Restore Settings again.");
            }
            catch (Exception ex)
            {
                report.AddFailure("Could not remove Unlocked Configs.", ex);
                SetVerifyNote(context, report, "To finish restoring MirrorsEdge.exe, verify your game files.");
            }
        }

        // The exe's config check rejects every changed config file, so the patch is only removed while all of them are stock.
        private static (bool Removed, List<string> ChangedConfigs) RemoveConfigUnlock(RestoreContext context)
        {
            if (ConfigUnlockHelper.GetState(context.ExePath) is not (ConfigUnlockState.Patched or ConfigUnlockState.Mixed))
                return (false, new List<string>());

            List<string> changedConfigs = StockConfigFiles.FindNonStockGameConfigs(context.GameDirectory);
            if (changedConfigs.Count > 0)
                return (false, changedConfigs);

            ConfigUnlockHelper.RestoreStock(context.ExePath);
            if (ConfigUnlockHelper.GetState(context.ExePath) != ConfigUnlockState.Unpatched)
                throw new InvalidOperationException("Unlocked Configs was still applied after it was removed.");
            return (true, changedConfigs);
        }

        private async Task RestoreUserConfigsAsync(RestoreContext context, RestoreSettingsReport report)
        {
            ShowProgress("Resetting your in-game settings...", isIndeterminate: true);
            try
            {
                report.Restored.AddRange(await Task.Run(() => StockConfigFiles.RestoreUserConfigs(
                    context.GameDirectory, context.TdEngineIniPath, context.TdInputIniPath, context.LanguageCode, context.NativeResolution)));
            }
            catch (Exception ex)
            {
                report.AddFailure("Could not reset your in-game settings and keybinds.", ex);
            }
        }

        // A pending snapshot re-applies tweaks after the next game file verification, which would undo this restore.
        // While MirrorsEdge.exe still needs verifying, an empty snapshot also stops the startup check from saving a new one.
        private void UpdatePendingReapply(RestoreContext context, RestoreSettingsReport report)
        {
            try
            {
                if (context.HasLegacyExeChanges == true)
                    _pending.Save(context.GameDirectory, new TweakSnapshot());
                else
                    _pending.Clear(context.GameDirectory);
            }
            catch (Exception ex)
            {
                report.AddFailure("Could not clear your saved tweaks, so they may be re-applied after you verify your game files.", ex);
            }
        }

        private static void SetVerifyNote(RestoreContext context, RestoreSettingsReport report, string reason) =>
            report.VerifyNote ??= $"{reason} {VerifyGameFilesHelper.GetInstructions(context.GameDirectory)}";

        // Downloads a runtime asset over the files it replaces, then decompresses the listed packages,
        // because the patchers and the package loader need them decompressed.
        // Returns false after adding the failure to the report.
        private async Task<bool> DownloadAsync(RestoreSettingsReport report, string url, string destination, string description,
            params string[] packages)
        {
            try
            {
                await RunDownloadAndExtractAsync(_download, _fileService, url, destination, description,
                    customExtract: (zipPath, extractPath) => Task.Run(() =>
                    {
                        ZipExtractHelper.ExtractOverwritingReadOnly(zipPath, extractPath);
                        foreach (string package in packages)
                        {
                            Post(() => _gameStatus.Status = "Decompressing packages...");
                            _decompressionService.RunDecompressor(package);
                            BackupRetentionService.PruneBackupForPath(package);
                        }
                    }));
                return true;
            }
            catch (Exception ex)
            {
                report.AddFailure($"Could not restore {description}.", ex);
                return false;
            }
        }

        private static async Task RemoveAsync(RestoreSettingsReport report, string name, Action remove)
        {
            try
            {
                await Task.Run(remove);
                report.Removed.Add(name);
            }
            catch (Exception ex)
            {
                report.AddFailure($"Could not remove {name}.", ex);
            }
        }

        private static async Task RunStepAsync(RestoreSettingsReport report, string failureSummary, Action step)
        {
            try
            {
                await Task.Run(step);
            }
            catch (Exception ex)
            {
                report.AddFailure(failureSummary, ex);
            }
        }
    }
}
