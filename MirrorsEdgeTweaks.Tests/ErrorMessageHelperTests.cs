using MirrorsEdgeTweaks.Helpers;
using MirrorsEdgeTweaks.Services;
using System.Net.Http;

namespace MirrorsEdgeTweaks.Tests
{
    public class ErrorMessageHelperTests
    {
        [Fact]
        public void Explain_ShowsAUserFacingMessageAsIs()
        {
            var ex = new UserFacingException("This version of MirrorsEdge.exe is not supported.");

            Assert.Equal(ex.Message, ErrorMessageHelper.Explain(ex));
            Assert.Null(ErrorMessageHelper.Details(ex));
        }

        [Fact]
        public void Details_ReturnsTheInnerMessageOfAUserFacingException()
        {
            var ex = new UserFacingException("Windows could not start Mirror's Edge.",
                new InvalidOperationException("Access is denied."));

            Assert.Equal("Windows could not start Mirror's Edge.", ErrorMessageHelper.Explain(ex));
            Assert.Equal("Access is denied.", ErrorMessageHelper.Details(ex));
        }

        [Fact]
        public void Explain_ShowsTheEaLicenseMessageAsIs()
        {
            var ex = new OoaLicenseNotFoundException(@"C:\ProgramData\Electronic Arts\EA Services\License\Mirror's Edge.dlf");

            Assert.Equal(ex.Message, ErrorMessageHelper.Explain(ex));
        }

        [Fact]
        public void Explain_ShowsAMissingFileOrFolderMessageAsIs()
        {
            var file = new FileNotFoundException(@"TdEngine.ini not found at: C:\Users\Player\TdEngine.ini");
            var folder = new DirectoryNotFoundException(@"CookedPC directory not found at: C:\Games\Mirror's Edge\TdGame\CookedPC");

            Assert.Equal(file.Message, ErrorMessageHelper.Explain(file));
            Assert.Null(ErrorMessageHelper.Details(file));
            Assert.Equal(folder.Message, ErrorMessageHelper.Explain(folder));
        }

        [Fact]
        public void Explain_DescribesDeniedAccess()
        {
            var ex = new UnauthorizedAccessException("Access to the path 'MirrorsEdge.exe' is denied.");

            Assert.StartsWith("Windows denied access to a file.", ErrorMessageHelper.Explain(ex));
            Assert.Equal(ex.Message, ErrorMessageHelper.Details(ex));
        }

        [Fact]
        public void Explain_DescribesAFileInUse()
        {
            var ex = new IOException("The process cannot access the file because it is being used by another process.");

            Assert.StartsWith("A file could not be read or changed.", ErrorMessageHelper.Explain(ex));
            Assert.Equal(ex.Message, ErrorMessageHelper.Details(ex));
        }

        [Fact]
        public void Explain_DescribesAFailedDownload()
        {
            Assert.StartsWith("The download failed.", ErrorMessageHelper.Explain(new HttpRequestException("Response status code does not indicate success: 404.")));
            Assert.StartsWith("The download failed.", ErrorMessageHelper.Explain(new TaskCanceledException()));
        }

        [Fact]
        public void Explain_FallsBackToVerifyingTheGameFiles()
        {
            var ex = new InvalidOperationException("No .rdata section in executable.");

            Assert.Contains("Verifying your game files usually fixes this.", ErrorMessageHelper.Explain(ex));
            Assert.Equal(ex.Message, ErrorMessageHelper.Details(ex));
        }
    }
}
