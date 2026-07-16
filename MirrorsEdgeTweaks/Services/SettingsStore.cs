namespace MirrorsEdgeTweaks.Services
{
    // The persisted application settings stored in metweaksconfig.ini next to the executable.
    // Null values represent keys that were absent from the file.
    public sealed record AppSettings
    {
        public string? GameDirectoryPath { get; init; }
        public string? UserFolderPath { get; init; }
        public string? Fov { get; init; }
        public string? Dpi { get; init; }
        public string? Cm360 { get; init; }
        public string? LaunchArguments { get; init; }
    }

    public interface ISettingsStore
    {
        AppSettings Load();
        void Save(AppSettings settings);
    }

    public class SettingsStore : ISettingsStore
    {
        private const string IniFileName = "metweaksconfig.ini";
        private readonly IFileService _fileService;

        public SettingsStore(IFileService fileService)
        {
            _fileService = fileService;
        }

        public AppSettings Load()
        {
            if (!_fileService.FileExists(IniFileName))
            {
                return new AppSettings();
            }

            var settings = _fileService.ReadAllLines(IniFileName)
                .Where(line => !string.IsNullOrWhiteSpace(line) && line.Contains('='))
                .Select(line => line.Split(new[] { '=' }, 2))
                .ToDictionary(parts => parts[0].Trim(), parts => parts[1].Trim(), StringComparer.OrdinalIgnoreCase);

            return new AppSettings
            {
                GameDirectoryPath = settings.TryGetValue("Path", out var path) ? path : null,
                UserFolderPath = settings.TryGetValue("UserFolderPath", out var userFolderPath) ? userFolderPath : null,
                Fov = settings.TryGetValue("FOV", out var fov) ? fov : null,
                Dpi = settings.TryGetValue("DPI", out var dpi) ? dpi : null,
                Cm360 = settings.TryGetValue("Cm360", out var cm360) ? cm360 : null,
                LaunchArguments = settings.TryGetValue("LaunchArguments", out var launchArguments) ? launchArguments : null,
            };
        }

        public void Save(AppSettings settings)
        {
            var lines = new List<string>
            {
                $"Path={settings.GameDirectoryPath}",
                $"UserFolderPath={settings.UserFolderPath}",
                $"FOV={settings.Fov}",
                $"DPI={settings.Dpi}",
                $"Cm360={settings.Cm360}",
                $"LaunchArguments={settings.LaunchArguments}"
            };
            _fileService.WriteAllLines(IniFileName, lines);
        }
    }
}
