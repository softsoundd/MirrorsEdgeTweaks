namespace MirrorsEdgeTweaks.Services
{
    // Returns memory to Windows after bursts of package loads and file reads,
    // because the GC keeps the peak committed while the app is idle.
    internal static class MemoryTrimmer
    {
        private static readonly TimeSpan Delay = TimeSpan.FromSeconds(2);
        private static readonly System.Threading.Timer Timer = new(_ => Trim(), null, Timeout.Infinite, Timeout.Infinite);

        // A request while a trim is pending pushes it back, so a run of operations ends in a single trim.
        public static void RequestTrim() => Timer.Change(Delay, Timeout.InfiniteTimeSpan);

        private static void Trim()
        {
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);

            // UELib objects have finalizers, so the packages they belong to are only reclaimed by a second collection.
            GC.WaitForPendingFinalizers();
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        }
    }
}
