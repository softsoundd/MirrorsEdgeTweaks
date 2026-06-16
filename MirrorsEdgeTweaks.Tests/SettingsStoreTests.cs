using MirrorsEdgeTweaks.Services;
using MirrorsEdgeTweaks.Tests.Fakes;

namespace MirrorsEdgeTweaks.Tests
{
    public class SettingsStoreTests
    {
        private const string IniFileName = "metweaksconfig.ini";

        [Fact]
        public void Save_WritesKeyValueLinesInExpectedFormat()
        {
            var fileService = new InMemoryFileService();
            var store = new SettingsStore(fileService);

            store.Save(new AppSettings
            {
                GameDirectoryPath = @"C:\ME",
                Fov = "100",
                Dpi = "800",
                Cm360 = "30",
                LaunchArguments = "-windowed"
            });

            Assert.True(fileService.FileExists(IniFileName));
            string[] lines = fileService.ReadAllLines(IniFileName);
            Assert.Contains(@"Path=C:\ME", lines);
            Assert.Contains("FOV=100", lines);
            Assert.Contains("DPI=800", lines);
            Assert.Contains("Cm360=30", lines);
            Assert.Contains("LaunchArguments=-windowed", lines);
        }

        [Fact]
        public void Load_MissingFile_ReturnsAllNullSettings()
        {
            var fileService = new InMemoryFileService();
            var store = new SettingsStore(fileService);

            var settings = store.Load();

            Assert.Null(settings.GameDirectoryPath);
            Assert.Null(settings.Fov);
            Assert.Null(settings.Dpi);
            Assert.Null(settings.Cm360);
            Assert.Null(settings.LaunchArguments);
        }

        [Fact]
        public void Load_ParsesSeededLines()
        {
            var fileService = new InMemoryFileService();
            fileService.Seed(IniFileName,
                @"Path=D:\Edge",
                "FOV=95",
                "DPI=1600",
                "Cm360=20",
                "LaunchArguments=-nomovies");
            var store = new SettingsStore(fileService);

            var settings = store.Load();

            Assert.Equal(@"D:\Edge", settings.GameDirectoryPath);
            Assert.Equal("95", settings.Fov);
            Assert.Equal("1600", settings.Dpi);
            Assert.Equal("20", settings.Cm360);
            Assert.Equal("-nomovies", settings.LaunchArguments);
        }

        [Fact]
        public void Load_KeysAreCaseInsensitive_AndValuesTrimmed()
        {
            var fileService = new InMemoryFileService();
            fileService.Seed(IniFileName,
                "path =  C:\\Trim  ",
                "fov= 110 ");
            var store = new SettingsStore(fileService);

            var settings = store.Load();

            Assert.Equal(@"C:\Trim", settings.GameDirectoryPath);
            Assert.Equal("110", settings.Fov);
        }

        [Fact]
        public void SaveThenLoad_RoundTripsValues()
        {
            var fileService = new InMemoryFileService();
            var store = new SettingsStore(fileService);
            var original = new AppSettings
            {
                GameDirectoryPath = @"F:\Game",
                Fov = "120",
                Dpi = "3200",
                Cm360 = "15",
                LaunchArguments = "-a -b -c"
            };

            store.Save(original);
            var loaded = store.Load();

            Assert.Equal(original, loaded);
        }
    }
}
