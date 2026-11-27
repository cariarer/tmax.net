using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;

namespace Tmax.App.Rpc;

/// <summary>
/// Request/response + event bridge between the React frontend and the backend services,
/// modelled on Electron's ipcRenderer semantics so the renderer only needs a thin shim:
///
///   client → server  {"type":"invoke","id":1,"channel":"pty:create","args":[...]}   (expects a reply)
///   client → server  {"type":"send","channel":"pty:write","args":[...]}              (fire-and-forget)
///   server → client  {"type":"reply","id":1,"result":...}  or  {"type":"reply","id":1,"error":"..."}
///   server → client  {"type":"event","channel":"pty:data","args":[...]}             (broadcast)
/// </summary>
public sealed class RpcServer(ILogger<RpcServer> logger)
{
    public delegate Task<object?> Handler(JsonElement[] args, CancellationToken ct);

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly ConcurrentDictionary<string, Handler> _handlers = new();
    private readonly ConcurrentDictionary<Guid, Channel<string>> _clients = new();

    public void Handle(string channel, Handler handler)
    {
        if (!_handlers.TryAdd(channel, handler))
            throw new InvalidOperationException($"Handler already registered: {channel}");
    }

    /// <summary>Convenience overload for handlers that return nothing.</summary>
    public void Handle(string channel, Func<JsonElement[], CancellationToken, Task> handler) =>
        Handle(channel, async (args, ct) => { await handler(args, ct); return null; });

    /// <summary>Send an event to every connected window.</summary>
    public void Broadcast(string channel, params object?[] args)
    {
        var json = JsonSerializer.Serialize(new { type = "event", channel, args }, Json);
        foreach (var client in _clients.Values) client.Writer.TryWrite(json);
    }

    public async Task RunConnectionAsync(WebSocket socket, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        // Unbounded for now; see backlog task on output batching/backpressure.
        var outbox = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
        _clients[id] = outbox;
        logger.LogInformation("Client {Id} connected ({Count} total)", id, _clients.Count);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var writer = WriteLoopAsync(socket, outbox.Reader, cts.Token);
        try
        {
            await ReadLoopAsync(socket, outbox.Writer, cts.Token);
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException ex)
        {
            logger.LogDebug(ex, "Client {Id} socket error", id);
        }
        finally
        {
            if (socket.State == WebSocketState.Open)
            {
                // Tell the client we're going away (app shutdown) instead of just dropping the TCP connection.
                using var closeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                try { await socket.CloseOutputAsync(WebSocketCloseStatus.EndpointUnavailable, "shutting down", closeTimeout.Token); }
                catch (Exception ex) when (ex is WebSocketException or OperationCanceledException) { }
            }
            _clients.TryRemove(id, out _);
            outbox.Writer.TryComplete();
            await cts.CancelAsync();
            try { await writer; } catch (OperationCanceledException) { } catch (WebSocketException) { }
            logger.LogInformation("Client {Id} disconnected", id);
        }
    }

    private async Task ReadLoopAsync(WebSocket socket, ChannelWriter<string> outbox, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        while (socket.State == WebSocketState.Open)
        {
            var result = await socket.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, ct);
                return;
            }
            message.Write(buffer, 0, result.Count);
            if (!result.EndOfMessage) continue;

            var text = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
            message.SetLength(0);
            // Dispatch without awaiting so a slow invoke (e.g. a git call) doesn't block keystrokes.
            // Fire-and-forget "send" messages are processed inline to keep their order (pty:write!).
            Dispatch(text, outbox, ct);
        }
    }

    private void Dispatch(string text, ChannelWriter<string> outbox, CancellationToken ct)
    {
        RpcRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<RpcRequest>(text, Json);
        }
        catch (JsonException ex)
        {
            logger.LogWarning("Malformed RPC message: {Error}", ex.Message);
            return;
        }
        if (request?.Channel is null) return;

        if (!_handlers.TryGetValue(request.Channel, out var handler))
        {
            logger.LogWarning("No handler for {Channel}", request.Channel);
            if (request.Type == "invoke")
                outbox.TryWrite(Reply(request.Id, error: $"No handler registered for '{request.Channel}'"));
            return;
        }

        var args = request.Args ?? [];
        if (request.Type == "send")
        {
            var task = handler(args, ct);
            if (!task.IsCompletedSuccessfully)
                task.ContinueWith(t => logger.LogWarning(t.Exception, "send {Channel} failed", request.Channel),
                    TaskContinuationOptions.OnlyOnFaulted);
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var result = await handler(args, ct);
                outbox.TryWrite(Reply(request.Id, result: result));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning("invoke {Channel} failed: {Error}", request.Channel, ex.Message);
                outbox.TryWrite(Reply(request.Id, error: ex.Message));
            }
        }, ct);
    }

    private static string Reply(int? id, object? result = null, string? error = null) =>
        JsonSerializer.Serialize(new { type = "reply", id, result, error }, Json);

    private static async Task WriteLoopAsync(WebSocket socket, ChannelReader<string> outbox, CancellationToken ct)
    {
        await foreach (var json in outbox.ReadAllAsync(ct))
        {
            if (socket.State != WebSocketState.Open) return;
            await socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, endOfMessage: true, ct);
        }
    }

    private sealed record RpcRequest(string Type, int? Id, string? Channel, JsonElement[]? Args);
}

public static class RpcArgs
{
    public static T Get<T>(this JsonElement[] args, int index) =>
        index < args.Length
            ? args[index].Deserialize<T>(RpcServer.Json) ?? throw new ArgumentException($"Argument {index} is null")
            : throw new ArgumentException($"Missing argument {index}");

    /// <summary>Optional argument: missing, null or undefined (sent as null) give default.</summary>
    public static T? Opt<T>(this JsonElement[] args, int index) =>
        index < args.Length && args[index].ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
            ? args[index].Deserialize<T>(RpcServer.Json)
            : default;
}
