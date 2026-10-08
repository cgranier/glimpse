using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;

namespace Glimpse.App;

public partial class App : Application
{
    /// <summary>Posted by a second launch to the running instance: "show yourself".</summary>
    public static readonly uint SummonMessage = RegisterWindowMessageW("Glimpse.Summon");

    static Mutex? _singleInstance;
    MainWindow? _window;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) => Log(e.Exception);
        Core.Diagnostics.Error = Log;
        TaskScheduler.UnobservedTaskException += (_, e) => { Log(e.Exception); e.SetObserved(); };
        if (Environment.GetEnvironmentVariable("GLIMPSE_TRACE") == "1")
            AppDomain.CurrentDomain.FirstChanceException += (_, e) => Log(e.Exception);
    }

    public static void Log(Exception ex)
    {
        try
        {
            File.AppendAllText(Path.Combine(Core.GlimpseConfig.DataDir, "glimpse.log"), $"[{DateTime.Now:O}] {ex}\n\n");
        }
        catch { }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // `Glimpse.exe --hidden` (e.g. at login) starts in the tray, waiting for the hotkey.
        var hidden = Environment.GetCommandLineArgs().Contains("--hidden", StringComparer.OrdinalIgnoreCase);

        _singleInstance = new Mutex(initiallyOwned: true, @"Local\Glimpse.SingleInstance", out var isFirst);
        if (!isFirst)
        {
            // Already running (it owns the hotkey and tray icon): bring that one up and leave.
            if (!hidden)
            {
                var existing = FindWindowW("WinUIDesktopWin32WindowClass", "Glimpse");
                if (existing != 0) PostMessageW(existing, SummonMessage, 0, 0);
            }
            Exit();
            return;
        }

        _window = new MainWindow();
        if (_window.IsFirstRun)
        {
            _window.StartHidden(); // the welcome screen comes first; the main window opens when it's done
            _window.ShowWelcome();
            return;
        }
        if (hidden) _window.StartHidden();
        else _window.Activate();
        if (Environment.GetCommandLineArgs().Contains("--settings", StringComparer.OrdinalIgnoreCase)) _window.OpenSettings();
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern uint RegisterWindowMessageW(string name);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern nint FindWindowW(string? className, string? title);

    [DllImport("user32.dll")]
    static extern bool PostMessageW(nint hwnd, uint msg, nint wParam, nint lParam);
}
