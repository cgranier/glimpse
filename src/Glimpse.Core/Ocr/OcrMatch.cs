namespace Glimpse.Core.Ocr;

/// <summary>Which OCR'd words to outline for a query: exactly the ones the search matched.</summary>
public static class OcrMatch
{
    /// <summary>
    /// Finds each term (case-insensitive, anywhere in a word, a phrase may span words) in each line's
    /// text, as the index sees it: words joined by single spaces. Returns the words those matches cover.
    /// So "network" outlines "network" and "Networking", but not a separate "two" or "work".
    /// </summary>
    public static IEnumerable<OcrWord> Find(IReadOnlyList<OcrLine> lines, IReadOnlyList<string> terms)
    {
        var needles = terms.Where(t => t.Length >= 2).ToList();
        if (needles.Count == 0) yield break;

        foreach (var line in lines)
        {
            // Rebuild the line from its words so character positions map back to words.
            var text = new System.Text.StringBuilder();
            var spans = new List<(int Start, int End, OcrWord Word)>(line.Words.Count);
            foreach (var word in line.Words)
            {
                if (text.Length > 0) text.Append(' ');
                spans.Add((text.Length, text.Length + word.Text.Length, word));
                text.Append(word.Text);
            }
            var haystack = text.ToString();

            var hit = new bool[spans.Count];
            foreach (var needle in needles)
            {
                for (var at = haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase); at >= 0;
                     at = haystack.IndexOf(needle, at + 1, StringComparison.OrdinalIgnoreCase))
                {
                    var end = at + needle.Length;
                    for (var i = 0; i < spans.Count; i++)
                        if (spans[i].Start < end && at < spans[i].End) hit[i] = true;
                }
            }
            for (var i = 0; i < spans.Count; i++)
                if (hit[i]) yield return spans[i].Word;
        }
    }
}
