using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Glimpse.Core.Visual;

/// <summary>
/// CLIP image + text encoders (ONNX). Images and text land in the same embedding space, so
/// "network diagram" scores high against pictures of network diagrams whatever text they contain.
/// The vision encoder runs on the GPU via DirectML when available (CPU otherwise); the small
/// quantized text encoder always runs on the CPU.
/// </summary>
public sealed class ClipModel : IDisposable
{
    public const int ImageSize = 224;
    const string VisionFile = "vision_model.onnx";
    const string TextFile = "text_model_quantized.onnx";

    static readonly float[] Mean = [0.48145466f, 0.4578275f, 0.40821073f];
    static readonly float[] Std = [0.26862954f, 0.26130258f, 0.27577711f];

    readonly string _dir;
    readonly bool _useGpu;
    readonly Lock _visionGate = new(); // DirectML sessions don't support concurrent Run
    readonly Lock _textGate = new();
    InferenceSession? _vision;
    InferenceSession? _text;
    ClipTokenizer? _tokenizer;

    public ClipModel(string modelDir, bool useGpu = true)
    {
        _dir = modelDir;
        _useGpu = useGpu;
    }

    /// <summary>Model id stored next to each embedding, so a model change triggers re-embedding.</summary>
    public string Name => Path.GetFileName(_dir.TrimEnd(Path.DirectorySeparatorChar));

    public static bool IsInstalled(string modelDir) =>
        new[] { VisionFile, TextFile, "vocab.json", "merges.txt" }.All(f => File.Exists(Path.Combine(modelDir, f)));

    public string VisionDevice { get; private set; } = "not loaded";

    // ---- images ---------------------------------------------------------------------------

    /// <summary>
    /// Decode → resize shortest side to 224 → center crop → normalize, as CLIP was trained.
    /// Returns CHW floats ready to be stacked into a batch.
    /// </summary>
    public static async Task<float[]> PreprocessAsync(string path, CancellationToken ct = default)
    {
        var file = await StorageFile.GetFileFromPathAsync(path).AsTask(ct);
        using IRandomAccessStream stream = await file.OpenAsync(FileAccessMode.Read).AsTask(ct);
        var decoder = await BitmapDecoder.CreateAsync(stream).AsTask(ct);

        double w = decoder.OrientedPixelWidth, h = decoder.OrientedPixelHeight;
        var scale = ImageSize / Math.Min(w, h);
        uint sw = (uint)Math.Max(ImageSize, Math.Round(w * scale)), sh = (uint)Math.Max(ImageSize, Math.Round(h * scale));
        var transform = new BitmapTransform
        {
            ScaledWidth = sw,
            ScaledHeight = sh,
            InterpolationMode = BitmapInterpolationMode.Fant,
            Bounds = new BitmapBounds { X = (sw - ImageSize) / 2, Y = (sh - ImageSize) / 2, Width = ImageSize, Height = ImageSize },
        };

        var pixels = await decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, transform,
            ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage).AsTask(ct);
        var bgra = pixels.DetachPixelData();

        const int plane = ImageSize * ImageSize;
        var chw = new float[3 * plane];
        for (var i = 0; i < plane; i++)
        {
            chw[i] = (bgra[i * 4 + 2] / 255f - Mean[0]) / Std[0];             // R
            chw[plane + i] = (bgra[i * 4 + 1] / 255f - Mean[1]) / Std[1];     // G
            chw[2 * plane + i] = (bgra[i * 4] / 255f - Mean[2]) / Std[2];     // B
        }
        return chw;
    }

    /// <summary>Embeds a batch of preprocessed images. Returns one unit-length vector per image.</summary>
    public float[][] EmbedImages(IReadOnlyList<float[]> preprocessed)
    {
        const int size = 3 * ImageSize * ImageSize;
        var input = new DenseTensor<float>([preprocessed.Count, 3, ImageSize, ImageSize]);
        var span = input.Buffer.Span;
        for (var i = 0; i < preprocessed.Count; i++) preprocessed[i].CopyTo(span.Slice(i * size, size));

        lock (_visionGate)
        {
            var session = _vision ??= LoadVision();
            var inputName = session.InputMetadata.Keys.First();
            using var results = session.Run([NamedOnnxValue.CreateFromTensor(inputName, input)]);
            return Rows(Pick(results, "image_embeds"));
        }
    }

    // ---- text -----------------------------------------------------------------------------

    public float[] EmbedText(string text)
    {
        lock (_textGate)
        {
            _tokenizer ??= new ClipTokenizer(Path.Combine(_dir, "vocab.json"), Path.Combine(_dir, "merges.txt"));
            _text ??= new InferenceSession(Path.Combine(_dir, TextFile), new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL });

            var ids = _tokenizer.Encode(text);
            var inputs = new List<NamedOnnxValue>();
            foreach (var name in _text.InputMetadata.Keys)
            {
                if (name == "input_ids")
                    inputs.Add(NamedOnnxValue.CreateFromTensor(name, new DenseTensor<long>(ids, [1, ids.Length])));
                else if (name == "attention_mask")
                {
                    // Attend to SOT … first EOT; the rest is padding.
                    var eot = Array.IndexOf(ids, ids[^1]);
                    var mask = ids.Select((_, i) => i <= eot ? 1L : 0L).ToArray();
                    inputs.Add(NamedOnnxValue.CreateFromTensor(name, new DenseTensor<long>(mask, [1, mask.Length])));
                }
            }
            using var results = _text.Run(inputs);
            return Rows(Pick(results, "text_embeds"))[0];
        }
    }

    // ---- helpers --------------------------------------------------------------------------

    InferenceSession LoadVision()
    {
        var path = Path.Combine(_dir, VisionFile);
        if (_useGpu)
        {
            try
            {
                var options = new SessionOptions
                {
                    EnableMemoryPattern = false,              // required by DirectML
                    ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
                    GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                };
                options.AppendExecutionProvider_DML(0);
                var session = new InferenceSession(path, options);
                VisionDevice = "GPU (DirectML)";
                return session;
            }
            catch (OnnxRuntimeException)
            {
                // No usable DirectX 12 device — fall through to CPU.
            }
        }
        VisionDevice = "CPU";
        return new InferenceSession(path, new SessionOptions { GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL });
    }

    static Tensor<float> Pick(IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results, string preferred) =>
        (results.FirstOrDefault(r => r.Name == preferred) ?? results.First()).AsTensor<float>();

    /// <summary>Splits a [batch, dim] tensor into L2-normalized rows (so dot product = cosine similarity).</summary>
    static float[][] Rows(Tensor<float> t)
    {
        int n = t.Dimensions[0], dim = t.Dimensions[1];
        var rows = new float[n][];
        for (var i = 0; i < n; i++)
        {
            var row = new float[dim];
            double norm = 0;
            for (var j = 0; j < dim; j++)
            {
                row[j] = t[i, j];
                norm += row[j] * row[j];
            }
            var inv = (float)(1 / Math.Sqrt(Math.Max(norm, 1e-12)));
            for (var j = 0; j < dim; j++) row[j] *= inv;
            rows[i] = row;
        }
        return rows;
    }

    public void Dispose()
    {
        _vision?.Dispose();
        _text?.Dispose();
    }
}
