using System.Diagnostics;
using Glimpse.Core.Ipc;
using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace Glimpse.CmdPal;

/// <summary>
/// Live search against the running Glimpse app. Every keystroke (debounced) is a pipe round trip;
/// the app answers in tens of milliseconds with cached thumbnails.
/// </summary>
internal sealed partial class SearchPage : DynamicListPage, IDisposable
{
    static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(120);
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    readonly ModeFilters _modes = new();
    CancellationTokenSource? _pending;
    IListItem[] _items = [];
    bool _started;

    /// <param name="id">
    /// Stable command id. Command Palette keys pins, aliases and fallback settings by it; leaving it
    /// empty makes the palette invent a new one every time the extension starts.
    /// </param>
    public SearchPage(string initialQuery = "", string id = "")
    {
        Id = id;
        Icon = Glimpse.Icon;
        Title = "Glimpse";
        Name = "Search images";
        PlaceholderText = "Text you remember, or describe what it looks like…   in:notes   after:2026-05";
        ShowDetails = true;
        GridProperties = new GalleryGridLayout { ShowTitle = true, ShowSubtitle = true };
        Filters = _modes;
        _modes.PropChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(Filters.CurrentFilterId)) Search(SearchText);
        };

        // Don't search yet: every result row builds a "More like this" page, and searching from the
        // constructor would make each of those search too (a chain reaction). Search on first display.
        SetSearchNoUpdate(initialQuery);
    }

    public override IListItem[] GetItems()
    {
        if (!_started)
        {
            _started = true;
            Search(SearchText);
        }
        return _items;
    }

    public override void UpdateSearchText(string oldSearch, string newSearch)
    {
        if (oldSearch != newSearch) Search(newSearch);
    }

    /// <summary>Re-arm with a new query (the fallback reuses one page as you type on the palette home).</summary>
    public void Reset(string query)
    {
        _pending?.Cancel();
        _items = [];
        _started = false;
        SetSearchNoUpdate(query);
    }

    void Search(string query)
    {
        _pending?.Cancel();
        var cts = _pending = new CancellationTokenSource();
        IsLoading = true;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(Debounce, cts.Token);
                var response = await GlimpseIpc.SendAsync(
                    new IpcRequest("search", query, Limit: 48, VisualOnly: _modes.CurrentFilterId == ModeFilters.Visual),
                    Timeout, cts.Token);
                if (cts.IsCancellationRequested) return;

                _items = response is { Ok: true, Hits: { } hits }
                    ? hits.Length > 0 ? hits.Select(ToItem).ToArray() : [Message("No matches", "Try fewer words, or the Visual filter to match by look", "\uE721")]
                    : [Message("Glimpse couldn't search", response.Error ?? "unknown error", "\uE783")];
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                return; // superseded by a newer keystroke
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or IOException)
            {
                _items = [NotRunningItem()];
            }
            catch (Exception ex)
            {
                // Anything else would kill this task silently and leave the old results up.
                _items = [Message("Glimpse extension error", $"{ex.GetType().Name}: {ex.Message}", "\uE783")];
            }

            IsLoading = false;
            RaiseItemsChanged(_items.Length);
        });
    }

    static ListItem ToItem(IpcHit hit)
    {
        var name = Path.GetFileName(hit.Path);
        var snippet = string.IsNullOrWhiteSpace(hit.Snippet) ? name : hit.Snippet.Trim();
        var kind = hit.VisualScore is float s ? $"looks like · {s:F2}" : "text match";

        return new ListItem(new OpenFileCommand(hit.Path))
        {
            Title = snippet.Length > 60 ? snippet[..57] + "…" : snippet,
            Subtitle = $"{hit.Source} · {hit.Modified:yyyy-MM-dd}",
            Icon = hit.ThumbnailPng is { } png ? IconFromPng(png) : new IconInfo(hit.Path),
            Tags = hit.VisualScore is null ? [] : [new Tag("≈ looks like")],
            Details = new Details
            {
                Title = name,
                // HeroImage renders at icon size; a markdown image fills the pane's width.
                Body = $"![{name}]({new Uri(hit.Path).AbsoluteUri}?--x-cmdpal-fit=fit)\n\n" +
                       (string.IsNullOrWhiteSpace(hit.Snippet) ? "_No text in this image._" : hit.Snippet),
                Size = ContentSize.Large,
                Metadata =
                [
                    new DetailsElement { Key = "Folder", Data = new DetailsLink { Text = Path.GetDirectoryName(hit.Path) ?? "" } },
                    new DetailsElement { Key = "Modified", Data = new DetailsLink { Text = $"{hit.Modified:dddd, MMMM d yyyy, HH:mm}" } },
                    new DetailsElement { Key = "Size", Data = new DetailsLink { Text = $"{hit.Width} × {hit.Height}" } },
                    new DetailsElement { Key = "Match", Data = new DetailsLink { Text = kind } },
                ],
            },
            MoreCommands =
            [
                new CommandContextItem(new AppCommand("copy", hit.Path, "Copy image", "\uE8C8", "Image copied")),
                new CommandContextItem(new CopyTextCommand(hit.Path) { Name = "Copy path" }),
                new CommandContextItem(new RevealCommand(hit.Path)),
                new CommandContextItem(new SearchPage($"like:{hit.Id}") { Name = "More like this" }) { Title = "More like this", Icon = new IconInfo("\uE7B3") },
                new CommandContextItem(new AppCommand("show", $"like:{hit.Id}", "Open in Glimpse", "\uE8A7")),
            ],
        };
    }

    static IconInfo IconFromPng(byte[] png)
    {
        var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
        using (var writer = new Windows.Storage.Streams.DataWriter(stream.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(png);
            writer.StoreAsync().AsTask().GetAwaiter().GetResult();
            writer.DetachStream();
        }
        return IconInfo.FromStream(stream);
    }

    static ListItem Message(string title, string subtitle, string glyph) =>
        new(new NoOpCommand()) { Title = title, Subtitle = subtitle, Icon = new IconInfo(glyph) };

    static ListItem NotRunningItem() =>
        new(new StartGlimpseCommand())
        {
            Title = "Glimpse isn't running",
            Subtitle = "Start it — it lives in the tray and answers searches",
            Icon = Glimpse.Icon,
        };

    public void Dispose() => _pending?.Cancel();
}

