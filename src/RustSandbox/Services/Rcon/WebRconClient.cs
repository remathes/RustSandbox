using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace RustSandbox.Services.Rcon;

/// <summary>A raw message pushed by the server (log output, chat, or a reply to a command).</summary>
public sealed record RconMessage(int Identifier, string Type, string Message);

public interface IRconTransport : IAsyncDisposable
{
    bool IsConnected { get; }
    event Action<RconMessage>? MessageReceived;
    event Action<string>? Disconnected;
    Task ConnectAsync(CancellationToken ct);
    /// <summary>Runs a console command and returns the server's reply text.</summary>
    Task<string> SendAsync(string command, TimeSpan timeout, CancellationToken ct = default);
}

/// <summary>
/// Facepunch WebRCON client (rcon.web 1). Connects to ws://host:port/password and exchanges JSON
/// frames of the form {"Identifier":1,"Message":"status","Name":"WebRcon"}. Replies echo the Identifier.
/// </summary>
public sealed class WebRconClient(string host, int port, string password) : IRconTransport
{
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _loopCts;
    private Task? _loop;
    private int _nextId = 1000;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<string>> _pending = new();

    public bool IsConnected => _ws?.State == WebSocketState.Open;
    public event Action<RconMessage>? MessageReceived;
    public event Action<string>? Disconnected;

    public async Task ConnectAsync(CancellationToken ct)
    {
        await DisposeSocketAsync();
        _ws = new ClientWebSocket();
        _ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);

        var uri = new Uri($"ws://{host}:{port}/{Uri.EscapeDataString(password)}");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(8));
        await _ws.ConnectAsync(uri, timeout.Token);

        _loopCts = new CancellationTokenSource();
        var ws = _ws;
        var token = _loopCts.Token;
        _loop = Task.Run(() => ReceiveLoopAsync(ws, token));
    }

    public async Task<string> SendAsync(string command, TimeSpan timeout, CancellationToken ct = default)
    {
        var ws = _ws;
        if (ws is null || ws.State != WebSocketState.Open)
            throw new InvalidOperationException("RCON is not connected.");

        var id = Interlocked.Increment(ref _nextId);
        if (id > 2_000_000_000) Interlocked.Exchange(ref _nextId, 1000);

        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        var json = JsonSerializer.Serialize(new { Identifier = id, Message = command, Name = "RustSandbox" });
        var bytes = Encoding.UTF8.GetBytes(json);
        await _sendLock.WaitAsync(ct);
        try
        {
            await ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, ct);
        }
        finally
        {
            _sendLock.Release();
        }

        var completed = await Task.WhenAny(tcs.Task, Task.Delay(timeout, ct));
        _pending.TryRemove(id, out _);
        if (completed != tcs.Task)
        {
            // Many commands legitimately produce no reply; treat silence as an empty response.
            return "";
        }
        return await tcs.Task;
    }

    private async Task ReceiveLoopAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        using var ms = new MemoryStream();
        string reason = "Connection closed";
        try
        {
            while (!ct.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                ms.SetLength(0);
                WebSocketReceiveResult result;
                do
                {
                    result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        reason = string.IsNullOrEmpty(result.CloseStatusDescription) ? "Server closed the connection" : result.CloseStatusDescription;
                        return;
                    }
                    ms.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);

                HandleFrame(Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length));
            }
        }
        catch (OperationCanceledException) { reason = "Disconnected"; }
        catch (Exception ex) { reason = ex.Message; }
        finally
        {
            foreach (var kv in _pending)
            {
                if (_pending.TryRemove(kv.Key, out var p)) p.TrySetResult("");
            }
            if (!ct.IsCancellationRequested) Disconnected?.Invoke(reason);
        }
    }

    private void HandleFrame(string text)
    {
        int identifier = 0;
        string type = "Generic", message = text;
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.TryGetProperty("Identifier", out var idEl) && idEl.ValueKind == JsonValueKind.Number) identifier = idEl.GetInt32();
            if (root.TryGetProperty("Type", out var tEl) && tEl.ValueKind == JsonValueKind.String) type = tEl.GetString() ?? "Generic";
            if (root.TryGetProperty("Message", out var mEl)) message = mEl.ValueKind == JsonValueKind.String ? mEl.GetString() ?? "" : mEl.GetRawText();
        }
        catch (JsonException) { /* not JSON, pass through raw */ }

        if (identifier > 0 && _pending.TryRemove(identifier, out var tcs))
        {
            tcs.TrySetResult(message);
            return;
        }
        MessageReceived?.Invoke(new RconMessage(identifier, type, message));
    }

    private async Task DisposeSocketAsync()
    {
        try { _loopCts?.Cancel(); } catch { /* ignore */ }
        if (_ws is { State: WebSocketState.Open })
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", cts.Token);
            }
            catch { /* ignore */ }
        }
        _ws?.Dispose();
        _ws = null;
        _loopCts?.Dispose();
        _loopCts = null;
    }

    public async ValueTask DisposeAsync()
    {
        await DisposeSocketAsync();
        if (_loop is not null)
        {
            try { await _loop.WaitAsync(TimeSpan.FromSeconds(2)); } catch { /* ignore */ }
        }
    }
}
