using System.Net.Http.Headers;

namespace Glimpse.Core.Visual;

public sealed record ModelFile(string RemotePath, long Size)
{
    public string LocalName => Path.GetFileName(RemotePath);
}

/// <summary>A downloadable model: where it comes from, what it weighs, and its license.</summary>
/// <param name="Revision">Branch or commit to download; a commit pins the exact files the sizes below describe.</param>
public sealed record ModelInfo(string Name, string DisplayName, string Repository, string License, IReadOnlyList<ModelFile> Files, string Revision = "main")
{
    public long TotalSize => Files.Sum(f => f.Size);
    public string SourceUrl => $"https://huggingface.co/{Repository}";
}

public static class ModelCatalog
{
    /// <summary>OpenAI CLIP ViT-B/16 (MIT), ONNX export by Xenova. Full-precision vision encoder (GPU or CPU), int8 text encoder.</summary>
    public static ModelInfo Default { get; } = new(
        "clip-vit-b16",
        "CLIP ViT-B/16",
        "Xenova/clip-vit-base-patch16",
        "MIT (OpenAI CLIP)",
        [
            new("vocab.json", 862_328),
            new("merges.txt", 524_619),
            new("onnx/text_model_quantized.onnx", 64_504_507),
            new("onnx/vision_model.onnx", 345_060_583),
        ]);

    /// <summary>
    /// Google's EmbeddingGemma 2, text part only (int8, CPU), ONNX export by onnx-community: search OCR text by
    /// meaning, in 100+ languages. Pinned to a commit, since the repository is still being updated.
    /// </summary>
    public static ModelInfo Meaning { get; } = new(
        "embeddinggemma-2-text",
        "EmbeddingGemma 2 (text)",
        "onnx-community/embeddinggemma-2-ONNX",
        "Apache 2.0 (Google)",
        [
            new("tokenizer.json", 32_170_510),
            new("onnx/model_quantized.onnx", 495_165),
            new("onnx/model_quantized.onnx_data", 313_724_928),
        ],
        Revision: "daa72c51243991dfcaf9f9137d2c573d8f7790c0");

    public static bool IsInstalled(ModelInfo model, string dir) =>
        model.Files.All(f => new FileInfo(Path.Combine(dir, f.LocalName)) is { Exists: true } fi && fi.Length == f.Size);

    /// <summary>
    /// Downloads every missing file into <paramref name="dir"/> (resumes by skipping complete files).
    /// Each file lands as .partial and is renamed once its size checks out, so a cancelled download
    /// never looks installed.
    /// </summary>
    public static async Task DownloadAsync(ModelInfo model, string dir, IProgress<(long Done, long Total)>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(dir);
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Glimpse", "1.0"));

        long done = 0;
        foreach (var file in model.Files)
        {
            var target = Path.Combine(dir, file.LocalName);
            if (new FileInfo(target) is { Exists: true } existing && existing.Length == file.Size)
            {
                done += file.Size;
                progress?.Report((done, model.TotalSize));
                continue;
            }

            var partial = target + ".partial";
            var url = $"https://huggingface.co/{model.Repository}/resolve/{model.Revision}/{file.RemotePath}";
            using (var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                response.EnsureSuccessStatusCode();
                await using var input = await response.Content.ReadAsStreamAsync(ct);
                await using var output = File.Create(partial);
                var buffer = new byte[1 << 16];
                int n;
                while ((n = await input.ReadAsync(buffer, ct)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, n), ct);
                    done += n;
                    progress?.Report((done, model.TotalSize));
                }
            }

            if (new FileInfo(partial).Length != file.Size)
            {
                File.Delete(partial);
                throw new IOException($"{file.LocalName}: unexpected size — the download may have been interrupted. Try again.");
            }
            File.Move(partial, target, overwrite: true);
        }
    }
}
