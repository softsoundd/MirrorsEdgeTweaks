using MirrorsEdgeTweaks.Services;

namespace MirrorsEdgeTweaks.Tests.Fakes
{
    // Stands in for the Engine.u offset lookup, which needs real game packages.
    public sealed class FakeGameDataService(Action<string> findEngineOffsets) : IGameDataService
    {
        public event Action? PackagesReloaded { add { } remove { } }

        public void LoadPackages() => throw new NotSupportedException();

        public Task ReloadPackagesAsync() => throw new NotSupportedException();

        public void FindEngineOffsets(string enginePath) => findEngineOffsets(enginePath);

        public void UpdateConsoleStatus() { }
    }
}
