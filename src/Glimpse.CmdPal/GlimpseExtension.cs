using System.Runtime.InteropServices;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace Glimpse.CmdPal;

// Must match com:Class Id and CreateInstance ClassId in Package.appxmanifest.
[Guid("1D69E6F6-7650-49BE-B14F-DEA6D8620F4F")]
public sealed partial class GlimpseExtension(ManualResetEvent disposed) : IExtension, IDisposable
{
    readonly GlimpseCommandsProvider _provider = new();

    public object? GetProvider(ProviderType providerType) => providerType switch
    {
        ProviderType.Commands => _provider,
        _ => null,
    };

    public void Dispose() => disposed.Set();
}

public sealed partial class GlimpseCommandsProvider : CommandProvider
{
    readonly ICommandItem[] _commands;
    readonly IFallbackCommandItem[] _fallbacks;

    public GlimpseCommandsProvider()
    {
        DisplayName = "Glimpse";
        Id = "Glimpse";
        Icon = Glimpse.Icon;

        _commands =
        [
            new CommandItem(new SearchPage(id: "Glimpse.Search"))
            {
                Title = "Glimpse",
                Subtitle = "Find screenshots and images by their text or how they look",
            },
        ];
        _fallbacks = [new SearchFallback()];
    }

    public override ICommandItem[] TopLevelCommands() => _commands;

    public override IFallbackCommandItem[] FallbackCommands() => _fallbacks;
}

/// <summary>
/// Shown under whatever is typed in the palette's main box: "Search images for 'moca'" opens the
/// Glimpse page with that query already filled in.
/// </summary>
internal sealed partial class SearchFallback : FallbackCommandItem
{
    // One page, re-armed per keystroke, with a fixed id: the palette stores fallback settings by
    // command id, and a fresh anonymous command each run showed up as a new "Glimpse" entry every time.
    readonly SearchPage _page;

    public SearchFallback()
        : this(new SearchPage(id: "Glimpse.Search.Fallback"))
    {
    }

    SearchFallback(SearchPage page)
        : base(page, "Search images", "Glimpse.Search.Fallback")
    {
        _page = page;
        Icon = Glimpse.Icon;
        Title = "";
    }

    public override void UpdateQuery(string query)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Trim().Length < 3)
        {
            Title = ""; // empty title hides the fallback
            return;
        }
        _page.Reset(query.Trim());
        Title = $"Search images for “{query.Trim()}”";
        Subtitle = "Glimpse";
    }
}

internal static class Glimpse
{
    public static IconInfo Icon { get; } = IconHelpers.FromRelativePath("Assets\\Glimpse.png");
}
