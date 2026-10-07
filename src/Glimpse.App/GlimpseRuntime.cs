using Glimpse.Core;
using Glimpse.Core.Index;
using Glimpse.Core.Visual;

namespace Glimpse.App;

/// <summary>
/// Everything that depends on the config — indexer, folder watcher, CLIP model, search engine —
/// in one place, so Settings can swap a new config in without restarting the app. Only the parts
/// affected by a change are rebuilt; the two index connections live for the whole session.
/// </summary>
public sealed class GlimpseRuntime : IDisposable
{
    // Separate connections: searches run on worker threads while indexing writes. WAL lets them coexist.
    public ImageIndex SearchIndex { get; } = new();
    public ImageIndex WriteIndex { get; } = new();

    public GlimpseConfig Config { get; private set; }
    public Indexer Indexer { get; private set; } = null!;
    public ClipModel? Clip { get; private set; }           // null when visual search is off or not installed
    public EmbeddingIndexer? Embedder { get; private set; }
    public SearchEngine Engine { get; private set; } = null!;

    FolderWatcher? _watcher;

    /// <summary>The folder watcher indexed this many new images.</summary>
    public event Action<int>? NewImagesIndexed;

    public GlimpseRuntime(GlimpseConfig config)
    {
        Config = config;
        BuildIndexing();
        BuildVisual();
    }

    public bool VisualAvailable => Clip is not null;

    /// <summary>Whether the configured model's files are present (visual search may still be switched off).</summary>
    public bool ModelInstalled =>
        Path.Combine(GlimpseConfig.ModelsDir, Config.VisualModel) is var dir && ClipModel.IsInstalled(dir);

    public sealed record ApplyResult(bool IndexingChanged, bool VisualChanged, bool HotkeyChanged, int ImagesForgotten);

    /// <summary>Saves <paramref name="next"/> and rebuilds whatever it changes.</summary>
    public ApplyResult Apply(GlimpseConfig next)
    {
        var previous = Config;
        var indexing = previous.IndexingDiffers(next);
        var visual = previous.VisualDiffers(next);
        var hotkey = previous.Hotkey != next.Hotkey;

        // Images from folders that are no longer configured (or now point elsewhere) leave the index.
        var forgotten = 0;
        foreach (var gone in previous.Sources.Where(p => !next.Sources.Any(n => n.Name == p.Name && n.Path == p.Path)))
            forgotten += WriteIndex.RemoveSource(gone.Name);

        Config = next;
        next.Save();
        if (indexing) BuildIndexing();
        if (visual || indexing) BuildVisual(); // the embedder reads Workers from the config too
        if (forgotten > 0) Engine.InvalidateVisual();
        return new ApplyResult(indexing, visual, hotkey, forgotten);
    }

    /// <summary>Re-reads a model that was just downloaded.</summary>
    public void ReloadVisual() => BuildVisual();

    void BuildIndexing()
    {
        _watcher?.Dispose();
        Indexer = new Indexer(Config, WriteIndex);
        _watcher = new FolderWatcher(Config, Indexer);
        _watcher.Indexed += n => NewImagesIndexed?.Invoke(n);
    }

    void BuildVisual()
    {
        var (oldClip, oldEmbedder) = (Clip, Embedder);

        if (Config.VisualModelPath is { } dir && ClipModel.IsInstalled(dir))
        {
            Clip = new ClipModel(dir, Config.UseGpu);
            Embedder = new EmbeddingIndexer(Config, WriteIndex, Clip);
            // Load the text encoder now so the first search (here or from Command Palette) doesn't pay for it.
            var clip = Clip;
            _ = Task.Run(() => clip.EmbedText("warm up"));
        }
        else
        {
            Clip = null;
            Embedder = null;
        }
        Engine = new SearchEngine(SearchIndex, Clip);

        // A pass or a search may still be using the old model; let them finish before releasing it.
        if (oldClip is not null)
        {
            _ = Task.Run(async () =>
            {
                if (oldEmbedder is not null) await oldEmbedder.WhenIdleAsync();
                await Task.Delay(TimeSpan.FromSeconds(5));
                oldClip.Dispose();
            });
        }
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        SearchIndex.Dispose();
        WriteIndex.Dispose();
        Clip?.Dispose();
    }
}
