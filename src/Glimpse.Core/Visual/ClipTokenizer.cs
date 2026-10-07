using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Glimpse.Core.Visual;

/// <summary>
/// OpenAI CLIP's byte-level BPE tokenizer (vocab.json + merges.txt), producing the fixed
/// 77-token input the text encoder expects: [SOT] tokens… [EOT] padded with EOT.
/// </summary>
public sealed partial class ClipTokenizer
{
    public const int ContextLength = 77;
    const string EndOfWord = "</w>";

    readonly Dictionary<string, int> _vocab;
    readonly Dictionary<(string, string), int> _ranks = [];
    readonly ConcurrentDictionary<string, string[]> _cache = new();
    readonly string[] _byteToChar = BuildByteMap();
    readonly int _sot, _eot;

    public ClipTokenizer(string vocabPath, string mergesPath)
    {
        _vocab = JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(vocabPath))!;
        _sot = _vocab["<|startoftext|>"];
        _eot = _vocab["<|endoftext|>"];

        var rank = 0;
        foreach (var line in File.ReadLines(mergesPath))
        {
            if (line.StartsWith("#version") || string.IsNullOrWhiteSpace(line)) continue;
            var parts = line.Split(' ');
            if (parts.Length == 2) _ranks.TryAdd((parts[0], parts[1]), rank++);
        }
    }

    /// <summary>Token ids, truncated and padded to <see cref="ContextLength"/>.</summary>
    public long[] Encode(string text)
    {
        var ids = new List<long>(ContextLength) { _sot };
        var cleaned = WhitespaceRegex().Replace(text, " ").Trim().ToLowerInvariant();

        foreach (Match m in TokenRegex().Matches(cleaned))
        {
            var bytes = Encoding.UTF8.GetBytes(m.Value);
            var token = string.Concat(bytes.Select(b => _byteToChar[b]));
            foreach (var piece in Bpe(token))
                if (_vocab.TryGetValue(piece, out var id)) ids.Add(id);
        }

        if (ids.Count > ContextLength - 1) ids.RemoveRange(ContextLength - 1, ids.Count - (ContextLength - 1));
        ids.Add(_eot);
        while (ids.Count < ContextLength) ids.Add(_eot); // pad with EOT, as the HF CLIP tokenizer does
        return ids.ToArray();
    }

    string[] Bpe(string token)
    {
        if (_cache.TryGetValue(token, out var cached)) return cached;

        // Start from characters, with the end-of-word marker glued to the last one.
        var word = token.Select(c => c.ToString()).ToList();
        word[^1] += EndOfWord;

        while (word.Count > 1)
        {
            var best = -1;
            var bestRank = int.MaxValue;
            for (var i = 0; i < word.Count - 1; i++)
            {
                if (_ranks.TryGetValue((word[i], word[i + 1]), out var r) && r < bestRank)
                {
                    bestRank = r;
                    best = i;
                }
            }
            if (best < 0) break;

            // Merge every occurrence of the best pair, left to right.
            var (a, b) = (word[best], word[best + 1]);
            var merged = new List<string>(word.Count);
            for (var i = 0; i < word.Count; i++)
            {
                if (i < word.Count - 1 && word[i] == a && word[i + 1] == b)
                {
                    merged.Add(a + b);
                    i++;
                }
                else merged.Add(word[i]);
            }
            word = merged;
        }

        var result = word.ToArray();
        _cache[token] = result;
        return result;
    }

    /// <summary>GPT-2's reversible byte → printable-unicode mapping.</summary>
    static string[] BuildByteMap()
    {
        var printable = Enumerable.Range('!', '~' - '!' + 1)
            .Concat(Enumerable.Range('¡', '¬' - '¡' + 1))
            .Concat(Enumerable.Range('®', 'ÿ' - '®' + 1))
            .ToHashSet();
        var map = new string[256];
        var next = 256;
        for (var b = 0; b < 256; b++)
            map[b] = ((char)(printable.Contains(b) ? b : next++)).ToString();
        return map;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"<\|startoftext\|>|<\|endoftext\|>|'s|'t|'re|'ve|'m|'ll|'d|[\p{L}]+|[\p{N}]|[^\s\p{L}\p{N}]+", RegexOptions.IgnoreCase)]
    private static partial Regex TokenRegex();
}
