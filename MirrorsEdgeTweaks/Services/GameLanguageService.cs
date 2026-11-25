using MirrorsEdgeTweaks.Helpers;
using System.Diagnostics;

namespace MirrorsEdgeTweaks.Services
{
    public enum IniLanguageOutcome
    {
        // The registry does not override the language.
        IniDecides,

        // MirrorsEdge.exe has the game language patch.
        Patched,

        // The registry Locale value names the language.
        RegistryUpdated,

        // The registry change was declined, so the registry still overrides the language.
        Declined
    }

    internal interface ILanguagePatch
    {
        InlinePatchState GetState(string exePath);
        void Apply(string exePath);
        void Remove(string exePath);
    }

    // Changing the registry needs administrator permission, so it only changes when the game language patch cannot be applied.
    public class GameLanguageService
    {
        private readonly IGameLanguageRegistry _registry;
        private readonly ILanguagePatch _patch;

        public GameLanguageService(IGameLanguageRegistry registry)
            : this(registry, new LanguagePatch())
        {
        }

        internal GameLanguageService(IGameLanguageRegistry registry, ILanguagePatch patch)
        {
            _registry = registry;
            _patch = patch;
        }

        // Returns the language that the registry selects, even while the patch makes the game ignore it.
        // Returns null when the registry leaves the language to TdEngine.ini, or selects languageCode.
        public string? FindRegistryOverride(string languageCode) =>
            GameLanguageResolver.FindOverride(_registry.ReadLocale(), languageCode);

        // registryBypassed is true while MirrorsEdge.exe has the patch.
        public string ResolveLoadedLanguage(string iniLanguageCode, bool registryBypassed) =>
            GameLanguageResolver.Resolve(_registry.ReadLocale(), iniLanguageCode, registryBypassed);

        // Makes the game load languageCode from TdEngine.ini.
        // A registry change that needs administrator permission only happens when confirmRegistryChange returns true.
        // confirmRegistryChange receives the language that the registry selects and the reason the patch failed.
        public async Task<IniLanguageOutcome> EnsureIniLanguageAsync(
            string exePath, string languageCode, Func<string, Exception, Task<bool>> confirmRegistryChange)
        {
            string? overridingLanguage = FindRegistryOverride(languageCode);
            if (overridingLanguage == null)
                return IniLanguageOutcome.IniDecides;

            Exception patchError;
            try
            {
                await Task.Run(() => EnsurePatched(exePath));
                return IniLanguageOutcome.Patched;
            }
            catch (Exception ex)
            {
                patchError = ex;
            }

            string locale = GameLanguageResolver.RegistryLocaleFor(languageCode)
                ?? throw new ArgumentException($"'{languageCode}' is not a language that the game supports.", nameof(languageCode));

            if (_registry.WriteNeedsPermission && !await confirmRegistryChange(overridingLanguage, patchError))
                return IniLanguageOutcome.Declined;

            return await _registry.WriteLocaleAsync(locale) == LocaleWriteResult.Written
                ? IniLanguageOutcome.RegistryUpdated
                : IniLanguageOutcome.Declined;
        }

        // Returns null when the registry does not override languageCode.
        // Returns true when the patch is applied.
        // Returns false when the patch could not be applied.
        public bool? ReapplyPatchIfOverridden(string exePath, string languageCode)
        {
            if (FindRegistryOverride(languageCode) == null)
                return null;

            try
            {
                EnsurePatched(exePath);
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to re-apply the game language patch: {ex.Message}");
                return false;
            }
        }

        // Returns null when the patch is not applied.
        // Returns true when the patch was removed.
        // Returns false when the patch stays, because the registry would override languageCode without it.
        public bool? RemovePatchIfUnneeded(string exePath, string languageCode)
        {
            if (_patch.GetState(exePath) != InlinePatchState.Patched)
                return null;

            if (FindRegistryOverride(languageCode) != null)
                return false;

            _patch.Remove(exePath);
            if (_patch.GetState(exePath) != InlinePatchState.Unpatched)
                throw new InvalidOperationException("The game language patch was still applied after it was removed.");
            return true;
        }

        private void EnsurePatched(string exePath)
        {
            if (_patch.GetState(exePath) != InlinePatchState.Patched)
                _patch.Apply(exePath);
        }

        private sealed class LanguagePatch : ILanguagePatch
        {
            public InlinePatchState GetState(string exePath) =>
                InlineVaPatchHelper.GetPatchState(exePath, InlineVaPatchHelper.LanguageRegistryBypassKey);

            public void Apply(string exePath) =>
                InlineVaPatchHelper.ApplyPatch(exePath, InlineVaPatchHelper.LanguageRegistryBypassKey);

            public void Remove(string exePath) =>
                InlineVaPatchHelper.RemovePatch(exePath, InlineVaPatchHelper.LanguageRegistryBypassKey);
        }
    }
}
