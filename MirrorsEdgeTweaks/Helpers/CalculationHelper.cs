using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using MirrorsEdgeTweaks.Models;

namespace MirrorsEdgeTweaks.Helpers
{
    public static class MathHelper
    {
        public static double DegreesToRadians(double degrees) => degrees * (Math.PI / 180.0);
        public static double RadiansToDegrees(double radians) => radians * (180.0 / Math.PI);

        public static double CalculateVerticalFov(double horizontalFovDegrees, double aspectRatio)
        {
            double horizontalFovRadians = DegreesToRadians(horizontalFovDegrees);
            double verticalFovRadians = 2 * Math.Atan(Math.Tan(horizontalFovRadians / 2) / aspectRatio);
            return RadiansToDegrees(verticalFovRadians);
        }

        public static AspectRatioInfo FormatAspectRatio(float ar)
        {
            string decimalFormat = $"{ar:F2}:1";
            string commonFormat = string.Empty;
            const double tolerance = 0.05;

            foreach (var ratio in CommonAspectRatios.Ratios)
            {
                if (Math.Abs(ar - ratio.Value) < tolerance)
                {
                    commonFormat = $"(≈ {ratio.Key})";
                    break;
                }
            }
            return new AspectRatioInfo { DecimalFormat = decimalFormat, CommonFormat = commonFormat, Value = ar };
        }
    }

    public static class ByteArrayHelper
    {
        public static byte[] StringToByteArray(string hex)
        {
            if (hex.Length % 2 != 0)
                throw new ArgumentException("The hex string cannot have an odd number of digits.", nameof(hex));

            return Enumerable.Range(0, hex.Length)
                             .Where(x => x % 2 == 0)
                             .Select(x => Convert.ToByte(hex.Substring(x, 2), 16))
                             .ToArray();
        }

        public static float ReadFloatFromBytes(byte[] bytes, int offset)
        {
            if (offset < 0 || offset + 4 > bytes.Length) return 0f;
            return BitConverter.ToSingle(bytes, offset);
        }

        public static void WriteFloatToBytes(byte[] bytes, int offset, float value)
        {
            if (offset < 0 || offset + 4 > bytes.Length) return;
            var valueBytes = BitConverter.GetBytes(value);
            Array.Copy(valueBytes, 0, bytes, offset, 4);
        }
    }

    public static class GameVersionHelper
    {
        public static GameVersion GetGameVersion(string gameDirectoryPath)
        {
            if (string.IsNullOrEmpty(gameDirectoryPath))
            {
                return new GameVersion { DisplayText = "Game Version: N/A", IsValid = false };
            }

            string exePath = System.IO.Path.Combine(gameDirectoryPath, "Binaries", "MirrorsEdge.exe");

            if (!System.IO.File.Exists(exePath))
            {
                return new GameVersion { DisplayText = "Game Version: MirrorsEdge.exe not found.", IsValid = false };
            }

            try
            {
                var versionInfo = System.Diagnostics.FileVersionInfo.GetVersionInfo(exePath);
                string? version = versionInfo.FileVersion;
                if (!string.IsNullOrEmpty(version))
                {
                    return new GameVersion 
                    { 
                        Version = version, 
                        DisplayText = $"Game Version: {version}", 
                        IsValid = true 
                    };
                }
                else
                {
                    return new GameVersion { DisplayText = "Game Version: Not found in executable details.", IsValid = false };
                }
            }
            catch (Exception)
            {
                return new GameVersion { DisplayText = "Game Version: Error reading version.", IsValid = false };
            }
        }

