using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using Glimpse.Core;
using Glimpse.Core.Index;
using Glimpse.Core.Ipc;
using Glimpse.Core.Visual;

namespace Glimpse.App;

/// <summary>
/// Answers the Command Palette extension (and anything else that speaks <see cref="GlimpseIpc"/>) over a
/// per-user named pipe. Searches run on the warm engine the window uses; UI-bound operations
/// (clipboard, showing the window) are handed to the window through the callbacks.
/// </summary>
public sealed class IpcServer(SearchEngine engine, Func<string, Task> copyImage, Action<string?> show) : IDisposable
{
    const int Listeners = 4;
    readonly CancellationTokenSource _stop = new();

    public void Start()
    {
        for (var i = 0; i < Listeners; i++) _ = Task.Run(ListenAsync);
    }

    async Task ListenAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(GlimpseIpc.PipeName, PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(_stop.Token);

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                var line = await GlimpseIpc.ReadLineAsync(pipe, timeout.Token);
                var request = line is null ? null : JsonSerializer.Deserialize(line, IpcJson.Default.IpcRequest);
                var response = request is null ? new IpcResponse(false, "bad request") : await HandleAsync(request, timeout.Token);
                await GlimpseIpc.WriteLineAsync(pipe, JsonSerializer.Serialize(response, IpcJson.Default.IpcResponse), timeout.Token);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException)
            {
                // The client hung up (a newer keystroke superseded its search) or timed out. Normal.
            }
            catch (Exception ex)
            {
                // One bad client mustn't take the listener down.
                App.Log(ex);
            }
        }
    }

    async Task<IpcResponse> HandleAsync(IpcRequest request, CancellationToken ct)
    {
        switch (request.Op)
        {
            case "ping":
                return new IpcResponse(true, VisualAvailable: engine.VisualAvailable);

            case "search":
                var query = request.Query ?? "";
                if (request.VisualOnly && !query.TrimStart().StartsWith('~')) query = "~" + query;
                var hits = engine.Search(query, Math.Clamp(request.Limit, 1, 120));
                var results = await Task.WhenAll(hits.Select(async h =>
                {
                    var thumb = await ThumbnailAsync(h.Path, ct);
                    return new IpcHit(
                        h.Id, h.Path, h.Source, h.Modified, h.Width, h.Height,
                        h.Snippet.Replace(ImageIndex.MatchStart.ToString(), "").Replace(ImageIndex.MatchEnd.ToString(), "").ReplaceLineEndings(" "),
                        h.VisualScore,
                        thumb,
                        thumb is null ? null : await File.ReadAllBytesAsync(thumb, ct));
                }));
                return new IpcResponse(true, Hits: results, VisualAvailable: engine.VisualAvailable);

            case "copy" when request.Query is { Length: > 0 } path && File.Exists(path):
                await copyImage(path);
                return new IpcResponse(true);

            case "reveal" when request.Query is { Length: > 0 } path && File.Exists(path):
                Process.Start("explorer.exe", $"/select,\"{path}\"");
                return new IpcResponse(true);

            case "show":
                show(request.Query);
                return new IpcResponse(true);

            default:
                return new IpcResponse(false, $"unknown op '{request.Op}'");
        }
    }

    // Decoding big screenshots is CPU-heavy; don't let one search start dozens at once.
    readonly SemaphoreSlim _thumbGate = new(4, 4);

    async Task<string?> ThumbnailAsync(string path, CancellationToken ct)
    {
        await _thumbGate.WaitAsync(ct);
        try { return await Thumbnails.GetAsync(path, ct); }
        finally { _thumbGate.Release(); }
    }

    public void Dispose() => _stop.Cancel();
}
