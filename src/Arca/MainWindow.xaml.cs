using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Arca.Core;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.System;
using WinRT.Interop;

namespace Arca;

public sealed partial class MainWindow : Window
{
    /// <summary>Files "View" refuses to open from inside an archive: they would run code rather than show content.</summary>
    private static readonly HashSet<string> Runnable = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".com", ".bat", ".cmd", ".ps1", ".psm1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh", ".msi", ".msp",
        ".scr", ".pif", ".lnk", ".hta", ".cpl", ".jar", ".reg", ".url", ".appref-ms", ".application", ".msix", ".appx",
        ".appxbundle", ".msixbundle", ".dll", ".sys", ".inf", ".chm", ".iso", ".vhd", ".vhdx",
    };

    private readonly ObservableCollection<EntryRow> _rows = new();
    private readonly ArcaSettings _settings = SettingsStore.Load();
    private ArchiveListing? _listing;
    private ArchiveNode? _root;
    private ArchiveNode? _current;
    private string? _password;
    private CancellationTokenSource? _cts;
    private bool _busy;
    private string _sortBy = "Name";
    private bool _sortDescending;

    public MainWindow()
    {
        InitializeComponent();
        Title = "Arca";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        try { AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "Arca.ico")); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { }
        ResizeForDpi(1180, 760);

        EntryList.ItemsSource = _rows;
        Root.ActualThemeChanged += (_, _) => UpdateThemeChrome();
        ApplyTheme(_settings.Theme);
        SmartFolderItem.IsChecked = _settings.SmartFolder;
        KeepMarkItem.IsChecked = _settings.KeepInternetMark;
        UpdateHeaders();
        ShowArchive(null);

        Closed += (_, _) => _cts?.Cancel();   // closing the window (or Integra's --quit) stops a running job
        _ = Task.Run(CleanOldViewFiles);
    }

    // ---------- Opening and showing an archive ----------

    /// <summary>Reads the archive's contents and shows them. Nothing is extracted.</summary>
    public async Task LoadAsync(string path)
    {
        if (_busy) return;
        HideMessage();
        SetBusy("Reading " + Path.GetFileName(path) + "…");
        try
        {
            var listing = await Task.Run(() => ArchiveService.List(path));
            ShowArchive(listing);
            if (listing.Warnings.Count > 0)
                ShowMessage(InfoBarSeverity.Warning, "Check this archive before extracting", string.Join("\n", listing.Warnings.Select(w => w.Message)));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Damaged or hostile archives can fail in many ways; all of them end up as a plain message.
            ShowMessage(InfoBarSeverity.Error, "Couldn't open " + Path.GetFileName(path), ex.Message);
        }
        finally
        {
            SetBusy(null);
        }
    }

    private void ShowArchive(ArchiveListing? listing)
    {
        _listing = listing;
        _password = null;
        _root = listing is null ? null : ArchiveTree.Build(listing.Entries);
        _current = _root;
        FindBox.Text = "";

        EmptyPanel.Visibility = listing is null ? Visibility.Visible : Visibility.Collapsed;
        TitleText.Text = listing is null ? "Arca" : Path.GetFileName(listing.ArchivePath) + " - Arca";
        Title = TitleText.Text;
        PathIcon.Source = listing is null ? null : ShellInfo.Icon(listing.ArchivePath, folder: false);
        UpdateCommands();
        Refresh();
    }

    /// <summary>Fills the list with the current folder (or the search results), sorted.</summary>
    private void Refresh(ArchiveNode? select = null)
    {
        _rows.Clear();
        if (_listing is null || _root is null || _current is null)
        {
            PathText.Text = "No archive open";
            SetEnabled(UpButton, false);
            UpdateStatus();
            return;
        }

        var query = FindBox.Text.Trim();
        bool searching = query.Length > 0;
        var nodes = searching
            ? _root.Descendants().Where(n => n.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase))
            : _current.Children;

        if (!searching && !_current.IsRoot) _rows.Add(EntryRow.Up());
        foreach (var node in Sort(nodes)) _rows.Add(EntryRow.From(node, showFullPath: searching));

        var archiveName = Path.GetFileName(_listing.ArchivePath);
        PathText.Text = searching ? $"{archiveName}   ·   files matching \"{query}\""
                      : _current.IsRoot ? archiveName
                      : archiveName + "\\" + _current.FullPath.Replace('/', '\\');
        SetEnabled(UpButton, !searching && !_current.IsRoot);

        if (select is not null)
        {
            var row = _rows.FirstOrDefault(r => r.Node == select);
            if (row is not null)
            {
                EntryList.SelectedItem = row;
                EntryList.ScrollIntoView(row);
            }
        }
        UpdateStatus();
    }

    private IEnumerable<ArchiveNode> Sort(IEnumerable<ArchiveNode> nodes)
    {
        var folderFirst = nodes.OrderByDescending(n => n.IsFolder);
        var text = StringComparer.CurrentCultureIgnoreCase;
        var sorted = _sortBy switch
        {
            "Size" => By(folderFirst, n => n.Size),
            "Packed" => By(folderFirst, n => n.PackedSize),
            "Type" => By(folderFirst, n => ShellInfo.TypeName(n.Name, n.IsFolder), text),
            "Modified" => By(folderFirst, n => n.Modified ?? DateTime.MinValue),
            "Crc" => By(folderFirst, n => (uint)n.Crc),
            _ => By(folderFirst, n => n.Name, text),
        };
        return sorted.ThenBy(n => n.Name, text);
    }

    private IOrderedEnumerable<ArchiveNode> By<T>(IOrderedEnumerable<ArchiveNode> source, Func<ArchiveNode, T> key, IComparer<T>? comparer = null) =>
        _sortDescending ? source.ThenByDescending(key, comparer) : source.ThenBy(key, comparer);

    private void UpdateHeaders()
    {
        string Label(string tag, string text) => _sortBy == tag ? text + (_sortDescending ? "  ▼" : "  ▲") : text;
        NameHeader.Content = Label("Name", "Name");
        SizeHeader.Content = Label("Size", "Size");
        PackedHeader.Content = Label("Packed", "Packed");
        TypeHeader.Content = Label("Type", "Type");
        ModifiedHeader.Content = Label("Modified", "Modified");
        CrcHeader.Content = Label("Crc", "CRC32");
    }

    private void UpdateStatus()
    {
        if (_listing is null || _current is null)
        {
            SelectionText.Text = "";
            TotalText.Text = "";
            return;
        }

        var selected = SelectedNodes();
        if (selected.Count > 0)
        {
            long bytes = selected.Sum(n => n.Size);
            int files = selected.Sum(n => n.FileCount);
            SelectionText.Text = $"Selected {bytes:N0} bytes in {files:N0} {Plural(files, "file")}";
        }
        else
        {
            SelectionText.Text = $"{ArchiveFormats.DisplayName(_listing.Format)} archive{(_listing.IsSolid ? ", solid" : "")}, {ArchiveService.FormatBytes(_listing.ArchiveSize)}";
        }

        var shown = _rows.Where(r => r.Node is not null).Select(r => r.Node!).ToList();
        int folders = shown.Count(n => n.IsFolder);
        var shownFiles = shown.Where(n => !n.IsFolder).ToList();
        TotalText.Text = $"Total {folders:N0} {Plural(folders, "folder")} and {shownFiles.Sum(n => n.Size):N0} bytes in {shownFiles.Count:N0} {Plural(shownFiles.Count, "file")}";
    }

    private static string Plural(int n, string word) => n == 1 ? word : word + "s";

    private List<ArchiveNode> SelectedNodes() =>
        EntryList.SelectedItems.OfType<EntryRow>().Where(r => r.Node is not null).Select(r => r.Node!).ToList();

    // ---------- Navigation ----------

    private void OpenRow(EntryRow row)
    {
        if (row.IsParent) { GoUp(); return; }
        if (row.Node is not { } node) return;

        if (node.IsFolder && node.UnsafeReason is null)
        {
            _current = node;
            if (FindBox.Text.Length > 0) FindBox.Text = "";   // leaving the search results; this refreshes too
            Refresh();
            return;
        }
        _ = ViewAsync(node);
    }

    private void GoUp()
    {
        if (_current?.Parent is not { } parent) return;
        var from = _current;
        _current = parent;
        Refresh(select: from);
    }

    private void Up_Click(object sender, RoutedEventArgs e) => GoUp();

    private void EntryList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        var row = (e.OriginalSource as FrameworkElement)?.DataContext as EntryRow ?? EntryList.SelectedItem as EntryRow;
        if (row is not null) OpenRow(row);
    }

    private void EntryList_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter && EntryList.SelectedItems.Count == 1 && EntryList.SelectedItem is EntryRow row)
        {
            OpenRow(row);
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Back)
        {
            GoUp();
            e.Handled = true;
        }
    }

    private void EntryList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateStatus();

    private void Header_Click(object sender, RoutedEventArgs e)
    {
        var tag = (sender as FrameworkElement)?.Tag as string ?? "Name";
        if (_sortBy == tag) _sortDescending = !_sortDescending;
        else
        {
            _sortBy = tag;
            _sortDescending = false;
        }
        UpdateHeaders();
        Refresh();
    }

    // ---------- Find ----------

    private void Find_Click(object sender, RoutedEventArgs e)
    {
        if (_listing is null) return;
        FindBox.Focus(FocusState.Programmatic);
        FindBox.SelectAll();
    }

    private void FindBox_TextChanged(object sender, TextChangedEventArgs e) => Refresh();

    private void FindBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            FindBox.Text = "";
            EntryList.Focus(FocusState.Programmatic);
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Enter || e.Key == VirtualKey.Down)
        {
            if (_rows.Count > 0 && EntryList.SelectedIndex < 0) EntryList.SelectedIndex = 0;
            EntryList.Focus(FocusState.Programmatic);
            e.Handled = true;
        }
    }

    // ---------- Commands ----------

    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var picker = new FileOpenPicker();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        picker.FileTypeFilter.Add("*");
        var file = await picker.PickSingleFileAsync();
        if (file is not null) await LoadAsync(file.Path);
    }

    private void CloseArchive_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        HideMessage();
        ShowArchive(null);
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    private async void Extract_Click(object sender, RoutedEventArgs e)
    {
        if (_listing is null || _root is null || _current is null || _busy) return;
        var listing = _listing;
        var selection = SelectedNodes();
        bool searching = FindBox.Text.Trim().Length > 0;

        var folder = await PickFolderAsync();
        if (folder is null) return;

        var scope = selection.Count > 0
            ? selection.SelectMany(n => n.IsFolder ? n.Descendants().Prepend(n) : new[] { n })
            : _root.Descendants();
        if (scope.Any(n => n.IsEncrypted) && !await EnsurePasswordAsync()) return;

        var options = new ExtractOptions
        {
            Password = _password,
            SmartFolder = _settings.SmartFolder,
            PropagateMarkOfTheWeb = _settings.KeepInternetMark,
            Only = selection.Count > 0 ? selection.Select(n => n.FullPath).ToList() : null,
            // A selection made inside a folder lands without that folder's path, as in File Explorer.
            StripPrefix = selection.Count > 0 && !searching ? _current.FullPath : null,
        };

        HideMessage();
        SetBusy("Extracting…");
        var token = NewToken();
        var progress = new Progress<ExtractProgress>(p =>
            BusyText.Text = $"Extracting: {p.FilesExtracted:N0} files, {ArchiveService.FormatBytes(p.BytesWritten)}   {p.CurrentEntry}");
        try
        {
            var result = await Task.Run(() => ArchiveService.Extract(listing.ArchivePath, folder, options, progress, token), token);
            if (result.Skipped.Count > 0) _password = null; // a wrong password shows up as skipped files: ask again next time
            ShowExtractResult(result);
        }
        catch (OperationCanceledException)
        {
            ShowMessage(InfoBarSeverity.Informational, "Extraction cancelled", "Files already written were left where they are.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ShowMessage(InfoBarSeverity.Error, "Extraction failed", ex.Message);
        }
        finally
        {
            SetBusy(null);
        }
    }

    private void ShowExtractResult(ExtractResult result)
    {
        if (!result.Completed)
        {
            ShowMessage(InfoBarSeverity.Error, "Extraction stopped for safety",
                result.AbortReason + $"\nFiles already written ({result.FilesExtracted:N0}) are in {result.Destination}.", OpenFolderButton(result.Destination));
            return;
        }

        var message = $"{result.FilesExtracted:N0} {Plural(result.FilesExtracted, "file")} ({ArchiveService.FormatBytes(result.BytesWritten)}) extracted to {result.Destination}";
        if (result.Skipped.Count > 0)
        {
            var examples = string.Join("\n", result.Skipped.Take(5).Select(s => $"• {s.Path}: {s.Reason}"));
            var more = result.Skipped.Count > 5 ? $"\n…and {result.Skipped.Count - 5:N0} more." : "";
            ShowMessage(InfoBarSeverity.Warning, $"{result.Skipped.Count:N0} {(result.Skipped.Count == 1 ? "entry" : "entries")} skipped",
                message + "\n" + examples + more, OpenFolderButton(result.Destination));
        }
        else
        {
            ShowMessage(InfoBarSeverity.Success, "Done", message, OpenFolderButton(result.Destination));
        }
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

    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        if (_listing is null || _root is null || _busy) return;
        var listing = _listing;
        if (_root.Descendants().Any(n => n.IsEncrypted)) await EnsurePasswordAsync(); // without one, locked files are skipped

        HideMessage();
        SetBusy("Testing…");
        var token = NewToken();
        var progress = new Progress<ExtractProgress>(p =>
            BusyText.Text = $"Testing: {p.FilesExtracted:N0} files, {ArchiveService.FormatBytes(p.BytesWritten)}   {p.CurrentEntry}");
        try
        {
            var r = await Task.Run(() => ArchiveService.Test(listing.ArchivePath, _password, progress, token), token);
            var skipped = r.Skipped.Count > 0 ? $"\n{r.Skipped.Count:N0} password-protected {Plural(r.Skipped.Count, "file")} not checked." : "";
            if (r.AbortReason is not null)
                ShowMessage(InfoBarSeverity.Error, "Test stopped for safety", r.AbortReason);
            else if (r.Failed.Count == 0)
                ShowMessage(InfoBarSeverity.Success, "No errors found", $"{r.FilesOk:N0} {Plural(r.FilesOk, "file")} checked, {ArchiveService.FormatBytes(r.BytesRead)} read." + skipped);
            else
            {
                _password = null; // a wrong password also shows up as damaged files: ask again next time
                var list = string.Join("\n", r.Failed.Take(8).Select(f => $"• {f.Path}: {f.Reason}"));
                var more = r.Failed.Count > 8 ? $"\n…and {r.Failed.Count - 8:N0} more." : "";
                ShowMessage(InfoBarSeverity.Error, $"{r.Failed.Count:N0} damaged {Plural(r.Failed.Count, "file")} ({r.FilesOk:N0} fine)", list + more + skipped);
            }
        }
        catch (OperationCanceledException)
        {
            ShowMessage(InfoBarSeverity.Informational, "Test cancelled", "");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ShowMessage(InfoBarSeverity.Error, "The test couldn't finish", ex.Message);
        }
        finally
        {
            SetBusy(null);
        }
    }

    private void View_Click(object sender, RoutedEventArgs e)
    {
        var selected = SelectedNodes();
        var file = selected.FirstOrDefault(n => !n.IsFolder);
        if (file is not null) { _ = ViewAsync(file); return; }
        if (selected.FirstOrDefault() is { IsFolder: true } folder && folder.UnsafeReason is null)
        {
            _current = folder;
            Refresh();
            return;
        }
        ShowMessage(InfoBarSeverity.Informational, "Select a file to view", "Pick a file in the list, then press View (or double-click it).");
    }

    /// <summary>Extracts one file to a private temporary folder and opens it in its usual app. Programs and scripts are refused.</summary>
    private async Task ViewAsync(ArchiveNode node)
    {
        if (_listing is null || _busy) return;
        if (node.UnsafeReason is not null)
        {
            ShowMessage(InfoBarSeverity.Warning, "Arca won't open this file", "Its name is unsafe: " + node.UnsafeReason + ".");
            return;
        }
        if (Runnable.Contains(Path.GetExtension(node.Name)))
        {
            ShowMessage(InfoBarSeverity.Warning, "Arca doesn't run programs from inside an archive",
                $"{node.Name} is a program, script or shortcut. Extract it first, and only run it if you trust where it came from.");
            return;
        }
        if (node.IsEncrypted && !await EnsurePasswordAsync()) return;

        var listing = _listing;
        var temp = Path.Combine(ViewRoot, Guid.NewGuid().ToString("N")[..12]);
        var options = new ExtractOptions
        {
            Only = new[] { node.FullPath },
            StripPrefix = node.Parent?.FullPath,
            SmartFolder = false,
            Password = _password,
            PropagateMarkOfTheWeb = true,   // always, so the opening app treats it as downloaded
            Overwrite = OverwriteMode.Overwrite,
        };

        HideMessage();
        SetBusy("Opening " + node.Name + "…");
        var token = NewToken();
        try
        {
            var r = await Task.Run(() => ArchiveService.Extract(listing.ArchivePath, temp, options, null, token), token);
            var path = Path.Combine(r.Destination, node.Name);
            if (r.FilesExtracted == 1 && File.Exists(path))
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            else
            {
                _password = null;
                ShowMessage(InfoBarSeverity.Error, "Couldn't open " + node.Name, r.AbortReason ?? r.Skipped.FirstOrDefault()?.Reason ?? "The file couldn't be read.");
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ShowMessage(InfoBarSeverity.Error, "Couldn't open " + node.Name, ex.Message);
        }
        finally
        {
            SetBusy(null);
        }
    }

    private static string ViewRoot => Path.Combine(Path.GetTempPath(), "Arca", "View");

    /// <summary>Files opened with View are kept a day (the app may still have them open), then removed.</summary>
    private static void CleanOldViewFiles()
    {
        try
        {
            if (!Directory.Exists(ViewRoot)) return;
            foreach (var dir in Directory.GetDirectories(ViewRoot))
            {
                try
                {
                    if (Directory.GetCreationTimeUtc(dir) < DateTime.UtcNow.AddDays(-1)) Directory.Delete(dir, recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        if (_listing is null || _busy) return;
        var scanner = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Windows Defender", "MpCmdRun.exe");
        if (!File.Exists(scanner))
        {
            ShowMessage(InfoBarSeverity.Warning, "Microsoft Defender isn't available",
                "Its command-line scanner wasn't found on this PC, so Arca can't ask it to scan. Another antivirus may still check files when you open them.");
            return;
        }

        var path = _listing.ArchivePath;
        var name = Path.GetFileName(path);
        HideMessage();
        SetBusy("Microsoft Defender is scanning " + name + "…");
        var token = NewToken();
        try
        {
            int code = await Task.Run(async () =>
            {
                var psi = new ProcessStartInfo(scanner)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                foreach (var arg in new[] { "-Scan", "-ScanType", "3", "-File", path }) psi.ArgumentList.Add(arg);
                using var process = Process.Start(psi) ?? throw new InvalidOperationException("The scanner didn't start.");
                var output = process.StandardOutput.ReadToEndAsync(token);
                var errors = process.StandardError.ReadToEndAsync(token);
                try
                {
                    await process.WaitForExitAsync(token);
                }
                catch (OperationCanceledException)
                {
                    try { process.Kill(); } catch (InvalidOperationException) { }
                    throw;
                }
                await Task.WhenAll(output, errors);
                return process.ExitCode;
            }, token);

            if (code == 0)
                ShowMessage(InfoBarSeverity.Success, "Microsoft Defender found no threats",
                    $"It checked {name} and the files inside it. Defender only recognises known malware, so Arca's own safety checks still apply.");
            else if (code == 2)
                ShowMessage(InfoBarSeverity.Error, "Microsoft Defender found a threat",
                    $"Don't extract {name}. Defender will handle it as set in Windows Security (usually it moves the file to quarantine).");
            else
                ShowMessage(InfoBarSeverity.Warning, "The scan didn't finish", $"Defender's scanner stopped with code {code}.");
        }
        catch (OperationCanceledException)
        {
            ShowMessage(InfoBarSeverity.Informational, "Scan cancelled", "");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ShowMessage(InfoBarSeverity.Error, "The scan couldn't run", ex.Message);
        }
        finally
        {
            SetBusy(null);
        }
    }

    private async void Info_Click(object sender, RoutedEventArgs e)
    {
        if (_listing is null || _root is null) return;
        var l = _listing;
        var all = _root.Descendants().ToList();
        int folders = all.Count(n => n.IsFolder);
        long unpacked = l.TotalSize, packed = l.TotalPacked;

        var grid = new Grid { ColumnSpacing = 20, RowSpacing = 6 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        void Row(string label, string value)
        {
            int r = grid.RowDefinitions.Count;
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var a = new TextBlock { Text = label, Opacity = 0.7 };
            var b = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
            Grid.SetRow(a, r);
            Grid.SetRow(b, r);
            Grid.SetColumn(b, 1);
            grid.Children.Add(a);
            grid.Children.Add(b);
        }

        Row("Archive", Path.GetFileName(l.ArchivePath));
        Row("Location", Path.GetDirectoryName(l.ArchivePath) ?? "");
        Row("Format", ArchiveFormats.DisplayName(l.Format) + (l.IsSolid ? ", solid" : ""));
        Row("Archive size", $"{ArchiveService.FormatBytes(l.ArchiveSize)} ({l.ArchiveSize:N0} bytes)");
        Row("Contents", $"{l.FileCount:N0} {Plural(l.FileCount, "file")} in {folders:N0} {Plural(folders, "folder")}");
        Row("Unpacked size", $"{ArchiveService.FormatBytes(unpacked)} ({unpacked:N0} bytes)");
        Row("Packed size", $"{ArchiveService.FormatBytes(packed)}" + (unpacked > 0 ? $" ({100.0 * packed / unpacked:0.#}% of the original)" : ""));
        Row("Password-protected", $"{all.Count(n => n.IsEncrypted):N0} {Plural(all.Count(n => n.IsEncrypted), "file")}");
        Row("From the internet", MarkOfTheWeb.Read(l.ArchivePath) is not null ? "Yes: extracted files keep the mark" : "No mark on this file");

        var panel = new StackPanel { Spacing = 16, MinWidth = 440 };
        panel.Children.Add(grid);
        var safety = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            Text = l.Warnings.Count == 0
                ? "Safety checks: no unsafe names and no zip-bomb pattern."
                : "Safety checks:\n" + string.Join("\n", l.Warnings.Select(w => "• " + w.Message)),
        };
        panel.Children.Add(safety);

        var dialog = new ContentDialog
        {
            Title = "Archive info",
            Content = new ScrollViewer { Content = panel },
            CloseButtonText = "Close",
            XamlRoot = Root.XamlRoot,
            RequestedTheme = Root.ActualTheme,
        };
        await dialog.ShowAsync();
    }

    private async void About_Click(object sender, RoutedEventArgs e)
    {
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new TextBlock { Text = "Version " + Cli.Version });
        panel.Children.Add(new TextBlock
        {
            Text = "A free archive manager for Windows 11, part of the suite Integra installs and updates. No account, no telemetry.",
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(new HyperlinkButton { Content = "Project page on GitHub", NavigateUri = new Uri("https://github.com/stephenstirling/arca"), Padding = new Thickness(0) });
        panel.Children.Add(new HyperlinkButton { Content = "Buy me a coffee", NavigateUri = new Uri("https://buymeacoffee.com/stephenstirling"), Padding = new Thickness(0) });

        var dialog = new ContentDialog
        {
            Title = "About Arca",
            Content = panel,
            CloseButtonText = "Close",
            XamlRoot = Root.XamlRoot,
            RequestedTheme = Root.ActualTheme,
        };
        await dialog.ShowAsync();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _cts?.Cancel();

    // ---------- Theme and options ----------

    private void ApplyTheme(string theme)
    {
        _settings.Theme = theme is "Light" or "Dark" ? theme : "System";
        Root.RequestedTheme = _settings.Theme switch
        {
            "Light" => ElementTheme.Light,
            "Dark" => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
        ThemeSystemItem.IsChecked = _settings.Theme == "System";
        ThemeLightItem.IsChecked = _settings.Theme == "Light";
        ThemeDarkItem.IsChecked = _settings.Theme == "Dark";
        UpdateThemeChrome();
    }

    /// <summary>Matches the theme button and the window's own caption buttons (minimise, maximise, close) to the theme in effect.</summary>
    private void UpdateThemeChrome()
    {
        bool dark = Root.ActualTheme == ElementTheme.Dark;
        ThemeIcon.Glyph = dark ? "" : "";   // the button shows what it switches to: sun or moon
        ThemeLabel.Text = dark ? "Light" : "Dark";
        var hint = dark ? "Switch to light mode" : "Switch to dark mode";
        ToolTipService.SetToolTip(ThemeButton, hint);
        AutomationProperties.SetName(ThemeButton, hint);

        var bar = AppWindow.TitleBar;
        var foreground = dark ? Colors.White : Colors.Black;
        bar.ButtonBackgroundColor = Colors.Transparent;
        bar.ButtonInactiveBackgroundColor = Colors.Transparent;
        bar.ButtonForegroundColor = foreground;
        bar.ButtonInactiveForegroundColor = dark ? Windows.UI.Color.FromArgb(255, 140, 140, 140) : Windows.UI.Color.FromArgb(255, 120, 120, 120);
        bar.ButtonHoverForegroundColor = foreground;
        bar.ButtonHoverBackgroundColor = dark ? Windows.UI.Color.FromArgb(30, 255, 255, 255) : Windows.UI.Color.FromArgb(24, 0, 0, 0);
        bar.ButtonPressedForegroundColor = foreground;
        bar.ButtonPressedBackgroundColor = dark ? Windows.UI.Color.FromArgb(20, 255, 255, 255) : Windows.UI.Color.FromArgb(16, 0, 0, 0);
    }

    private void ThemeButton_Click(object sender, RoutedEventArgs e)
    {
        ApplyTheme(Root.ActualTheme == ElementTheme.Dark ? "Light" : "Dark");
        SettingsStore.Save(_settings);
    }

    private void ThemeItem_Click(object sender, RoutedEventArgs e)
    {
        ApplyTheme((sender as FrameworkElement)?.Tag as string ?? "System");
        SettingsStore.Save(_settings);
    }

    private void Option_Click(object sender, RoutedEventArgs e)
    {
        _settings.SmartFolder = SmartFolderItem.IsChecked;
        _settings.KeepInternetMark = KeepMarkItem.IsChecked;
        SettingsStore.Save(_settings);
    }

    // ---------- Helpers ----------

    private async Task<string?> PickFolderAsync()
    {
        var picker = new FolderPicker();
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        picker.FileTypeFilter.Add("*");
        var folder = await picker.PickSingleFolderAsync();
        return folder?.Path;
    }

    /// <summary>Asks for the archive's password once; true when there is one to use.</summary>
    private async Task<bool> EnsurePasswordAsync()
    {
        if (_password is not null) return true;
        var box = new PasswordBox { PlaceholderText = "Password" };
        var dialog = new ContentDialog
        {
            Title = "This archive has password-protected files",
            Content = box,
            PrimaryButtonText = "OK",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Root.XamlRoot,
            RequestedTheme = Root.ActualTheme,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || box.Password.Length == 0) return false;
        _password = box.Password;
        return true;
    }

    private CancellationToken NewToken()
    {
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        return _cts.Token;
    }

    private void SetBusy(string? text)
    {
        _busy = text is not null;
        BusyPanel.Visibility = _busy ? Visibility.Visible : Visibility.Collapsed;
        BusyText.Text = text ?? "";
        UpdateCommands();
    }

    private void UpdateCommands()
    {
        bool ready = _listing is not null && !_busy;
        foreach (var control in new Control[] { ExtractButton, TestButton, ViewButton, FindButton, InfoButton, ScanButton })
            SetEnabled(control, ready);
        SetEnabled(FindBox, _listing is not null);
    }

    /// <summary>Disabled toolbar buttons also fade, since their icons keep the accent colour.</summary>
    private static void SetEnabled(Control control, bool enabled)
    {
        control.IsEnabled = enabled;
        control.Opacity = enabled ? 1 : 0.45;
    }

    private void ShowMessage(InfoBarSeverity severity, string title, string message, ButtonBase? action = null)
    {
        Bar.Severity = severity;
        Bar.Title = title;
        Bar.Message = message;
        Bar.ActionButton = action;
        Bar.IsOpen = true;
    }

    private void HideMessage()
    {
        Bar.IsOpen = false;
        Bar.ActionButton = null;
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    private void ResizeForDpi(int width, int height)
    {
        uint dpi = GetDpiForWindow(WindowNative.GetWindowHandle(this));
        double scale = dpi == 0 ? 1 : dpi / 96.0;
        AppWindow.Resize(new SizeInt32((int)(width * scale), (int)(height * scale)));
    }

    // ---------- Drag and drop ----------

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
