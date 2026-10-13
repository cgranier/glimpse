using Glimpse.Core.Index;
using Glimpse.Core.Meaning;

namespace Glimpse.Core.Visual;

/// <summary>
/// One entry point for every kind of search:
/// <list type="bullet">
/// <item><c>moca network</c> — text matches first (exact), then loose text, "related text" (by meaning) and
/// "looks like" (visual) matches, interleaved.</item>
/// <item><c>~network diagram</c> — visual only.</item>
/// <item><c>like:1234</c> — images that look like image 1234 ("more like this").</item>
/// </list>
/// Filters (<c>in:</c>, <c>after:</c>, <c>before:</c>) apply to all of them.
/// </summary>
public sealed class SearchEngine(ImageIndex index, ClipModel? clip, MeaningModel? meaning = null)
{
    /// <summary>Below this cosine similarity a text→image match is noise for CLIP B/16.</summary>
    public float MinTextScore { get; set; } = 0.20f;
    /// <summary>Visual matches more than this far below the best one are dropped.</summary>
    public float ScoreWindow { get; set; } = 0.07f;
    public float MinImageScore { get; set; } = 0.70f;
    /// <summary>Below this cosine similarity a query/text meaning match is noise for EmbeddingGemma 2.</summary>
    public float MinMeaningScore { get; set; } = 0.70f;
    /// <summary>Meaning matches more than this far below the best one are dropped.</summary>
    public float MeaningWindow { get; set; } = 0.03f;

    EmbeddingSet? _set, _meaningSet;
    readonly Lock _setGate = new();

    public bool VisualAvailable => clip is not null;
    public bool MeaningAvailable => meaning is not null;

    /// <summary>Call after embeddings are written or images removed; the next search reloads them.</summary>
    public void InvalidateEmbeddings()
    {
        lock (_setGate) (_set, _meaningSet) = (null, null);
    }

    public List<SearchHit> Search(string query, int limit = 120) => DropMissing(SearchCore(query, limit));

    /// <summary>
    /// Self-healing: files renamed or deleted while Glimpse wasn't watching (or missed by the watcher)
    /// leave stale entries. Drop them from the results and the index, so nobody gets an image that
    /// can't open. Only when the file's drive is present: an unplugged drive or offline share keeps its index.
    /// </summary>
    List<SearchHit> DropMissing(List<SearchHit> hits)
    {
        var missing = hits.Where(h => !File.Exists(h.Path) && Path.GetPathRoot(h.Path) is { } root && Directory.Exists(root)).ToList();
        if (missing.Count == 0) return hits;

        // Forget them in the background: a write may have to wait for the indexer's write lock, and this
        // search (and anything else on this connection) shouldn't wait with it.
        var ids = missing.Select(m => m.Id).ToList();
        _ = Task.Run(() =>
        {
            try
            {
                index.RemoveMany(ids);
                InvalidateEmbeddings();
            }
            catch
            {
                // Not fatal: they're hidden from results meanwhile, and the next full scan removes them.
            }
        });
        var gone = ids.ToHashSet();
        return hits.Where(h => !gone.Contains(h.Id)).ToList();
    }

    List<SearchHit> SearchCore(string query, int limit)
    {
        var text = query.TrimStart();
        var visualOnly = text.StartsWith('~');
        if (visualOnly) text = text[1..];
        var q = SearchQuery.Parse(text);

        if (q.Like is long likeId) return Similar(likeId, q, limit);

        var textHits = visualOnly ? [] : index.Search(q, limit);
        var useMeaning = meaning is not null && !visualOnly;
        if (q.Terms.Count == 0 || (clip is null && !useMeaning)) return textHits;

        var words = string.Join(' ', q.Terms);
        List<(long Id, float Score)> visual = [], related = [];
        Parallel.Invoke(
            () => { if (clip is not null) visual = Rank(VisualSet(), clip.EmbedText(words), q, limit, [], MinTextScore, ScoreWindow); },
            () => { if (useMeaning) related = Rank(MeaningSet(), meaning!.EmbedQuery(words), q, limit, [], MinMeaningScore, MeaningWindow); });
        if (visualOnly) return index.GetHits(visual);

        // "Exact" text hits contain what was typed as written: any hit of a one-word query ("moca"), or the
        // words together as a phrase. Hits with the words scattered across a busy screenshot are "loose".
        var exact = q.Terms.Count <= 1
            ? textHits.Select(h => h.Id).ToHashSet()
            : index.Search(q with { Terms = [string.Join(' ', q.Terms)] }, limit).Select(h => h.Id).ToHashSet();
        return Fuse(textHits, exact, visual, related, limit);
    }

