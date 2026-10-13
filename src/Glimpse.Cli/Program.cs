using Glimpse.Core;
using Glimpse.Core.Index;
using Glimpse.Core.Meaning;
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
        "model" => await Model(rest),
        "bench" => Bench(),
        "meaning-check" => MeaningCheck(rest), // debug: tokens + vectors as JSON, to compare with the reference
        "eval" => Eval(rest), // debug: answer ranks per search mode
        "outline" => Outline(rest), // debug: words the preview outlines
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
    using var meaning = OpenMeaning(config);
    var hits = new SearchEngine(index, clip, meaning).Search(query, limit: 20);
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
        var kind = (h.MeaningScore is float m ? $"\x1b[35m~ {m:F3}\x1b[0m " : "") + (h.VisualScore is float v ? $"\x1b[36m≈ {v:F3}\x1b[0m " : "");
        Console.WriteLine($"  {kind}\x1b[2m{h.Source} · {h.Modified:yyyy-MM-dd HH:mm}\x1b[0m  {snippet}");
    }
    return 0;
}

static async Task<int> Model(string[] args)
{
    var model = args.FirstOrDefault()?.ToLowerInvariant() == "meaning" ? ModelCatalog.Meaning : ModelCatalog.Default;
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
    Console.WriteLine($"\ninstalled — run `glimpse-cli embed` to index with it");
    return 0;
}

/// <summary>Search latency on a warm engine, as the app runs it (model and embeddings loaded once).</summary>
static int Bench()
{
    var config = GlimpseConfig.Load();
    using var index = new ImageIndex();
    using var clip = OpenClip(config);
    using var meaning = OpenMeaning(config);
    var engine = new SearchEngine(index, clip, meaning);
    var stats = index.GetStats();
    Console.WriteLine($"{stats.Images:N0} images, {stats.WithText:N0} with text, {stats.Embedded:N0} visual, {stats.MeaningEmbedded:N0} meaning");

    var sw = System.Diagnostics.Stopwatch.StartNew();
    engine.Search("warm up");   // loads the text encoders + all embeddings
    Console.WriteLine($"cold start (models + {stats.Embedded + stats.MeaningEmbedded:N0} vectors): {sw.ElapsedMilliseconds} ms");

    string[] text = ["moca", "invoice", "error", "password reset", "localhost:3000", "in:notes claude"];
    string[] hybrid = ["network diagram", "bar chart", "home router setup", "receipt"];
    string[] visual = ["~network diagram", "~dark dashboard", "~photo of a cat", "~spreadsheet"];
    void Measure(string label, string[] queries, int rounds = 20)
    {
        var times = new List<double>();
        foreach (var q in queries) engine.Search(q); // warm
        for (var r = 0; r < rounds; r++)
            foreach (var q in queries)
            {
                sw.Restart();
                engine.Search(q, 60);
                times.Add(sw.Elapsed.TotalMilliseconds);
            }
        times.Sort();
        Console.WriteLine($"{label,-8} median {times[times.Count / 2],6:F1} ms   p95 {times[(int)(times.Count * 0.95)],6:F1} ms   ({times.Count} searches)");
    }
    var textOnly = new SearchEngine(index, clip: null);
    void MeasureOn(SearchEngine e, string label, string[] queries) { var keep = engine; engine = e; Measure(label, queries); engine = keep; }
    MeasureOn(textOnly, "text", text);
    if (clip is not null)
    {
        Measure("text+vis", text);   // what the app does when visual search is on: every query also asks CLIP
        Measure("hybrid", hybrid);
        Measure("visual", visual);
        var embedded = index.LoadEmbeddings(clip.Name);
        if (embedded.Count > 0) Measure("similar", [$"like:{embedded.Ids[embedded.Count / 2]}"]);

        sw.Restart();
        for (var i = 0; i < 20; i++) clip.EmbedText($"query number {i}");
        Console.WriteLine($"CLIP text encoder alone: {sw.Elapsed.TotalMilliseconds / 20:F1} ms per query");
    }
    if (meaning is not null)
    {
        MeasureOn(new SearchEngine(index, clip: null, meaning), "meaning", hybrid); // text + meaning, no CLIP
        sw.Restart();
        for (var i = 0; i < 20; i++) meaning.EmbedQuery($"query number {i}");
        Console.WriteLine($"EmbeddingGemma query encoder alone: {sw.Elapsed.TotalMilliseconds / 20:F1} ms per query");
    }
    Console.WriteLine($"index on disk: {new FileInfo(GlimpseConfig.DatabasePath).Length / 1048576.0:F1} MB " +
                      $"({new FileInfo(GlimpseConfig.DatabasePath).Length / 1024.0 / Math.Max(1, stats.Images):F1} KB per image)");
    return 0;
}

