using Microsoft.UI.Xaml;

namespace Arca;

public partial class App : Application
{
    private MainWindow? _window;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();

        // "Arca.exe photos.zip" (or "Open with Arca") opens that archive straight away.
        var path = Environment.GetCommandLineArgs().Skip(1)
            .FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal) && File.Exists(a));
        if (path is not null) _ = _window.LoadAsync(path);
    }
}
