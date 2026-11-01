using MirrorsEdgeTweaks.Services;

namespace MirrorsEdgeTweaks.Tests.Fakes
{
    // Load returns ToLoad.
    // Saved holds the last value passed to Save.
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
