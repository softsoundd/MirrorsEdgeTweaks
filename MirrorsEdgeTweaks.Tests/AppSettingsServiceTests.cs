using MirrorsEdgeTweaks.Services;
using MirrorsEdgeTweaks.Tests.Fakes;
using MirrorsEdgeTweaks.ViewModels;

namespace MirrorsEdgeTweaks.Tests
{
    public class AppSettingsServiceTests
    {
        [Fact]
        public void Save_WritesAllConfigScalarsToTheStore()
        {
            var session = new GameSession();
            session.Config.GameDirectoryPath = @"C:\Games\MirrorsEdge";
            session.Config.UserFolderPath = @"C:\Games\MirrorsEdge\TdGame";
            session.Config.Fov = "100";
            session.Config.Dpi = "800";
            session.Config.Cm360 = "30";
            session.Config.LaunchArguments = "-windowed";

            var store = new FakeSettingsStore();
            var service = new AppSettingsService(store, session);

            service.Save();

            Assert.Equal(1, store.SaveCount);
            Assert.NotNull(store.Saved);
            Assert.Equal(@"C:\Games\MirrorsEdge", store.Saved!.GameDirectoryPath);
            Assert.Equal(@"C:\Games\MirrorsEdge\TdGame", store.Saved.UserFolderPath);
            Assert.Equal("100", store.Saved.Fov);
            Assert.Equal("800", store.Saved.Dpi);
            Assert.Equal("30", store.Saved.Cm360);
            Assert.Equal("-windowed", store.Saved.LaunchArguments);
        }

        [Fact]
        public void Load_PopulatesConfigFromTheStore()
        {
            var session = new GameSession();
            var store = new FakeSettingsStore
            {
                ToLoad = new AppSettings
                {
                    GameDirectoryPath = @"D:\ME",
                    UserFolderPath = @"D:\ME\TdGame",
                    Fov = "95",
                    Dpi = "1600",
                    Cm360 = "20",
                    LaunchArguments = "-nointro"
                }
            };
            var service = new AppSettingsService(store, session);

            service.Load();

            Assert.Equal(@"D:\ME", session.Config.GameDirectoryPath);
            Assert.Equal(@"D:\ME\TdGame", session.Config.UserFolderPath);
            Assert.Equal("95", session.Config.Fov);
            Assert.Equal("1600", session.Config.Dpi);
            Assert.Equal("20", session.Config.Cm360);
            Assert.Equal("-nointro", session.Config.LaunchArguments);
        }

        [Fact]
        public void Load_NullLaunchArguments_BecomesEmptyString()
        {
            var session = new GameSession();
            var store = new FakeSettingsStore
            {
                ToLoad = new AppSettings { LaunchArguments = null, Fov = null, Dpi = null, Cm360 = null }
            };
            var service = new AppSettingsService(store, session);

            service.Load();

            Assert.Equal(string.Empty, session.Config.LaunchArguments);
            Assert.Null(session.Config.Fov);
            Assert.Null(session.Config.Dpi);
            Assert.Null(session.Config.Cm360);
        }

        [Fact]
        public void Load_NullGameDirectory_LeavesExistingValueUntouched()
        {
            var session = new GameSession();
            session.Config.GameDirectoryPath = @"C:\Existing";
            var store = new FakeSettingsStore { ToLoad = new AppSettings { GameDirectoryPath = null } };
            var service = new AppSettingsService(store, session);

            service.Load();

            Assert.Equal(@"C:\Existing", session.Config.GameDirectoryPath);
        }

        [Fact]
        public void Save_ThenLoad_RoundTripsThroughTheRealStore()
        {
            var fileService = new InMemoryFileService();
            var store = new SettingsStore(fileService);

            var writer = new GameSession();
            writer.Config.GameDirectoryPath = @"E:\Edge";
            writer.Config.UserFolderPath = @"E:\Edge\TdGame";
            writer.Config.Fov = "110";
            writer.Config.Dpi = "400";
            writer.Config.Cm360 = "45";
            writer.Config.LaunchArguments = "-foo -bar";
            new AppSettingsService(store, writer).Save();

            var reader = new GameSession();
            new AppSettingsService(store, reader).Load();

            Assert.Equal(writer.Config.GameDirectoryPath, reader.Config.GameDirectoryPath);
            Assert.Equal(writer.Config.UserFolderPath, reader.Config.UserFolderPath);
            Assert.Equal(writer.Config.Fov, reader.Config.Fov);
            Assert.Equal(writer.Config.Dpi, reader.Config.Dpi);
            Assert.Equal(writer.Config.Cm360, reader.Config.Cm360);
            Assert.Equal(writer.Config.LaunchArguments, reader.Config.LaunchArguments);
        }
    }
}
