using MirrorsEdgeTweaks.Helpers;
using MirrorsEdgeTweaks.ViewModels;
using System.IO;

namespace MirrorsEdgeTweaks.Services
{
    public interface IAppSettingsService
    {
        void Load();
        void Save();
    }

    // Copies the persisted values between metweaksconfig.ini and GameSession.Config.
    // Feature view models save a setting by writing it into GameSession.Config and calling Save.
    public class AppSettingsService : IAppSettingsService
    {
        private readonly ISettingsStore _store;
        private readonly GameSession _session;
        private readonly IDialogService _dialogService;
        private bool _saveFailureShown;

        public AppSettingsService(ISettingsStore store, GameSession session, IDialogService dialogService)
        {
            _store = store;
            _session = session;
            _dialogService = dialogService;
        }

        public void Load()
        {
            var settings = _store.Load();
            var config = _session.Config;

            if (settings.GameDirectoryPath != null)
            {
                config.GameDirectoryPath = settings.GameDirectoryPath;
            }

            if (!string.IsNullOrEmpty(settings.UserFolderPath))
            {
                config.UserFolderPath = settings.UserFolderPath;
            }

            config.Fov = settings.Fov;
            config.Dpi = settings.Dpi;
            config.Cm360 = settings.Cm360;
            config.LaunchArguments = settings.LaunchArguments ?? string.Empty;
        }

        // Tweaks works without its settings file, so a failed save is reported once rather than on every change.
        public void Save()
        {
            var config = _session.Config;
            try
            {
                _store.Save(new AppSettings
                {
                    GameDirectoryPath = config.GameDirectoryPath,
                    UserFolderPath = config.UserFolderPath,
                    Fov = config.Fov,
                    Dpi = config.Dpi,
                    Cm360 = config.Cm360,
                    LaunchArguments = config.LaunchArguments
                });
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (_saveFailureShown)
                    return;

                _saveFailureShown = true;
                _dialogService.ShowMessage("Settings Not Saved",
                    "Mirror's Edge Tweaks could not save its settings to metweaksconfig.ini next to its executable, " +
                    "so it will not remember your game directory and other entries the next time it starts.\n\n" +
                    "Move Mirror's Edge Tweaks to a folder you can write to, such as your Desktop or Documents.",
                    DialogMessageType.Warning,
                    ErrorMessageHelper.Details(ex));
            }
        }
    }
}