        public static string? GetDownloadUrl(string gameVersionInfo, string selectedFix)
        {
            string baseUrl;

            if (gameVersionInfo.Contains("1.0.0.0") || gameVersionInfo.Contains("1.0.1.0"))
            {
                baseUrl = "https://github.com/softsoundd/MirrorsEdgeTweaks/raw/refs/heads/main/Downloads/Base_";
            }
            else if (gameVersionInfo.Contains("1.1.0.0"))
            {
                baseUrl = "https://github.com/softsoundd/MirrorsEdgeTweaks/raw/refs/heads/main/Downloads/DLC_";
            }
            else
            {
                return null;
            }

            string fileName = selectedFix switch
            {
                "Original" => "TdGame.zip",
                "TdGame Fix (by Keku)" => "TdGameFix.zip",
                "Time Trials Timer Fix (by Nulaft)" => "TimerFix.zip",
                "TdGame Fix + Time Trials Timer Fix" => "TdGameFix+TimerFix.zip",
                _ => string.Empty
            };

            return string.IsNullOrEmpty(fileName) ? null : baseUrl + fileName;
        }
    }

    public static class TdGameVersionDetector
    {
        public static string DetectTdGameVersion(string packagePath)
        {
            if (string.IsNullOrEmpty(packagePath) || !System.IO.File.Exists(packagePath))
            {
                return "Unknown";
            }

            try
            {
                byte[] data = System.IO.File.ReadAllBytes(packagePath);

                var patterns = new Dictionary<byte[], string>
                {
                    { ByteArrayHelper.StringToByteArray("4E6F6E65000900A80070"), "Original_1010" },
                    { ByteArrayHelper.StringToByteArray("4E6F6E65000900A800FB"), "Original_1100" },
                    { ByteArrayHelper.StringToByteArray("4E6F6E650001002800AC"), "TdGameFix_1010" },
                    { ByteArrayHelper.StringToByteArray("9D6F2900050000004E6F6E65000100280014"), "TdGameFix_1100" },
                    { ByteArrayHelper.StringToByteArray("4E6F6E650001002800D6"), "TdGameFixTimerFix_1010" },
                    { ByteArrayHelper.StringToByteArray("706F2900050000004E6F6E65000100280014"), "TdGameFixTimerFix_1100" }
                };

                foreach (var pattern in patterns)
                {
                    int offset = FindPattern(data, pattern.Key);
                    if (offset != -1)
                    {
                        return DetermineVersionFromPattern(pattern.Value, data, offset);
                    }
                }

                return "Unknown";
            }
            catch (Exception)
            {
                return "Unknown";
            }
        }

        private static string DetermineVersionFromPattern(string patternType, byte[] data, int offset)
        {
            return patternType switch
            {
                "Original_1010" => data.Length > 2114997 ? (data[2114997] == 0x74 ? "Original" : "Time Trials Timer Fix (by Nulaft)") : "Original",
                "Original_1100" => data.Length > 2125557 ? (data[2125557] == 0x74 ? "Original" : "Time Trials Timer Fix (by Nulaft)") : "Original",
                "TdGameFix_1010" or "TdGameFix_1100" => "TdGame Fix (by Keku)",
                "TdGameFixTimerFix_1010" or "TdGameFixTimerFix_1100" => "TdGame Fix + Time Trials Timer Fix",
                _ => "Unknown"
            };
        }

        private static int FindPattern(byte[] source, byte[] pattern)
        {
            for (int i = 0; i < source.Length - pattern.Length + 1; i++)
            {
                if (source.Skip(i).Take(pattern.Length).SequenceEqual(pattern))
                {
                    return i;
                }
            }
            return -1;
        }
    }

    public static class ConfigFileHelper
    {
        public static void ModifyIniFile(string filePath, string section, string key, string value)
        {
            var fileInfo = new System.IO.FileInfo(filePath);
            bool wasReadOnly = fileInfo.IsReadOnly;

            try
            {
                if (wasReadOnly)
                    fileInfo.IsReadOnly = false;

                var lines = System.IO.File.ReadAllLines(filePath).ToList();
                string sectionHeader = $"[{section}]";
                int sectionIndex = -1;
                int keyIndex = -1;

                for (int i = 0; i < lines.Count; i++)
                {
                    if (lines[i].Trim().Equals(sectionHeader, StringComparison.OrdinalIgnoreCase))
                    {
                        sectionIndex = i;
                        for (int j = i + 1; j < lines.Count; j++)
                        {
                            if (lines[j].Trim().StartsWith("[")) break;

                            if (lines[j].Trim().StartsWith(key + "=", StringComparison.OrdinalIgnoreCase))
                            {
                                keyIndex = j;
                                break;
                            }
                        }
                        break;
                    }
                }

                string newEntry = $"{key}={value}";
                if (keyIndex != -1)
                {
                    lines[keyIndex] = newEntry;
                }
                else if (sectionIndex != -1)
                {
                    lines.Insert(sectionIndex + 1, newEntry);
                }
                else
                {
                    if (lines.Any() && !string.IsNullOrWhiteSpace(lines.Last()))
                    {
                        lines.Add(string.Empty);
                    }
                    lines.Add(sectionHeader);
                    lines.Add(newEntry);
                }

                System.IO.File.WriteAllLines(filePath, lines);
            }
            finally
            {
                if (wasReadOnly)
                    fileInfo.IsReadOnly = true;
            }
        }
    }