/// <summary>Debug: which words the preview would outline for a query, in an indexed image (by file name).</summary>
static int Outline(string[] args)
{
    if (args.Length < 2) return Help();
    using var index = new ImageIndex();
    var hit = index.Search($"\"{Path.GetFileNameWithoutExtension(args[0])}\"", 200)
        .FirstOrDefault(h => Path.GetFileName(h.Path).Equals(args[0], StringComparison.OrdinalIgnoreCase));
    if (hit is null) { Console.Error.WriteLine($"not indexed: {args[0]}"); return 1; }
    var words = OcrMatch.Find(index.GetOcrLines(hit.Id), SearchQuery.Parse(string.Join(' ', args.Skip(1))).Terms).ToList();
    Console.WriteLine($"{words.Count} outlined: {string.Join(" | ", words.Select(w => w.Text))}");
    return 0;
}

static int MeaningCheck(string[] args)
{
    // args: <model dir> <inputs.json: ["text", ...]>
    using var model = new Glimpse.Core.Meaning.MeaningModel(args[0]);
    var texts = System.Text.Json.JsonSerializer.Deserialize<List<string>>(File.ReadAllText(args[1]))!;
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var tokens = texts.Select(t => model.Tokenize(t)).ToList();
    var tokenMs = sw.Elapsed.TotalMilliseconds;
    sw.Restart();
    var vectors = texts.Select(t => model.EmbedRaw([t])[0]).ToList(); // one at a time, like the reference
    Console.Error.WriteLine($"tokenize {tokenMs:F0} ms total, embed {sw.Elapsed.TotalMilliseconds / texts.Count:F0} ms/text (incl. load)");
    sw.Restart();
    var batched = texts.Chunk(8).SelectMany(c => model.EmbedRaw(c)).ToList();
    var worst = batched.Select((v, i) => v.Zip(vectors[i], (a, b) => a * b).Sum()).Min();
    Console.Error.WriteLine($"batched (8): {sw.Elapsed.TotalMilliseconds / texts.Count:F0} ms/text, lowest cosine vs single {worst:F5}");
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { tokens, vectors }));
    return 0;
}

