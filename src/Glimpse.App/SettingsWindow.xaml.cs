using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Glimpse.Core;
using Glimpse.Core.Index;
using Glimpse.Core.Visual;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Pickers;
using Windows.System;
using WinRT.Interop;

namespace Glimpse.App;

/// <summary>
/// Settings apply as you change them, like Windows Settings: every edit clones the current config,
/// changes one thing, and hands it to the main window, which rebuilds only what that affects.
/// </summary>
public sealed partial class SettingsWindow : Window
{
    static readonly string[] Keys =
        [.. Enumerable.Range('A', 26).Select(c => ((char)c).ToString()),
         .. Enumerable.Range(0, 10).Select(n => n.ToString()),
         .. Enumerable.Range(1, 12).Select(n => $"F{n}"),
         "Space"];

    readonly GlimpseRuntime _runtime;
    readonly Action<GlimpseConfig> _apply;
    readonly Func<Task> _reindex;
    readonly Func<Task> _rebuild;
    readonly Action _modelDownloaded;
    readonly ModelInfo _model = ModelCatalog.Default;
    CancellationTokenSource? _download;
    bool _loading;

    public SettingsWindow(GlimpseRuntime runtime, Action<GlimpseConfig> apply, Func<Task> reindex, Func<Task> rebuild, Action modelDownloaded)
    {
        _runtime = runtime;
        _apply = apply;
        _reindex = reindex;
        _rebuild = rebuild;
        _modelDownloaded = modelDownloaded;

        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "glimpse.ico");
        if (File.Exists(icon)) AppWindow.SetIcon(icon);
        var png = Path.ChangeExtension(icon, ".png");
        if (File.Exists(png)) TitleIcon.Source = AboutIcon.Source = new BitmapImage(new Uri(png));
        Resize(900, 860);

        HotkeyKey.ItemsSource = Keys;
        WorkersBox.Maximum = Environment.ProcessorCount;
        WorkersDescription.Text = $"How many images are read at once. More is faster but busier; this PC has {Environment.ProcessorCount} logical processors.";
        ModelSource.Content = $"{_model.DisplayName} · {_model.License} · {_model.SourceUrl.Replace("https://", "")}";
        ModelSource.NavigateUri = new Uri(_model.SourceUrl);
        var version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "dev";
        AboutVersion.Text = $"Glimpse {version.Split('+')[0]}";

