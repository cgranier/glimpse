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

    /// <summary>Images were renamed, moved or deleted (no new text to read, but results are stale).</summary>
    public event Action? Changed;

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
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                InternalBufferSize = 64 * 1024,
            };
            w.Created += (_, e) => Queue(e.FullPath, source);
            w.Changed += (_, e) => Queue(e.FullPath, source);
            w.Renamed += (_, e) => OnRenamed(e.OldFullPath, e.FullPath, source);
            w.Deleted += (_, e) => OnDeleted(e.FullPath);
            w.EnableRaisingEvents = true;
            _watchers.Add(w);
        }
    }

    void OnRenamed(string oldPath, string newPath, Source source)
    {
        try
        {
            var isFolder = Directory.Exists(newPath);
            var wasImage = _extensions.Contains(Path.GetExtension(oldPath));
            var isImage = _extensions.Contains(Path.GetExtension(newPath));
            // Downloads folders see constant non-image renames (browser .crdownload/.part → final name).
            if (!isFolder && !wasImage && !isImage) return;
            if (!isFolder && !isImage)
            {
                // Renamed to something that isn't an image (photo.png → photo.png.bak).
                if (_indexer.Forget(oldPath) > 0) Changed?.Invoke();
                return;
            }
            if (_indexer.Move(oldPath, newPath, source) > 0)
            {
                Changed?.Invoke();
                return;
            }
        }
        catch (Exception ex)
        {
            Diagnostics.Report(ex); // fall through: reading it fresh is always correct, just slower
        }
        // Not indexed under its old name (e.g. ShareX writes a temp file, then renames it to .png).
        Queue(newPath, source);
    }

    void OnDeleted(string path)
    {
        // Only images, or something without an extension that may have been a folder; temp files and
        // finished downloads come and go constantly and were never indexed.
        var ext = Path.GetExtension(path);
        if (ext.Length > 0 && !_extensions.Contains(ext)) return;
        try
        {
            _pending.TryRemove(path, out _);
            if (_indexer.Forget(path) > 0) Changed?.Invoke();
        }
        catch (Exception ex)
        {
            Diagnostics.Report(ex); // the next full scan removes it anyway
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
        catch (Exception ex)
        {
            // Unreadable files don't throw (they're recorded as failed), so this is a real problem: say so.
            Diagnostics.Report(ex);
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
