using Microsoft.Win32;
using MirrorsEdgeTweaks.Helpers;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security;
using System.Text.RegularExpressions;

namespace MirrorsEdgeTweaks.Services
{
    public enum LocaleWriteResult
    {
        Written,
        Declined
    }

    // Reads and writes the registry Locale value that Mirror's Edge picks its language from.
    public interface IGameLanguageRegistry
    {
        // Is true when writing shows a Windows administrator prompt, because Tweaks cannot change the key itself.
        bool WriteNeedsPermission { get; }

        // Returns null when the game's registry key or its Locale value is missing.
        string? ReadLocale();

        // Returns Declined when the administrator prompt is declined.
        // Throws when the value could not be written.
        Task<LocaleWriteResult> WriteLocaleAsync(string locale);
    }

    public sealed class GameLanguageRegistry : IGameLanguageRegistry
    {
        // The game is 32-bit, so it reads this key through the 32-bit registry view, which is WOW6432Node on 64-bit Windows.
        private const string KeyPath = @"SOFTWARE\EA Games\Mirror's Edge";

        private const string LocaleValueName = "Locale";
        private const int ErrorCancelled = 1223;
        private const string WriteFailedMessage = "Windows could not change the game's language setting.";

        // The value goes on reg.exe's command line, so it is limited to the characters of the game's locale values.
        private static readonly Regex LocalePattern = new(@"^[A-Za-z_]{2,8}\z");

        // A key whose permissions let the user change it needs no administrator prompt.
        public bool WriteNeedsPermission => !Environment.IsPrivilegedProcess && !CanOpenKeyForWriting();

        internal static bool IsValidLocale(string locale) => LocalePattern.IsMatch(locale);

        public string? ReadLocale()
        {
            try
            {
                using RegistryKey machine = OpenMachineView();
                using RegistryKey? key = machine.OpenSubKey(KeyPath);

                // The game ignores a value that is not a string, and reads the value as a C string, which ends at the first null.
                if (key?.GetValue(LocaleValueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not string locale)
                    return null;
                int end = locale.IndexOf('\0');
                return end < 0 ? locale : locale[..end];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
            {
                Debug.WriteLine($"Failed to read the game's registry Locale value: {ex.Message}");
                return null;
            }
        }

        public async Task<LocaleWriteResult> WriteLocaleAsync(string locale)
        {
            if (!IsValidLocale(locale))
                throw new ArgumentException($"'{locale}' is not a locale value that the game recognises.", nameof(locale));

            if (!WriteNeedsPermission)
            {
                WriteDirectly(locale);
                return LocaleWriteResult.Written;
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "reg.exe"),
                Arguments = $"add \"HKLM\\{KeyPath}\" /v {LocaleValueName} /t REG_SZ /d {locale} /f /reg:32",
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            };

            int exitCode;
            try
            {
                using Process process = Process.Start(startInfo)
                    ?? throw new UserFacingException(WriteFailedMessage, new InvalidOperationException("Windows did not start reg.exe."));
                await process.WaitForExitAsync().ConfigureAwait(false);
                exitCode = process.ExitCode;
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == ErrorCancelled)
            {
                return LocaleWriteResult.Declined;
            }
            catch (Win32Exception ex)
            {
                throw new UserFacingException(WriteFailedMessage, ex);
            }

            if (exitCode != 0)
                throw new UserFacingException(WriteFailedMessage, new InvalidOperationException($"reg.exe exited with code {exitCode}."));

            if (!string.Equals(ReadLocale(), locale, StringComparison.Ordinal))
                throw new UserFacingException(WriteFailedMessage, new InvalidOperationException("The registry Locale value did not change."));
            return LocaleWriteResult.Written;
        }

        private static bool CanOpenKeyForWriting()
        {
            try
            {
                using RegistryKey machine = OpenMachineView();
                using RegistryKey? key = machine.OpenSubKey(KeyPath, writable: true);
                return key != null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
            {
                return false;
            }
        }

        private static void WriteDirectly(string locale)
        {
            try
            {
                using RegistryKey machine = OpenMachineView();
                using RegistryKey key = machine.CreateSubKey(KeyPath);
                key.SetValue(LocaleValueName, locale, RegistryValueKind.String);
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or SecurityException)
            {
                throw new UserFacingException(WriteFailedMessage, ex);
            }
        }

        private static RegistryKey OpenMachineView() =>
            RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
    }
}
