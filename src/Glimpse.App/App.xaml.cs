using Microsoft.UI.Xaml;

namespace Glimpse.App;

public partial class App : Application
{
    MainWindow? _window;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) => Log(e.Exception);
        if (Environment.GetEnvironmentVariable("GLIMPSE_TRACE") == "1")
            AppDomain.CurrentDomain.FirstChanceException += (_, e) => Log(e.Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => { Log(e.Exception); e.SetObserved(); };
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
        // `Glimpse.exe --hidden` (e.g. from the Startup folder) starts in the background, waiting for the hotkey.
        var hidden = Environment.GetCommandLineArgs().Contains("--hidden", StringComparer.OrdinalIgnoreCase);
        _window = new MainWindow();
        if (hidden) _window.StartHidden();
        else _window.Activate();
    }
}
