using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Arca;

/// <summary>
/// Custom entry point so the command-line switches Integra uses (--version, --status, --quit) answer
/// before any window or WinRT start-up. Unlike Porta, Arca is not single-instance: each archive can have its own window.
/// </summary>
public static class Program
{
    [DllImport("Microsoft.ui.xaml.dll")]
    private static extern void XamlCheckProcessRequirements();

    [STAThread]
    private static int Main(string[] args)
    {
        if (Cli.TryRun(args) is int exitCode) return exitCode;

        WinRT.ComWrappersSupport.InitializeComWrappers();
        XamlCheckProcessRequirements();
        Application.Start(p =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });
        return 0;
    }
}
