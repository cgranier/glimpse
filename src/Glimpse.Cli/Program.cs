using Glimpse.Core;
using Glimpse.Core.Index;
using Glimpse.Core.Ocr;
using Glimpse.Core.Visual;

Console.OutputEncoding = System.Text.Encoding.UTF8;

var command = args.FirstOrDefault()?.ToLowerInvariant();
var rest = args.Skip(1).ToArray();

try
{
    return command switch
    {
        "index" => await Index(rest),
        "embed" => await Embed(),
        "model" => await Model(),
        "tokens" => Tokens(string.Join(' ', rest)), // debug: CLIP token ids
        "search" or "s" => Search(string.Join(' ', rest)),
        "ocr" => await Ocr(rest),
        "stats" => Stats(),
        "sources" => Sources(),
        _ => Help(),
    };
}
catch (Exception ex)
{
    Console.Error.WriteLine($"error: {ex.Message}");
    return 1;
}

static async Task<int> Index(string[] args)
{
    var config = GlimpseConfig.Load();
    var only = args.Length == 0 ? null : config.Sources.Where(s => args.Contains(s.Name, StringComparer.OrdinalIgnoreCase)).ToList();
    if (only is { Count: 0 })
    {
        Console.Error.WriteLine($"no source named {string.Join(", ", args)} (see `glimpse-cli sources`)");
        return 1;
    }

    using var index = new ImageIndex();
    var indexer = new Indexer(config, index);
    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

    var lastDraw = DateTime.MinValue;
    var progress = new Progress<IndexProgress>(p =>
    {
        if (p.Total == 0) { Console.WriteLine(p.Phase + "…"); return; }
        if ((DateTime.Now - lastDraw).TotalMilliseconds < 200 && p.Done < p.Total) return;
        lastDraw = DateTime.Now;
        var name = p.Current is { Length: > 40 } c ? c[..37] + "..." : p.Current;
        Console.Write($"\r  ocr {p.Done}/{p.Total} ({100.0 * p.Done / p.Total:F0}%)  {name,-40}");
    });

    try
    {
        var r = await indexer.RunAsync(only, progress, cts.Token);
        Console.WriteLine();
        Console.WriteLine($"scanned {r.Scanned}, indexed {r.Indexed}, unchanged {r.Unchanged}, removed {r.Removed}, " +
                          $"failed {r.Failed}, cloud-only skipped {r.SkippedCloud} in {r.Elapsed:mm\\:ss}");
        if (r.Indexed > 0)
            Console.WriteLine($"  ≈ {r.Elapsed.TotalSeconds / Math.Max(1, r.Indexed + r.Failed):F2}s per image with {config.Workers} workers");
    }
    catch (OperationCanceledException)
    {
        Console.WriteLine("\ncancelled — progress so far is saved; run again to continue.");
    }
    return 0;
}

static int Search(string query)
{
    var config = GlimpseConfig.Load();
    using var index = new ImageIndex();
    using var clip = OpenClip(config);
    var hits = new SearchEngine(index, clip).Search(query, limit: 20);
    if (hits.Count == 0)
    {
        Console.WriteLine("no matches");
        return 0;
    }
    foreach (var h in hits)
    {
        Console.WriteLine($"\x1b[1m{h.Path}\x1b[0m");
        var snippet = h.Snippet.ReplaceLineEndings(" ")
            .Replace(ImageIndex.MatchStart.ToString(), "\x1b[30;43m")
            .Replace(ImageIndex.MatchEnd.ToString(), "\x1b[0m");
        var kind = h.VisualScore is float score ? $"\x1b[36m≈ {score:F3}\x1b[0m " : "";
        Console.WriteLine($"  {kind}\x1b[2m{h.Source} · {h.Modified:yyyy-MM-dd HH:mm}\x1b[0m  {snippet}");
    }
    return 0;
}