        Load();
        _ = MeasureThumbnailsAsync();
        _ = Task.Run(_runtime.SearchIndex.GetStats).ContinueWith(t => UpdateStats(t.Result), TaskScheduler.FromCurrentSynchronizationContext());
        Closed += (_, _) => _download?.Cancel();
    }

    // The window's own copy is the source of truth for what it shows. Applying happens in the background,
    // so reading the runtime's config right after a change would still show the old state.
    GlimpseConfig? _draft;
    GlimpseConfig Config => _draft ??= _runtime.Config.Clone();

    /// <summary>Fills every control from the current config without triggering change handlers.</summary>
    void Load()
    {
        _loading = true;
        try
        {
            LoadHotkey(Config.Hotkey);
            AutoStartSwitch.IsOn = AutoStart.IsEnabled && !AutoStart.PointsElsewhere;
            AutoStartDescription.Text = AutoStart.PointsElsewhere
                ? "Currently starts another copy of Glimpse. Turn on to start this one instead."
                : "Starts in the tray when you sign in, ready for the shortcut.";

            BuildFolderList();

            VisualSwitch.IsOn = Config.VisualSearch;
            GpuSwitch.IsOn = Config.UseGpu;
            GpuSwitch.IsEnabled = Config.VisualSearch;
            CloudSwitch.IsOn = Config.HydrateCloudFiles;
            WorkersBox.Value = Config.Workers;
            UpdateModelCard();
        }
        finally
        {
            _loading = false;
        }
    }

    void Change(Action<GlimpseConfig> edit)
    {
        if (_loading) return;
        var next = Config.Clone();
        edit(next);
        _draft = next;
        _apply(next.Clone());
    }

    // ---- hotkey ---------------------------------------------------------------------------

    void LoadHotkey(string gesture)
    {
        var parts = gesture.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        bool Has(string p) => parts.Any(x => x.Equals(p, StringComparison.OrdinalIgnoreCase));
        ModWin.IsChecked = Has("Win");
        ModCtrl.IsChecked = Has("Ctrl") || Has("Control");
        ModAlt.IsChecked = Has("Alt");
        ModShift.IsChecked = Has("Shift");
        HotkeyKey.SelectedItem = Keys.FirstOrDefault(k => parts.Any(p => p.Equals(k, StringComparison.OrdinalIgnoreCase)));
        SetHotkeyStatus("Current shortcut", ok: true);
    }

    void OnHotkeyChanged(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var mods = new[] { (ModWin, "Win"), (ModCtrl, "Ctrl"), (ModAlt, "Alt"), (ModShift, "Shift") }
            .Where(m => m.Item1.IsChecked == true).Select(m => m.Item2).ToList();
        if (HotkeyKey.SelectedItem is not string key) return;
        if (mods.Count == 0)
        {
            SetHotkeyStatus("Pick at least one of Win, Ctrl, Alt or Shift.", ok: false);
            return;
        }

        var gesture = string.Join('+', [.. mods, key]);
        if (string.Equals(gesture, Config.Hotkey, StringComparison.OrdinalIgnoreCase))
        {
            SetHotkeyStatus("Current shortcut", ok: true);
            return;
        }
        if (!GlobalHotkey.IsAvailable(gesture))
        {
            SetHotkeyStatus($"{gesture} is already used by Windows or another app. Still using {Config.Hotkey}.", ok: false);
            return;
        }
        Change(c => c.Hotkey = gesture);
        SetHotkeyStatus($"Saved: {gesture}", ok: true);
    }

    void SetHotkeyStatus(string text, bool ok)
    {
        HotkeyStatus.Text = text;
        HotkeyStatus.Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[
            ok ? "SystemFillColorSuccessBrush" : "SystemFillColorCriticalBrush"];
    }

    void OnAutoStartToggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        AutoStart.Set(AutoStartSwitch.IsOn);
        AutoStartDescription.Text = "Starts in the tray when you sign in, ready for the shortcut.";
    }

    // ---- folders --------------------------------------------------------------------------

    // Latest counts pushed by the main window (never queried from here: indexing calls in often).
    IndexStats? _stats;
    readonly Dictionary<string, TextBlock> _folderCounts = [];

    void BuildFolderList()
    {
        FolderList.Children.Clear();
        _folderCounts.Clear();
        foreach (var source in Config.Sources) FolderList.Children.Add(FolderCard(source));
        RenderFolderCounts();
    }

    /// <summary>New counts from the main window (at most once a second while indexing).</summary>
    public void UpdateStats(IndexStats stats)
    {
        _stats = stats;
        var db = new FileInfo(GlimpseConfig.DatabasePath);
        var size = db.Exists ? Mb(db.Length + (new FileInfo(db.FullName + "-wal") is { Exists: true } w ? w.Length : 0)) : "—";
        IndexStatsText.Text = $"{stats.Images:N0} images · {stats.WithText:N0} with text · {stats.Embedded:N0} visual · {stats.Errors:N0} unreadable · {size} on disk";
        RenderFolderCounts();
    }

    void RenderFolderCounts()
    {
        foreach (var (name, text) in _folderCounts)
        {
            var count = _stats?.BySource.GetValueOrDefault(name) ?? 0;
            text.Text = _stats is null ? "Counting…" : count > 0 ? $"{count:N0} images" : "Not indexed yet — indexing starts right away";
        }
        FolderSummary.Text = Config.Sources.Count == 0
            ? "No folders yet: add the ones where your screenshots and images live."
            : $"{Config.Sources.Count} folder{(Config.Sources.Count == 1 ? "" : "s")}" +
              (_stats is { } s ? $" · {s.Images:N0} images indexed" : "");
    }

    Border FolderCard(Source source)
    {
        var exists = Directory.Exists(source.Path);

        var name = new TextBox { Text = source.Name, Width = 150, VerticalAlignment = VerticalAlignment.Center };
        ToolTipService.SetToolTip(name, $"Search only this folder with in:{source.Name}");
        void CommitName()
        {
            var clean = Slug(name.Text);
            if (clean.Length == 0 || clean == source.Name) { name.Text = source.Name; return; }
            if (Config.Sources.Any(s => s.Name == clean)) clean = Unique(clean);
            Change(c => c.Sources = c.Sources.Select(s => s == source ? s with { Name = clean } : s).ToList());
            BuildFolderList();
        }
        name.LostFocus += (_, _) => CommitName();
        name.KeyDown += (_, e) => { if (e.Key == VirtualKey.Enter) { CommitName(); e.Handled = true; } };

        var subfolders = new CheckBox { Content = "Subfolders", IsChecked = source.Recursive, MinWidth = 0, VerticalAlignment = VerticalAlignment.Center };
        subfolders.Click += (_, _) =>
        {
            Change(c => c.Sources = c.Sources.Select(s => s == source ? s with { Recursive = subfolders.IsChecked == true } : s).ToList());
            BuildFolderList();
        };

        var open = new Button { Content = new FontIcon { Glyph = "", FontSize = 14 }, Style = (Style)Application.Current.Resources["SubtleButtonStyle"] };
        ToolTipService.SetToolTip(open, "Open folder");
        open.Click += (_, _) => { if (exists) Process.Start("explorer.exe", $"\"{source.Path}\""); };

        var remove = new Button { Content = new FontIcon { Glyph = "", FontSize = 14 }, Style = (Style)Application.Current.Resources["SubtleButtonStyle"] };
        ToolTipService.SetToolTip(remove, "Remove folder (its images leave the index; the files are not touched)");
        remove.Click += (_, _) =>
        {
            Change(c => c.Sources = c.Sources.Where(s => s != source).ToList());
            BuildFolderList();
        };

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = source.Path, TextTrimming = TextTrimming.CharacterEllipsis });
        var countText = new TextBlock
        {
            Text = "Folder not found — it may have moved or be on a disconnected drive",
            Style = (Style)((FrameworkElement)Content).Resources["CardDescription"],
        };
        if (exists) _folderCounts[source.Name] = countText; // filled in by RenderFolderCounts
        text.Children.Add(countText);

        var grid = new Grid { ColumnSpacing = 12 };
        foreach (var w in new[] { GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto, GridLength.Auto, GridLength.Auto })
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = w });
        var icon = new FontIcon { Glyph = exists ? "" : "", Style = (Style)((FrameworkElement)Content).Resources["CardIcon"] };
        Add(grid, icon, 0);
        Add(grid, text, 1);
        Add(grid, name, 2);
        Add(grid, subfolders, 3);
        Add(grid, open, 4);
        Add(grid, remove, 5);

        return new Border { Style = (Style)((FrameworkElement)Content).Resources["Card"], Child = grid };

        static void Add(Grid g, FrameworkElement e, int column)
        {
            Grid.SetColumn(e, column);
            g.Children.Add(e);
        }
    }

    async void OnAddFolder(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
        picker.FileTypeFilter.Add("*");
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        var folder = await picker.PickSingleFolderAsync();
        if (folder is null) return;

        var path = folder.Path.TrimEnd('\\');
        if (Config.Sources.Any(s => string.Equals(s.Path.TrimEnd('\\'), path, StringComparison.OrdinalIgnoreCase)))
            return;
        var name = Unique(Slug(Path.GetFileName(path)) is { Length: > 0 } n ? n : "folder");
        Change(c => c.Sources = [.. c.Sources, new Source(name, path)]);
        BuildFolderList();
    }

    /// <summary>in:name has to be one word: lowercase, dashes for spaces, nothing exotic.</summary>
    static string Slug(string text) =>
        new string(text.Trim().ToLowerInvariant().Select(ch => char.IsLetterOrDigit(ch) ? ch : '-').ToArray()).Trim('-');

    string Unique(string name)
    {
        var candidate = name;
        for (var i = 2; Config.Sources.Any(s => s.Name == candidate); i++) candidate = $"{name}-{i}";
        return candidate;
    }

    // ---- visual search --------------------------------------------------------------------

    void OnVisualToggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        Change(c => c.VisualSearch = VisualSwitch.IsOn);
        GpuSwitch.IsEnabled = VisualSwitch.IsOn;
        UpdateModelCard();
    }

    void OnGpuToggled(object sender, RoutedEventArgs e) => Change(c => c.UseGpu = GpuSwitch.IsOn);

    void UpdateModelCard()
    {
        var installed = _runtime.ModelInstalled;
        ModelTitle.Text = $"Model: {_model.DisplayName}";
        if (_download is not null)
        {
            ModelButton.Content = "Cancel";
            return;
        }

        ModelButton.Content = installed ? "Remove" : $"Download ({Mb(_model.TotalSize)})";
        ModelButton.IsEnabled = true;
        ModelStatus.Text = installed switch
        {
            true when !Config.VisualSearch => "Installed. Visual search is off.",
            true => $"Installed · {(_runtime.Clip?.VisionDevice is { } d && d != "not loaded" ? $"running on {d}" : Config.UseGpu ? "uses the GPU when available" : "runs on the processor")}",
            false => $"Not installed. One-time {Mb(_model.TotalSize)} download from Hugging Face; nothing is uploaded.",
        };
    }

    /// <summary>The welcome screen's "download the visual model now".</summary>
    public void StartModelDownload()
    {
        if (_download is not null || _runtime.ModelInstalled) return;
        ModelButton.StartBringIntoView();
        OnModelButton(ModelButton, new RoutedEventArgs());
    }

    async void OnModelButton(object sender, RoutedEventArgs e)
    {
        if (_download is not null)
        {
            _download.Cancel();
            return;
        }

        var dir = Path.Combine(GlimpseConfig.ModelsDir, _model.Name);
        if (_runtime.ModelInstalled)
        {
            if (!await ConfirmAsync("Remove the visual search model?",
                    $"Deletes {Mb(_model.TotalSize)} from this PC. Text search keeps working; you can download the model again any time.", "Remove"))
                return;
            Change(c => c.VisualSearch = false);   // releases the model files
            await Task.Delay(TimeSpan.FromSeconds(6));
            try { Directory.Delete(dir, recursive: true); }
            catch (IOException) { ModelStatus.Text = "Couldn't delete every file yet; try again in a moment."; }
            _loading = true; VisualSwitch.IsOn = false; GpuSwitch.IsEnabled = false; _loading = false;
            UpdateModelCard();
            return;
        }

        _download = new CancellationTokenSource();
        ModelProgressPanel.Visibility = Visibility.Visible;
        UpdateModelCard();
        var progress = new Progress<(long Done, long Total)>(p =>
        {
            ModelProgress.Value = (double)p.Done / p.Total;
            ModelProgressText.Text = $"{Mb(p.Done)} of {Mb(p.Total)}";
        });
        try
        {
            await Task.Run(() => ModelCatalog.DownloadAsync(_model, dir, progress, _download.Token));
            _download = null;
            if (!Config.VisualSearch) Change(c => c.VisualSearch = true);
            _loading = true; VisualSwitch.IsOn = true; GpuSwitch.IsEnabled = true; _loading = false;
            _modelDownloaded();
            ModelStatus.Text = "Installed. Indexing your images visually now; this takes a few minutes.";
        }
        catch (OperationCanceledException)
        {
            _download = null;
            ModelStatus.Text = "Download cancelled. It resumes from the finished files next time.";
        }
        catch (Exception ex)
        {
            _download = null;
            App.Log(ex);
            ModelStatus.Text = $"Download failed: {ex.Message}";
        }
        finally
        {
            ModelProgressPanel.Visibility = Visibility.Collapsed;
            ModelButton.Content = _runtime.ModelInstalled ? "Remove" : $"Download ({Mb(_model.TotalSize)})";
        }
    }

    // ---- indexing -------------------------------------------------------------------------

    void OnCloudToggled(object sender, RoutedEventArgs e) => Change(c => c.HydrateCloudFiles = CloudSwitch.IsOn);

    void OnWorkersChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (double.IsNaN(args.NewValue)) { sender.Value = Config.Workers; return; }
        var workers = (int)Math.Clamp(Math.Round(args.NewValue), 1, Environment.ProcessorCount);
        if (workers != Config.Workers) Change(c => c.Workers = workers);
    }

    async void OnReindex(object sender, RoutedEventArgs e) => await _reindex();

    async void OnRebuild(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmAsync("Rebuild the index?",
                "Forgets everything and reads every image again (text, then visual). Takes a few minutes; your files aren't touched.", "Rebuild"))
            return;
        await _rebuild();
    }

    async Task MeasureThumbnailsAsync()
    {
        var (files, bytes) = await Task.Run(() =>
        {
            if (!Directory.Exists(Thumbnails.Dir)) return (0, 0L);
            var all = new DirectoryInfo(Thumbnails.Dir).EnumerateFiles("*", SearchOption.AllDirectories).ToList();
            return (all.Count, all.Sum(f => f.Length));
        });
        ThumbStats.Text = files == 0
            ? "Empty. Small previews are made as Command Palette searches need them."
            : $"{files:N0} previews · {Mb(bytes)}. Made for Command Palette results; safe to clear any time.";
        ClearThumbsButton.IsEnabled = files > 0;
    }

    async void OnClearThumbnails(object sender, RoutedEventArgs e)
    {
        ClearThumbsButton.IsEnabled = false;
        ThumbStats.Text = "Clearing…";
        await Task.Run(() =>
        {
            if (Directory.Exists(Thumbnails.Dir)) Directory.Delete(Thumbnails.Dir, recursive: true);
        });
        await MeasureThumbnailsAsync();
    }

    // ---- about ----------------------------------------------------------------------------

    void OnOpenDataFolder(object sender, RoutedEventArgs e) => Process.Start("explorer.exe", $"\"{GlimpseConfig.DataDir}\"");

    void OnOpenLog(object sender, RoutedEventArgs e)
    {
        var log = Path.Combine(GlimpseConfig.DataDir, "glimpse.log");
        if (!File.Exists(log)) File.WriteAllText(log, "");
        Process.Start(new ProcessStartInfo(log) { UseShellExecute = true });
    }

    // ---- helpers --------------------------------------------------------------------------

    async Task<bool> ConfirmAsync(string title, string body, string action)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = body,
            PrimaryButtonText = action,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    static string Mb(long bytes) => bytes >= 1 << 30 ? $"{bytes / (double)(1 << 30):F1} GB" : $"{Math.Max(1, bytes >> 20):N0} MB";

    void Resize(int width, int height)
    {
        var scale = GetDpiForWindow(WindowNative.GetWindowHandle(this)) / 96.0;
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        int w = (int)(width * scale), h = Math.Min((int)(height * scale), area.Height - 40);
        AppWindow.MoveAndResize(new(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2, w, h));
    }

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(nint hwnd);
}
