using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Glimpse.Core.Ipc;

// Shared by the Glimpse app (server) and the Command Palette extension (client). The extension compiles
// this file directly instead of referencing Glimpse.Core, so it doesn't drag SQLite/ONNX into its package.
// Wire format: one UTF-8 JSON request line, one JSON response line, then the connection closes.

public sealed record IpcRequest(
    string Op,                 // search | copy | copytext (Id) | reveal | show | ping
    string? Query = null,
    long? Id = null,
    int Limit = 40,
    bool VisualOnly = false);

public sealed record IpcHit(
    long Id,
    string Path,
    string Source,
    DateTime Modified,
    int Width,
    int Height,
    string Snippet,            // plain text, match markers stripped
    float? VisualScore,
    string? Thumbnail,         // cached ~256px PNG path, or null if it couldn't be made
    // The same thumbnail's bytes. Packaged apps (Command Palette) get a virtualized view of AppData and
    // can't read the cache path, so the image travels in the response instead.
    byte[]? ThumbnailPng = null);

public sealed record IpcResponse(bool Ok, string? Error = null, IpcHit[]? Hits = null, bool VisualAvailable = false);

[JsonSerializable(typeof(IpcRequest))]
[JsonSerializable(typeof(IpcResponse))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
public sealed partial class IpcJson : JsonSerializerContext;

public static class GlimpseIpc
{
    /// <summary>Per-user pipe name, so two people signed in to one PC don't see each other's images.</summary>
    public static string PipeName => $"Glimpse.Search.{Environment.UserName}";

    public static async Task<IpcResponse> SendAsync(IpcRequest request, TimeSpan timeout, CancellationToken ct = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        await using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(cts.Token);
        await WriteLineAsync(pipe, JsonSerializer.Serialize(request, IpcJson.Default.IpcRequest), cts.Token);
        var line = await ReadLineAsync(pipe, cts.Token);
        return (line is null ? null : JsonSerializer.Deserialize(line, IpcJson.Default.IpcResponse))
            ?? new IpcResponse(false, "empty response");
    }

    public static async Task WriteLineAsync(Stream stream, string line, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(line + "\n");
        await stream.WriteAsync(bytes, ct);
        await stream.FlushAsync(ct);
    }

    public static async Task<string?> ReadLineAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new MemoryStream();
        var one = new byte[4096];
        while (true)
        {
            var n = await stream.ReadAsync(one, ct);
            if (n == 0) break;
            var newline = Array.IndexOf(one, (byte)'\n', 0, n);
            if (newline >= 0)
            {
                buffer.Write(one, 0, newline);
                break;
            }
            buffer.Write(one, 0, n);
        }
        return buffer.Length == 0 ? null : Encoding.UTF8.GetString(buffer.ToArray());
    }
}
