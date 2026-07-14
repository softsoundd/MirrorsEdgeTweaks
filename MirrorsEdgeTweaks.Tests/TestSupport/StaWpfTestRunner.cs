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
            Exception? failure = null;

            var thread = new Thread(() =>
            {
                App? app = null;

                try
                {
                    lock (Sync)
                    {
                        app = new App();
                        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                        app.InitializeComponent();
                        action(app);
                    }
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
                finally
                {
                    if (app is not null)
                    {
                        app.Dispatcher.InvokeShutdown();
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
    }
}
