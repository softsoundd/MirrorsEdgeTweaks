using System.IO;

namespace MirrorsEdgeTweaks.Services
{
    // These settings are stored in metweaksconfig.ini next to the executable.
    // A null value means that the key is absent from the file.
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
        private readonly string _path;
        private readonly string? _workingDirectoryPath;

        // Older versions save the file in the working directory, so a copy there is read when the executable's folder has none.
        public SettingsStore(IFileService fileService)
            : this(fileService, Path.Combine(AppContext.BaseDirectory, IniFileName), Path.GetFullPath(IniFileName))
        {
        }

        internal SettingsStore(IFileService fileService, string path, string? workingDirectoryPath = null)
        {
            _fileService = fileService;
            _path = path;
            _workingDirectoryPath = workingDirectoryPath;
        }

        public AppSettings Load()
        {
            string? filePath = _fileService.FileExists(_path) ? _path
                : _workingDirectoryPath != null && _fileService.FileExists(_workingDirectoryPath) ? _workingDirectoryPath
                : null;
            if (filePath == null)
            {
                return new AppSettings();
            }

            // A key that appears more than once, such as after a hand edit, takes its last value.
            var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string line in _fileService.ReadAllLines(filePath))
            {
                int separator = line.IndexOf('=');
                if (separator >= 0)
                {
                    settings[line[..separator].Trim()] = line[(separator + 1)..].Trim();
                }
            }

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
            _fileService.WriteAllLines(_path, lines);
        }
    }
}
