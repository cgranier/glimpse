using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Glimpse.Core;
using Glimpse.Core.Index;
using Glimpse.Core.Visual;
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
    readonly GlimpseRuntime _runtime = new(GlimpseConfig.Load());
    // After _runtime: loading the config is what tells us this is a fresh install.
    readonly bool _firstRun = GlimpseConfig.CreatedThisSession
                              || Environment.GetCommandLineArgs().Contains("--welcome", StringComparer.OrdinalIgnoreCase);
    readonly IpcServer _ipc;
    readonly TrayIcon _tray;
    GlobalHotkey _hotkey;
    SettingsWindow? _settings;
    bool _quitting;
    bool _indexing;

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

        VisualOnly.Visibility = _runtime.VisualAvailable ? Visibility.Visible : Visibility.Collapsed;
        // Renamed/moved/deleted images: refresh what's on screen so nothing points at an old path.
        _runtime.ImagesChanged += () => DispatcherQueue.TryEnqueue(() =>
        {
            _ = RunSearchAsync(SearchBox.Text);
            RefreshStatsSoon();
        });
        _runtime.NewImagesIndexed += n => DispatcherQueue.TryEnqueue(async () =>
        {
            SetIndexStatus($"indexed {n} new image{(n == 1 ? "" : "s")}");
            await EmbedNewAsync();
            _ = RunSearchAsync(SearchBox.Text);
        });

        var hwnd = WindowNative.GetWindowHandle(this);
        _hotkey = RegisterHotkey(_runtime.Config.Hotkey);

        _tray = new TrayIcon(hwnd, icon, "Glimpse");
        _tray.Clicked += Summon;
        _tray.BuildMenu = BuildTrayMenu;
        _tray.Message += msg => { if (msg == App.SummonMessage) Show(); };

        // The X button tucks the window into the tray; Quit (tray menu / Ctrl+Q) really exits.
        AppWindow.Closing += (_, e) =>
        {
            if (_quitting) return;
            e.Cancel = true;
            AppWindow.Hide();
        };
        Closed += (_, _) => Shutdown();
        Activated += (_, e) =>
        {
            if (e.WindowActivationState != WindowActivationState.Deactivated) SearchBox.Focus(FocusState.Programmatic);
        };

        // The Command Palette extension searches through the app over a named pipe.
        _ipc = new IpcServer(_runtime, OnUiThread(CopyImageAsync), OnUiThread(CopyTextAsync), query => DispatcherQueue.TryEnqueue(() =>
        {
            if (query is not null) SearchBox.Text = query;
            Show();
        }));
        _ipc.Start();

        // Ctrl+, opens Settings (comma has no named VirtualKey, so it's added here rather than in XAML).
        var settingsKey = new KeyboardAccelerator { Modifiers = VirtualKeyModifiers.Control, Key = (VirtualKey)188 };
        settingsKey.Invoked += OnShowSettings;
        Root.KeyboardAccelerators.Add(settingsKey);

        _ = RunSearchAsync("");
        if (!_firstRun) _ = CatchUpIndexAsync(); // on first run, indexing waits for the welcome screen's folders
    }

    // ---- first run ------------------------------------------------------------------------

    public bool IsFirstRun => _firstRun;

    public void ShowWelcome() =>
        new WelcomeWindow(_runtime.Config.Clone(), _hotkey.IsRegistered, OnWelcomeDone).Activate();

    async void OnWelcomeDone(WelcomeChoices choices)
    {
        AutoStart.Set(choices.StartWithWindows);
        var result = await Task.Run(() => _runtime.Apply(choices.Config));
        OnApplied(result);                                 // re-registers a changed shortcut; re-indexes if folders changed
        if (!result.IndexingChanged) _ = CatchUpIndexAsync(); // folders as proposed: start the first index here
        Show();
        if (choices.DownloadModel)
        {
            ShowSettings();
            _settings?.StartModelDownload();
        }
    }

    /// <summary>Wraps a UI-thread operation so it can be awaited from a background thread.</summary>
    Func<string, Task> OnUiThread(Func<string, Task> action) => arg =>
    {
        var done = new TaskCompletionSource();
        DispatcherQueue.TryEnqueue(async () =>
        {
            try { await action(arg); done.SetResult(); }
            catch (Exception ex) { done.SetException(ex); }
        });
        return done.Task;
    };

    // ---- window lifecycle -----------------------------------------------------------------

    public void StartHidden() => AppWindow.Hide();

    /// <summary>Hotkey / tray click: toggle.</summary>
    void Summon()
    {
        if (AppWindow.IsVisible && IsForeground()) AppWindow.Hide();
        else Show();
    }

    void Show()
    {
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

    void Quit()
    {
        _quitting = true;
        Close();
    }

    IReadOnlyList<TrayMenuItem> BuildTrayMenu() =>
    [
        new($"Open Glimpse\t{_runtime.Config.Hotkey}", Show, IsDefault: true),
        new(_indexing ? "Indexing…" : "Re-index now", () => _ = CatchUpIndexAsync()),
        TrayMenuItem.Separator,
        new("Settings…", ShowSettings),
        TrayMenuItem.Separator,
        new("Quit", Quit),
    ];

    void Shutdown()
    {
        _settings?.Close();
        _ipc.Dispose();
        _tray.Dispose();
        _hotkey.Dispose();
        _runtime.Dispose();
    }

    // ---- settings -------------------------------------------------------------------------

    public void OpenSettings() => ShowSettings();

    void ShowSettings()
    {
        if (_settings is null)
        {
            _settings = new SettingsWindow(_runtime, ApplySettings, () => CatchUpIndexAsync(), RebuildIndexAsync, OnModelDownloaded);
            _settings.Closed += (_, _) => _settings = null;
        }
        _settings.Activate();
    }

    void OnShowSettings(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        ShowSettings();
    }

    void OnSettingsClick(object sender, RoutedEventArgs e) => ShowSettings();

    GlimpseConfig? _pendingConfig;
    bool _applying;

    /// <summary>
    /// Settings changed something: rebuild what depends on it and catch the index up. Each config is
    /// complete, so if changes arrive while one is applying, only the newest needs applying next.
    /// Runs off the UI thread: forgetting a removed folder can touch thousands of rows.
    /// </summary>
    async void ApplySettings(GlimpseConfig next)
    {
        _pendingConfig = next;
        if (_applying) return;
        _applying = true;
        try
        {
            while (_pendingConfig is { } config)
            {
                _pendingConfig = null;
                OnApplied(await Task.Run(() => _runtime.Apply(config)));
            }
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
        finally
        {
            _applying = false;
        }
    }

    void OnApplied(GlimpseRuntime.ApplyResult result)
    {
        if (result.HotkeyChanged)
        {
            _hotkey.Dispose();
            _hotkey = RegisterHotkey(_runtime.Config.Hotkey);
        }
        VisualOnly.Visibility = _runtime.VisualAvailable ? Visibility.Visible : Visibility.Collapsed;
        if (!_runtime.VisualAvailable) VisualOnly.IsChecked = false;

        if (result.IndexingChanged || result.VisualChanged) _ = CatchUpIndexAsync();
        else SetIndexStatus(_indexStatus);
        if (result.ImagesForgotten > 0 || result.VisualChanged) _ = RunSearchAsync(SearchBox.Text);
    }

    void OnModelDownloaded()
    {
        _runtime.ReloadVisual();
        OnApplied(new GlimpseRuntime.ApplyResult(IndexingChanged: false, VisualChanged: true, HotkeyChanged: false, ImagesForgotten: 0));
    }

    async Task RebuildIndexAsync()
    {
        await Task.Run(_runtime.WriteIndex.Clear);
        _runtime.Engine.InvalidateVisual();
        _items.Clear();
        await CatchUpIndexAsync();
    }

    GlobalHotkey RegisterHotkey(string gesture)
    {
        var hotkey = new GlobalHotkey(WindowNative.GetWindowHandle(this), gesture);
        hotkey.Pressed += () => DispatcherQueue.TryEnqueue(Summon);
        return hotkey;
    }

    // ---- indexing -------------------------------------------------------------------------

    bool _indexAgain;

    /// <summary>Text pass, then visual pass. Calls while one is running queue exactly one more run.</summary>
    async Task CatchUpIndexAsync()
    {
        if (_indexing)
        {
            _indexAgain = true;
            return;
        }
        _indexing = true;
        try
        {
            do
            {
                _indexAgain = false;
                var indexer = _runtime.Indexer;
                var progress = new Progress<IndexProgress>(p =>
                    SetIndexStatus(p.Total == 0 ? p.Phase + "…" : $"indexing {p.Done}/{p.Total}"));
                var report = await Task.Run(() => indexer.RunAsync(progress: progress));
                SetIndexStatus(report.Indexed > 0 ? $"indexed {report.Indexed} new" : "up to date");
                var embedded = await EmbedNewAsync();
                if (report.Indexed > 0 || report.Removed > 0 || embedded > 0) _ = RunSearchAsync(SearchBox.Text);
            }
            while (_indexAgain);
        }
        catch (Exception ex)
        {
            App.Log(ex);
            SetIndexStatus("indexing failed: " + ex.Message);
        }
        finally
        {
            _indexing = false;
        }
    }

    /// <summary>Visual pass: CLIP-embed anything the text pass added. Returns how many were embedded.</summary>
    async Task<int> EmbedNewAsync()
    {
        if (_runtime.Embedder is not { } embedder) return 0;
        var progress = new Progress<IndexProgress>(p => SetIndexStatus($"visual indexing {p.Done}/{p.Total}"));
        try
        {
            var report = await Task.Run(() => embedder.RunAsync(progress));
            if (report.Embedded + report.Failed > 0)
            {
                _runtime.Engine.InvalidateVisual();
                SetIndexStatus($"up to date · visual on {_runtime.Clip?.VisionDevice}");
            }
            return report.Embedded;
        }
        catch (Exception ex)
        {
            App.Log(ex);
            SetIndexStatus("visual indexing failed: " + ex.Message);
            return 0;
        }
    }

    IndexStats? _stats;
    DateTime _statsAt;
    bool _statsRunning, _statsDirty;

    /// <summary>Called on every indexing progress tick, so it only re-renders; the counts refresh in the background.</summary>
    void SetIndexStatus(string status)
    {
        _indexStatus = status;
        RenderStatus();
        RefreshStatsSoon();
    }

    void RenderStatus()
    {
        var hotkey = _runtime.Config.Hotkey;
        var counts = _stats is { } s
            ? $"{s.Images:N0} images · {s.WithText:N0} with text · {(_runtime.VisualAvailable ? $"{s.Embedded:N0} visual" : "visual off")} · "
            : "";
        StatusText.Text = counts + _indexStatus +
                          (_hotkey.IsRegistered ? $" · {hotkey} to summon" : $" · hotkey {hotkey} unavailable");
        ToolTipService.SetToolTip(StatusText, StatusText.Text); // the bar trims it when the window is narrow
        _tray.Tooltip = $"Glimpse — {(_stats is { } t ? $"{t.Images:N0} images · " : "")}{_indexStatus}";
    }

    /// <summary>Status first: drop the key hints when the bar can't fit both (they're also in the tooltip and README).</summary>
    void OnStatusBarSizeChanged(object sender, SizeChangedEventArgs e)
    {
        const double minStatusWidth = 560;
        // A collapsed element measures as zero, so remember the hints' width from when they were showing.
        if (KeyHints.Visibility == Visibility.Visible)
        {
            KeyHints.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            _keyHintsWidth = KeyHints.DesiredSize.Width;
        }
        var room = e.NewSize.Width - 40 /* padding */ - 24 /* column gap */;
        KeyHints.Visibility = room - _keyHintsWidth >= minStatusWidth ? Visibility.Visible : Visibility.Collapsed;
    }

    double _keyHintsWidth;

    /// <summary>
    /// Recount at most once a second, off the UI thread, and push the result to the status bar and Settings.
    /// Requests that arrive mid-count are coalesced into one more count afterwards, so the final numbers are right.
    /// </summary>
    async void RefreshStatsSoon()
    {
        if (_statsRunning)
        {
            _statsDirty = true;
            return;
        }
        _statsRunning = true;
        try
        {
            do
            {
                _statsDirty = false;
                var wait = TimeSpan.FromSeconds(1) - (DateTime.UtcNow - _statsAt);
                if (wait > TimeSpan.Zero) await Task.Delay(wait);
                _stats = await Task.Run(_runtime.SearchIndex.GetStats);
                _statsAt = DateTime.UtcNow;
                RenderStatus();
                _settings?.UpdateStats(_stats);
            }
            while (_statsDirty);
        }
        catch (Exception ex)
        {
            App.Log(ex);
        }
        finally
        {
            _statsRunning = false;
        }
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
            var effective = VisualOnly.IsChecked == true && !query.TrimStart().StartsWith('~') ? "~" + query : query;
            var engine = _runtime.Engine;
            hits = await Task.Run(() => engine.Search(effective, limit: 120));
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
        bmp.ImageFailed += (_, e) => PreviewMeta.Text = File.Exists(hit.Path)
            ? $"Windows can't display this image ({e.ErrorMessage})."
            : "This image was moved, renamed or deleted since it was indexed. Search again to refresh.";

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

        var lines = _runtime.SearchIndex.GetOcrLines(id);

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
        await CopyImageAsync(item.Hit.Path);
        CountText.Text = "image copied";
    }

    /// <summary>Puts the image on the clipboard both as a bitmap and as a file. Must run on the UI thread.</summary>
    static async Task CopyImageAsync(string path)
    {
        var file = await StorageFile.GetFileFromPathAsync(path);
        var data = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        data.SetBitmap(RandomAccessStreamReference.CreateFromFile(file)); // paste into chat, docs, Obsidian
        data.SetStorageItems([file]);                                     // paste into Explorer
        Clipboard.SetContent(data);
        Clipboard.Flush();
    }

    void OnVisualOnlyToggled(object sender, RoutedEventArgs e) => _ = RunSearchAsync(SearchBox.Text);

    void OnToggleVisual(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        if (!_runtime.VisualAvailable) return;
        VisualOnly.IsChecked = VisualOnly.IsChecked != true;
        _ = RunSearchAsync(SearchBox.Text);
    }

    void OnMoreLikeThis(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        if (!_runtime.VisualAvailable || Selected is not { } item) return;
        SearchBox.Text = $"like:{item.Hit.Id}";
        SearchBox.Focus(FocusState.Programmatic);
        SearchBox.SelectionStart = SearchBox.Text.Length;
    }

    void OnCopyPath(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        if (Selected is not { } item) return;
        CopyTextAsync(item.Hit.Path);
        CountText.Text = "path copied";
    }

    /// <summary>Ctrl+Shift+T: the text Glimpse read from the image, line breaks kept.</summary>
    void OnCopyText(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        if (Selected is not { } item) return;
        var text = _runtime.SearchIndex.GetText(item.Hit.Id);
        if (string.IsNullOrWhiteSpace(text))
        {
            CountText.Text = "no text in this image";
            return;
        }
        CopyTextAsync(text);
        var lines = text.Split('\n').Length;
        CountText.Text = $"text copied · {lines} line{(lines == 1 ? "" : "s")}";
    }

    /// <summary>Must run on the UI thread.</summary>
    static Task CopyTextAsync(string text)
    {
        var data = new DataPackage();
        data.SetText(text);
        Clipboard.SetContent(data);
        Clipboard.Flush();
        return Task.CompletedTask;
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
        else AppWindow.Hide(); // stays resident in the tray; hotkey or tray click brings it back
    }

    void OnQuit(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args) => Quit();

    bool IsForeground() => GetForegroundWindow() == WindowNative.GetWindowHandle(this);

    [LibraryImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(nint hwnd);
}
