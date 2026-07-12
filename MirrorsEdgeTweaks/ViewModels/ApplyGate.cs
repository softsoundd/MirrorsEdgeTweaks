namespace MirrorsEdgeTweaks.ViewModels
{
    public sealed class ApplyGate
    {
        private readonly SemaphoreSlim _gate = new(1, 1);

        public void Enqueue(Func<Task> work) => _ = RunAsync(work);

        public void Enqueue(Action work) => Enqueue(() =>
        {
            work();
            return Task.CompletedTask;
        });

        public async Task RunAsync(Func<Task> work)
        {
            await _gate.WaitAsync();
            try
            {
                await work();
            }
            finally
            {
                _gate.Release();
            }
        }
    }
}
