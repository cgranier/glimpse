using Glimpse.Core;
using Glimpse.Core.Index;
using Glimpse.Core.Meaning;
using Glimpse.Core.Visual;

namespace Glimpse.App;

/// <summary>
/// Everything that depends on the config — indexer, folder watcher, CLIP and meaning models, search engine —
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
    public MeaningModel? Meaning { get; private set; }     // null when search by meaning is off or not installed
    public MeaningIndexer? MeaningEmbedder { get; private set; }
    public SearchEngine Engine { get; private set; } = null!;

    FolderWatcher? _watcher;

    /// <summary>The folder watcher indexed this many new images.</summary>
    public event Action<int>? NewImagesIndexed;

    /// <summary>Indexed images were renamed, moved or deleted.</summary>
    public event Action? ImagesChanged;

    public GlimpseRuntime(GlimpseConfig config)
    {
        Config = config;
        BuildIndexing();
        BuildModels(visual: true, meaning: true);
    }

    public bool VisualAvailable => Clip is not null;
    public bool MeaningAvailable => Meaning is not null;

    /// <summary>Whether the configured model's files are present (visual search may still be switched off).</summary>
    public bool ModelInstalled =>
        Path.Combine(GlimpseConfig.ModelsDir, Config.VisualModel) is var dir && ClipModel.IsInstalled(dir);

    public bool MeaningModelInstalled =>
        Path.Combine(GlimpseConfig.ModelsDir, Config.MeaningModel) is var dir && MeaningModel.IsInstalled(dir);

    public sealed record ApplyResult(bool IndexingChanged, bool VisualChanged, bool HotkeyChanged, int ImagesForgotten, bool MeaningChanged = false);

    /// <summary>Saves <paramref name="next"/> and rebuilds whatever it changes.</summary>
    public ApplyResult Apply(GlimpseConfig next)
    {
        var previous = Config;
        var indexing = previous.IndexingDiffers(next);
        var visual = previous.VisualDiffers(next);
        var meaning = previous.MeaningDiffers(next);
        var hotkey = previous.Hotkey != next.Hotkey;

        // Images from folders that are no longer configured (or now point elsewhere) leave the index.
        var forgotten = 0;
        foreach (var gone in previous.Sources.Where(p => !next.Sources.Any(n => n.Name == p.Name && n.Path == p.Path)))
            forgotten += WriteIndex.RemoveSource(gone.Name);

        Config = next;
        next.Save();
        if (indexing) BuildIndexing();
        if (visual || meaning) BuildModels(visual, meaning);
        if (!visual && indexing && Clip is not null) Embedder = new EmbeddingIndexer(next, WriteIndex, Clip); // reads Workers; keep the loaded model
        if (forgotten > 0) Engine.InvalidateEmbeddings();
        return new ApplyResult(indexing, visual, hotkey, forgotten, meaning);
    }

    /// <summary>Re-reads a model that was just downloaded.</summary>
    public void ReloadVisual() => BuildModels(visual: true, meaning: false);

    public void ReloadMeaning() => BuildModels(visual: false, meaning: true);

    void BuildIndexing()
    {
        _watcher?.Dispose();
        Indexer = new Indexer(Config, WriteIndex);
        _watcher = new FolderWatcher(Config, Indexer);
        _watcher.Indexed += n => NewImagesIndexed?.Invoke(n);
        _watcher.Changed += () =>
        {
            Engine.InvalidateEmbeddings();
            ImagesChanged?.Invoke();
        };
    }

    /// <summary>(Re)loads the models whose settings changed, then the search engine that uses them.</summary>
    void BuildModels(bool visual, bool meaning)
    {
        if (visual) BuildVisual();
        if (meaning) BuildMeaning();
        Engine = new SearchEngine(SearchIndex, Clip, Meaning);
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

    void BuildMeaning()
    {
        var (oldMeaning, oldEmbedder) = (Meaning, MeaningEmbedder);

        if (Config.MeaningModelPath is { } dir && MeaningModel.IsInstalled(dir))
        {
            Meaning = new MeaningModel(dir);
            MeaningEmbedder = new MeaningIndexer(WriteIndex, Meaning);
            var meaning = Meaning;
            _ = Task.Run(() => meaning.EmbedQuery("warm up"));
        }
        else
        {
            Meaning = null;
            MeaningEmbedder = null;
        }

        if (oldMeaning is not null)
        {
            oldEmbedder?.Stop();
            _ = Task.Run(async () =>
            {
                if (oldEmbedder is not null) await oldEmbedder.WhenIdleAsync();
                await Task.Delay(TimeSpan.FromSeconds(5));
                oldMeaning.Dispose();
            });
        }
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        SearchIndex.Dispose();
        WriteIndex.Dispose();
        Clip?.Dispose();
        Meaning?.Dispose();
    }
}