/// <summary>Text + visual (default) or visual only — the palette's filter dropdown.</summary>
internal sealed partial class ModeFilters : Filters
{
    public const string All = "all", Visual = "visual";

    public ModeFilters() => CurrentFilterId = All;

    public override IFilterItem[] GetFilters() =>
    [
        new Filter { Id = All, Name = "Text + visual", Icon = new IconInfo("\uE721") },
        new Filter { Id = Visual, Name = "Visual only", Icon = new IconInfo("\uE7B3") },
    ];
}

internal sealed partial class OpenFileCommand(string path) : InvokableCommand
{
    public override string Name => "Open";
    public override IconInfo Icon => new("\uE8E5");

    public override CommandResult Invoke()
    {
        Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        return CommandResult.Dismiss();
    }
}

internal sealed partial class RevealCommand(string path) : InvokableCommand
{
    public override string Name => "Show in folder";
    public override IconInfo Icon => new("\uE838");

    public override CommandResult Invoke()
    {
        Process.Start("explorer.exe", $"/select,\"{path}\"");
        return CommandResult.Dismiss();
    }
}

/// <summary>Asks the Glimpse app to do something that needs its UI thread (clipboard, window).</summary>
internal sealed partial class AppCommand(string op, string arg, string name, string glyph, string? toast = null) : InvokableCommand
{
    public override string Name => name;
    public override IconInfo Icon => new(glyph);

    public override CommandResult Invoke()
    {
        try
        {
            var response = GlimpseIpc.SendAsync(new IpcRequest(op, arg), TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            if (!response.Ok) return CommandResult.ShowToast(response.Error ?? "Glimpse couldn't do that");
        }
        catch (Exception)
        {
            return CommandResult.ShowToast("Glimpse isn't running");
        }
        return toast is null ? CommandResult.Dismiss() : CommandResult.ShowToast(toast);
    }
}

internal sealed partial class StartGlimpseCommand : InvokableCommand
{
    public override string Name => "Start Glimpse";
    public override IconInfo Icon => Glimpse.Icon;

    public override CommandResult Invoke()
    {
        var exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Glimpse", "Glimpse.exe");
        if (!File.Exists(exe)) return CommandResult.ShowToast("Glimpse isn't installed (run scripts/install.ps1)");
        Process.Start(new ProcessStartInfo(exe, "--hidden") { UseShellExecute = true });
        return CommandResult.ShowToast("Starting Glimpse… search again in a moment");
    }
}
