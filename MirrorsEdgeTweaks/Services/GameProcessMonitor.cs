using System.Diagnostics;
using System.Windows.Threading;

namespace MirrorsEdgeTweaks.Services
{
    public interface IGameProcessMonitor
    {
        event Action<bool>? RunningStateChanged;
        bool IsGameRunning { get; }
        void Start();
        void Stop();
    }

    public class GameProcessMonitor : IGameProcessMonitor
    {
        private const string ProcessName = "MirrorsEdge";

        private readonly DispatcherTimer _timer;
        private bool _isGameRunning;

        public event Action<bool>? RunningStateChanged;

        public bool IsGameRunning => _isGameRunning;

        public GameProcessMonitor()
        {
            _timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(1500)
            };
            _timer.Tick += (_, _) => Poll();
        }

        public void Start()
        {
            Poll();
            _timer.Start();
        }

        public void Stop() => _timer.Stop();

        private void Poll()
        {
            bool running = IsProcessRunning();
            if (running == _isGameRunning)
            {
                return;
            }

            _isGameRunning = running;
            RunningStateChanged?.Invoke(running);
        }

        private static bool IsProcessRunning()
        {
            Process[] processes = Process.GetProcessesByName(ProcessName);
            try
            {
                return processes.Length > 0;
            }
            finally
            {
                foreach (Process process in processes)
                {
                    process.Dispose();
                }
            }
        }
    }
}
