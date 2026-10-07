using Glimpse.Core.Index;

namespace Glimpse.Core.Visual;

/// <summary>
/// One entry point for both kinds of search:
/// <list type="bullet">
/// <item><c>moca network</c> — text matches first (exact), then visual "looks like" matches.</item>
/// <item><c>~network diagram</c> — visual only.</item>
/// <item><c>like:1234</c> — images that look like image 1234 ("more like this").</item>
/// </list>
/// Filters (<c>in:</c>, <c>after:</c>, <c>before:</c>) apply to all of them.
/// </summary>
public sealed class SearchEngine(ImageIndex index, ClipModel? clip)
{
    /// <summary>Below this cosine similarity a text→image match is noise for CLIP B/16.</summary>
    public float MinTextScore { get; set; } = 0.20f;
    /// <summary>Visual matches more than this far below the best one are dropped.</summary>
    public float ScoreWindow { get; set; } = 0.07f;
    public float MinImageScore { get; set; } = 0.70f;

    EmbeddingSet? _set;
    readonly Lock _setGate = new();

    public bool VisualAvailable => clip is not null;

    /// <summary>Call after new embeddings are written; the next visual search reloads them.</summary>
    public void InvalidateVisual()
    {
        lock (_setGate) _set = null;
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
        try
        {
            index.RemoveMany(missing.Select(m => m.Id));
            InvalidateVisual();
        }
        catch
        {
            // Not fatal: they're still hidden from these results, and the next full scan removes them.
        }
        var gone = missing.Select(m => m.Id).ToHashSet();
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
        if (clip is null || q.Terms.Count == 0) return textHits;

        var queryVector = clip.EmbedText(string.Join(' ', q.Terms));
        var visual = Rank(queryVector, q, limit, [], MinTextScore, ScoreWindow);
        if (visualOnly) return index.GetHits(visual);

        // "Exact" text hits contain what was typed as written: any hit of a one-word query ("moca"), or the
        // words together as a phrase. Hits with the words scattered across a busy screenshot are "loose".
        var exact = q.Terms.Count <= 1
            ? textHits.Select(h => h.Id).ToHashSet()
            : index.Search(q with { Terms = [string.Join(' ', q.Terms)] }, limit).Select(h => h.Id).ToHashSet();
        return Fuse(textHits, exact, visual, limit);
    }

    /// <summary>
    /// Exact text hits first, as typed; then loose text hits and visual matches interleaved by
    /// reciprocal rank fusion (each list adds 1/(k + rank), so an image found both ways rises).
    /// CLIP scores alone can't tell signal from noise ("moca" noise scores like a real match),
    /// which is why exact text always wins.
    /// </summary>
    List<SearchHit> Fuse(List<SearchHit> textHits, HashSet<long> exact, List<(long Id, float Score)> visual, int limit)
    {
        const double k = 10;
        var fused = new Dictionary<long, (double Score, int TextRank)>();

        var loose = textHits.Where(h => !exact.Contains(h.Id)).ToList();
        for (var i = 0; i < loose.Count; i++)
            fused[loose[i].Id] = (1 / (k + i), i);
        var visualRank = 0;
        foreach (var v in visual.Where(v => !exact.Contains(v.Id)))
        {
            var (score, textRank) = fused.GetValueOrDefault(v.Id, (0, int.MaxValue));
            fused[v.Id] = (score + 1 / (k + visualRank++), textRank);
        }

        var order = textHits.Where(h => exact.Contains(h.Id)).Select(h => h.Id)
            .Concat(fused.OrderByDescending(f => f.Value.Score).ThenBy(f => f.Value.TextRank).Select(f => f.Key))
            .Take(limit).ToList();
        var byId = textHits.ToDictionary(h => h.Id);
        var visualScores = visual.ToDictionary(v => v.Id, v => v.Score);
        foreach (var hit in index.GetHits(visual.Where(v => !byId.ContainsKey(v.Id)).ToList()))
            byId[hit.Id] = hit;

        return order.Where(byId.ContainsKey)
            .Select(id => visualScores.TryGetValue(id, out var s) ? byId[id] with { VisualScore = s } : byId[id])
            .ToList();
    }

    List<SearchHit> Similar(long id, SearchQuery q, int limit)
    {
        var set = Embeddings();
        if (set is null) return [];
        var i = Array.IndexOf(set.Ids, id);
        if (i < 0) return [];

        var vector = set.Vector(i).ToArray();
        var ranked = Rank(vector, q, limit, new HashSet<long> { id }, MinImageScore, window: 1f);
        // Lead with the image itself so it's obvious what we're comparing against.
        return [.. index.GetHits([(id, 1f)]), .. index.GetHits(ranked)];
    }

    List<(long Id, float Score)> Rank(float[] query, SearchQuery q, int limit, HashSet<long> exclude, float minScore, float window)
    {
        var set = Embeddings();
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

    EmbeddingSet? Embeddings()
    {
        if (clip is null) return null;
        lock (_setGate) return _set ??= index.LoadEmbeddings(clip.Name);
    }
}
