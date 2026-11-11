using MirrorsEdgeTweaks.Helpers;
using MirrorsEdgeTweaks.ViewModels;
using System.Net.Http;

namespace MirrorsEdgeTweaks.Tests
{
    public class RestoreSettingsReportTests
    {
        [Fact]
        public void FailureParagraphs_ExplainASharedCauseOnce()
        {
            var report = new RestoreSettingsReport();
            report.AddFailure("Could not restore Engine.u.", new HttpRequestException("Connection refused."));
            report.AddFailure("Could not restore Tweaks Scripts UI, because the game version was not recognised.");
            report.AddFailure("Could not restore TdGame.u.", new HttpRequestException("Connection refused."));

            string downloadFailed = ErrorMessageHelper.Explain(new HttpRequestException());
            Assert.Equal(new[]
            {
                $"Could not restore Engine.u. Could not restore TdGame.u. {downloadFailed}",
                "Could not restore Tweaks Scripts UI, because the game version was not recognised.",
            }, report.FailureParagraphs());
            Assert.Equal(2, report.FailureDetails.Count);
        }

        [Fact]
        public void HasFailures_IsFalse_UntilAFailureIsAdded()
        {
            var report = new RestoreSettingsReport();
            Assert.False(report.HasFailures);

            report.AddFailure("Could not restore the language files, because the game language was not recognised.");

            Assert.True(report.HasFailures);
            Assert.Empty(report.FailureDetails);
        }
    }
}
