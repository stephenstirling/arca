using System.Collections.ObjectModel;
using System.Diagnostics;
using Arca.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Arca;

public sealed partial class MainWindow : Window
{
    private readonly ObservableCollection<EntryRow> _rows = new();
    private ArchiveListing? _listing;
    private string? _password;
    private CancellationTokenSource? _cts;

    public MainWindow()
    {
        InitializeComponent();
        Title = "Arca";
        SystemBackdrop = new MicaBackdrop();
        EntryList.ItemsSource = _rows;
        Closed += (_, _) => _cts?.Cancel();   // closing the window (or Integra's --quit) stops a running extraction
    }

    /// <summary>Reads the archive's contents and shows them. Nothing is extracted.</summary>
    public async Task LoadAsync(string path)
    {
        ShowInfo(InfoBarSeverity.Informational, "", null);
        StatusText.Text = "Reading " + Path.GetFileName(path) + "…";
        BusyBar.Visibility = Visibility.Visible;
        _password = null;
        try
        {
            var listing = await Task.Run(() => ArchiveService.List(path));
            _listing = listing;

            _rows.Clear();
            foreach (var entry in listing.Entries) _rows.Add(new EntryRow(entry));
            EmptyText.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            ExtractButton.IsEnabled = listing.FileCount > 0;

            Title = Path.GetFileName(path) + " - Arca";
            SummaryText.Text = $"{ArchiveFormats.DisplayName(listing.Format)}  ·  {listing.FileCount:N0} files  ·  {ArchiveService.FormatBytes(listing.TotalSize)}";
            StatusText.Text = path;

            if (listing.Warnings.Count > 0)
                ShowInfo(InfoBarSeverity.Warning, "Check this archive before extracting", string.Join("\n", listing.Warnings.Select(w => w.Message)));
        }
        catch (Exception ex)   // damaged or hostile archives can fail in many ways; all of them end up as a plain message
        {
            _listing = null;
            _rows.Clear();
            EmptyText.Visibility = Visibility.Visible;
            ExtractButton.IsEnabled = false;
            SummaryText.Text = "";
            StatusText.Text = "";
            ShowInfo(InfoBarSeverity.Error, "Couldn't open " + Path.GetFileName(path), ex.Message);
        }
        finally
        {
            BusyBar.Visibility = Visibility.Collapsed;
        }
    }

    private async void OpenButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        picker.FileTypeFilter.Add("*");
        var file = await picker.PickSingleFileAsync();
        if (file is not null) await LoadAsync(file.Path);
    }

    private async void ExtractButton_Click(object sender, RoutedEventArgs e)
    {
        if (_listing is null) return;
        var listing = _listing;

        var picker = new FolderPicker();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        picker.FileTypeFilter.Add("*");
        var folder = await picker.PickSingleFolderAsync();
        if (folder is null) return;

        if (listing.Entries.Any(en => en.IsEncrypted) && _password is null)
        {
            _password = await AskPasswordAsync();
            if (_password is null) return;
        }

        ShowInfo(InfoBarSeverity.Informational, "", null);
        ExtractButton.IsEnabled = false;
        OpenButton.IsEnabled = false;
        BusyBar.Visibility = Visibility.Visible;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        var options = new ExtractOptions { Password = _password };
        var progress = new Progress<ExtractProgress>(p =>
            StatusText.Text = $"{p.FilesExtracted:N0} files, {ArchiveService.FormatBytes(p.BytesWritten)}: {p.CurrentEntry}");

        try
        {
            var result = await Task.Run(() => ArchiveService.Extract(listing.ArchivePath, folder.Path, options, progress, token), token);
            ShowResult(result);
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Extraction cancelled.";
        }
        catch (Exception ex)   // damaged or hostile archives can fail in many ways; all of them end up as a plain message
        {
            ShowInfo(InfoBarSeverity.Error, "Extraction failed", ex.Message);
        }
        finally
        {
            BusyBar.Visibility = Visibility.Collapsed;
            ExtractButton.IsEnabled = true;
            OpenButton.IsEnabled = true;
        }
    }

    private void ShowResult(ExtractResult result)
    {
        StatusText.Text = result.Destination;

        if (!result.Completed)
        {
            ShowInfo(InfoBarSeverity.Error, "Extraction stopped for safety",
                result.AbortReason + $"\nFiles already written ({result.FilesExtracted:N0}) are in {result.Destination}.");
            return;
        }

        var message = $"{result.FilesExtracted:N0} files ({ArchiveService.FormatBytes(result.BytesWritten)}) extracted to {result.Destination}";
        if (result.Skipped.Count > 0)
        {
            var examples = string.Join("\n", result.Skipped.Take(5).Select(s => $"• {s.Path}: {s.Reason}"));
            var more = result.Skipped.Count > 5 ? $"\n…and {result.Skipped.Count - 5:N0} more." : "";
            ShowInfo(InfoBarSeverity.Warning, $"{result.Skipped.Count:N0} entries were skipped", message + "\n" + examples + more);
        }
        else
        {
            ShowInfo(InfoBarSeverity.Success, "Done", message);
        }

        var open = new Button { Content = "Open folder" };
        open.Click += (_, _) =>
        {
            var psi = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
            psi.ArgumentList.Add(result.Destination);
            Process.Start(psi);
        };
        Bar.ActionButton = open;
    }

    private async Task<string?> AskPasswordAsync()
    {
        var box = new PasswordBox { PlaceholderText = "Password" };
        var dialog = new ContentDialog
        {
            Title = "This archive has password-protected files",
            Content = box,
            PrimaryButtonText = "Extract",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Root.XamlRoot,
        };
        var answer = await dialog.ShowAsync();
        return answer == ContentDialogResult.Primary ? box.Password : null;
    }

    private void ShowInfo(InfoBarSeverity severity, string title, string? message)
    {
        Bar.ActionButton = null;
        if (string.IsNullOrEmpty(title))
        {
            Bar.IsOpen = false;
            return;
        }
        Bar.Severity = severity;
        Bar.Title = title;
        Bar.Message = message ?? "";
        Bar.IsOpen = true;
    }

    private void Root_DragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = "Open archive";
    }

    private async void Root_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var items = await e.DataView.GetStorageItemsAsync();
        var file = items.OfType<StorageFile>().FirstOrDefault();
        if (file is not null) await LoadAsync(file.Path);
    }
}
