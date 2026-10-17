using MirrorsEdgeTweaks.Helpers;
using MirrorsEdgeTweaks.Services;
using System.IO;
using System.IO.Compression;

namespace MirrorsEdgeTweaks.ViewModels
{
    // Handles game files that older Tweaks releases changed in ways the current patchers cannot manage.
    public class LegacyRestoreViewModel : BusyViewModel
    {
        private const string UnsupportedChangesIntro =
            "Mirror's Edge Tweaks found changes from an older version of Tweaks that this version cannot manage:";

        private readonly IDialogService _dialogService;
        private readonly IDownloadService _download;
        private readonly IFileService _fileService;
        private readonly IAssetUrlProvider _assetUrls;
        private readonly IDecompressionService _decompressionService;
        private readonly ITweakSnapshotService _snapshots;
        private readonly PendingReapplyStore _pending;

        public LegacyRestoreViewModel(
            IDialogService dialogService,
            IDownloadService download,
            IFileService fileService,
            IAssetUrlProvider assetUrls,
            IDecompressionService decompressionService,
            ITweakSnapshotService snapshots,
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
            _snapshots = snapshots;
            _pending = pending;
        }

        // Runs while the game directory is processed, before the packages are loaded.
        // The caller holds the apply gate, so the flows call the patchers directly.
        public async Task RunAsync(string gameDirectoryPath)
        {
            _gameStatus.Status = "Checking for changes from older versions...";
            string? languageCode = LanguageSettingsViewModel.ReadInstalledLanguageCode(_session.Config);
            LegacyChangeReport report = await Task.Run(() => LegacyChangeDetector.Detect(gameDirectoryPath, languageCode));
            TweakSnapshot? pending = _pending.Load(gameDirectoryPath);

            if (report.Affects(LegacyFile.Exe))
            {
                if (pending == null)
                    _pending.Save(gameDirectoryPath, await Task.Run(() => _snapshots.Capture(report)));
                await ShowVerifyDialogAsync(gameDirectoryPath, report);
            }
            else if (!report.IsEmpty)
            {
                await RestorePackagesAsync(gameDirectoryPath, languageCode, report, pending);
            }
            else if (pending != null)
            {
                _gameStatus.Status = "Re-applying your tweaks...";
                TweakReapplyResult result = await _snapshots.ReapplyAsync(pending, TweakScope.All);
                _pending.Clear(gameDirectoryPath);
                await ShowSummaryAsync(new List<string>(), new List<string>(), result);
            }
        }

        private async Task RestorePackagesAsync(string gameDir, string? languageCode, LegacyChangeReport report,
            TweakSnapshot? pending)
        {
            string cookedPc = Path.Combine(gameDir, "TdGame", "CookedPC");
            string enginePath = Path.Combine(cookedPc, "Engine.u");
            string tdGamePath = Path.Combine(cookedPc, "TdGame.u");
            string gameVersion = GameVersionHelper.GetGameVersion(gameDir).Version ?? string.Empty;
            await _assetUrls.EnsureLoadedAsync();

            var assets = new List<RestoreAsset>();
            var notes = new List<string>();
            TweakScope unrestored = TweakScope.None;

            if (report.Affects(LegacyFile.Engine))
            {
                if (GameVersionHelper.GetEngineDownloadUrl(gameVersion, _assetUrls) is { } url)
                    assets.Add(new RestoreAsset(url, "Engine.u", TweakScope.Engine, new[] { enginePath }, Decompress: true));
                else
                {
                    notes.Add("Engine.u cannot be restored automatically, because the game version was not recognised.");
                    unrestored |= TweakScope.Engine;
                }
            }

            if (report.Affects(LegacyFile.TdGame))
            {
                string variant = await Task.Run(() => TdGameVersionDetector.DetectTdGameVersion(tdGamePath));
                if (GameVersionHelper.GetDownloadUrl(gameVersion, variant, _assetUrls) is { } url)
                    assets.Add(new RestoreAsset(url, "TdGame.u", TweakScope.TdGame, new[] { tdGamePath }, Decompress: true));
                else
                {
                    notes.Add("TdGame.u cannot be restored automatically, because its TdGame version was not recognised. " +
                        "Reinstall it from the TdGame version selector instead.");
                    unrestored |= TweakScope.TdGame;
                }
            }

            // The language pack replaces both of the language's packages.
            if (report.Affects(LegacyFile.Localisation))
            {
                if (languageCode != null && LanguageSettingsViewModel.LanguagePackFor(languageCode) is { } pack)
                    assets.Add(new RestoreAsset(_assetUrls.For(pack), "language files", TweakScope.Localisation,
                        LegacyChangeDetector.LocalisationPackages(cookedPc, languageCode).ToArray(), Decompress: false));
                else
                {
                    notes.Add("The language packages cannot be restored automatically, because the game language was not recognised.");
                    unrestored |= TweakScope.Localisation;
                }
            }

            string findings = string.Join("\n", report.Changes.Select(c => $"• {c.FileName}: {c.Description}"));
            string noteText = notes.Count > 0 ? "\n\n" + string.Join("\n\n", notes) : string.Empty;

            if (assets.Count == 0)
            {
                await _dialogService.ShowMessageAsync("Unsupported Game Files",
                    $"{UnsupportedChangesIntro}\n\n{findings}{noteText}\n\n" +
                    "Some tweaks cannot be applied to these files until they are restored.",
                    DialogMessageType.Warning);
                return;
            }

            bool confirmed = await _dialogService.ShowConfirmationAsync("Restore Game Files",
                $"{UnsupportedChangesIntro}\n\n{findings}{noteText}\n\n" +
                "Download stock copies of these files and re-apply your tweaks? " +
                "Until they are restored, some tweaks cannot be applied to them.");
            if (!confirmed)
                return;

            TweakSnapshot snapshot = pending ?? await Task.Run(() => _snapshots.Capture(report));

            var restored = new List<string>();
            var failures = new List<string>();
            TweakScope restoredScope = TweakScope.None;
            foreach (RestoreAsset asset in assets)
            {
                try
                {
                    await RunDownloadAndExtractAsync(_download, _fileService, asset.Url, gameDir, asset.Description,
                        customExtract: (zip, dest) => Task.Run(() => Extract(zip, dest, asset)));
                    foreach (string path in asset.Paths)
                        BackupRetentionService.PruneBackupForPath(path);

                    restored.AddRange(asset.Paths.Select(path => Path.GetFileName(path)));
                    restoredScope |= asset.Scope;
                }
                catch (Exception ex)
                {
                    failures.Add($"Could not restore {asset.Description}: {ex.Message}");
                    unrestored |= asset.Scope;
                }
            }

            // A pending snapshot was taken before a file verification, which replaces every patched file.
            // Files that still hold older changes are left out, and the snapshot is kept until they are restored.
            TweakScope scope = pending != null ? TweakScope.All & ~unrestored : restoredScope;
            if (restoredScope == TweakScope.None)
            {
                await ShowSummaryAsync(restored, failures, new TweakReapplyResult());
                return;
            }

            _gameStatus.Status = "Re-applying your tweaks...";
            TweakReapplyResult result = await _snapshots.ReapplyAsync(snapshot, scope);
            if (pending != null && unrestored == TweakScope.None)
                _pending.Clear(gameDir);
            await ShowSummaryAsync(restored, failures, result);
        }

