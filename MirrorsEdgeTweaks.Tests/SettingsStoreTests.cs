using MirrorsEdgeTweaks.Services;
using MirrorsEdgeTweaks.Tests.Fakes;
using MirrorsEdgeTweaks.Tests.TestSupport;

namespace MirrorsEdgeTweaks.Tests
{
    public class SettingsStoreTests
    {
        private const string IniFileName = "metweaksconfig.ini";
        private const string WorkingDirectoryIni = @"C:\Shortcut Start Folder\metweaksconfig.ini";

        [Fact]
        public void Save_WritesKeyValueLinesInExpectedFormat()
        {
            var fileService = new InMemoryFileService();
            var store = new SettingsStore(fileService, IniFileName);

            store.Save(new AppSettings
            {
                GameDirectoryPath = @"C:\ME",
                UserFolderPath = @"C:\ME\TdGame",
                Fov = "100",
                Dpi = "800",
                Cm360 = "30",
                LaunchArguments = "-windowed"
            });

            Assert.True(fileService.FileExists(IniFileName));
            string[] lines = fileService.ReadAllLines(IniFileName);
            Assert.Contains(@"Path=C:\ME", lines);
            Assert.Contains(@"UserFolderPath=C:\ME\TdGame", lines);
            Assert.Contains("FOV=100", lines);
            Assert.Contains("DPI=800", lines);
            Assert.Contains("Cm360=30", lines);
            Assert.Contains("LaunchArguments=-windowed", lines);
        }

        [Fact]
        public void Load_MissingFile_ReturnsAllNullSettings()
        {
            var fileService = new InMemoryFileService();
            var store = new SettingsStore(fileService, IniFileName);

            var settings = store.Load();

            Assert.Null(settings.GameDirectoryPath);
            Assert.Null(settings.UserFolderPath);
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
                @"UserFolderPath=D:\Edge\TdGame",
                "FOV=95",
                "DPI=1600",
                "Cm360=20",
                "LaunchArguments=-nomovies");
            var store = new SettingsStore(fileService, IniFileName);

            var settings = store.Load();

            Assert.Equal(@"D:\Edge", settings.GameDirectoryPath);
            Assert.Equal(@"D:\Edge\TdGame", settings.UserFolderPath);
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
            var store = new SettingsStore(fileService, IniFileName);

            var settings = store.Load();

            Assert.Equal(@"C:\Trim", settings.GameDirectoryPath);
            Assert.Equal("110", settings.Fov);
        }

        [Fact]
        public void Load_RepeatedKey_TakesTheLastValue()
        {
            var fileService = new InMemoryFileService();
            fileService.Seed(IniFileName,
                @"Path=C:\Old",
                "FOV=90",
                "not a setting",
                @"path=C:\New");
            var store = new SettingsStore(fileService, IniFileName);

            var settings = store.Load();

            Assert.Equal(@"C:\New", settings.GameDirectoryPath);
            Assert.Equal("90", settings.Fov);
        }

        [Fact]
        public void Load_WithoutAnExecutableFolderCopy_ReadsTheWorkingDirectoryCopy()
        {
            var fileService = new InMemoryFileService();
            fileService.Seed(WorkingDirectoryIni, @"Path=D:\Edge");
            var store = new SettingsStore(fileService, IniFileName, WorkingDirectoryIni);

            Assert.Equal(@"D:\Edge", store.Load().GameDirectoryPath);
        }

        [Fact]
        public void Load_PrefersTheExecutableFolderCopy_AndSaveWritesOnlyThere()
        {
            var fileService = new InMemoryFileService();
            fileService.Seed(IniFileName, @"Path=C:\Current");
            fileService.Seed(WorkingDirectoryIni, @"Path=D:\Stale");
            var store = new SettingsStore(fileService, IniFileName, WorkingDirectoryIni);

            Assert.Equal(@"C:\Current", store.Load().GameDirectoryPath);

            store.Save(new AppSettings { GameDirectoryPath = @"E:\Saved" });

            Assert.Contains(@"Path=E:\Saved", fileService.ReadAllLines(IniFileName));
            Assert.Contains(@"Path=D:\Stale", fileService.ReadAllLines(WorkingDirectoryIni));
        }

        [Fact]
        public void PublicConstructor_KeepsTheFileNextToTheExecutable()
        {
            var fileService = new InMemoryFileService();
            var store = new SettingsStore(fileService);

            store.Save(new AppSettings { GameDirectoryPath = @"C:\ME" });

            Assert.True(fileService.FileExists(Path.Combine(AppContext.BaseDirectory, IniFileName)));
        }

        [Fact]
        public void Save_OnDisk_ReplacesTheFileWithoutLeavingATemporaryCopy()
        {
            using var temp = new TempDirectory();
            string path = Path.Combine(temp.Path, IniFileName);
            File.WriteAllLines(path, [@"Path=C:\Old", "FOV=90"]);
            var store = new SettingsStore(new FileService(), path);

            store.Save(new AppSettings { GameDirectoryPath = @"C:\New" });

            Assert.Equal(@"C:\New", store.Load().GameDirectoryPath);
            Assert.Equal(IniFileName, Path.GetFileName(Assert.Single(Directory.GetFiles(temp.Path))));
        }

        [Fact]
        public void SaveThenLoad_RoundTripsValues()
        {
            var fileService = new InMemoryFileService();
            var store = new SettingsStore(fileService, IniFileName);
            var original = new AppSettings
            {
                GameDirectoryPath = @"F:\Game",
                UserFolderPath = @"F:\Game\TdGame",
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
