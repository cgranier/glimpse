using System.Text.Json;
using System.Text.Json.Serialization;

namespace Glimpse.Core;

/// <summary>A folder Glimpse watches and indexes. <see cref="Name"/> is what `in:` filters match.</summary>
public sealed record Source(string Name, string Path, bool Recursive = true);

public sealed class GlimpseConfig
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

    /// <summary>Folder name under models\ holding the CLIP ONNX files. Empty disables visual search.</summary>
    public string VisualModel { get; set; } = "clip-vit-b16";

    /// <summary>Run the image encoder on the GPU (DirectML). Falls back to CPU automatically.</summary>
    public bool UseGpu { get; set; } = true;

    [JsonIgnore]
    public string? VisualModelPath => string.IsNullOrWhiteSpace(VisualModel) ? null : System.IO.Path.Combine(DataDir, "models", VisualModel);

    /// <summary>Concurrent OCR workers. Windows OCR is CPU-bound; half the cores keeps the machine responsive.</summary>
    public int Workers { get; set; } = Math.Max(1, Environment.ProcessorCount / 2);

    public static string DataDir { get; } =
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Glimpse");

    public static string ConfigPath => System.IO.Path.Combine(DataDir, "config.json");
    public static string DatabasePath => System.IO.Path.Combine(DataDir, "index.db");

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

    /// <summary>First-run defaults: whichever of the usual suspects exist on this machine.</summary>
    static GlimpseConfig CreateDefault()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidates = new[]
        {
            new Source("screenshots", System.IO.Path.Combine(home, "OneDrive", "Pictures", "Screenshots")),
            new Source("screenshots-old", System.IO.Path.Combine(home, "OneDrive", "Pictures", "Screenshots 1")),
            new Source("screenshots-local", System.IO.Path.Combine(home, "Pictures", "Screenshots")),
            new Source("notes", System.IO.Path.Combine(home, "_cowork", "_mNOTES")),
            new Source("downloads", System.IO.Path.Combine(home, "Downloads")),
        };
        return new GlimpseConfig { Sources = candidates.Where(s => Directory.Exists(s.Path)).ToList() };
    }
}
