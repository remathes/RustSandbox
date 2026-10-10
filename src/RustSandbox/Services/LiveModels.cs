using RustSandbox.Data;

namespace RustSandbox.Services;

public enum ConsoleKind { Log, Warning, Error, Chat, Command, Response, System, Process }

public sealed record ConsoleLine(DateTime Time, ConsoleKind Kind, string Text);

public sealed record ChatMessage(DateTime Time, string SteamId, string Name, string Message, int Channel)
{
    public bool IsTeam => Channel == 1;
}

/// <summary>Parsed result of the Rust "serverinfo" RCON command.</summary>
public sealed class ServerInfo
{
    public string Hostname { get; set; } = "";
    public int MaxPlayers { get; set; }
    public int Players { get; set; }
    public int Queued { get; set; }
    public int Joining { get; set; }
    public int EntityCount { get; set; }
    public string GameTime { get; set; } = "";
    public int Uptime { get; set; }
    public string Map { get; set; } = "";
    public double Framerate { get; set; }
    public int Memory { get; set; }
    public long NetworkIn { get; set; }
    public long NetworkOut { get; set; }
    public string Version { get; set; } = "";
    public string Protocol { get; set; } = "";
    public string SaveCreatedTime { get; set; } = "";
    public bool Restarting { get; set; }
}

public sealed class OnlinePlayer
{
    public string SteamId { get; set; } = "";
    public string Name { get; set; } = "";
    public int Ping { get; set; }
    public string Address { get; set; } = "";
    public int ConnectedSeconds { get; set; }
    public double Health { get; set; }
    public double ViolationLevel { get; set; }
}

public sealed record MetricPoint(DateTime Time, double Fps, int Players, int Entities, int MemoryMb, long NetIn, long NetOut, int Queued);

public sealed class LivePlayer
{
    public string SteamId { get; set; } = "";
    public string Name { get; set; } = "";
    public float X { get; set; }
    public float Z { get; set; }
    public float Health { get; set; }
    public DateTime Updated { get; set; }
}

/// <summary>Small thread-safe fixed-size buffer for console lines, metrics and the like.</summary>
public sealed class RingBuffer<T>(int capacity)
{
    private readonly Queue<T> _items = new(capacity);
    private readonly object _lock = new();

    public int Capacity { get; } = capacity;

    public void Add(T item)
    {
        lock (_lock)
        {
            if (_items.Count >= Capacity) _items.Dequeue();
            _items.Enqueue(item);
        }
    }

    public List<T> Snapshot()
    {
        lock (_lock) return _items.ToList();
    }

    public List<T> Last(int count)
    {
        lock (_lock) return _items.Skip(Math.Max(0, _items.Count - count)).ToList();
    }

    public void Clear()
    {
        lock (_lock) _items.Clear();
    }

    public int Count { get { lock (_lock) return _items.Count; } }
}

/// <summary>
/// In-process pub/sub so Blazor components refresh when something happens on a server.
/// Handlers are isolated so one misbehaving subscriber can't break the others.
/// </summary>
public sealed class LiveEvents(ILogger<LiveEvents> log)
{
    public event Action<int, ConsoleLine>? ConsoleLineAdded;
    public event Action<int, ChatMessage>? ChatReceived;
    public event Action<int, KillEvent>? KillRecorded;
    public event Action<int>? PositionsUpdated;
    public event Action<int>? StatusChanged;
    public event Action? ServersChanged;

    public void RaiseConsole(int id, ConsoleLine line) => Fire(ConsoleLineAdded, h => h(id, line));
    public void RaiseChat(int id, ChatMessage msg) => Fire(ChatReceived, h => h(id, msg));
    public void RaiseKill(int id, KillEvent kill) => Fire(KillRecorded, h => h(id, kill));
    public void RaisePositions(int id) => Fire(PositionsUpdated, h => h(id));
    public void RaiseStatus(int id) => Fire(StatusChanged, h => h(id));
    public void RaiseServersChanged() => Fire(ServersChanged, h => h());

    private void Fire<TDelegate>(TDelegate? evt, Action<TDelegate> invoke) where TDelegate : Delegate
    {
        if (evt is null) return;
        foreach (var d in evt.GetInvocationList())
        {
            try { invoke((TDelegate)d); }
            catch (Exception ex) { log.LogDebug(ex, "Live event subscriber threw"); }
        }
    }
}
