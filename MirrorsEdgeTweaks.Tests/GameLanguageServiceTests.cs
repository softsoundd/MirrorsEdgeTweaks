using MirrorsEdgeTweaks.Helpers;
using MirrorsEdgeTweaks.Services;

namespace MirrorsEdgeTweaks.Tests
{
    public class GameLanguageServiceTests
    {
        private const string ExePath = @"C:\Game\Binaries\MirrorsEdge.exe";

        private readonly FakeRegistry _registry = new();
        private readonly FakePatch _patch = new();
        private readonly List<(string OverridingLanguage, Exception PatchError)> _confirmations = new();
        private bool _confirmationAnswer = true;

        private GameLanguageService CreateService() => new(_registry, _patch);

        private Task<IniLanguageOutcome> EnsureAsync(string languageCode) =>
            CreateService().EnsureIniLanguageAsync(ExePath, languageCode, (overridingLanguage, patchError) =>
            {
                _confirmations.Add((overridingLanguage, patchError));
                return Task.FromResult(_confirmationAnswer);
            });

        [Theory]
        [InlineData(null)]
        [InlineData("ja_JP")]
        [InlineData("fr_FR")]
        public async Task Ensure_ChangesNothing_WhenTheRegistryDoesNotOverrideTheLanguage(string? locale)
        {
            _registry.Locale = locale;

            Assert.Equal(IniLanguageOutcome.IniDecides, await EnsureAsync("fra"));
            Assert.Equal(0, _patch.ApplyCount);
            Assert.Empty(_registry.WriteAttempts);
            Assert.Empty(_confirmations);
        }

        [Fact]
        public async Task Ensure_PatchesTheExe_WhenTheRegistryOverridesTheLanguage()
        {
            _registry.Locale = "en_UK";

            Assert.Equal(IniLanguageOutcome.Patched, await EnsureAsync("fra"));
            Assert.Equal(1, _patch.ApplyCount);
            Assert.Empty(_registry.WriteAttempts);
            Assert.Empty(_confirmations);
        }

        [Fact]
        public async Task Ensure_DoesNotPatchAgain_WhenThePatchIsApplied()
        {
            _registry.Locale = "en_UK";
            _patch.State = InlinePatchState.Patched;

            Assert.Equal(IniLanguageOutcome.Patched, await EnsureAsync("fra"));
            Assert.Equal(0, _patch.ApplyCount);
        }

        [Fact]
        public async Task Ensure_ChangesTheRegistry_OnlyAfterThePatchFails_AndTheUserAgrees()
        {
            _registry.Locale = "en_UK";
            _patch.ApplyFailure = new UserFacingException("This version of MirrorsEdge.exe is not supported.");

            Assert.Equal(IniLanguageOutcome.RegistryUpdated, await EnsureAsync("cht"));
            Assert.Equal(1, _patch.ApplyCount);
            var confirmation = Assert.Single(_confirmations);
            Assert.Equal("int", confirmation.OverridingLanguage);
            Assert.Same(_patch.ApplyFailure, confirmation.PatchError);
            Assert.Equal(new[] { "zh_TW" }, _registry.WriteAttempts);
        }

        [Fact]
        public async Task Ensure_LeavesTheRegistry_WhenTheUserDoesNotAgree()
        {
            _registry.Locale = "en_UK";
            _patch.ApplyFailure = new IOException("MirrorsEdge.exe is in use.");
            _confirmationAnswer = false;

            Assert.Equal(IniLanguageOutcome.Declined, await EnsureAsync("fra"));
            Assert.Empty(_registry.WriteAttempts);
        }

        [Fact]
        public async Task Ensure_ReportsADeclinedAdministratorPrompt()
        {
            _registry.Locale = "en_UK";
            _registry.WriteResult = LocaleWriteResult.Declined;
            _patch.ApplyFailure = new IOException("MirrorsEdge.exe is in use.");

            Assert.Equal(IniLanguageOutcome.Declined, await EnsureAsync("fra"));
            Assert.Equal("en_UK", _registry.Locale);
        }

