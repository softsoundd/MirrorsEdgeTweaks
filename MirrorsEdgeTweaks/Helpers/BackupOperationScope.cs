namespace MirrorsEdgeTweaks.Helpers
{
    public sealed class BackupOperationScope : IDisposable
    {
        private readonly HashSet<string> _paths = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _stagedPaths = new(StringComparer.OrdinalIgnoreCase);
        private bool _completed;

        internal void Register(string path) => _paths.Add(path);

        // Returns false when the operation already staged a backup of path.
        internal bool TryClaimBackup(string path) => _stagedPaths.Add(path);

        public void Complete()
        {
            if (_completed)
                return;

            _completed = true;

            // An enclosing operation may still fail, so it takes these backups over and prunes them when it completes.
            if (PatchUtility.FindEnclosingBackupOperation(this) is { } enclosing)
            {
                enclosing._paths.UnionWith(_paths);
                return;
            }

            foreach (string path in _paths)
                BackupRetentionService.PruneBackupForPath(path);
        }

        public void Dispose() => PatchUtility.ReleaseBackupOperation(this);
    }
}
