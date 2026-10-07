using System.ComponentModel;
using System.Runtime.CompilerServices;
using Glimpse.Core.Index;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Glimpse.App;

public sealed class ResultItem(SearchHit hit) : INotifyPropertyChanged
{
    public SearchHit Hit { get; } = hit;
    public string Snippet { get; } = hit.Snippet.ReplaceLineEndings(" ");
    public string Meta { get; } = $"{hit.Source} · {hit.Modified:yyyy-MM-dd HH:mm} · {Path.GetFileName(hit.Path)}";

    /// <summary>Marks visual matches, which may not contain any of the typed words.</summary>
    public string Badge { get; } = hit.VisualScore is float s ? $"≈ looks like  ·  {s:F2}" : "";

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
