using System.ComponentModel;
using System.Runtime.CompilerServices;
using Glimpse.Core.Index;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Glimpse.App;

/// <param name="visualSearch">Visual-only (Ctrl+T, ~) or more like this (Ctrl+M): similarity is the point.</param>
public sealed class ResultItem(SearchHit hit, bool visualSearch) : INotifyPropertyChanged
{
    public SearchHit Hit { get; } = hit;
    public string Snippet { get; } = hit.Snippet.ReplaceLineEndings(" ");
    public string Meta { get; } = $"{hit.Source} · {hit.Modified:yyyy-MM-dd HH:mm} · {Path.GetFileName(hit.Path)}";

    public string Badge { get; } = BadgeFor(hit, visualSearch);

    /// <summary>
    /// Visual searches show the similarity score on every result. In a normal search, only results found
    /// purely by how they look get a plain "≈ looks like", explaining why they're there without your words;
    /// text matches get nothing.
    /// </summary>
    static string BadgeFor(SearchHit hit, bool visualSearch) => hit.VisualScore switch
    {
        float s when visualSearch => $"≈ {s:F2}",
        not null when !hit.MatchedText => "≈ looks like",
        _ => "",
    };

    BitmapImage? _thumbnail;
    public BitmapImage? Thumbnail
    {
        get => _thumbnail;
        set { _thumbnail = value; OnPropertyChanged(); }
    }

    public bool ThumbnailRequested { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;
    void OnPropertyChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}
