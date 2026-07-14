using System.Windows;
using System.Windows.Threading;

namespace MirrorsEdgeTweaks.Tests.TestSupport
{
    internal static class StaWpfTestRunner
    {
        private static readonly Lock Sync = new();

        public static void Run(Action action)
        {
            Exception? failure = null;

            var thread = new Thread(() =>
            {
                try
                {
                    lock (Sync)
                    {
                        action();
                    }
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
                finally
                {
                    if (Application.Current is not null)
                    {
                        Application.Current.Shutdown();
                    }
                }
            });

            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();

            if (failure is not null)
            {
                throw failure;
            }
        }

        public static void RunWithAppResources(Action<Application> action)
        {
            Run(() =>
            {
                var app = new App();
                app.InitializeComponent();
                app.Dispatcher.Invoke(DispatcherPriority.Send, () => action(app));
            });
        }
    }
}
