using Microsoft.UI.Xaml;

namespace Arca;

public partial class App : Application
{
    private MainWindow? _window;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) => CrashReport.Show("UI", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => CrashReport.Show("background", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            CrashReport.Show("task", e.Exception);
            e.SetObserved();
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            _window = new MainWindow();
            _window.Activate();
        }
        catch (Exception ex)
        {
            // Most often a problem building the window itself; without this the app would just vanish.
            CrashReport.Show("starting", ex);
            Exit();
            return;
        }

        _ = Task.Run(Associations.Register);

        // "Arca.exe photos.zip" (or "Open with Arca") opens that archive straight away.
        var path = Environment.GetCommandLineArgs().Skip(1)
            .FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal) && File.Exists(a));
        if (path is not null) _ = _window.LoadAsync(path);
    }
}
