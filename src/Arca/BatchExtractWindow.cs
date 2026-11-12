using System.Diagnostics;
using System.Runtime.InteropServices;
using Arca.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using WinRT.Interop;

namespace Arca;

/// <summary>
/// Extracts several archives one after another, from File Explorer's right-click menu: each beside itself
/// ("Extract here") or into a new folder named after it ("Extract to a folder"). One row per archive shows how far
/// it got and where it went. The parts of a multi-part archive count as one archive.
/// </summary>
public sealed class BatchExtractWindow : Window
{
    private readonly ArcaSettings _settings = SettingsStore.Load();
    private readonly bool _here;
    private readonly List<Job> _jobs;
    private readonly CancellationTokenSource _cts = new();
    private readonly Grid _root = new();
    private readonly TextBlock _heading = new() { FontSize = 20, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
    private readonly Button _cancel = new() { Content = "Cancel" };
    private readonly Button _close = new() { Content = "Close", Visibility = Visibility.Collapsed };

    private sealed class Job
    {
        public required string Path { get; init; }
        public string? Problem { get; init; }
        public TextBlock Status { get; } = new() { Opacity = 0.8, TextWrapping = TextWrapping.Wrap };
        public ProgressBar Bar { get; } = new() { Margin = new Thickness(0, 6, 0, 0), Visibility = Visibility.Collapsed };
        public StackPanel Actions { get; } = new() { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 6, 0, 0) };
    }

