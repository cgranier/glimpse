namespace Glimpse.Core;

/// <summary>
/// Where Core reports errors it recovers from (background indexing, the folder watcher). The app points
/// this at its log file; without it, a swallowed exception leaves no trace at all.
/// </summary>
public static class Diagnostics
{
    public static Action<Exception>? Error { get; set; }

    public static void Report(Exception ex)
    {
        try { Error?.Invoke(ex); } catch { }
    }
}
