using System.Diagnostics;
using Glimpse.Core.Index;
using Glimpse.Core.Visual;

namespace Glimpse.Core.Meaning;

/// <summary>
/// Third indexing pass, after text and visual: embeds the OCR text of every image that has some, so search can
/// match by meaning. Images without text are skipped. Runs on the CPU, one text at a time: with int8 compute,
/// batching doesn't speed it up (padding only adds work). ~0.3 s per image on 8 cores.
/// </summary>
public sealed class MeaningIndexer(ImageIndex index, MeaningModel model)
{
    const int SaveEvery = 16;
    const int ChunkSize = 256; // texts read from the index at a time
    readonly SemaphoreSlim _runLock = new(1, 1);
    readonly CancellationTokenSource _stop = new();

    /// <summary>
    /// Returns early, with what's done so far saved, when <see cref="Stop"/> is called. The first pass over a
    /// library can take half an hour, so switching the feature off mustn't wait for it.
    /// </summary>
    public async Task<EmbedReport> RunAsync(IProgress<IndexProgress>? progress = null, CancellationToken ct = default)
    {
        await _runLock.WaitAsync(ct);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stop.Token);
        try
        {
            return await Task.Run(() => RunCore(progress, linked.Token), CancellationToken.None);
        }
        finally
        {
            _runLock.Release();
        }
    }

    /// <summary>Ends the running pass (if any) and any future one: this indexer's model is being replaced.</summary>
    public void Stop() => _stop.Cancel();

    /// <summary>Completes once no pass is running (so the model can be disposed safely).</summary>
    public async Task WhenIdleAsync()
    {
        await _runLock.WaitAsync();
        _runLock.Release();
    }

    EmbedReport RunCore(IProgress<IndexProgress>? progress, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var todo = index.GetImagesMissingMeaning(model.Name);
        int done = 0, failed = 0;

        foreach (var chunk in todo.Chunk(ChunkSize))
        {
            if (ct.IsCancellationRequested) break;
            var texts = index.GetTexts(chunk);
            foreach (var group in chunk.Where(texts.ContainsKey).Chunk(SaveEvery)) // newest first
            {
                var results = new List<(long, float[]?)>(group.Length);
                foreach (var id in group)
                {
                    if (ct.IsCancellationRequested) break;
                    try
                    {
                        results.Add((id, model.EmbedDocuments([texts[id]])[0]));
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // If nothing has worked yet, the model itself is broken: stop without marking anything.
                        // Otherwise it's this text; recorded as failed (empty vector) so it isn't retried every run.
                        if (done - failed == 0) throw;
                        Diagnostics.Report(ex);
                        results.Add((id, null));
                        failed++;
                    }
                    done++;
                    progress?.Report(new IndexProgress("meaning", done, todo.Count));
                }
                if (results.Count > 0) index.SaveEmbeddings(model.Name, results, VectorKind.Meaning);
            }
        }
        ct.ThrowIfCancellationRequested(); // after saving what's done
        return new EmbedReport(done - failed, failed, sw.Elapsed);
    }
}
