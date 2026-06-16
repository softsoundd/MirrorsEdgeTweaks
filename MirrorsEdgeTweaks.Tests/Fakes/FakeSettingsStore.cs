using MirrorsEdgeTweaks.Services;

namespace MirrorsEdgeTweaks.Tests.Fakes
{
    // In-memory ISettingsStore for isolating AppSettingsService from the on-disk ini format. ToLoad
    // is returned by Load; the last value passed to Save is captured in Saved.
    public sealed class FakeSettingsStore : ISettingsStore
    {
        public AppSettings ToLoad { get; set; } = new AppSettings();
        public AppSettings? Saved { get; private set; }
        public int SaveCount { get; private set; }

        public AppSettings Load() => ToLoad;

        public void Save(AppSettings settings)
        {
            Saved = settings;
            SaveCount++;
        }
    }
}