static async Task<int> Model()
{
    var model = ModelCatalog.Default;
    var dir = Path.Combine(GlimpseConfig.ModelsDir, model.Name);
    if (ModelCatalog.IsInstalled(model, dir))
    {
        Console.WriteLine($"{model.DisplayName} is installed in {dir}");
        return 0;
    }
    Console.WriteLine($"Downloading {model.DisplayName} ({model.TotalSize / 1048576} MB, {model.License}) from {model.SourceUrl}");
    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
    var progress = new Progress<(long Done, long Total)>(p => Console.Write($"\r  {p.Done / 1048576} / {p.Total / 1048576} MB   "));
    await ModelCatalog.DownloadAsync(model, dir, progress, cts.Token);
    Console.WriteLine($"\ninstalled — run `glimpse-cli embed` to index visually");
    return 0;
}

static int Tokens(string text)
{
    var dir = GlimpseConfig.Load().VisualModelPath!;
    var ids = new ClipTokenizer(Path.Combine(dir, "vocab.json"), Path.Combine(dir, "merges.txt")).Encode(text);
    Console.WriteLine(string.Join(", ", ids.TakeWhile((id, i) => i == 0 || ids[i - 1] != 49407)));
    return 0;
}

static ClipModel? OpenClip(GlimpseConfig config) =>
    config.VisualModelPath is { } dir && ClipModel.IsInstalled(dir) ? new ClipModel(dir, config.UseGpu) : null;

static async Task<int> Embed()
{
    var config = GlimpseConfig.Load();
    using var clip = OpenClip(config);
    if (clip is null)
    {
        Console.Error.WriteLine($"no CLIP model in {config.VisualModelPath} — visual search is off");
        return 1;
    }

    using var index = new ImageIndex();
    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

    var lastDraw = DateTime.MinValue;
    var progress = new Progress<IndexProgress>(p =>
    {
        if ((DateTime.Now - lastDraw).TotalMilliseconds < 250 && p.Done < p.Total) return;
        lastDraw = DateTime.Now;
        Console.Write($"\r  embed {p.Done}/{p.Total} ({100.0 * p.Done / p.Total:F0}%) on {clip.VisionDevice}   ");
    });

    try
    {
        var r = await new EmbeddingIndexer(config, index, clip).RunAsync(progress, cts.Token);
        Console.WriteLine();
        Console.WriteLine($"embedded {r.Embedded}, failed {r.Failed} in {r.Elapsed:mm\\:ss} on {clip.VisionDevice}");
    }
    catch (OperationCanceledException)
    {
        Console.WriteLine("\ncancelled — progress so far is saved; run again to continue.");
    }
    return 0;
}

static async Task<int> Ocr(string[] args)
{
    if (args.Length == 0) return Help();
    var ocr = new WindowsOcr();
    var page = await ocr.RecognizeAsync(Path.GetFullPath(args[0]));
    Console.WriteLine($"[{ocr.Language}, {page.Width}x{page.Height}, {page.Lines.Count} lines]");
    Console.WriteLine(page.FullText);
    return 0;
}

static int Stats()
{
    using var index = new ImageIndex();
    var s = index.GetStats();
    Console.WriteLine($"{s.Images} images, {s.WithText} with text, {s.Errors} errors");
    foreach (var (source, count) in s.BySource) Console.WriteLine($"  {source,-20} {count}");
    Console.WriteLine($"db: {GlimpseConfig.DatabasePath} ({new FileInfo(GlimpseConfig.DatabasePath).Length / 1024.0 / 1024:F1} MB)");
    return 0;
}

static int Sources()
{
    var config = GlimpseConfig.Load();
    foreach (var s in config.Sources) Console.WriteLine($"  {s.Name,-20} {s.Path}");
    Console.WriteLine($"edit: {GlimpseConfig.ConfigPath}");
    return 0;
}

static int Help()
{
    Console.WriteLine("""
        glimpse-cli — find images by the text in them or how they look

          glimpse-cli index [source...]    scan sources and OCR new/changed images
          glimpse-cli model                download the visual search model (~392 MB, once)
          glimpse-cli embed                CLIP-embed images for visual search (after index)
          glimpse-cli search <query>       e.g.  moca network  ·  ~network diagram  ·  in:notes after:2026-05 invoice  ·  like:1234
          glimpse-cli ocr <file>           OCR a single image and print the text
          glimpse-cli stats                index size and per-source counts
          glimpse-cli sources              list configured folders
        """);
    return 0;
}
