using System;
using System.IO;
using UELib;

namespace MirrorsEdgeTweaks.Services
{
    public interface IPackageService
    {
        UnrealPackage? LoadPackage(string path);
        void DisposePackage(UnrealPackage? package);
        bool IsValidGameDirectory(string path);
    }

    public class PackageService : IPackageService
    {
        public UnrealPackage? LoadPackage(string path)
        {
            try
            {
                var package = UnrealLoader.LoadPackage(path, FileAccess.Read);
                package?.InitializePackage();
                return package;
            }
            catch (Exception)
            {
                return null;
            }
        }

        public void DisposePackage(UnrealPackage? package)
        {
            package?.Dispose();
        }

        public bool IsValidGameDirectory(string path)
        {
            return File.Exists(Path.Combine(path, "TdGame", "CookedPC", "Engine.u")) &&
                   File.Exists(Path.Combine(path, "TdGame", "CookedPC", "TdGame.u"));
        }
    }

}