    public static class ResolutionHelper
    {
        public class Resolution
        {
            public int Width { get; set; }
            public int Height { get; set; }
            public string DisplayText => $"{Width} x {Height}";
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
        public struct DEVMODE
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string dmDeviceName;
            public short dmSpecVersion;
            public short dmDriverVersion;
            public short dmSize;
            public short dmDriverExtra;
            public int dmFields;
            public int dmPositionX;
            public int dmPositionY;
            public int dmDisplayOrientation;
            public int dmDisplayFixedOutput;
            public short dmColor;
            public short dmDuplex;
            public short dmYResolution;
            public short dmTTOption;
            public short dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string dmFormName;
            public short dmLogPixels;
            public short dmBitsPerPel;
            public int dmPelsWidth;
            public int dmPelsHeight;
            public int dmDisplayFlags;
            public int dmDisplayFrequency;
            public int dmICMMethod;
            public int dmICMIntent;
            public int dmMediaType;
            public int dmDitherType;
            public int dmReserved1;
            public int dmReserved2;
            public int dmPanningWidth;
            public int dmPanningHeight;
        }

        [DllImport("user32.dll")]
        public static extern int EnumDisplaySettings(string? deviceName, int modeNum, ref DEVMODE devMode);

        [DllImport("user32.dll")]
        public static extern int EnumDisplayDevices(string lpDevice, uint iDevNum, ref DISPLAY_DEVICE lpDisplayDevice, uint dwFlags);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct DISPLAY_DEVICE
        {
            public int cb;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string DeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string DeviceString;
            public int StateFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string DeviceID;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string DeviceKey;
        }

        public static List<Resolution> GetAvailableResolutions()
        {
            var resolutions = new List<Resolution>();
            var resolutionSet = new HashSet<string>();

            try
            {
                DEVMODE devMode = new DEVMODE();
                devMode.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));

                int modeNum = 0;
                while (EnumDisplaySettings(null, modeNum, ref devMode) != 0)
                {
                    if (devMode.dmPelsWidth > 0 && devMode.dmPelsHeight > 0)
                    {
                        string resolutionKey = $"{devMode.dmPelsWidth}x{devMode.dmPelsHeight}";
                        
                        if (!resolutionSet.Contains(resolutionKey) && 
                            devMode.dmPelsWidth >= 800 && devMode.dmPelsHeight >= 600 &&
                            devMode.dmPelsWidth <= 7680 && devMode.dmPelsHeight <= 4320)
                        {
                            resolutions.Add(new Resolution
                            {
                                Width = devMode.dmPelsWidth,
                                Height = devMode.dmPelsHeight
                            });
                            resolutionSet.Add(resolutionKey);
                        }
                    }
                    modeNum++;
                }
            }
            catch (Exception)
            {
                resolutions.AddRange(new[]
                {
                    new Resolution { Width = 1920, Height = 1080 },
                    new Resolution { Width = 2560, Height = 1440 },
                    new Resolution { Width = 1366, Height = 768 },
                    new Resolution { Width = 1280, Height = 720 }
                });
            }

            return resolutions.OrderBy(r => r.Width).ThenBy(r => r.Height).ToList();
        }
    }
}
