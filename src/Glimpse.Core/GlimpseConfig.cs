using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Glimpse.Core;

/// <summary>A folder Glimpse watches and indexes. <see cref="Name"/> is what `in:` filters match.</summary>
public sealed record Source(string Name, string Path, bool Recursive = true);

public sealed partial class GlimpseConfig
{
    public List<Source> Sources { get; set; } = [];

    /// <summary>
    /// Global shortcut that summons the search window. Win+Alt+S sits next to Win+Shift+S (take a
    /// screenshot), so "take" and "find" share a key.
    /// </summary>
    public string Hotkey { get; set; } = "Win+Alt+S";

    public string[] Extensions { get; set; } = [".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".tif", ".tiff"];

    /// <summary>
    /// OneDrive "online-only" files would be downloaded just to OCR them. Off by default;
    /// flip on if you want the whole cloud history indexed.
    /// </summary>
    public bool HydrateCloudFiles { get; set; } = false;

    /// <summary>Search by what images look like (CLIP). Needs the model (Settings → Visual search → Download).</summary>
    public bool VisualSearch { get; set; } = true;

    /// <summary>Folder name under models\ holding the CLIP ONNX files.</summary>
    public string VisualModel { get; set; } = Visual.ModelCatalog.Default.Name;

    /// <summary>Run the image encoder on the GPU (DirectML). Falls back to CPU automatically.</summary>
    public bool UseGpu { get; set; } = true;

    [JsonIgnore]
    public string? VisualModelPath =>
        !VisualSearch || string.IsNullOrWhiteSpace(VisualModel) ? null : System.IO.Path.Combine(ModelsDir, VisualModel);

    /// <summary>Concurrent OCR workers. Windows OCR is CPU-bound; half the cores keeps the machine responsive.</summary>
    public int Workers { get; set; } = Math.Max(1, Environment.ProcessorCount / 2);

    public static string DataDir { get; } =
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Glimpse");

    public static string ConfigPath => System.IO.Path.Combine(DataDir, "config.json");
    public static string DatabasePath => System.IO.Path.Combine(DataDir, "index.db");
    public static string ModelsDir => System.IO.Path.Combine(DataDir, "models");

    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static GlimpseConfig Load()
    {
        if (File.Exists(ConfigPath))
            return JsonSerializer.Deserialize<GlimpseConfig>(File.ReadAllText(ConfigPath), Json) ?? CreateDefault();

        var config = CreateDefault();
        config.Save();
        return config;
    }

    public void Save()
    {
        Directory.CreateDirectory(DataDir);
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(this, Json));
    }

    /// <summary>Deep copy, for editing in the settings window before applying.</summary>
    public GlimpseConfig Clone() => JsonSerializer.Deserialize<GlimpseConfig>(JsonSerializer.Serialize(this, Json), Json)!;

    /// <summary>True if switching from this config to <paramref name="other"/> changes what gets indexed.</summary>
    public bool IndexingDiffers(GlimpseConfig other) =>
        !Sources.SequenceEqual(other.Sources)
        || !Extensions.SequenceEqual(other.Extensions, StringComparer.OrdinalIgnoreCase)
        || HydrateCloudFiles != other.HydrateCloudFiles
        || Workers != other.Workers;

    public bool VisualDiffers(GlimpseConfig other) =>
        VisualModelPath != other.VisualModelPath || UseGpu != other.UseGpu || Workers != other.Workers;

    /// <summary>
    /// First-run defaults: the Windows Screenshots and Downloads known folders (they follow OneDrive
    /// folder backup and any relocation), plus OneDrive's own Screenshots folder if it's separate.
    /// More folders (a notes vault, a work share…) are added in Settings.
    /// </summary>
    static GlimpseConfig CreateDefault()
    {
        var candidates = new List<Source>();
        void Add(string name, string? path)
        {
            if (path is null || !Directory.Exists(path)) return;
            if (candidates.Any(c => string.Equals(c.Path, path, StringComparison.OrdinalIgnoreCase))) return;
            candidates.Add(new Source(candidates.Any(c => c.Name == name) ? $"{name}-{candidates.Count}" : name, path));
        }

        Add("screenshots", KnownFolder(Screenshots));
        Add("screenshots", Environment.GetEnvironmentVariable("OneDrive") is { } od ? System.IO.Path.Combine(od, "Pictures", "Screenshots") : null);
        Add("screenshots", System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots"));
        Add("downloads", KnownFolder(Downloads));
        return new GlimpseConfig { Sources = candidates };
    }

    static readonly Guid Screenshots = new("b7bede81-df94-4682-a7d8-57a52620b86f");
    static readonly Guid Downloads = new("374de290-123f-4565-9164-39c4925e467b");

    static string? KnownFolder(Guid id)
    {
        if (SHGetKnownFolderPath(id, 0, 0, out var ptr) != 0) return null;
        try { return Marshal.PtrToStringUni(ptr); }
        finally { Marshal.FreeCoTaskMem(ptr); }
    }

    [LibraryImport("shell32.dll")]
    private static partial int SHGetKnownFolderPath(in Guid id, uint flags, nint token, out nint path);
}
