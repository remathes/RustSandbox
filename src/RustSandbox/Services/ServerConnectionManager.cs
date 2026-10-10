using System.Collections.Concurrent;
using System.Net.WebSockets;
using RustSandbox.Data;
using RustSandbox.Services.Demo;
using RustSandbox.Services.Rcon;

namespace RustSandbox.Services;

public enum ConnectionState { Offline, Connecting, Online, Error }

/// <summary>All live (non-persisted) state for one server: connection, console, metrics, players.</summary>
public sealed class ServerRuntime(int serverId)
{
    public int ServerId { get; } = serverId;
    public ServerInstance? Server { get; internal set; }

    public ConnectionState State { get; internal set; } = ConnectionState.Offline;
    public string? LastError { get; internal set; }
    public DateTime? OnlineSinceUtc { get; internal set; }

    public ServerInfo? Info { get; internal set; }
    public DateTime? InfoUpdatedUtc { get; internal set; }
    public IReadOnlyList<OnlinePlayer> Players { get; internal set; } = Array.Empty<OnlinePlayer>();

    public RingBuffer<ConsoleLine> Console { get; } = new(3000);
    public RingBuffer<ChatMessage> Chat { get; } = new(500);
    /// <summary>One point every poll (5s) — one hour of history.</summary>
    public RingBuffer<MetricPoint> Metrics { get; } = new(720);

    public bool IsOnline => State == ConnectionState.Online && Transport is { IsConnected: true };

    internal IRconTransport? Transport { get; set; }
    internal string? TransportKey { get; set; }
    internal int Failures { get; set; }
    internal DateTime NextAttemptUtc { get; set; }
    internal bool? WasOnline { get; set; }
    internal SemaphoreSlim ConnectLock { get; } = new(1, 1);
}

/// <summary>Owns one RCON connection per server and routes everything it says into the live buffers.</summary>
public sealed class ServerConnectionManager(LiveEvents live, DemoWorld demo, ILogger<ServerConnectionManager> log)
{
    private readonly ConcurrentDictionary<int, ServerRuntime> _runtimes = new();

    public ServerRuntime Get(int serverId) => _runtimes.GetOrAdd(serverId, id => new ServerRuntime(id));

    public IEnumerable<ServerRuntime> All => _runtimes.Values;

    private static string KeyFor(ServerInstance s) =>
        $"{s.IsDemo}|{s.Settings.RconHost}|{s.Settings.RconPort}|{s.Settings.RconPassword}";

    /// <summary>Refresh the cached server definition; drops the connection if RCON details changed.</summary>
    public void UpdateServer(ServerInstance s)
    {
        var rt = Get(s.Id);
        rt.Server = s;
        if (rt.TransportKey is not null && rt.TransportKey != KeyFor(s))
        {
            _ = DisconnectAsync(s.Id, "RCON settings changed — reconnecting");
            rt.NextAttemptUtc = DateTime.MinValue;
        }
    }

    public async Task<bool> EnsureConnectedAsync(ServerInstance s, bool force = false, CancellationToken ct = default)
    {
        var rt = Get(s.Id);
        rt.Server = s;
        if (rt.IsOnline && rt.TransportKey == KeyFor(s)) return true;
        if (!force && DateTime.UtcNow < rt.NextAttemptUtc) return false;

        await rt.ConnectLock.WaitAsync(ct);
        IRconTransport? transport = null;
        try
        {
            if (rt.IsOnline && rt.TransportKey == KeyFor(s)) return true;

            if (!s.IsDemo && string.IsNullOrWhiteSpace(s.Settings.RconPassword))
            {
                SetState(rt, ConnectionState.Error, "Set an RCON password in Settings to connect.");
                rt.NextAttemptUtc = DateTime.UtcNow.AddSeconds(30);
                return false;
            }

            await DropTransportAsync(rt);

            transport = s.IsDemo
                ? demo.CreateTransport(s.Id)
                : new WebRconClient(s.Settings.RconHost, s.Settings.RconPort, s.Settings.RconPassword);
            transport.MessageReceived += m => OnMessage(rt, m);
            var created = transport;
            transport.Disconnected += reason => OnDisconnected(rt, created, reason);

            SetState(rt, ConnectionState.Connecting, null);
            await transport.ConnectAsync(ct);

            rt.Transport = transport;
            rt.TransportKey = KeyFor(s);
            rt.Failures = 0;
            rt.OnlineSinceUtc = DateTime.UtcNow;
            SetState(rt, ConnectionState.Online, null);
            AppendConsole(s.Id, ConsoleKind.System, s.IsDemo
                ? "Connected to demo server (simulated data)."
                : $"Connected to RCON at {s.Settings.RconHost}:{s.Settings.RconPort}.");
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            if (transport is not null && !ReferenceEquals(rt.Transport, transport))
            {
                try { await transport.DisposeAsync(); } catch { /* ignore */ }
            }
            rt.Failures++;
            var backoff = Math.Min(60, 5 * Math.Pow(2, Math.Min(rt.Failures - 1, 4)));
            rt.NextAttemptUtc = DateTime.UtcNow.AddSeconds(backoff);
            var msg = FriendlyError(ex, s);
            if (rt.Failures == 1 || rt.LastError != msg)
                AppendConsole(s.Id, ConsoleKind.System, $"RCON connection failed: {msg} (retrying in {backoff:0}s)");
            SetState(rt, ConnectionState.Error, msg);
            log.LogDebug(ex, "RCON connect failed for server {Id}", s.Id);
            return false;
        }
        finally
        {
            rt.ConnectLock.Release();
        }
    }

