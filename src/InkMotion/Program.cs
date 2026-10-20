using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace InkMotion;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        if (args.Length > 0 && args[0] == "--export-job")
        {
            if (args.Length != 3 || !ExportJobStore.IsJobId(args[1]) || !Path.IsPathFullyQualified(args[2]))
            {
                return 2;
            }

            return Task.Run(() => ExportJobWorker.Run(new ExportJobStore(args[2]), args[1])).GetAwaiter().GetResult();
        }

        Application.Start(parameters =>
        {
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new App();
        });
        return 0;
    }
}
