using MirrorsEdgeTweaks.Helpers;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using UELib;

namespace MirrorsEdgeTweaks.Services
{
    // Shares file reads and loaded packages between the status checks of one read-only pass.
    // The scope flows with the async context, so Task.Run and dispatcher work started inside it share it too.
    // Outside a scope, every read is fresh and private.
    // Inside a scope, the returned arrays and packages are shared, so callers must not modify them or keep them past the scope.
    // Shared packages are not thread-safe, so the checks inside a scope must run one at a time.
    // Only PatchUtility.WritePreservingAttributes releases a file that the scope holds, so other writes must run outside a scope.
    internal sealed class GameFileReadScope : IDisposable
    {
        private static readonly AsyncLocal<GameFileReadScope?> Ambient = new();
        private static readonly StringComparer KeyComparer = StringComparer.OrdinalIgnoreCase;

        private readonly object _sync = new();
        private readonly Dictionary<string, Cached<byte[]>> _bytes = new(KeyComparer);
        private readonly Dictionary<string, Cached<ExeImage>> _exes = new(KeyComparer);
        private readonly Dictionary<string, Cached<UnrealPackage>> _packages = new(KeyComparer);

        // A package whose file changed stays open until the scope ends or the file is invalidated, because a lease may still use it.
        private readonly List<(string Key, UnrealPackage Package)> _replacedPackages = new();
        private volatile bool _disposed;

        private GameFileReadScope()
        {
        }

        private static GameFileReadScope? Current => Ambient.Value is { _disposed: false } scope ? scope : null;

        // Joins the active scope when there is one, so only the outermost scope releases what the pass loaded.
        public static IDisposable Begin()
        {
            if (Current != null)
                return JoinedScope.Instance;

            var scope = new GameFileReadScope();
            Ambient.Value = scope;
            return scope;
        }

        public static byte[] ReadAllBytes(string path) =>
            Current is { } scope && scope.TryGet(scope._bytes, path, File.ReadAllBytes, out byte[]? data)
                ? data
                : File.ReadAllBytes(path);

        public static ExeImage ReadExe(string path) =>
            Current is { } scope && scope.TryGet(scope._exes, path, scope.LoadExe, out ExeImage? image)
                ? image
                : new ExeImage(path, File.ReadAllBytes(path));

        // Inside a scope, headerOnly is ignored and the shared package is fully loaded.
        // Disposing the lease of a shared package leaves the package loaded until the scope ends.
        public static PackageLease OpenPackage(string path, bool headerOnly = false)
        {
            if (Current is { } scope && scope.TryGet(scope._packages, path, UePackageLocator.Load, out UnrealPackage? package))
                return new PackageLease(package, owned: false);

            return new PackageLease(headerOnly ? UePackageLocator.LoadHeader(path) : UePackageLocator.Load(path), owned: true);
        }

        // Drops the file's shared reads and closes its shared packages, so a write can replace the file.
        public static void Invalidate(string path) => Current?.Release(Path.GetFullPath(path));

        public void Dispose()
        {
            List<UnrealPackage> packages;
            lock (_sync)
            {
                if (_disposed)
                    return;

                _disposed = true;
                packages = _packages.Values.Select(cached => cached.Value)
                    .Concat(_replacedPackages.Select(replaced => replaced.Package))
                    .ToList();
                _bytes.Clear();
                _exes.Clear();
                _packages.Clear();
                _replacedPackages.Clear();
            }

            if (ReferenceEquals(Ambient.Value, this))
                Ambient.Value = null;

            foreach (UnrealPackage package in packages)
                package.Dispose();
        }

        // Returns false once the scope has ended.
        // Returns false when the file is missing, so that the caller's private read throws FileNotFoundException.
        private bool TryGet<T>(Dictionary<string, Cached<T>> cache, string path, Func<string, T> load, [NotNullWhen(true)] out T? value)
            where T : class
        {
            string key = Path.GetFullPath(path);
            lock (_sync)
            {
                value = _disposed ? null : GetOrLoad(cache, key, load);
                return value != null;
            }
        }

        // The caller must hold _sync.
        private T? GetOrLoad<T>(Dictionary<string, Cached<T>> cache, string key, Func<string, T> load)
            where T : class
        {
            FileStamp? stamp = FileStamp.Read(key);
            if (cache.TryGetValue(key, out Cached<T>? cached))
            {
                if (cached.Stamp == stamp)
                    return cached.Value;

                cache.Remove(key);
                if (cached.Value is UnrealPackage replaced)
                    _replacedPackages.Add((key, replaced));
            }

            if (stamp == null)
                return null;

            T value = load(key);
            cache[key] = new Cached<T>(stamp.Value, value);
            return value;
        }

        // The caller must hold _sync.
        private ExeImage LoadExe(string key) =>
            new(key, GetOrLoad(_bytes, key, File.ReadAllBytes) ?? File.ReadAllBytes(key));

        private void Release(string key)
        {
            var packages = new List<UnrealPackage>();
            lock (_sync)
            {
                _bytes.Remove(key);
                _exes.Remove(key);
                if (_packages.Remove(key, out Cached<UnrealPackage>? cached))
                    packages.Add(cached.Value);

                packages.AddRange(_replacedPackages
                    .Where(replaced => KeyComparer.Equals(replaced.Key, key))
                    .Select(replaced => replaced.Package));
                _replacedPackages.RemoveAll(replaced => KeyComparer.Equals(replaced.Key, key));
            }

            foreach (UnrealPackage package in packages)
                package.Dispose();
        }

        private readonly record struct FileStamp(long Length, DateTime LastWriteTimeUtc)
        {
            public static FileStamp? Read(string path)
            {
                var info = new FileInfo(path);
                return info.Exists ? new FileStamp(info.Length, info.LastWriteTimeUtc) : null;
            }
        }

        private sealed record Cached<T>(FileStamp Stamp, T Value);

        private sealed class JoinedScope : IDisposable
        {
            public static readonly JoinedScope Instance = new();

            public void Dispose()
            {
            }
        }
    }

    internal sealed class PackageLease : IDisposable
    {
        private readonly bool _owned;

        internal PackageLease(UnrealPackage package, bool owned)
        {
            Package = package;
            _owned = owned;
        }

        public UnrealPackage Package { get; }

        public void Dispose()
        {
            if (_owned)
                Package.Dispose();
        }
    }

    // The version and the OOA-decrypted copy are computed on first use and kept for later calls.
    internal sealed class ExeImage
    {
        private readonly Lazy<string?> _version;
        private readonly Lazy<byte[]> _decrypted;

        internal ExeImage(string path, byte[] raw)
        {
            Raw = raw;
            _version = new Lazy<string?>(() => ExeVersionDetector.DetectVersion(raw, path));
            _decrypted = new Lazy<byte[]>(() =>
            {
                byte[] copy = (byte[])raw.Clone();
                PatchUtility.DecryptOoaInPlace(copy);
                return copy;
            });
        }

        public byte[] Raw { get; }

        public string? Version => _version.Value;

        // A failed decryption, such as a missing licence file, throws the same exception on every call.
        public byte[] GetDecrypted() => _decrypted.Value;
    }
}
