using MirrorsEdgeTweaks.Services;
using System.IO;

namespace MirrorsEdgeTweaks.Helpers
{
    internal static class PatchUtility
    {
        public static void WritePreservingAttributes(string path, byte[] content)
        {
            FileAttributes attributes = File.GetAttributes(path);
            bool wasReadOnly = (attributes & FileAttributes.ReadOnly) == FileAttributes.ReadOnly;
            if (wasReadOnly)
                File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
            try
            {
                File.WriteAllBytes(path, content);
            }
            finally
            {
                if (wasReadOnly)
                    File.SetAttributes(path, attributes);
            }
        }

        public static void DecryptOoaInPlace(byte[] data)
        {
            string? dlfPath = OoaService.FindLicensePath(data);
            if (dlfPath == null)
                throw new InvalidOperationException("OOA license file not found.");
            byte[] key = OoaService.DecryptDlf(File.ReadAllBytes(dlfPath));
            OoaService.DecryptSections(data, key);
        }

        public sealed class OoaSession
        {
            internal byte[] Key { get; init; } = Array.Empty<byte>();
            internal OoaContext Ctx { get; init; } = new();
        }

        public static OoaSession BeginOoa(byte[] data)
        {
            string? dlfPath = OoaService.FindLicensePath(data);
            if (dlfPath == null)
                throw new OoaLicenseNotFoundException(OoaService.GetExpectedLicensePath(data));

            byte[] key = OoaService.DecryptDlf(File.ReadAllBytes(dlfPath));
            OoaService.StripAuthenticode(data);
            OoaContext ctx = OoaService.DecryptSections(data, key);
            return new OoaSession { Key = key, Ctx = ctx };
        }

        public static byte[] FinishOoa(byte[] data, OoaSession session)
        {
            OoaService.UpdateEncBlockCrcs(data, session.Ctx);
            OoaService.ReencryptSections(data, session.Key, session.Ctx);
            return OoaService.TruncateToSections(data);
        }

        public static byte[] ApplyUnderOoa(byte[] data, Func<byte[], byte[]> patch)
        {
            OoaSession session = BeginOoa(data);
            data = patch(data);
            return FinishOoa(data, session);
        }
    }
}
