using MirrorsEdgeTweaks.ViewModels;

namespace MirrorsEdgeTweaks.Services
{
    public interface IAppSettingsService
    {
        // Reads persisted settings from the store into GameSession.Config.
        void Load();

        // Writes the current GameSession.Config values back to the store.
        void Save();
    }

    // Higher-level settings persistence: marshals the persisted user values between the ISettingsStore
    // (metweaksconfig.ini) and the shared GameSession.Config. Depends only on the store and the
    // session, so feature view models can persist settings by writing their value into
    // GameSession.Config and calling Save without a circular dependency.
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
                Fov = config.Fov,
                Dpi = config.Dpi,
                Cm360 = config.Cm360,
                LaunchArguments = config.LaunchArguments
            });
        }
    }
}
