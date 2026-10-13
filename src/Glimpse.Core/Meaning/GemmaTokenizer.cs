using System.Text;
using System.Text.Json;

namespace Glimpse.Core.Meaning;

/// <summary>
/// The Gemma BPE tokenizer, read from a Hugging Face tokenizer.json (EmbeddingGemma 2).
/// Matches its definition: spaces become '▁'; each character is a starting symbol, or its UTF-8 bytes as
/// &lt;0xXX&gt; tokens when it's not in the vocabulary (byte fallback); adjacent symbols are merged by rank;
/// the result is wrapped as &lt;bos&gt; … &lt;eos&gt;.
/// </summary>
public sealed class GemmaTokenizer
{
    public const int Bos = 2, Eos = 1, Pad = 0;

    readonly Dictionary<string, int> _vocab;
    readonly Dictionary<long, (int Rank, int Merged)> _merges;
    readonly int[] _byteTokens = new int[256];

    public GemmaTokenizer(string tokenizerJsonPath)
    {
        using var stream = File.OpenRead(tokenizerJsonPath);
        using var doc = JsonDocument.Parse(stream);
        var model = doc.RootElement.GetProperty("model");

        var vocab = model.GetProperty("vocab");
        _vocab = new Dictionary<string, int>(300_000, StringComparer.Ordinal);
        foreach (var entry in vocab.EnumerateObject()) _vocab[entry.Name] = entry.Value.GetInt32();

        for (var b = 0; b < 256; b++) _byteTokens[b] = _vocab.TryGetValue($"<0x{b:X2}>", out var id) ? id : 3; // 3 = <unk>

        var merges = model.GetProperty("merges");
        _merges = new Dictionary<long, (int, int)>(merges.GetArrayLength());
        var rank = 0;
        foreach (var merge in merges.EnumerateArray())
        {
            string left, right;
            if (merge.ValueKind == JsonValueKind.Array)
            {
                left = merge[0].GetString()!;
                right = merge[1].GetString()!;
            }
            else
            {
                var parts = merge.GetString()!.Split(' ', 2);
                (left, right) = (parts[0], parts[1]);
            }
            if (_vocab.TryGetValue(left, out var l) && _vocab.TryGetValue(right, out var r) && _vocab.TryGetValue(left + right, out var m))
                _merges.TryAdd(Key(l, r), (rank, m));
            rank++;
        }
    }

    static long Key(int left, int right) => ((long)left << 32) | (uint)right;

    /// <summary>Token ids for <paramref name="text"/>, at most <paramref name="maxTokens"/> including &lt;bos&gt;/&lt;eos&gt;.</summary>
    public int[] Encode(string text, int maxTokens = 512)
    {
        // Starting symbols: one per character, or its bytes when the character isn't in the vocabulary.
        var ids = new List<int>(text.Length);
        foreach (var rune in text.Replace(' ', '▁').EnumerateRunes())
        {
            if (_vocab.TryGetValue(rune.ToString(), out var id))
            {
                ids.Add(id);
                continue;
            }
            Span<byte> utf8 = stackalloc byte[4];
            var n = rune.EncodeToUtf8(utf8);
            for (var i = 0; i < n; i++) ids.Add(_byteTokens[utf8[i]]);
        }

        var merged = MergeAll(ids);
        var body = Math.Min(merged.Count, maxTokens - 2);
        var result = new int[body + 2];
        result[0] = Bos;
        for (var i = 0; i < body; i++) result[i + 1] = merged[i];
        result[^1] = Eos;
        return result;
    }

    /// <summary>
    /// Repeatedly merges the adjacent pair with the lowest rank (ties: leftmost), as Hugging Face's BPE does.
    /// A linked list plus a priority queue keeps long OCR texts fast (O(n log n), not O(n²)).
    /// </summary>
    List<int> MergeAll(List<int> symbols)
    {
        var n = symbols.Count;
        if (n < 2) return symbols;
        var id = symbols.ToArray();
        var prev = new int[n];
        var next = new int[n];
        var alive = new bool[n];
        for (var i = 0; i < n; i++) { prev[i] = i - 1; next[i] = i + 1 < n ? i + 1 : -1; alive[i] = true; }

        // Entries remember the pair they were made for; stale ones (a side already merged) are skipped.
        var queue = new PriorityQueue<(int Left, int LeftId, int RightId), (int Rank, int Pos)>();
        void Consider(int left)
        {
            if (left < 0) return;
            var right = next[left];
            if (right < 0) return;
            if (_merges.TryGetValue(Key(id[left], id[right]), out var m))
                queue.Enqueue((left, id[left], id[right]), (m.Rank, left));
        }
        for (var i = 0; i < n - 1; i++) Consider(i);

        while (queue.TryDequeue(out var e, out _))
        {
            var right = next[e.Left];
            if (!alive[e.Left] || right < 0 || id[e.Left] != e.LeftId || id[right] != e.RightId) continue;
            id[e.Left] = _merges[Key(e.LeftId, e.RightId)].Merged;
            alive[right] = false;
            next[e.Left] = next[right];
            if (next[right] >= 0) prev[next[right]] = e.Left;
            Consider(prev[e.Left]);
            Consider(e.Left);
        }

        var result = new List<int>(n);
        for (var i = 0; i >= 0; i = next[i]) result.Add(id[i]);
        return result;
    }
}
