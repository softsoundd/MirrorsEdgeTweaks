using MirrorsEdgeTweaks.ViewModels;

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

        public AppSettingsService(ISettingsStore store, GameSession session)
        {
            _store = store;
            _session = session;
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

        public void Save()
        {
            var config = _session.Config;
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
    }
}