        [Fact]
        public async Task Ensure_ChangesTheRegistryWithoutAsking_WhenTweaksIsAdministrator()
        {
            _registry.Locale = "en_UK";
            _registry.WriteNeedsPermission = false;
            _patch.ApplyFailure = new IOException("MirrorsEdge.exe is in use.");

            Assert.Equal(IniLanguageOutcome.RegistryUpdated, await EnsureAsync("fra"));
            Assert.Empty(_confirmations);
            Assert.Equal(new[] { "fr_FR" }, _registry.WriteAttempts);
        }

        [Fact]
        public void Reapply_DoesNothing_WhenTheRegistryDoesNotOverrideTheLanguage()
        {
            _registry.Locale = "fr_FR";

            Assert.Null(CreateService().ReapplyPatchIfOverridden(ExePath, "fra"));
            Assert.Equal(0, _patch.ApplyCount);
        }

        [Fact]
        public void Reapply_AppliesThePatch_WhenTheRegistryOverridesTheLanguage()
        {
            _registry.Locale = "en_UK";

            Assert.True(CreateService().ReapplyPatchIfOverridden(ExePath, "fra"));
            Assert.Equal(1, _patch.ApplyCount);
        }

        [Fact]
        public void Reapply_ReturnsFalse_WhenThePatchCannotBeApplied()
        {
            _registry.Locale = "en_UK";
            _patch.ApplyFailure = new UserFacingException("This version of MirrorsEdge.exe is not supported.");

            Assert.False(CreateService().ReapplyPatchIfOverridden(ExePath, "fra"));
            Assert.Empty(_registry.WriteAttempts);
        }

        [Fact]
        public void RemoveIfUnneeded_ReturnsNull_WhenThePatchIsNotApplied()
        {
            Assert.Null(CreateService().RemovePatchIfUnneeded(ExePath, "fra"));
            Assert.Equal(0, _patch.RemoveCount);
        }

        [Fact]
        public void RemoveIfUnneeded_KeepsThePatch_WhileTheRegistryWouldOverrideTheLanguage()
        {
            _registry.Locale = "en_UK";
            _patch.State = InlinePatchState.Patched;

            Assert.False(CreateService().RemovePatchIfUnneeded(ExePath, "fra"));
            Assert.Equal(0, _patch.RemoveCount);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("fr_FR")]
        public void RemoveIfUnneeded_RemovesThePatch_WhenTheGameWouldLoadTheLanguageWithoutIt(string? locale)
        {
            _registry.Locale = locale;
            _patch.State = InlinePatchState.Patched;

            Assert.True(CreateService().RemovePatchIfUnneeded(ExePath, "fra"));
            Assert.Equal(InlinePatchState.Unpatched, _patch.State);
        }

        private sealed class FakeRegistry : IGameLanguageRegistry
        {
            public string? Locale { get; set; }
            public bool WriteNeedsPermission { get; set; } = true;
            public LocaleWriteResult WriteResult { get; set; } = LocaleWriteResult.Written;
            public List<string> WriteAttempts { get; } = new();

            public string? ReadLocale() => Locale;

            public Task<LocaleWriteResult> WriteLocaleAsync(string locale)
            {
                WriteAttempts.Add(locale);
                if (WriteResult == LocaleWriteResult.Written)
                    Locale = locale;
                return Task.FromResult(WriteResult);
            }
        }

        private sealed class FakePatch : ILanguagePatch
        {
            public InlinePatchState State { get; set; } = InlinePatchState.Unpatched;
            public Exception? ApplyFailure { get; set; }
            public int ApplyCount { get; private set; }
            public int RemoveCount { get; private set; }

            public InlinePatchState GetState(string exePath) => State;

            public void Apply(string exePath)
            {
                ApplyCount++;
                if (ApplyFailure != null)
                    throw ApplyFailure;
                State = InlinePatchState.Patched;
            }

            public void Remove(string exePath)
            {
                RemoveCount++;
                State = InlinePatchState.Unpatched;
            }
        }
    }
}