    /// <summary>
    /// Exact text hits first, as typed; then loose text hits, meaning matches and visual matches interleaved by
    /// reciprocal rank fusion (each list adds 1/(k + rank), so an image found more than one way rises).
    /// Similarity scores alone can't tell signal from noise ("moca" noise scores like a real match),
    /// which is why exact text always wins.
    /// </summary>
    List<SearchHit> Fuse(List<SearchHit> textHits, HashSet<long> exact,
        List<(long Id, float Score)> visual, List<(long Id, float Score)> related, int limit)
    {
        const double k = 10;
        var fused = new Dictionary<long, (double Score, int TextRank)>();

        var loose = textHits.Where(h => !exact.Contains(h.Id)).ToList();
        for (var i = 0; i < loose.Count; i++)
            fused[loose[i].Id] = (1 / (k + i), i);
        foreach (var ranking in new[] { related, visual })
        {
            var rank = 0;
            foreach (var v in ranking.Where(v => !exact.Contains(v.Id)))
            {
                var (score, textRank) = fused.GetValueOrDefault(v.Id, (0, int.MaxValue));
                fused[v.Id] = (score + 1 / (k + rank++), textRank);
            }
        }

        var order = textHits.Where(h => exact.Contains(h.Id)).Select(h => h.Id)
            .Concat(fused.OrderByDescending(f => f.Value.Score).ThenBy(f => f.Value.TextRank).Select(f => f.Key))
            .Take(limit).ToList();
        var byId = textHits.ToDictionary(h => h.Id);
        var visualScores = visual.ToDictionary(v => v.Id, v => v.Score);
        var meaningScores = related.ToDictionary(v => v.Id, v => v.Score);
        foreach (var hit in index.GetHits(order.Where(id => !byId.ContainsKey(id)).Select(id => (id, 0f)).ToList()))
            byId[hit.Id] = hit;

        return order.Where(byId.ContainsKey)
            .Select(id => byId[id] with
            {
                VisualScore = visualScores.TryGetValue(id, out var v) ? v : null,
                MeaningScore = meaningScores.TryGetValue(id, out var m) ? m : null,
            })
            .ToList();
    }

    List<SearchHit> Similar(long id, SearchQuery q, int limit)
    {
        var set = VisualSet();
        if (set is null) return [];
        var i = Array.IndexOf(set.Ids, id);
        if (i < 0) return [];

        var vector = set.Vector(i).ToArray();
        var ranked = Rank(set, vector, q, limit, new HashSet<long> { id }, MinImageScore, window: 1f);
        // Lead with the image itself so it's obvious what we're comparing against.
        return [.. index.GetHits([(id, 1f)]), .. index.GetHits(ranked)];
    }

    static List<(long Id, float Score)> Rank(EmbeddingSet? set, float[] query, SearchQuery q, int limit, HashSet<long> exclude, float minScore, float window)
    {
        if (set is null || set.Count == 0 || limit <= 0) return [];

        long? after = q.After?.Ticks, before = q.Before?.Ticks;
        var scores = new List<(long Id, float Score)>(set.Count);
        for (var i = 0; i < set.Count; i++)
        {
            if (exclude.Contains(set.Ids[i])) continue;
            if (q.Source is not null && !set.Sources[i].StartsWith(q.Source, StringComparison.OrdinalIgnoreCase)) continue;
            if (after is not null && set.Mtimes[i] < after) continue;
            if (before is not null && set.Mtimes[i] >= before) continue;

            var dot = Dot(set.Vector(i), query);
            if (dot >= minScore) scores.Add((set.Ids[i], dot));
        }
        if (scores.Count == 0) return [];

        scores.Sort((a, b) => b.Score.CompareTo(a.Score));
        var floor = scores[0].Score - window;
        return scores.TakeWhile(s => s.Score >= floor).Take(limit).ToList();
    }

    /// <summary>SIMD dot product (vectors are unit length, so this is cosine similarity).</summary>
    static float Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        var sum = System.Numerics.Vector<float>.Zero;
        var width = System.Numerics.Vector<float>.Count;
        var i = 0;
        for (; i <= a.Length - width; i += width)
            sum += new System.Numerics.Vector<float>(a[i..]) * new System.Numerics.Vector<float>(b[i..]);
        var dot = System.Numerics.Vector.Sum(sum);
        for (; i < a.Length; i++) dot += a[i] * b[i];
        return dot;
    }

    EmbeddingSet? VisualSet()
    {
        if (clip is null) return null;
        lock (_setGate) return _set ??= index.LoadEmbeddings(clip.Name);
    }

    EmbeddingSet? MeaningSet()
    {
        if (meaning is null) return null;
        lock (_setGate) return _meaningSet ??= index.LoadEmbeddings(meaning.Name, VectorKind.Meaning);
    }
}