    /// <summary>Run a command typed by a person: echoes it and the reply into the console.</summary>
    public async Task<string> ExecuteAsync(int serverId, string command, CancellationToken ct = default)
    {
        command = command.Trim();
        if (command.Length == 0) return "";
        var rt = Get(serverId);
        AppendConsole(serverId, ConsoleKind.Command, command);
        if (!rt.IsOnline)
        {
            if (rt.Server is null || !await EnsureConnectedAsync(rt.Server, force: true, ct))
            {
                AppendConsole(serverId, ConsoleKind.Error, "Not connected — command not sent.");
                return "";
            }
        }
        try
        {
            var reply = await rt.Transport!.SendAsync(command, TimeSpan.FromSeconds(10), ct);
            if (!string.IsNullOrWhiteSpace(reply)) AppendConsole(serverId, ConsoleKind.Response, reply);
            return reply;
        }
        catch (Exception ex)
        {
            AppendConsole(serverId, ConsoleKind.Error, $"Command failed: {ex.Message}");
            return "";
        }
    }

    /// <summary>Run a command silently (polling, automation). Returns null when not connected.</summary>
    public async Task<string?> QueryAsync(int serverId, string command, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var rt = Get(serverId);
        if (!rt.IsOnline) return null;
        try
        {
            return await rt.Transport!.SendAsync(command, timeout ?? TimeSpan.FromSeconds(6), ct);
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "RCON query '{Cmd}' failed on {Id}", command, serverId);
            return null;
        }
    }

    public void AppendConsole(int serverId, ConsoleKind kind, string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        var rt = Get(serverId);
        var now = DateTime.UtcNow;
        // Keep multi-line replies (status, plugin lists) together as one entry, but cap runaway output.
        if (text.Length > 20000) text = text[..20000] + "\n… (truncated)";
        var line = new ConsoleLine(now, kind, text.TrimEnd('\r', '\n'));
        rt.Console.Add(line);
        live.RaiseConsole(serverId, line);
    }

    public async Task DisconnectAsync(int serverId, string? reason = null)
    {
        var rt = Get(serverId);
        await DropTransportAsync(rt);
        rt.OnlineSinceUtc = null;
        SetState(rt, ConnectionState.Offline, reason);
        if (reason is not null) AppendConsole(serverId, ConsoleKind.System, reason);
    }

    public async Task RemoveAsync(int serverId)
    {
        if (_runtimes.TryRemove(serverId, out var rt)) await DropTransportAsync(rt);
    }

    private async Task DropTransportAsync(ServerRuntime rt)
    {
        var t = rt.Transport;
        rt.Transport = null;
        rt.TransportKey = null;
        if (t is not null)
        {
            try { await t.DisposeAsync(); } catch { /* ignore */ }
        }
    }

    private void OnMessage(ServerRuntime rt, RconMessage m)
    {
        if (string.IsNullOrWhiteSpace(m.Message)) return;
        switch (m.Type)
        {
            case "Chat":
                var chat = RconParsers.ParseChat(m.Message);
                if (chat is not null)
                {
                    rt.Chat.Add(chat);
                    live.RaiseChat(rt.ServerId, chat);
                    AppendConsole(rt.ServerId, ConsoleKind.Chat, $"{(chat.IsTeam ? "[TEAM] " : "")}{chat.Name}: {chat.Message}");
                }
                break;
            case "Warning":
                AppendConsole(rt.ServerId, ConsoleKind.Warning, m.Message);
                break;
            case "Error":
                AppendConsole(rt.ServerId, ConsoleKind.Error, m.Message);
                break;
            default:
                AppendConsole(rt.ServerId, ConsoleKind.Log, m.Message);
                break;
        }
    }

    private void OnDisconnected(ServerRuntime rt, IRconTransport transport, string reason)
    {
        if (!ReferenceEquals(rt.Transport, transport)) return; // an old connection we already replaced
        rt.Transport = null;
        rt.TransportKey = null;
        rt.OnlineSinceUtc = null;
        rt.NextAttemptUtc = DateTime.UtcNow.AddSeconds(5);
        AppendConsole(rt.ServerId, ConsoleKind.System, $"RCON disconnected: {reason}");
        SetState(rt, ConnectionState.Offline, reason);
        _ = transport.DisposeAsync().AsTask();
    }

    private void SetState(ServerRuntime rt, ConnectionState state, string? error)
    {
        rt.State = state;
        rt.LastError = error;
        live.RaiseStatus(rt.ServerId);
    }

    private static string FriendlyError(Exception ex, ServerInstance s) => ex switch
    {
        WebSocketException or System.Net.Http.HttpRequestException =>
            $"Couldn't reach {s.Settings.RconHost}:{s.Settings.RconPort}. Check the server is running with +rcon.web 1, the port is open, and the password is right.",
        OperationCanceledException => $"Timed out connecting to {s.Settings.RconHost}:{s.Settings.RconPort}.",
        _ => ex.Message,
    };
}
