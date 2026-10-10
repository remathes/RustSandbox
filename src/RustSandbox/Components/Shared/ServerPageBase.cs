using Microsoft.AspNetCore.Components;
using RustSandbox.Data;
using RustSandbox.Services;

namespace RustSandbox.Components.Shared;

/// <summary>
/// Base for every /s/{Id}/... page: loads the server, follows live updates (coalesced so a busy
/// server doesn't re-render dozens of times a second) and tells the shell which server is active.
/// </summary>
public abstract class ServerPageBase : ComponentBase, IDisposable
{
    [Parameter] public int Id { get; set; }

    [Inject] protected ServerService Servers { get; set; } = default!;
    [Inject] protected ServerConnectionManager Connections { get; set; } = default!;
    [Inject] protected ProcessManager Processes { get; set; } = default!;
    [Inject] protected LiveEvents Live { get; set; } = default!;
    [Inject] protected ServerContext Context { get; set; } = default!;
    [Inject] protected ToastService Toasts { get; set; } = default!;
    [Inject] protected NavigationManager Nav { get; set; } = default!;

    protected ServerInstance? Server { get; private set; }
    protected ServerRuntime Runtime => Connections.Get(Id);
    protected ProcessState Process => Processes.Get(Id);
    protected bool NotFound { get; private set; }

    private int _loadedId;
    private int _refreshQueued;
    private bool _disposed;

    /// <summary>Minimum gap between live-driven re-renders.</summary>
    protected virtual TimeSpan RefreshInterval => TimeSpan.FromMilliseconds(700);

    protected override async Task OnParametersSetAsync()
    {
        if (_loadedId != Id)
        {
            _loadedId = Id;
            await ReloadServerAsync();
            Context.Set(Server?.Id);
            await OnServerChangedAsync();
        }
    }

    protected async Task ReloadServerAsync()
    {
        Server = await Servers.GetAsync(Id);
        NotFound = Server is null;
    }

    /// <summary>Called after the server for this page is (re)loaded.</summary>
    protected virtual Task OnServerChangedAsync() => Task.CompletedTask;

    protected override void OnInitialized()
    {
        Live.StatusChanged += OnStatus;
        Live.ServersChanged += OnServersChanged;
    }

    private void OnStatus(int id)
    {
        if (id == Id) RequestRefresh();
    }

    private void OnServersChanged()
    {
        _ = InvokeAsync(async () =>
        {
            if (_disposed) return;
            await ReloadServerAsync();
            StateHasChanged();
        });
    }

    /// <summary>Schedule a re-render soon; repeated calls within the interval collapse into one.</summary>
    protected void RequestRefresh()
    {
        if (_disposed || Interlocked.Exchange(ref _refreshQueued, 1) == 1) return;
        _ = Task.Run(async () =>
        {
            await Task.Delay(RefreshInterval);
            Interlocked.Exchange(ref _refreshQueued, 0);
            if (_disposed) return;
            try { await InvokeAsync(OnLiveRefreshAsync); }
            catch (ObjectDisposedException) { }
        });
    }

    /// <summary>Override to pull fresh data before re-rendering on live updates.</summary>
    protected virtual Task OnLiveRefreshAsync()
    {
        StateHasChanged();
        return Task.CompletedTask;
    }

    public virtual void Dispose()
    {
        _disposed = true;
        Live.StatusChanged -= OnStatus;
        Live.ServersChanged -= OnServersChanged;
        GC.SuppressFinalize(this);
    }
}
