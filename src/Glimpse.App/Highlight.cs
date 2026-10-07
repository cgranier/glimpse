using Glimpse.Core.Index;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;

namespace Glimpse.App;

/// <summary>Attached property: renders an FTS snippet, bolding and accent-coloring the matched spans.</summary>
public static class Highlight
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(Highlight), new PropertyMetadata(null, OnTextChanged));

    public static string GetText(DependencyObject o) => (string)o.GetValue(TextProperty);
    public static void SetText(DependencyObject o, string value) => o.SetValue(TextProperty, value);

    static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock tb) return;
        tb.Inlines.Clear();
        var text = e.NewValue as string ?? "";
        var accent = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"];

        var inMatch = false;
        foreach (var part in text.Split(ImageIndex.MatchStart, ImageIndex.MatchEnd))
        {
            if (part.Length > 0)
                tb.Inlines.Add(inMatch
                    ? new Run { Text = part, FontWeight = FontWeights.SemiBold, Foreground = accent }
                    : new Run { Text = part });
            inMatch = !inMatch;
        }
        // Split alternates correctly only when markers pair up; FTS snippets always do.
    }
}
