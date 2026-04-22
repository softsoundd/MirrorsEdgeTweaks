using System;
using System.IO;
using MirrorsEdgeTweaks.Services;

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
    }
}
