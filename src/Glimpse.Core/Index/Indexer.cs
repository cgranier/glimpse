using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using Glimpse.Core.Ocr;

namespace Glimpse.Core.Index;

public sealed record IndexProgress(string Phase, int Done, int Total, string? Current = null);

public sealed record IndexReport(int Scanned, int Indexed, int Unchanged, int Removed, int Failed, int SkippedCloud, TimeSpan Elapsed);

/// <summary>
/// Walks configured sources, OCRs new/changed images in parallel, and writes results through a
/// single writer (SQLite prefers one). Unchanged files are skipped by size + mtime, so re-runs are cheap.
/// </summary>
public sealed class Indexer(GlimpseConfig config, ImageIndex index)
{
    // Attributes OneDrive uses for online-only placeholders. Opening these triggers a download.
    const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;
    const FileAttributes RecallOnOpen = (FileAttributes)0x00040000;

    static readonly string[] SkipDirs = [".obsidian", ".trash", ".git", "node_modules"];

    public async Task<IndexReport> RunAsync(IEnumerable<Source>? only = null, IProgress<IndexProgress>? progress = null, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var sources = (only ?? config.Sources).ToList();
        var extensions = new HashSet<string>(config.Extensions, StringComparer.OrdinalIgnoreCase);

        var work = new List<(FileInfo File, Source Source)>();
        int scanned = 0, unchanged = 0, removed = 0, skippedCloud = 0;
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var source in sources)
        {
            progress?.Report(new IndexProgress($"scanning {source.Name}", 0, 0));
            var known = index.GetFiles(source.Name);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var file in Enumerate(source, extensions))
            {
                // Nested sources: the first one listed owns the file.
                if (!claimed.Add(file.FullName)) continue;
                scanned++;
                seen.Add(file.FullName);

                if (!config.HydrateCloudFiles && IsCloudOnly(file))
                {
                    skippedCloud++;
                    continue;
                }
                if (known.TryGetValue(file.FullName, out var k) && k.Size == file.Length && k.MtimeTicks == file.LastWriteTimeUtc.Ticks)
                {
                    unchanged++;
                    continue;
                }
                work.Add((file, source));
            }

            // Files that vanished since the last run.
            var gone = known.Values.Where(k => !seen.Contains(k.Path) && !File.Exists(k.Path)).Select(k => k.Id).ToList();
            index.RemoveMany(gone);
            removed += gone.Count;
        }

        // Newest first: the screenshot you're hunting for is usually recent.
        work.Sort((a, b) => b.File.LastWriteTimeUtc.CompareTo(a.File.LastWriteTimeUtc));

        await _runLock.WaitAsync(ct);
        try
        {
            var (indexed, failed) = await OcrAndStoreAsync(work, progress, ct);
            return new IndexReport(scanned, indexed, unchanged, removed, failed, skippedCloud, sw.Elapsed);
        }
        finally
        {
            _runLock.Release();
        }
    }

    /// <summary>Index specific files (used by the folder watcher). Waits for a running full scan to finish.</summary>
    public async Task<(int Indexed, int Failed)> IndexFilesAsync(IEnumerable<(string Path, Source Source)> files, CancellationToken ct = default)
    {
        await _runLock.WaitAsync(ct);
        try
        {
            return await OcrAndStoreAsync(files.Select(f => (new FileInfo(f.Path), f.Source)).Where(f => f.Item1.Exists).ToList(), null, ct);
        }
        finally
        {
            _runLock.Release();
        }
    }

    /// <summary>A file or folder was renamed: keep its text and embeddings under the new path.</summary>
    public int Move(string oldPath, string newPath, Source source) => index.MovePath(oldPath, newPath, source.Name);

    /// <summary>A file or folder was deleted.</summary>
    public int Forget(string path) => index.RemovePath(path);

    // One OCR pass at a time, so the watcher and a full scan don't OCR the same new file twice.
    readonly SemaphoreSlim _runLock = new(1, 1);

    async Task<(int Indexed, int Failed)> OcrAndStoreAsync(List<(FileInfo File, Source Source)> work, IProgress<IndexProgress>? progress, CancellationToken ct)
    {
        if (work.Count == 0) return (0, 0);

        var results = Channel.CreateBounded<(FileInfo File, Source Source, OcrPage? Page, string? Error)>(64);
        var engines = new ConcurrentBag<WindowsOcr>();
        int done = 0, failed = 0;

        var writer = Task.Run(async () =>
        {
            const int batchSize = 25;
            var batch = new List<(FileInfo File, Source Source, OcrPage? Page, string? Error)>(batchSize);
            await foreach (var item in results.Reader.ReadAllAsync(CancellationToken.None))
            {
                batch.Add(item);
                if (batch.Count >= batchSize) Flush();
            }
            Flush();

            void Flush()
            {
                if (batch.Count == 0) return;
                index.UpsertBatch(batch.Select(b =>
                    new IndexEntry(b.File.FullName, b.Source.Name, b.File.Length, b.File.LastWriteTimeUtc, b.Page, b.Error)));
                batch.Clear();
            }
        });

        try
        {
            await Parallel.ForEachAsync(work, new ParallelOptions { MaxDegreeOfParallelism = config.Workers, CancellationToken = ct },
                async (item, token) =>
                {
                    var engine = engines.TryTake(out var e) ? e : new WindowsOcr();
                    OcrPage? page = null;
                    string? error = null;
                    try
                    {
                        page = await engine.RecognizeAsync(item.File.FullName, token);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        error = ex.Message;
                        Interlocked.Increment(ref failed);
                    }
                    finally
                    {
                        engines.Add(engine);
                    }
                    await results.Writer.WriteAsync((item.File, item.Source, page, error), token);
                    progress?.Report(new IndexProgress("ocr", Interlocked.Increment(ref done), work.Count, item.File.Name));
                });
        }
        finally
        {
            results.Writer.Complete();
            await writer;
        }

        return (done - failed, failed);
    }

    static IEnumerable<FileInfo> Enumerate(Source source, HashSet<string> extensions)
    {
        if (!Directory.Exists(source.Path)) yield break;

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.System | FileAttributes.Hidden,
        };

        var pending = new Stack<DirectoryInfo>([new DirectoryInfo(source.Path)]);
        while (pending.TryPop(out var dir))
        {
            foreach (var entry in dir.EnumerateFileSystemInfos("*", options))
            {
                if (entry is DirectoryInfo sub)
                {
                    if (source.Recursive && !SkipDirs.Contains(sub.Name, StringComparer.OrdinalIgnoreCase))
                        pending.Push(sub);
                }
                else if (entry is FileInfo file && extensions.Contains(file.Extension))
                {
                    yield return file;
                }
            }
        }
    }

    static bool IsCloudOnly(FileInfo file) =>
        (file.Attributes & (FileAttributes.Offline | RecallOnDataAccess | RecallOnOpen)) != 0;
}
