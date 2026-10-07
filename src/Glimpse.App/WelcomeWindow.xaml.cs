using System.Runtime.InteropServices;
using Glimpse.Core;
using Glimpse.Core.Visual;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace Glimpse.App;

/// <summary>What the person chose on the welcome screen.</summary>
public sealed record WelcomeChoices(GlimpseConfig Config, bool StartWithWindows, bool DownloadModel);

/// <summary>
/// First run: confirm the folders to index, optionally fetch the visual model, learn the shortcut.
/// Indexing waits for this, so it starts on the folders actually chosen. Closing it = defaults.
/// </summary>
public sealed partial class WelcomeWindow : Window
{
    /// <summary>Tried in order when the default shortcut is taken by another app.</summary>
    static readonly string[] FallbackHotkeys = ["Ctrl+Alt+S", "Win+Shift+G", "Ctrl+Alt+G", "Ctrl+Shift+Space"];
    const int CountCap = 50_000;

    readonly GlimpseConfig _config;
    readonly Action<WelcomeChoices> _done;
    readonly List<(Source Source, CheckBox Box)> _folders = [];
    bool _finished;

    /// <param name="hotkeyWorks">Whether the main window managed to register the configured shortcut.</param>
    public WelcomeWindow(GlimpseConfig config, bool hotkeyWorks, Action<WelcomeChoices> done)
    {
        _config = config;
        _done = done;
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBar);
        var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "glimpse.ico");
        if (File.Exists(icon)) AppWindow.SetIcon(icon);
        var png = Path.ChangeExtension(icon, ".png");
        if (File.Exists(png)) Logo.Source = new BitmapImage(new Uri(png));
        if (AppWindow.Presenter is OverlappedPresenter p) { p.IsMaximizable = false; p.IsMinimizable = false; }
        Resize(640, 820);

        foreach (var source in config.Sources) AddFolderRow(source, isChecked: true);
        UpdateStartButton();

        var model = ModelCatalog.Default;
        var installed = ModelCatalog.IsInstalled(model, Path.Combine(GlimpseConfig.ModelsDir, model.Name));
        DownloadModel.IsEnabled = !installed;
        DownloadModel.Content = installed ? "Visual search model already installed" : $"Download the visual search model now ({model.TotalSize >> 20} MB)";
        DownloadModel.IsChecked = installed;
        ModelHint.Text = "Search by what images look like (“network diagram”, “bar chart”) and find similar images. " +
                         "It runs on this PC; the download comes from Hugging Face once. You can also do this later in Settings.";

        if (!hotkeyWorks && FallbackHotkeys.FirstOrDefault(GlobalHotkey.IsAvailable) is { } free)
        {
            ShortcutHint.Text = $"{_config.Hotkey} is already used by another app, so Glimpse will use {free}. You can change it any time in Settings.";
            _config.Hotkey = free;
        }
        else
        {
            ShortcutHint.Text = "Press it anywhere to open Glimpse. Glimpse also lives in the tray (^ next to the clock). Change the shortcut any time in Settings.";
        }
        ShowKeyCaps(_config.Hotkey);

        // Closing the window without pressing Start means "go with what's shown".
        Closed += (_, _) => Finish();
    }

    // ---- folders --------------------------------------------------------------------------

    void AddFolderRow(Source source, bool isChecked)
    {
        var count = new TextBlock { Text = "Counting images…", Style = (Style)((FrameworkElement)Content).Resources["Hint"] };
        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = source.Path, TextTrimming = TextTrimming.CharacterEllipsis });
        text.Children.Add(count);

        var box = new CheckBox { Content = text, IsChecked = isChecked, HorizontalAlignment = HorizontalAlignment.Stretch };
        box.Click += (_, _) => UpdateStartButton();
        FolderList.Children.Add(new Border { Style = (Style)((FrameworkElement)Content).Resources["Card"], Child = box });
        _folders.Add((source, box));

        var extensions = new HashSet<string>(_config.Extensions, StringComparer.OrdinalIgnoreCase);
        _ = Task.Run(() => CountImages(source.Path, extensions)).ContinueWith(t =>
        {
            var n = t.Result;
            count.Text = n switch
            {
                0 => "No images yet. New ones will be picked up as they arrive",
                >= CountCap => $"{CountCap:N0}+ images",
                _ => $"{n:N0} image{(n == 1 ? "" : "s")}",
            };
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    static int CountImages(string path, HashSet<string> extensions)
    {
        try
        {
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System | FileAttributes.Hidden };
            var n = 0;
            foreach (var file in Directory.EnumerateFiles(path, "*", options))
                if (extensions.Contains(Path.GetExtension(file)) && ++n >= CountCap) break;
            return n;
        }
        catch
        {
            return 0;
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
        if (_folders.Any(f => string.Equals(f.Source.Path.TrimEnd('\\'), path, StringComparison.OrdinalIgnoreCase))) return;

        var name = new string(Path.GetFileName(path).ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-');
        if (name.Length == 0) name = "folder";
        var unique = name;
        for (var i = 2; _folders.Any(f => f.Source.Name == unique); i++) unique = $"{name}-{i}";

        AddFolderRow(new Source(unique, path), isChecked: true);
        UpdateStartButton();
    }

    void UpdateStartButton()
    {
        var any = _folders.Any(f => f.Box.IsChecked == true);
        StartButton.IsEnabled = any;
        StartButton.Content = any ? "Start" : "Pick a folder";
    }

    // ---- shortcut -------------------------------------------------------------------------

    void ShowKeyCaps(string gesture)
    {
        KeyCaps.Children.Clear();
        var parts = gesture.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < parts.Length; i++)
        {
            if (i > 0) KeyCaps.Children.Add(new TextBlock { Text = "+", VerticalAlignment = VerticalAlignment.Center, Opacity = 0.6 });
            KeyCaps.Children.Add(new Border
            {
                Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["ControlFillColorDefaultBrush"],
                BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["ControlStrongStrokeColorDefaultBrush"],
                BorderThickness = new Thickness(1, 1, 1, 3),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(12, 4, 12, 4),
                MinWidth = 44,
                Child = new TextBlock { Text = parts[i], FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center },
            });
        }
    }

    // ---- finish ---------------------------------------------------------------------------

    void OnStart(object sender, RoutedEventArgs e)
    {
        Finish();
        Close();
    }

    void Finish()
    {
        if (_finished) return;
        _finished = true;
        _config.Sources = _folders.Where(f => f.Box.IsChecked == true).Select(f => f.Source).ToList();
        _done(new WelcomeChoices(_config, AutoStartBox.IsChecked == true, DownloadModel.IsEnabled && DownloadModel.IsChecked == true));
    }

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
