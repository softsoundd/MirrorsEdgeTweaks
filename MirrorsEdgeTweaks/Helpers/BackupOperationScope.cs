namespace MirrorsEdgeTweaks.Helpers
{
    public sealed class BackupOperationScope : IDisposable
    {
        private readonly HashSet<string> _paths = new(StringComparer.OrdinalIgnoreCase);
        private bool _completed;

        internal void Register(string path) => _paths.Add(path);

        public void Complete()
        {
            if (_completed)
                return;

            foreach (string path in _paths)
                BackupRetentionService.PruneBackupForPath(path);

            _completed = true;
        }

        public void Dispose() => PatchUtility.ReleaseBackupOperation(this);
    }
}
