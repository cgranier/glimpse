using System.Collections.Concurrent;

namespace Glimpse.Core.Index;

/// <summary>
/// Watches every source folder and indexes new/changed images shortly after they land.
/// Events are debounced: ShareX and Snipping Tool write files in several steps.
/// </summary>
public sealed class FolderWatcher : IDisposable
{
    static readonly TimeSpan Debounce = TimeSpan.FromSeconds(2);

    readonly Indexer _indexer;
    readonly HashSet<string> _extensions;
    readonly List<FileSystemWatcher> _watchers = [];
    readonly ConcurrentDictionary<string, Source> _pending = new(StringComparer.OrdinalIgnoreCase);
    readonly Timer _timer;
    int _flushing;

    public event Action<int>? Indexed;

    public FolderWatcher(GlimpseConfig config, Indexer indexer)
    {
        _indexer = indexer;
        _extensions = new HashSet<string>(config.Extensions, StringComparer.OrdinalIgnoreCase);
        _timer = new Timer(_ => _ = FlushAsync());

        foreach (var source in config.Sources.Where(s => Directory.Exists(s.Path)))
        {
            var w = new FileSystemWatcher(source.Path)
            {
                IncludeSubdirectories = source.Recursive,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                InternalBufferSize = 64 * 1024,
            };
            w.Created += (_, e) => Queue(e.FullPath, source);
            w.Changed += (_, e) => Queue(e.FullPath, source);
            w.Renamed += (_, e) => Queue(e.FullPath, source);
            w.EnableRaisingEvents = true;
            _watchers.Add(w);
        }
    }

    void Queue(string path, Source source)
    {
        if (!_extensions.Contains(Path.GetExtension(path))) return;
        _pending[path] = source;
        _timer.Change(Debounce, Timeout.InfiniteTimeSpan);
    }

    async Task FlushAsync()
    {
        if (Interlocked.Exchange(ref _flushing, 1) == 1) return;
        try
        {
            var batch = _pending.Keys.ToList()
                .Select(p => _pending.TryRemove(p, out var s) ? (p, s) : default)
                .Where(x => x.s is not null)
                .Select(x => (x.p, x.s!))
                .ToList();
            var (indexed, _) = await _indexer.IndexFilesAsync(batch);
            if (indexed > 0) Indexed?.Invoke(indexed);
        }
        catch
        {
            // A file still being written fails to decode; it'll be picked up on the next Changed event or full scan.
        }
        finally
        {
            Interlocked.Exchange(ref _flushing, 0);
            if (!_pending.IsEmpty) _timer.Change(Debounce, Timeout.InfiniteTimeSpan);
        }
    }

    public void Dispose()
    {
        foreach (var w in _watchers) w.Dispose();
        _timer.Dispose();
    }
}
