using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Glimpse.Core.Meaning;

/// <summary>
/// EmbeddingGemma 2's text model (8-bit, CPU): turns OCR text and search queries into 768-number vectors
/// so search can match by meaning, across languages ("factura" finds "invoice"). Text only: the image,
/// video and audio inputs get empty tensors.
/// </summary>
public sealed class MeaningModel : IDisposable
{
    public const int Dimensions = 768;
    public const string ModelFile = "model_quantized.onnx";
    public const string TokenizerFile = "tokenizer.json";

    // Task prefixes the model was trained with (asymmetric retrieval).
    const string QueryPrefix = "task: search result | query: ";
    const string DocumentPrefix = "title: none | text: ";
    const int MaxDocumentTokens = 512; // OCR text past this adds little and costs time
    const int MaxQueryTokens = 64;

    readonly string _dir;
    readonly Lock _gate = new();
    InferenceSession? _session;
    GemmaTokenizer? _tokenizer;
    bool _disposed;

    public MeaningModel(string modelDir) => _dir = modelDir;

    /// <summary>Stored next to each vector, so a model change triggers re-embedding.</summary>
    public string Name => Path.GetFileName(_dir.TrimEnd(Path.DirectorySeparatorChar));

    public static bool IsInstalled(string dir) =>
        File.Exists(Path.Combine(dir, ModelFile)) && File.Exists(Path.Combine(dir, ModelFile + "_data"))
        && File.Exists(Path.Combine(dir, TokenizerFile));

    public float[] EmbedQuery(string query) => Embed([QueryPrefix + query.Trim()], MaxQueryTokens)[0];

    public float[][] EmbedDocuments(IReadOnlyList<string> texts) =>
        Embed(texts.Select(t => DocumentPrefix + Clean(t)).ToList(), MaxDocumentTokens);

    /// <summary>OCR text: collapse line breaks and runs of spaces (they cost tokens and carry no meaning).</summary>
    static string Clean(string text) =>
        string.Join(' ', text.Split((char[])[' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries));

    /// <summary>Exact input, no prefix or cleanup (for checking against the reference implementation).</summary>
    public float[][] EmbedRaw(IReadOnlyList<string> texts, int maxTokens = MaxDocumentTokens) => Embed(texts, maxTokens);

    public int[] Tokenize(string text, int maxTokens = MaxDocumentTokens) => Load().Tokenizer.Encode(text, maxTokens);

    /// <summary>
    /// Loads on first use. Only loading is locked: ONNX Runtime sessions can run concurrently, so a search
    /// doesn't wait behind a background indexing batch.
    /// </summary>
    (GemmaTokenizer Tokenizer, InferenceSession Session) Load()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _tokenizer ??= new GemmaTokenizer(Path.Combine(_dir, TokenizerFile));
            _session ??= new InferenceSession(FastGraph(), new SessionOptions
            {
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
                IntraOpNumThreads = Math.Max(1, Environment.ProcessorCount / 2), // indexing runs in the background; keep the machine responsive
            });
            return (_tokenizer, _session);
        }
    }

    /// <summary>
    /// The graph with int8 compute switched on (see <see cref="OnnxPatch"/>), written once next to the original;
    /// it shares the original's weights file.
    /// </summary>
    string FastGraph()
    {
        var original = Path.Combine(_dir, ModelFile);
        var patched = Path.Combine(_dir, Path.GetFileNameWithoutExtension(ModelFile) + ".int8.onnx");
        if (!File.Exists(patched) || File.GetLastWriteTimeUtc(patched) < File.GetLastWriteTimeUtc(original))
        {
            var bytes = OnnxPatch.SetMatMulNBitsAccuracyLevel(File.ReadAllBytes(original), level: 4);
            File.WriteAllBytes(patched + ".tmp", bytes);
            File.Move(patched + ".tmp", patched, overwrite: true);
        }
        return patched;
    }

    float[][] Embed(IReadOnlyList<string> texts, int maxTokens)
    {
        var (tokenizer, session) = Load();
        var encoded = texts.Select(t => tokenizer.Encode(t, maxTokens)).ToList();
        var length = encoded.Max(e => e.Length);
        var ids = new DenseTensor<long>([encoded.Count, length]);
        var mask = new DenseTensor<long>([encoded.Count, length]);
        for (var b = 0; b < encoded.Count; b++)
            for (var i = 0; i < encoded[b].Length; i++)
            {
                ids[b, i] = encoded[b][i];
                mask[b, i] = 1;
            } // the rest stays 0 = <pad>, masked out

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor("input_ids", ids),
            NamedOnnxValue.CreateFromTensor("attention_mask", mask),
        };
        foreach (var name in new[] { "image_features", "video_features", "audio_features" })
            if (session.InputMetadata.ContainsKey(name))
                inputs.Add(NamedOnnxValue.CreateFromTensor(name, new DenseTensor<float>([0, 512])));

        using var results = session.Run(inputs, ["sentence_embedding"]);
        var output = results.First().AsTensor<float>();
        var vectors = new float[encoded.Count][];
        for (var b = 0; b < encoded.Count; b++)
        {
            var v = new float[Dimensions];
            double norm = 0;
            for (var j = 0; j < Dimensions; j++) { v[j] = output[b, j]; norm += v[j] * v[j]; }
            var inv = (float)(1 / Math.Sqrt(Math.Max(norm, 1e-12)));
            for (var j = 0; j < Dimensions; j++) v[j] *= inv;
            vectors[b] = v;
        }
        return vectors;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _session?.Dispose();
        }
    }
}