        // The runtime assets hold compressed packages, but the patchers need Engine.u and TdGame.u decompressed.
        private void Extract(string zipPath, string destination, RestoreAsset asset)
        {
            ZipFile.ExtractToDirectory(zipPath, destination, overwriteFiles: true);
            if (!asset.Decompress)
                return;

            Post(() => _gameStatus.Status = "Decompressing packages...");
            foreach (string package in asset.Paths)
                _decompressionService.RunDecompressor(package);
        }

        private async Task ShowVerifyDialogAsync(string gameDir, LegacyChangeReport report)
        {
            string findings = string.Join("\n", report.Changes.Select(c => $"• {c.FileName}: {c.Description}"));
            string version = await Task.Run(() => ReadExeVersion(gameDir));
            string verify = version switch
            {
                "steam" => "In Steam, right-click Mirror's Edge in your library, select Properties, open Installed Files, " +
                    "and select \"Verify integrity of game files\".",
                "ea" => "In the EA app, open Mirror's Edge from your library, select the three dots next to Play, " +
                    "and select \"Repair\".",
                "gog" => "In GOG Galaxy, open Mirror's Edge, select the settings icon next to Play, " +
                    "and select Manage installation > Verify / Repair.",
                _ => "Reinstall Mirror's Edge, along with any official patches you had installed.",
            };

            await _dialogService.ShowMessageAsync("Verify Game Files",
                $"{UnsupportedChangesIntro}\n\n{findings}\n\n" +
                "Mirror's Edge Tweaks cannot download a stock MirrorsEdge.exe, so the game files need to be verified. " +
                $"{verify}\n\n" +
                "Your tweaks have been saved, and Mirror's Edge Tweaks will re-apply them the next time it starts. " +
                "If you use Unlocked Configs, Launch Arguments, Enable Logging, Multi-instance or Ambiguous Bypass, " +
                "enable them again afterwards.",
                DialogMessageType.Warning);
        }

        private static string ReadExeVersion(string gameDir)
        {
            string exePath = Path.Combine(gameDir, "Binaries", "MirrorsEdge.exe");
            try { return ExeVersionDetector.DetectVersion(File.ReadAllBytes(exePath), exePath) ?? string.Empty; }
            catch { return string.Empty; }
        }

        private async Task ShowSummaryAsync(List<string> restored, List<string> failures, TweakReapplyResult result)
        {
            var parts = new List<string>();
            if (restored.Count > 0)
                parts.Add($"Restored stock copies of {string.Join(", ", restored)}.");
            parts.AddRange(failures);
            if (result.ReappliedSettings.Count > 0)
                parts.Add($"Re-applied: {string.Join(", ", result.ReappliedSettings)}.");
            if (result.FailedSettings.Count > 0)
                parts.Add($"Could not re-apply: {string.Join(", ", result.FailedSettings)}. " +
                    "Please review these settings and apply them again.");

            if (parts.Count == 0)
                return;

            bool succeeded = failures.Count == 0 && result.FailedSettings.Count == 0;
            await _dialogService.ShowMessageAsync(
                succeeded ? "Tweaks Restored" : "Tweaks Partly Restored",
                string.Join("\n\n", parts),
                succeeded ? DialogMessageType.Success : DialogMessageType.Warning);
        }

        private sealed record RestoreAsset(string Url, string Description, TweakScope Scope, string[] Paths, bool Decompress);
    }
}