/// <summary>
/// Debug: ranks of known answers (queries.json: [{q, a: [file-name endings]}]) for text-only, text+visual and
/// text+visual+meaning search, plus raw meaning scores (best answer vs best overall) for tuning the cutoffs.
/// </summary>
static int Eval(string[] args)
{
    var config = GlimpseConfig.Load();
    using var index = new ImageIndex();
    using var clip = OpenClip(config);
    using var meaning = OpenMeaning(config);
    var queries = System.Text.Json.JsonDocument.Parse(File.ReadAllText(args[0])).RootElement.EnumerateArray()
        .Select(e => (Q: e.GetProperty("q").GetString()!, A: e.GetProperty("a").EnumerateArray().Select(a => a.GetString()!).ToList())).ToList();
    var engines = new List<(string Label, SearchEngine Engine)>
    {
        ("text", new SearchEngine(index, null)),
        ("text+visual (today)", new SearchEngine(index, clip)),
        ("text+meaning", new SearchEngine(index, null, meaning)),
        ("text+visual+meaning", new SearchEngine(index, clip, meaning)),
    };
    // Optional: meaning windows to compare, e.g. 0.02,0.04,0.08
    foreach (var w in args.Skip(1).FirstOrDefault()?.Split(',') ?? [])
        engines.Add(($"t+v+m window {w}", new SearchEngine(index, clip, meaning) { MeaningWindow = float.Parse(w, System.Globalization.CultureInfo.InvariantCulture) }));
    var set = meaning is null ? null : index.LoadEmbeddings(meaning.Name, VectorKind.Meaning);
    var paths = set is null ? [] : index.GetHits(set.Ids.Select(id => (id, 0f)).ToList()).ToDictionary(h => h.Id, h => h.Path);
    var totals = engines.Select(_ => (Hit1: 0, Hit5: 0, Mrr: 0.0)).ToArray();
    var verbose = args.Length < 2;

    Console.WriteLine($"{"query",-32} {string.Join(" ", engines.Select(e => $"{e.Label,20}"))}   meaning: answer / best / 10th");
    foreach (var (q, answers) in queries)
    {
        bool IsAnswer(string path) => answers.Any(a => path.EndsWith(a, StringComparison.OrdinalIgnoreCase));
        var cells = new List<string>();
        for (var e = 0; e < engines.Count; e++)
        {
            var hits = engines[e].Engine.Search(q, 120);
            var rank = hits.FindIndex(h => IsAnswer(h.Path)) + 1;
            if (rank == 1) totals[e].Hit1++;
            if (rank is > 0 and <= 5) totals[e].Hit5++;
            if (rank > 0) totals[e].Mrr += 1.0 / rank;
            cells.Add($"{(rank == 0 ? "-" : rank.ToString()),20}");
        }
        var raw = "";
        if (set is not null && meaning is not null)
        {
            var qv = meaning.EmbedQuery(q);
            var scores = Enumerable.Range(0, set.Count).Select(i => (Id: set.Ids[i], S: set.Vector(i).ToArray().Zip(qv, (x, y) => x * y).Sum()))
                .OrderByDescending(x => x.S).ToList();
            var answer = scores.Select((x, i) => (x.S, Rank: i + 1)).FirstOrDefault(x => IsAnswer(paths[scores[x.Rank - 1].Id]));
            raw = $"{(answer.Rank == 0 ? "  (no text)" : $"{answer.S:F3} #{answer.Rank}")} / {scores[0].S:F3} / {scores[9].S:F3}";
        }
        Console.WriteLine($"{q,-32} {string.Join(" ", cells)}   {raw}");
    }
    Console.WriteLine();
    for (var e = 0; e < engines.Count; e++)
        Console.WriteLine($"{engines[e].Label,-22} hit@1 {totals[e].Hit1,2}  hit@5 {totals[e].Hit5,2}  MRR {totals[e].Mrr / queries.Count:F3}");
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

static MeaningModel? OpenMeaning(GlimpseConfig config) =>
    config.MeaningModelPath is { } dir && MeaningModel.IsInstalled(dir) ? new MeaningModel(dir) : null;

/// <summary>Visual pass (CLIP), then meaning pass (EmbeddingGemma), for whichever models are installed.</summary>
static async Task<int> Embed()
{
    var config = GlimpseConfig.Load();
    using var clip = OpenClip(config);
    using var meaning = OpenMeaning(config);
    if (clip is null && meaning is null)
    {
        Console.Error.WriteLine("no models installed (or both are switched off) — run `glimpse-cli model` / `glimpse-cli model meaning`");
        return 1;
    }

    using var index = new ImageIndex();
    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

    Progress<IndexProgress> Progress(string label)
    {
        var lastDraw = DateTime.MinValue;
        return new Progress<IndexProgress>(p =>
        {
            if ((DateTime.Now - lastDraw).TotalMilliseconds < 250 && p.Done < p.Total) return;
            lastDraw = DateTime.Now;
            Console.Write($"\r  {label} {p.Done}/{p.Total} ({100.0 * p.Done / p.Total:F0}%)   ");
        });
    }

    try
    {
        if (clip is not null)
        {
            var r = await new EmbeddingIndexer(config, index, clip).RunAsync(Progress($"visual on {clip.VisionDevice}"), cts.Token);
            Console.WriteLine();
            Console.WriteLine($"visual: embedded {r.Embedded}, failed {r.Failed} in {r.Elapsed:mm\\:ss} on {clip.VisionDevice}");
        }
        if (meaning is not null)
        {
            var r = await new MeaningIndexer(index, meaning).RunAsync(Progress("meaning"), cts.Token);
            Console.WriteLine();
            Console.WriteLine($"meaning: embedded {r.Embedded}, failed {r.Failed} in {r.Elapsed:mm\\:ss}" +
                              (r.Embedded > 0 ? $" ({r.Elapsed.TotalMilliseconds / r.Embedded:F0} ms per image)" : ""));
        }
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
    Console.WriteLine($"{s.Images} images, {s.WithText} with text, {s.Embedded} visual, {s.MeaningEmbedded} meaning, {s.Errors} errors");
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
          glimpse-cli model meaning        download the search-by-meaning model (~330 MB, once)
          glimpse-cli embed                embed images for visual search and search by meaning (after index)
          glimpse-cli search <query>       e.g.  moca network  ·  ~network diagram  ·  in:notes after:2026-05 invoice  ·  like:1234
          glimpse-cli ocr <file>           OCR a single image and print the text
          glimpse-cli stats                index size and per-source counts
          glimpse-cli bench                search latency on this machine (build with -c Release)
          glimpse-cli sources              list configured folders
        """);
    return 0;
}