    public BatchExtractWindow(IReadOnlyList<string> paths, bool here)
    {
        _here = here;
        _jobs = Distinct(paths);
        Title = "Arca";
        try { AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Arca.ico")); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }

        _root.RequestedTheme = _settings.Theme switch { "Light" => ElementTheme.Light, "Dark" => ElementTheme.Dark, _ => ElementTheme.Default };
        _root.ActualThemeChanged += (_, _) => PaintBackground();
        _root.Padding = new Thickness(24);
        _root.RowSpacing = 16;
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        _root.Children.Add(_heading);

        var list = new StackPanel { Spacing = 14 };
        foreach (var job in _jobs)
        {
            var row = new StackPanel();
            row.Children.Add(new TextBlock { Text = System.IO.Path.GetFileName(job.Path), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            row.Children.Add(job.Status);
            row.Children.Add(job.Bar);
            row.Children.Add(job.Actions);
            job.Status.Text = job.Problem ?? "Waiting";
            list.Children.Add(row);
        }
        var scroller = new ScrollViewer { Content = list };
        Grid.SetRow(scroller, 1);
        _root.Children.Add(scroller);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        buttons.Children.Add(_cancel);
        buttons.Children.Add(_close);
        Grid.SetRow(buttons, 2);
        _root.Children.Add(buttons);
        _cancel.Click += (_, _) => _cts.Cancel();
        _close.Click += (_, _) => Close();

        Content = _root;
        Closed += (_, _) => _cts.Cancel();
        Resize(600, Math.Min(640, 190 + 96 * _jobs.Count));
        _root.Loaded += async (_, _) =>
        {
            PaintBackground();
            await RunAsync();
        };
    }

    /// <summary>The same base colours as the main window's background brush, which code can't take from a theme dictionary.</summary>
    private void PaintBackground() =>
        _root.Background = new SolidColorBrush(_root.ActualTheme == ElementTheme.Dark
            ? Windows.UI.Color.FromArgb(255, 32, 32, 32)
            : Windows.UI.Color.FromArgb(255, 243, 243, 243));

    /// <summary>One job per archive: the parts of a multi-part archive selected together are extracted once.</summary>
    private static List<Job> Distinct(IReadOnlyList<string> paths)
    {
        var jobs = new List<Job>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            string key;
            string? problem = null;
            try { key = Volumes.Find(path).OpenPath; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                key = path;
                problem = ex.Message;
            }
            if (seen.Add(key)) jobs.Add(new Job { Path = path, Problem = problem });
        }
        return jobs;
    }

    private async Task RunAsync()
    {
        int done = 0, failed = 0;
        foreach (var job in _jobs)
        {
            _heading.Text = $"Extracting {done + failed + 1} of {_jobs.Count}";
            if (_cts.IsCancellationRequested) { job.Status.Text = "Cancelled"; continue; }
            if (job.Problem is not null) { failed++; continue; }
            if (await ExtractAsync(job)) done++; else failed++;
        }

        _heading.Text = _cts.IsCancellationRequested ? "Cancelled"
            : failed == 0 ? (done == 1 ? "Extracted" : $"All {done} extracted")
            : $"{done} extracted, {failed} with problems";
        _cancel.Visibility = Visibility.Collapsed;
        _close.Visibility = Visibility.Visible;
    }

    private async Task<bool> ExtractAsync(Job job)
    {
        job.Bar.Visibility = Visibility.Visible;
        job.Bar.IsIndeterminate = true;
        job.Status.Text = "Reading…";
        var token = _cts.Token;
        try
        {
            // Look first, so a password is asked for before anything is written rather than after half the files fail.
            string? password = null;
            ArchiveListing listing;
            while (true)
            {
                try
                {
                    var pw = password;
                    listing = await Task.Run(() => ArchiveService.List(job.Path, pw), token);
                    break;
                }
                catch (PasswordRequiredException ex)
                {
                    password = await AskPasswordAsync(job.Path, ex.WrongPassword);
                    if (password is null) { job.Status.Text = "Skipped: no password given"; return false; }
                }
            }
            if (password is null && listing.Entries.Any(e => e.IsEncrypted))
            {
                password = await AskPasswordAsync(job.Path, wrong: false);
                if (password is null) { job.Status.Text = "Skipped: no password given"; return false; }
            }

            var options = new ExtractOptions
            {
                Password = password,
                SmartFolder = _here,
                PropagateMarkOfTheWeb = _settings.KeepInternetMark,
            };
            var destination = _here ? System.IO.Path.GetDirectoryName(Volumes.Find(job.Path).OpenPath)! : ArchiveService.OwnFolderFor(job.Path);
            long total = Math.Max(1, listing.TotalSize);
            job.Bar.IsIndeterminate = false;
            job.Bar.Maximum = 100;
            var progress = new Progress<ExtractProgress>(p =>
            {
                job.Bar.Value = Math.Min(100, p.BytesWritten * 100.0 / total);
                job.Status.Text = $"{p.FilesExtracted:N0} files, {ArchiveService.FormatBytes(p.BytesWritten)}";
            });

            var result = await Task.Run(() => ArchiveService.Extract(job.Path, destination, options, progress, token), token);
            job.Bar.Visibility = Visibility.Collapsed;
            job.Actions.Children.Add(OpenFolderButton(result.Destination));

            if (!result.Completed)
            {
                job.Status.Text = "Stopped for safety: " + result.AbortReason;
                return false;
            }
            var summary = $"{result.FilesExtracted:N0} {(result.FilesExtracted == 1 ? "file" : "files")} to {result.Destination}";
            if (result.Skipped.Count > 0)
            {
                job.Status.Text = $"{summary}. {result.Skipped.Count:N0} skipped, e.g. {result.Skipped[0].Path}: {result.Skipped[0].Reason}";
                return false;
            }
            job.Status.Text = summary;
            return true;
        }
        catch (OperationCanceledException)
        {
            job.Status.Text = "Cancelled. Files already written were left where they are.";
            return false;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            job.Status.Text = "Couldn't extract: " + ex.Message;
            return false;
        }
        finally
        {
            job.Bar.Visibility = Visibility.Collapsed;
        }
    }

    private async Task<string?> AskPasswordAsync(string path, bool wrong)
    {
        var box = new PasswordBox { PlaceholderText = "Password" };
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(new TextBlock { Text = System.IO.Path.GetFileName(path), TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(box);
        var dialog = new ContentDialog
        {
            Title = wrong ? "That password is wrong" : "This archive is password-protected",
            Content = panel,
            PrimaryButtonText = "OK",
            CloseButtonText = "Skip",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = _root.XamlRoot,
            RequestedTheme = _root.ActualTheme,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || box.Password.Length == 0) return null;
        return box.Password;
    }

    private static Button OpenFolderButton(string folder)
    {
        var button = new Button { Content = "Open folder" };
        button.Click += (_, _) =>
        {
            var psi = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
            psi.ArgumentList.Add(folder);
            Process.Start(psi);
        };
        return button;
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    private void Resize(int width, int height)
    {
        uint dpi = GetDpiForWindow(WindowNative.GetWindowHandle(this));
        double scale = dpi == 0 ? 1 : dpi / 96.0;
        AppWindow.Resize(new SizeInt32((int)(width * scale), (int)(height * scale)));
    }
}
