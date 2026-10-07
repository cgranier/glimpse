using System.Diagnostics;
using System.Threading.Channels;
using Glimpse.Core.Index;

namespace Glimpse.Core.Visual;

public sealed record EmbedReport(int Embedded, int Failed, TimeSpan Elapsed);

/// <summary>
/// Second indexing pass: CLIP-embeds every image that has no embedding yet. CPU workers decode and
/// preprocess in parallel; one consumer runs the model on batches (the GPU likes batches).
/// </summary>
public sealed class EmbeddingIndexer(GlimpseConfig config, ImageIndex index, ClipModel model)
{
    const int BatchSize = 16;
    readonly SemaphoreSlim _runLock = new(1, 1);

    public async Task<EmbedReport> RunAsync(IProgress<IndexProgress>? progress = null, CancellationToken ct = default)
    {
        await _runLock.WaitAsync(ct);
        try
        {
            return await RunCoreAsync(progress, ct);
        }
        finally
        {
            _runLock.Release();
        }
    }

    async Task<EmbedReport> RunCoreAsync(IProgress<IndexProgress>? progress, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var todo = index.GetImagesMissingEmbedding(model.Name);
        if (todo.Count == 0) return new EmbedReport(0, 0, sw.Elapsed);

        var prepared = Channel.CreateBounded<(long Id, float[]? Pixels)>(BatchSize * 4);
        int done = 0, failed = 0;

        var consumer = Task.Run(() =>
        {
            var batch = new List<(long Id, float[] Pixels)>(BatchSize);
            var failures = new List<(long, float[]?)>();

            void Flush()
            {
                var results = new List<(long, float[]?)>(failures);
                if (batch.Count > 0)
                {
                    var vectors = model.EmbedImages(batch.Select(b => b.Pixels).ToList());
                    results.AddRange(batch.Select((b, i) => (b.Id, (float[]?)vectors[i])));
                }
                if (results.Count == 0) return;
                index.SaveEmbeddings(model.Name, results);
                done += results.Count;
                progress?.Report(new IndexProgress("visual", done, todo.Count));
                batch.Clear();
                failures.Clear();
            }

            while (prepared.Reader.WaitToReadAsync(CancellationToken.None).AsTask().Result)
            {
                while (prepared.Reader.TryRead(out var item))
                {
                    if (item.Pixels is null)
                    {
                        failures.Add((item.Id, null));
                        failed++;
                    }
                    else batch.Add((item.Id, item.Pixels));
                    if (batch.Count >= BatchSize) Flush();
                }
            }
            Flush();
        }, CancellationToken.None);

        try
        {
            await Parallel.ForEachAsync(todo, new ParallelOptions { MaxDegreeOfParallelism = config.Workers, CancellationToken = ct },
                async (item, token) =>
                {
                    float[]? pixels = null;
                    try
                    {
                        pixels = await ClipModel.PreprocessAsync(item.Path, token);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        // Missing file or a format Windows can't decode; recorded as failed.
                    }
                    await prepared.Writer.WriteAsync((item.Id, pixels), token);
                });
        }
        finally
        {
            prepared.Writer.Complete();
            await consumer;
        }

        return new EmbedReport(done - failed, failed, sw.Elapsed);
    }
}
