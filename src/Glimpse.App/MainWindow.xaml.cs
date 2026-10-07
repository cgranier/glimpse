using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Glimpse.Core;
using Glimpse.Core.Index;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Streams;
using Windows.System;
using WinRT.Interop;

namespace Glimpse.App;

public sealed partial class MainWindow : Window
{
    readonly GlimpseConfig _config = GlimpseConfig.Load();

    // Separate connections: searches run on a worker thread, indexing writes on another. WAL lets them coexist.
    readonly ImageIndex _searchIndex = new();
    readonly ImageIndex _writeIndex = new();
    readonly Indexer _indexer;
    readonly FolderWatcher _watcher;
    readonly GlobalHotkey _hotkey;

    readonly ObservableCollection<ResultItem> _items = [];
    readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _searchDebounce;
    int _searchGeneration;
    string _lastQuery = "";
    string _indexStatus = "";

    public MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        var icon = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "glimpse.ico");
        if (File.Exists(icon)) AppWindow.SetIcon(icon);
        var iconPng = System.IO.Path.ChangeExtension(icon, ".png");
        if (File.Exists(iconPng)) TitleIcon.Source = new BitmapImage(new Uri(iconPng));
        CenterOnScreen(1180, 760);

        Results.ItemsSource = _items;

        _searchDebounce = DispatcherQueue.CreateTimer();
        _searchDebounce.Interval = TimeSpan.FromMilliseconds(90);
        _searchDebounce.IsRepeating = false;
        _searchDebounce.Tick += (_, _) => _ = RunSearchAsync(SearchBox.Text);

        _indexer = new Indexer(_config, _writeIndex);
        _watcher = new FolderWatcher(_config, _indexer);
        _watcher.Indexed += n => DispatcherQueue.TryEnqueue(() =>
        {
            SetIndexStatus($"indexed {n} new image{(n == 1 ? "" : "s")}");
            _ = RunSearchAsync(SearchBox.Text);
        });

        var hwnd = WindowNative.GetWindowHandle(this);
        _hotkey = new GlobalHotkey(hwnd, _config.Hotkey);
        _hotkey.Pressed += () => DispatcherQueue.TryEnqueue(Summon);

        AppWindow.Closing += (_, _) => Shutdown();
        Activated += (_, e) =>
        {
            if (e.WindowActivationState != WindowActivationState.Deactivated) SearchBox.Focus(FocusState.Programmatic);
        };

        _ = RunSearchAsync("");
        _ = CatchUpIndexAsync();
    }

    // ---- window lifecycle -----------------------------------------------------------------

    public void StartHidden() => AppWindow.Hide();

    void Summon()
    {
        if (AppWindow.IsVisible && IsForeground())
        {
            AppWindow.Hide();
            return;
        }
        AppWindow.Show();
        Activate();
        SetForegroundWindow(WindowNative.GetWindowHandle(this));
        SearchBox.Focus(FocusState.Programmatic);
        SearchBox.SelectAll();
    }

    void CenterOnScreen(int width, int height)
    {
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var scale = GetDpiForWindow(WindowNative.GetWindowHandle(this)) / 96.0;
        int w = (int)(width * scale), h = (int)(height * scale);
        AppWindow.MoveAndResize(new(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2, w, h));
    }

    void Shutdown()
    {
        _hotkey.Dispose();
        _watcher.Dispose();
        _searchIndex.Dispose();
        _writeIndex.Dispose();
    }

    // ---- indexing -------------------------------------------------------------------------

    async Task CatchUpIndexAsync()
    {
        var progress = new Progress<IndexProgress>(p =>
            SetIndexStatus(p.Total == 0 ? p.Phase + "…" : $"indexing {p.Done}/{p.Total}"));
        try
        {
            var report = await Task.Run(() => _indexer.RunAsync(progress: progress));
            SetIndexStatus(report.Indexed > 0 ? $"indexed {report.Indexed} new" : "up to date");
            if (report.Indexed > 0 || report.Removed > 0) _ = RunSearchAsync(SearchBox.Text);
        }
        catch (Exception ex)
        {
            SetIndexStatus("indexing failed: " + ex.Message);
        }
    }

    void SetIndexStatus(string status)
    {
        _indexStatus = status;
        var stats = _searchIndex.GetStats(); // a few COUNT(*)s — cheap enough for the UI thread
        StatusText.Text = $"{stats.Images:N0} images · {stats.WithText:N0} with text · {_indexStatus}" +
                          (_hotkey.IsRegistered ? $" · {_config.Hotkey} to summon" : $" · hotkey {_config.Hotkey} unavailable");
    }

    // ---- search ---------------------------------------------------------------------------

    void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        _searchDebounce.Stop();
        _searchDebounce.Start();
    }

    async Task RunSearchAsync(string query)
    {
        var generation = ++_searchGeneration;
        var sw = Stopwatch.StartNew();
        List<SearchHit> hits;
        try
        {
            hits = await Task.Run(() => _searchIndex.Search(query, limit: 120));
        }
        catch (Exception ex)
        {
            CountText.Text = ex.Message;
            return;
        }
        if (generation != _searchGeneration) return; // a newer keystroke won

        _lastQuery = query;
        _items.Clear();
        foreach (var hit in hits) _items.Add(new ResultItem(hit));
        CountText.Text = $"{hits.Count}{(hits.Count == 120 ? "+" : "")} results · {sw.ElapsedMilliseconds} ms";
        if (_items.Count > 0) Results.SelectedIndex = 0;
        else ShowPreview(null);
        if (string.IsNullOrEmpty(_indexStatus)) SetIndexStatus("starting…");
    }

    void OnSearchKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Enter:
                OpenSelected();
                e.Handled = true;
                break;
            case VirtualKey.Down when _items.Count > 0:
                Results.Focus(FocusState.Keyboard);
                e.Handled = true;
                break;
        }
    }

    // ---- results --------------------------------------------------------------------------

    void OnContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue || args.Item is not ResultItem item || item.ThumbnailRequested) return;
        item.ThumbnailRequested = true;
        // XAML reads and decodes off-thread at thumbnail size. (Not the shell thumbnail cache: it hands back
        // a generic icon for files Explorer hasn't visited. Not SetSourceAsync: XAML may decode lazily, after
        // we'd have disposed the stream.)
        item.Thumbnail = new BitmapImage { DecodePixelWidth = 400, UriSource = new Uri(item.Hit.Path) };
    }

    void OnSelectionChanged(object sender, SelectionChangedEventArgs e) => ShowPreview(Results.SelectedItem as ResultItem);

    void OnResultClick(object sender, ItemClickEventArgs e) => Results.SelectedItem = e.ClickedItem;

    void OnResultDoubleTapped(object sender, DoubleTappedRoutedEventArgs e) => OpenSelected();

    void OnResultsKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            OpenSelected();
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Up && Results.SelectedIndex is >= 0 and < 3)
        {
            // Top row: arrow up returns to the search box.
            SearchBox.Focus(FocusState.Keyboard);
            e.Handled = true;
        }
    }

    void ShowPreview(ResultItem? item)
    {
        PreviewOverlay.Children.Clear();
        PreviewImage.Source = null;
        EmptyPreview.Visibility = item is null ? Visibility.Visible : Visibility.Collapsed;
        PreviewPath.Text = item?.Hit.Path ?? "";
        PreviewMeta.Text = item is null ? "" : $"{item.Hit.Width}×{item.Hit.Height} · {item.Hit.Modified:dddd, MMMM d yyyy, HH:mm} · {item.Hit.Source}";
        if (item is null) return;

        var hit = item.Hit;
        var bmp = new BitmapImage();
        if (hit.Width > 2000) bmp.DecodePixelWidth = 2000;
        bmp.ImageFailed += (_, e) => PreviewMeta.Text = "can't open: " + e.ErrorMessage;

        if (hit.Width > 0)
        {
            // Lay the surface out in source pixels so OCR boxes line up; the Viewbox scales both.
            PreviewSurface.Width = hit.Width;
            PreviewSurface.Height = hit.Height;
            DrawMatches(hit.Id);
        }
        else
        {
            // OCR failed so we never learned the size; take it from the decoded image.
            bmp.ImageOpened += (_, _) =>
            {
                PreviewSurface.Width = bmp.PixelWidth;
                PreviewSurface.Height = bmp.PixelHeight;
            };
        }
        bmp.UriSource = new Uri(hit.Path);
        PreviewImage.Source = bmp;
    }

    void DrawMatches(long id)
    {
        var terms = SearchQuery.Parse(_lastQuery).Terms
            .SelectMany(t => t.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(t => t.Length >= 2).ToList();
        if (terms.Count == 0) return;

        var lines = _searchIndex.GetOcrLines(id);

        var accent = (Windows.UI.Color)Application.Current.Resources["SystemAccentColor"];
        var stroke = Math.Max(2, PreviewSurface.Width / 400);
        foreach (var word in lines.SelectMany(l => l.Words))
        {
            if (!terms.Any(t => word.Text.Contains(t, StringComparison.OrdinalIgnoreCase)
                             || (word.Text.Length >= 3 && t.Contains(word.Text, StringComparison.OrdinalIgnoreCase))))
                continue;

            var pad = word.Height * 0.2;
            var box = new Rectangle
            {
                Width = word.Width + pad * 2,
                Height = word.Height + pad * 2,
                RadiusX = pad, RadiusY = pad,
                Stroke = new SolidColorBrush(accent),
                StrokeThickness = stroke,
                Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(0x40, accent.R, accent.G, accent.B)),
            };
            Canvas.SetLeft(box, word.X - pad);
            Canvas.SetTop(box, word.Y - pad);
            PreviewOverlay.Children.Add(box);
        }
    }

    // ---- actions --------------------------------------------------------------------------

    ResultItem? Selected => Results.SelectedItem as ResultItem ?? _items.FirstOrDefault();

    void OpenSelected()
    {
        if (Selected is { } item)
            Process.Start(new ProcessStartInfo(item.Hit.Path) { UseShellExecute = true });
    }

    void OnReveal(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        if (Selected is { } item) Process.Start("explorer.exe", $"/select,\"{item.Hit.Path}\"");
        args.Handled = true;
    }

    async void OnCopyImage(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        // Let Ctrl+C copy text when the search box has a selection.
        if (SearchBox.FocusState != FocusState.Unfocused && SearchBox.SelectionLength > 0) return;
        args.Handled = true;
        if (Selected is not { } item) return;

        var file = await StorageFile.GetFileFromPathAsync(item.Hit.Path);
        var data = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        data.SetBitmap(RandomAccessStreamReference.CreateFromFile(file)); // paste into chat, docs, Obsidian
        data.SetStorageItems([file]);                                     // paste into Explorer
        Clipboard.SetContent(data);
        Clipboard.Flush();
        CountText.Text = "image copied";
    }

    void OnCopyPath(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        if (Selected is not { } item) return;
        var data = new DataPackage();
        data.SetText(item.Hit.Path);
        Clipboard.SetContent(data);
        Clipboard.Flush();
        CountText.Text = "path copied";
    }

    void OnFocusSearch(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        SearchBox.Focus(FocusState.Keyboard);
        SearchBox.SelectAll();
        args.Handled = true;
    }

    void OnEscape(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        if (SearchBox.Text.Length > 0 && SearchBox.FocusState != FocusState.Unfocused) SearchBox.Text = "";
        else if (_hotkey.IsRegistered) AppWindow.Hide(); // stays resident; the hotkey brings it back
        else Close();
    }

    void OnQuit(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) => Close();

    bool IsForeground() => GetForegroundWindow() == WindowNative.GetWindowHandle(this);

    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(nint hwnd);
}
