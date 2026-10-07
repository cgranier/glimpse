namespace Glimpse.Core.Ocr;

/// <summary>A recognized word and its box in source-image pixels (used to highlight matches).</summary>
public sealed record OcrWord(string Text, double X, double Y, double Width, double Height);

public sealed record OcrLine(string Text, IReadOnlyList<OcrWord> Words);

public sealed record OcrPage(int Width, int Height, IReadOnlyList<OcrLine> Lines)
{
    public string FullText => string.Join('\n', Lines.Select(l => l.Text));
}
