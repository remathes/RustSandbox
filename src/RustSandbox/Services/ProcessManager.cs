using System.Collections.Concurrent;
using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RustSandbox.Data;

namespace RustSandbox.Services;

public sealed class ProcessState
{
    public Process? Process { get; internal set; }
    public DateTime? StartedUtc { get; internal set; }
    public bool IsRunning
    {
        get
        {
            try { return Process is { HasExited: false }; }
            catch { return false; }
        }
    }
    public int? Pid => IsRunning ? Process!.Id : null;

    /// <summary>Long-running job in progress (update, install, countdown...).</summary>
    public string? Activity { get; internal set; }
    public DateTime? CountdownEndUtc { get; internal set; }

    internal bool StopRequested { get; set; }
    internal bool StartAfterExit { get; set; }
    internal CancellationTokenSource? CountdownCts { get; set; }
    internal List<DateTime> RecentCrashes { get; } = new();
    internal SemaphoreSlim Lock { get; } = new(1, 1);
}

/// <summary>
/// Runs RustDedicated on this machine (when "Manage process" is on): start/stop/restart with in-game
/// countdowns, crash auto-restart, SteamCMD updates, Oxide/Carbon installs, wipes and plugin install.
/// For servers hosted elsewhere it falls back to RCON commands where that makes sense.
/// </summary>
public sealed class ProcessManager(
    ServerConnectionManager conn,
    LiveEvents live,
    IDbContextFactory<AppDbContext> dbFactory,
    DiscordNotifier discord,
    LiveWorldStore world,
    IHttpClientFactory http,
    ILogger<ProcessManager> log)
{
    private readonly ConcurrentDictionary<int, ProcessState> _states = new();

    public ProcessState Get(int serverId) => _states.GetOrAdd(serverId, _ => new ProcessState());

    private void Console(int id, string text, ConsoleKind kind = ConsoleKind.System) => conn.AppendConsole(id, kind, text);

    private void SetActivity(int id, string? activity, DateTime? countdownEnd = null)
    {
        var st = Get(id);
        st.Activity = activity;
        st.CountdownEndUtc = countdownEnd;
        live.RaiseStatus(id);
    }

    private async Task<ServerInstance?> ReloadAsync(int id)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Servers.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id);
    }

    // ------------------------------------------------------------------ start / stop

    public string? ValidateLocal(ServerInstance s)
    {
        if (s.IsDemo) return "Demo servers are simulated and can't be started or stopped.";
        if (!s.Settings.ManageProcess) return "This server isn't managed by this machine. Turn on \"Run the server process on this machine\" in Settings → Hosting.";
        if (string.IsNullOrWhiteSpace(s.Settings.InstallDir)) return "Set the server install folder in Settings → Hosting.";
        if (!Directory.Exists(s.Settings.InstallDir)) return $"Install folder not found: {s.Settings.InstallDir}";
        return null;
    }

    private readonly ConcurrentDictionary<int, DateTime> _lastAdoptCheck = new();

    /// <summary>RustDedicated processes launched from this server's install folder.</summary>
    private static List<Process> FindRunning(ServerInstance s)
    {
        var found = new List<Process>();
        if (string.IsNullOrWhiteSpace(s.Settings.InstallDir)) return found;
        var exe = Path.GetFullPath(LaunchArguments.ExecutablePath(s.Settings));
        foreach (var p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(LaunchArguments.ExecutableName)))
        {
            try
            {
                var path = p.MainModule?.FileName;
                if (path is not null && string.Equals(Path.GetFullPath(path), exe, StringComparison.OrdinalIgnoreCase))
                {
                    found.Add(p);
                    continue;
                }
            }
            catch { /* no access to that process — not ours */ }
            p.Dispose();
        }
        return found;
    }

    /// <summary>
    /// Pick up a server that is already running from this folder (e.g. after the manager restarted),
    /// so it isn't started twice. Returns true when the server is running and tracked.
    /// </summary>
    public bool TryAdopt(ServerInstance s, bool force = false)
    {
        if (s.IsDemo || !s.Settings.ManageProcess) return false;
        var st = Get(s.Id);
        if (st.IsRunning) return true;
        if (!force && _lastAdoptCheck.TryGetValue(s.Id, out var last) && DateTime.UtcNow - last < TimeSpan.FromSeconds(30)) return false;
        _lastAdoptCheck[s.Id] = DateTime.UtcNow;

        var found = FindRunning(s);
        if (found.Count == 0) return false;
        found = found.OrderBy(x => { try { return x.StartTime; } catch { return DateTime.MaxValue; } }).ToList();
        var p = found[0];
        var id = s.Id;
        try
        {
            p.EnableRaisingEvents = true;
            p.Exited += (_, _) => _ = OnExitedAsync(id, p);
        }
        catch { /* we can still see it; we just won't get an exit event */ }
        st.Process = p;
        try { st.StartedUtc = p.StartTime.ToUniversalTime(); } catch { st.StartedUtc = DateTime.UtcNow; }
        st.StopRequested = false;
        Console(id, $"Found {LaunchArguments.ExecutableName} already running from this folder (PID {p.Id}) — tracking it again. Its startup output isn't shown, but RCON works as normal.");
        foreach (var extra in found.Skip(1))
            Console(id, $"Another copy is also running from the same folder (PID {extra.Id}). It can't use the same ports — end it in Task Manager.", ConsoleKind.Warning);
        live.RaiseStatus(id);
        return true;
    }

    public async Task<string?> StartAsync(ServerInstance s)
    {
        var error = ValidateLocal(s);
        if (error is not null) return error;
        if (TryAdopt(s, force: true))
            return "The server was already running, so a second copy wasn't started — the manager reconnected to it.";
        var exe = LaunchArguments.ExecutablePath(s.Settings);
        if (!File.Exists(exe)) return $"{LaunchArguments.ExecutableName} not found in {s.Settings.InstallDir}. Use \"Install / update server\" first.";

        var st = Get(s.Id);
        await st.Lock.WaitAsync();
        try
        {
            if (st.IsRunning) return "The server is already running.";

            var psi = new ProcessStartInfo(exe, LaunchArguments.Build(s.Settings))
            {
                WorkingDirectory = s.Settings.InstallDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                CreateNoWindow = true,
            };
            if (!OperatingSystem.IsWindows())
            {
                var lib = Path.Combine(s.Settings.InstallDir, "RustDedicated_Data", "Plugins", "x86_64");
                var existing = Environment.GetEnvironmentVariable("LD_LIBRARY_PATH");
                psi.Environment["LD_LIBRARY_PATH"] = string.IsNullOrEmpty(existing) ? lib : $"{existing}:{lib}";
            }

            var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            var id = s.Id;
            p.OutputDataReceived += (_, e) => OnProcessOutput(id, e.Data);
            p.ErrorDataReceived += (_, e) => OnProcessOutput(id, e.Data);
            p.Exited += (_, _) => _ = OnExitedAsync(id, p);

            if (!p.Start()) return "The process failed to start.";
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();

            st.Process = p;
            st.StartedUtc = DateTime.UtcNow;
            st.StopRequested = false;
            st.StartAfterExit = false;
            Console(s.Id, $"Started {LaunchArguments.ExecutableName} (PID {p.Id}). The world takes a few minutes to load before RCON answers.");
            live.RaiseStatus(s.Id);
            _ = discord.NotifyAsync(s.Id, "🟡 Server starting", $"{s.Settings.Hostname} is booting.", DiscordNotifier.Amber, isStatus: true);
            return null;
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Failed to start server {Id}", s.Id);
            return $"Couldn't start the server: {ex.Message}";
        }
        finally
        {
            st.Lock.Release();
        }
    }

    private void OnProcessOutput(int id, string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        // Once RCON is up it streams the same log, so only echo stdout while the server is loading.
        if (conn.Get(id).IsOnline) return;

        // Rust writes many lines twice to stdout: once plain (Unity log) and once wrapped in
        // ANSI colour codes like "ESC[37m...ESC[0m" (its own console). Strip the codes and drop the repeat.
        var clean = AnsiRegex.Replace(line, "").Trim();
        if (clean.Length == 0) return;
        var recent = _recentStdout.GetOrAdd(id, _ => new Queue<string>());
        lock (recent)
        {
            if (recent.Contains(clean)) return;
            recent.Enqueue(clean);
            while (recent.Count > 40) recent.Dequeue();
        }
        conn.AppendConsole(id, ConsoleKind.Process, clean);
    }

    // Matches real ESC sequences and the bare "[37m" form left when the ESC character gets lost.
    private static readonly System.Text.RegularExpressions.Regex AnsiRegex =
        new(@"\x1B?\[[0-9;]*m", System.Text.RegularExpressions.RegexOptions.Compiled);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, Queue<string>> _recentStdout = new();

    private async Task OnExitedAsync(int id, Process p)
    {
        var st = Get(id);
        int code;
        try { code = p.ExitCode; } catch { code = -1; }
        if (!ReferenceEquals(st.Process, p)) return;
        st.Process = null;
        st.StartedUtc = null;
        live.RaiseStatus(id);

        var server = await ReloadAsync(id);
        if (server is null) return;

        if (st.StopRequested)
        {
            st.StopRequested = false;
            Console(id, $"Server process exited (code {code}).");
            if (st.StartAfterExit)
            {
                st.StartAfterExit = false;
                await Task.Delay(3000);
                var err = await StartAsync(server);
                if (err is not null) Console(id, err, ConsoleKind.Error);
            }
            else
            {
                _ = discord.NotifyAsync(id, "🔴 Server stopped", $"{server.Settings.Hostname} was stopped.", DiscordNotifier.Red, isStatus: true);
            }
            return;
        }

        Console(id, $"Server process exited unexpectedly (code {code}).", ConsoleKind.Error);
        _ = discord.NotifyAsync(id, "💥 Server crashed", $"{server.Settings.Hostname} exited unexpectedly (code {code}).", DiscordNotifier.Red, isStatus: true);

        if (!server.Settings.AutoRestartOnCrash) return;
        st.RecentCrashes.RemoveAll(t => DateTime.UtcNow - t > TimeSpan.FromMinutes(15));
        st.RecentCrashes.Add(DateTime.UtcNow);
        if (st.RecentCrashes.Count > 3)
        {
            Console(id, "Crashed more than 3 times in 15 minutes — auto-restart paused. Check the log, then start it manually.", ConsoleKind.Error);
            return;
        }
        Console(id, "Auto-restarting in 10 seconds…");
        await Task.Delay(10_000);
        var error = await StartAsync(server);
        if (error is not null) Console(id, error, ConsoleKind.Error);
    }

    /// <summary>Graceful stop: save, quit over RCON, then kill if it hangs.</summary>
    public async Task<string?> StopAsync(ServerInstance s, bool startAgain = false)
    {
        if (s.IsDemo) return "Demo servers are simulated and can't be stopped.";
        var st = Get(s.Id);

        if (!s.Settings.ManageProcess || !st.IsRunning)
        {
            if (s.Settings.ManageProcess && !st.IsRunning && !startAgain) return "The server isn't running.";
            if (s.Settings.ManageProcess && !st.IsRunning && startAgain) return await StartAsync(s);
            // Remote server: we can only ask it to quit.
            if (!conn.Get(s.Id).IsOnline) return "Not connected over RCON.";
            if (startAgain)
            {
                await conn.ExecuteAsync(s.Id, "restart 5 \"Restarting\"");
                return null;
            }
            await conn.ExecuteAsync(s.Id, "server.save");
            await conn.ExecuteAsync(s.Id, "quit");
            return null;
        }

        st.StopRequested = true;
        st.StartAfterExit = startAgain;
        SetActivity(s.Id, startAgain ? "Restarting…" : "Stopping…");
        try
        {
            var p = st.Process!;
            if (conn.Get(s.Id).IsOnline)
            {
                Console(s.Id, "Saving world and shutting down…");
                await conn.QueryAsync(s.Id, "server.save", TimeSpan.FromSeconds(60));
                await conn.QueryAsync(s.Id, "quit", TimeSpan.FromSeconds(5));
            }
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(conn.Get(s.Id).IsOnline ? 120 : 5));
            try { await p.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException)
            {
                Console(s.Id, "Server didn't exit in time — killing the process.", ConsoleKind.Warning);
                try { p.Kill(entireProcessTree: true); } catch { /* already gone */ }
            }
            await conn.DisconnectAsync(s.Id);
            return null;
        }
        finally
        {
            SetActivity(s.Id, null);
        }
    }

    // ------------------------------------------------------------------ countdowns

    public bool CancelCountdown(int serverId)
    {
        var st = Get(serverId);
        if (st.CountdownCts is null) return false;
        st.CountdownCts.Cancel();
        return true;
    }

    private static readonly int[] WarningMarks = { 3600, 1800, 900, 600, 300, 180, 120, 60, 30, 10, 5 };

    private static string Human(int seconds) => seconds >= 60
        ? $"{seconds / 60} minute{(seconds >= 120 ? "s" : "")}"
        : $"{seconds} seconds";

    /// <summary>Announce in chat on a schedule, then return when the time is up (false if cancelled).</summary>
    private async Task<bool> CountdownAsync(ServerInstance s, int minutes, string what)
    {
        if (minutes <= 0) return true;
        var st = Get(s.Id);
        st.CountdownCts?.Cancel();
        using var cts = new CancellationTokenSource();
        st.CountdownCts = cts;
        var end = DateTime.UtcNow.AddMinutes(minutes);
        SetActivity(s.Id, $"{char.ToUpper(what[0])}{what[1..]} countdown", end);
        Console(s.Id, $"{what} countdown started — {minutes} minute(s). You can cancel it from the dashboard.");
        try
        {
            foreach (var mark in WarningMarks.Where(m => m <= minutes * 60))
            {
                var wait = end.AddSeconds(-mark) - DateTime.UtcNow;
                if (wait > TimeSpan.Zero) await Task.Delay(wait, cts.Token);
                await conn.QueryAsync(s.Id, $"say \"<color=#ce422b>Server {what} in {Human(mark)}.</color>\"");
            }
            var rest = end - DateTime.UtcNow;
            if (rest > TimeSpan.Zero) await Task.Delay(rest, cts.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            Console(s.Id, $"{what} cancelled.", ConsoleKind.Warning);
            await conn.QueryAsync(s.Id, $"say \"<color=#3fb37f>Server {what} cancelled.</color>\"");
            return false;
        }
        finally
        {
            if (ReferenceEquals(st.CountdownCts, cts)) st.CountdownCts = null;
            SetActivity(s.Id, null);
        }
    }

    public async Task<string?> RestartAsync(ServerInstance s, int warnMinutes, string reason = "restart")
    {
        if (s.IsDemo)
        {
            await conn.ExecuteAsync(s.Id, $"restart {warnMinutes * 60} \"{reason}\"");
            return null;
        }
        if (!s.Settings.ManageProcess)
        {
            // Rust's own restart command broadcasts a countdown and exits; the host must bring it back up.
            if (!conn.Get(s.Id).IsOnline) return "Not connected over RCON.";
            await conn.ExecuteAsync(s.Id, $"restart {Math.Max(5, warnMinutes * 60)} \"{reason}\"");
            return null;
        }
        if (!await CountdownAsync(s, warnMinutes, "restart")) return "Restart cancelled.";
        _ = discord.NotifyAsync(s.Id, "🔄 Server restarting", $"{s.Settings.Hostname} is restarting.", DiscordNotifier.Blue, isStatus: true);
        return await StopAsync(s, startAgain: true);
    }

    // ------------------------------------------------------------------ SteamCMD / frameworks

    public async Task<string?> UpdateServerAsync(ServerInstance s)
    {
        var error = ValidateLocalForInstall(s);
        if (error is not null) return error;
        if (string.IsNullOrWhiteSpace(s.Settings.SteamCmdPath) || !File.Exists(s.Settings.SteamCmdPath))
        {
            var (path, err) = await EnsureSteamCmdAsync(s);
            if (err is not null) return err;
            s.Settings.SteamCmdPath = path!;
        }
        var st = Get(s.Id);
        if (st.IsRunning) return "Stop the server before updating it.";

        // A RustDedicated left running from this folder (e.g. started before the manager restarted)
        // locks its files and makes SteamCMD fail with exit code 8.
        var leftovers = FindRunning(s);
        if (leftovers.Count > 0)
        {
            foreach (var lp in leftovers) lp.Dispose();
            return "RustDedicated is still running from this install folder (the manager may have lost track of it). " +
                   "Type quit in the console or end RustDedicated.exe in Task Manager, then update again.";
        }

        Directory.CreateDirectory(s.Settings.InstallDir);
        var branch = string.IsNullOrWhiteSpace(s.Settings.Branch) ? "" : $" -beta {s.Settings.Branch.Trim()}";
        var args = $"+force_install_dir \"{s.Settings.InstallDir}\" +login anonymous +app_update 258550{branch} validate +quit";
        const int maxAttempts = 3;
        string? lastState = null;
        int lastCode = -1;
        try
        {
            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                SetActivity(s.Id, attempt == 1 ? "Updating via SteamCMD…" : $"Updating via SteamCMD (attempt {attempt}/{maxAttempts})…");
                Console(s.Id, $"steamcmd {args}");
                var psi = new ProcessStartInfo(s.Settings.SteamCmdPath, args)
                {
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetDirectoryName(s.Settings.SteamCmdPath) ?? s.Settings.InstallDir,
                };
                using var p = new Process { StartInfo = psi };
                void OnLine(string? line)
                {
                    if (string.IsNullOrWhiteSpace(line)) return;
                    Console(s.Id, line, ConsoleKind.Process);
                    var m = SteamStateRegex.Match(line);
                    if (m.Success) lastState = m.Groups[1].Value.ToLowerInvariant();
                }
                p.OutputDataReceived += (_, e) => OnLine(e.Data);
                p.ErrorDataReceived += (_, e) => OnLine(e.Data);
                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(60));
                await p.WaitForExitAsync(cts.Token);
                lastCode = p.ExitCode;
                Console(s.Id, $"SteamCMD finished (exit code {lastCode}).");
                if (lastCode is 0 or 7) return null;

                // Disk full won't fix itself by retrying.
                if (lastState is "0x202") break;
                if (attempt < maxAttempts)
                {
                    Console(s.Id, $"SteamCMD didn't finish cleanly{(lastState is null ? "" : $" ({DescribeSteamState(lastState)})")} — this is common right after SteamCMD updates itself. Retrying in 5 seconds…", ConsoleKind.Warning);
                    lastState = null;
                    await Task.Delay(TimeSpan.FromSeconds(5));
                }
            }
            var reason = lastState is null ? "" : $" {DescribeSteamState(lastState)}.";
            return $"SteamCMD failed after retrying (exit code {lastCode}).{reason} See the console for details.";
        }
        catch (Exception ex)
        {
            return $"SteamCMD failed: {ex.Message}";
        }
        finally
        {
            SetActivity(s.Id, null);
        }
    }

    private static readonly System.Text.RegularExpressions.Regex SteamStateRegex =
        new(@"state is (0x[0-9a-fA-F]+)", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Plain-English meaning of the "App '258550' state is 0x…" codes SteamCMD prints.</summary>
    private static string DescribeSteamState(string state) => state switch
    {
        "0x202" => "Not enough free disk space on the install drive",
        "0x206" => "Steam download servers timed out — check your internet connection and try again",
        "0x402" or "0x602" => "The download was interrupted (network/update problem) — try again",
        "0x6" or "0x2" => "Files are locked — make sure RustDedicated isn't running and no other SteamCMD is open",
        "0x10502" or "0x10e02" => "Files couldn't be written — check folder permissions and antivirus",
        _ => $"Steam reported state {state}",
    };

    private static string? ValidateLocalForInstall(ServerInstance s)
    {
        if (s.IsDemo) return "Demo servers are simulated.";
        if (!s.Settings.ManageProcess) return "Turn on \"Run the server process on this machine\" in Settings → Hosting first.";
        if (string.IsNullOrWhiteSpace(s.Settings.InstallDir)) return "Set the server install folder in Settings → Hosting.";
        return null;
    }

    public static string? FrameworkDownloadUrl(ModFramework fw) => fw switch
    {
        ModFramework.Oxide => OperatingSystem.IsWindows()
            ? "https://github.com/OxideMod/Oxide.Rust/releases/latest/download/Oxide.Rust.zip"
            : "https://github.com/OxideMod/Oxide.Rust/releases/latest/download/Oxide.Rust-linux.zip",
        ModFramework.Carbon => OperatingSystem.IsWindows()
            ? "https://github.com/CarbonCommunity/Carbon/releases/download/production_build/Carbon.Windows.Release.zip"
            : "https://github.com/CarbonCommunity/Carbon/releases/download/production_build/Carbon.Linux.Release.tar.gz",
        _ => null,
    };

    public async Task<string?> InstallFrameworkAsync(ServerInstance s)
    {
        var error = ValidateLocalForInstall(s);
        if (error is not null) return error;
        var url = FrameworkDownloadUrl(s.Settings.Framework);
        if (url is null) return "Pick Oxide or Carbon as the mod framework first.";
        if (Get(s.Id).IsRunning) return "Stop the server before installing a mod framework.";

        SetActivity(s.Id, $"Installing {s.Settings.Framework}…");
        var tmp = Path.Combine(Path.GetTempPath(), $"rsm-{Guid.NewGuid():N}{(url.EndsWith(".tar.gz") ? ".tar.gz" : ".zip")}");
        try
        {
            Console(s.Id, $"Downloading {url}");
            var client = http.CreateClient("downloads");
            await using (var src = await client.GetStreamAsync(url))
            await using (var dst = File.Create(tmp))
            {
                await src.CopyToAsync(dst);
            }
            Console(s.Id, "Extracting into the server folder…");
            if (tmp.EndsWith(".tar.gz"))
            {
                await using var fs = File.OpenRead(tmp);
                await using var gz = new GZipStream(fs, CompressionMode.Decompress);
                await TarFile.ExtractToDirectoryAsync(gz, s.Settings.InstallDir, overwriteFiles: true);
            }
            else
            {
                ZipFile.ExtractToDirectory(tmp, s.Settings.InstallDir, overwriteFiles: true);
            }
            Console(s.Id, $"{s.Settings.Framework} installed. Re-install it after every Rust update.");
            if (s.Settings.Framework == ModFramework.Carbon && !OperatingSystem.IsWindows())
                Console(s.Id, "Note: Carbon on Linux must be launched through carbon/tools/environment.sh — see the README.", ConsoleKind.Warning);
            return null;
        }
        catch (Exception ex)
        {
            return $"Install failed: {ex.Message}";
        }
        finally
        {
            try { File.Delete(tmp); } catch { /* ignore */ }
            SetActivity(s.Id, null);
        }
    }

    /// <summary>Folder where the manager keeps its own SteamCMD copy.</summary>
    public static string SteamCmdHome => Path.Combine(AppPaths.DataDir ?? AppContext.BaseDirectory, "steamcmd");

    /// <summary>Find or download SteamCMD and remember its path on the server. Returns (path, error).</summary>
    public async Task<(string? Path, string? Error)> EnsureSteamCmdAsync(ServerInstance s)
    {
        var exe = Path.Combine(SteamCmdHome, OperatingSystem.IsWindows() ? "steamcmd.exe" : "steamcmd.sh");
        if (!File.Exists(exe))
        {
            var url = OperatingSystem.IsWindows()
                ? "https://steamcdn-a.akamaihd.net/client/installer/steamcmd.zip"
                : "https://steamcdn-a.akamaihd.net/client/installer/steamcmd_linux.tar.gz";
            SetActivity(s.Id, "Downloading SteamCMD…");
            var tmp = Path.Combine(Path.GetTempPath(), $"steamcmd-{Guid.NewGuid():N}{(OperatingSystem.IsWindows() ? ".zip" : ".tar.gz")}");
            try
            {
                Console(s.Id, $"SteamCMD not found — downloading it to {SteamCmdHome}");
                Directory.CreateDirectory(SteamCmdHome);
                var client = http.CreateClient("downloads");
                await using (var src = await client.GetStreamAsync(url))
                await using (var dst = File.Create(tmp))
                {
                    await src.CopyToAsync(dst);
                }
                if (OperatingSystem.IsWindows())
                {
                    ZipFile.ExtractToDirectory(tmp, SteamCmdHome, overwriteFiles: true);
                }
                else
                {
                    await using (var fs = File.OpenRead(tmp))
                    await using (var gz = new GZipStream(fs, CompressionMode.Decompress))
                    {
                        await TarFile.ExtractToDirectoryAsync(gz, SteamCmdHome, overwriteFiles: true);
                    }
                    const UnixFileMode exec = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                                              UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
                    foreach (var f in new[] { exe, Path.Combine(SteamCmdHome, "linux32", "steamcmd") })
                        if (File.Exists(f)) File.SetUnixFileMode(f, exec);
                    Console(s.Id, "On Linux SteamCMD needs 32-bit libraries (e.g. apt install lib32gcc-s1).", ConsoleKind.Warning);
                }
            }
            catch (Exception ex)
            {
                return (null, $"Couldn't download SteamCMD: {ex.Message}");
            }
            finally
            {
                try { File.Delete(tmp); } catch { /* ignore */ }
                SetActivity(s.Id, null);
            }
        }

        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            var entity = await db.Servers.FirstOrDefaultAsync(x => x.Id == s.Id);
            if (entity is not null && entity.Settings.SteamCmdPath != exe)
            {
                entity.Settings.SteamCmdPath = exe;
                await db.SaveChangesAsync();
                live.RaiseServersChanged();
            }
        }
        return (exe, null);
    }

    /// <summary>
    /// The safe update sequence: warn players, save and stop, update Rust, reinstall the mod
    /// framework so it matches the new build, then start again if it was running.
    /// </summary>
    public async Task<string?> UpdateAndRestartAsync(ServerInstance s, int warnMinutes)
    {
        var error = ValidateLocalForInstall(s);
        if (error is not null) return error;
        var wasRunning = Get(s.Id).IsRunning;
        if (wasRunning)
        {
            if (!await CountdownAsync(s, warnMinutes, "update")) return "Update cancelled.";
            var stop = await StopAsync(s);
            if (stop is not null) return stop;
        }
        _ = discord.NotifyAsync(s.Id, "⬇️ Updating", $"{s.Settings.Hostname} is updating.", DiscordNotifier.Blue, isStatus: true);
        var update = await UpdateServerAsync(s);
        if (update is not null)
        {
            Console(s.Id, update, ConsoleKind.Error);
            if (wasRunning) await StartAsync(s);
            return update;
        }
        if (s.Settings.Framework != ModFramework.Vanilla)
        {
            var fw = await InstallFrameworkAsync(s);
            if (fw is not null) Console(s.Id, fw, ConsoleKind.Error);
        }
        if (wasRunning)
        {
            var start = await StartAsync(s);
            if (start is not null) return start;
        }
        Console(s.Id, "Update complete.");
        return null;
    }

    // ------------------------------------------------------------------ telemetry plugin

    public const string PluginFileName = "RustSandboxTelemetry.cs";
    public const string PluginConfigFileName = "RustSandboxTelemetry.json";

    public static string ReadPluginSource()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(PluginFileName)
            ?? throw new InvalidOperationException("Plugin resource missing from build.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public static string PluginConfigJson(ServerInstance s, string managerUrl) => JsonSerializer.Serialize(new Dictionary<string, object>
    {
        // "localhost" can resolve to IPv6 and hang the game server's requests; use the IPv4 loopback.
        ["Manager URL"] = managerUrl.TrimEnd('/').Replace("://localhost", "://127.0.0.1"),
        ["Server ID"] = s.Id,
        ["API Key"] = s.TelemetryApiKey,
        ["Position interval (seconds)"] = s.Settings.PositionIntervalSeconds,
        ["Flush interval (seconds)"] = 5,
        ["Track NPC and animal kills"] = true,
        ["Track weapon usage"] = true,
    }, new JsonSerializerOptions { WriteIndented = true });

    public async Task<string?> InstallTelemetryPluginAsync(ServerInstance s, string managerUrl)
    {
        var error = ValidateLocalForInstall(s);
        if (error is not null) return error;
        var (pluginDir, configDir) = s.Settings.Framework switch
        {
            ModFramework.Oxide => (Path.Combine(s.Settings.InstallDir, "oxide", "plugins"), Path.Combine(s.Settings.InstallDir, "oxide", "config")),
            ModFramework.Carbon => (Path.Combine(s.Settings.InstallDir, "carbon", "plugins"), Path.Combine(s.Settings.InstallDir, "carbon", "configs")),
            _ => (null, null),
        };
        if (pluginDir is null || configDir is null) return "The telemetry plugin needs Oxide or Carbon. Pick one in Settings → Hosting and install it.";
        try
        {
            Directory.CreateDirectory(pluginDir);
            Directory.CreateDirectory(configDir);
            // Config first so the plugin picks it up when the framework hot-loads the .cs file.
            await File.WriteAllTextAsync(Path.Combine(configDir, PluginConfigFileName), PluginConfigJson(s, managerUrl));
            await File.WriteAllTextAsync(Path.Combine(pluginDir, PluginFileName), ReadPluginSource());
            Console(s.Id, $"Telemetry plugin installed to {pluginDir}. It loads automatically if the server is running.");
            if (conn.Get(s.Id).IsOnline)
                await conn.QueryAsync(s.Id, s.Settings.Framework == ModFramework.Carbon ? "c.reload RustSandboxTelemetry" : "oxide.reload RustSandboxTelemetry");
            return null;
        }
        catch (Exception ex)
        {
            return $"Couldn't install the plugin: {ex.Message}";
        }
    }

    // ------------------------------------------------------------------ wipes

    private static readonly string[] MapWipePatterns = { "*.map", "*.sav", "*.sav.*", "sv.files.*" };
    private static readonly string[] FullWipePatterns = { "player.blueprints.*", "player.deaths.*", "player.identities.*", "player.states.*", "player.tokens.*" };

    public async Task<string?> WipeAsync(ServerInstance s, bool blueprints, bool newSeed, int warnMinutes)
    {
        var error = ValidateLocal(s);
        if (error is not null) return error;

        var label = blueprints ? "full wipe" : "map wipe";
        if (Get(s.Id).IsRunning)
        {
            if (!await CountdownAsync(s, warnMinutes, label)) return "Wipe cancelled.";
            var stopErr = await StopAsync(s);
            if (stopErr is not null) return stopErr;
        }

        SetActivity(s.Id, $"Running {label}…");
        try
        {
            var dataDir = Path.Combine(s.Settings.InstallDir, "server", s.Settings.Identity);
            var backup = Path.Combine(s.Settings.InstallDir, "wipe-backups", s.Settings.Identity, DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            var moved = 0;
            if (Directory.Exists(dataDir))
            {
                var patterns = blueprints ? MapWipePatterns.Concat(FullWipePatterns) : MapWipePatterns;
                foreach (var file in patterns.SelectMany(p => Directory.GetFiles(dataDir, p)).Distinct())
                {
                    Directory.CreateDirectory(backup);
                    File.Move(file, Path.Combine(backup, Path.GetFileName(file)), overwrite: true);
                    moved++;
                }
            }
            Console(s.Id, moved > 0
                ? $"Moved {moved} save file(s) to {backup} (delete them when you're happy with the wipe)."
                : "No save files found — the next start generates a fresh world.");

            await using (var db = await dbFactory.CreateDbContextAsync())
            {
                var entity = await db.Servers.FirstAsync(x => x.Id == s.Id);
                if (newSeed && entity.Settings.MapMode == MapMode.Procedural)
                {
                    entity.Settings.Seed = RandomNumberGenerator.GetInt32(1, int.MaxValue);
                    Console(s.Id, $"New map seed: {entity.Settings.Seed} (size {entity.Settings.WorldSize}).");
                }
                entity.LastWipeUtc = DateTime.UtcNow;
                if (newSeed && entity.Settings.MapImageAuto) entity.Settings.MapImageUrl = ""; // new map; the next upload replaces it
                if (newSeed) await db.AppSettings.Where(a => a.Key == TelemetryService.MonumentsKey(s.Id)).ExecuteDeleteAsync();
                await db.SaveChangesAsync();
                s = entity;
            }
            world.Clear(s.Id);
            conn.UpdateServer(s);
            live.RaiseServersChanged();
            _ = discord.NotifyAsync(s.Id, blueprints ? "🧹 Full wipe" : "🗺️ Map wipe",
                $"{s.Settings.Hostname} has wiped. {s.Settings.MapDisplayName}", DiscordNotifier.Orange);

            SetActivity(s.Id, null);
            return await StartAsync(s);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "Wipe failed for {Id}", s.Id);
            return $"Wipe failed: {ex.Message}";
        }
        finally
        {
            SetActivity(s.Id, null);
        }
    }

    /// <summary>Called on app start: launch servers flagged to start with the manager.</summary>
    public async Task AutoStartAsync(IEnumerable<ServerInstance> servers)
    {
        var list = servers.ToList();
        // First reconnect to anything still running from before the manager restarted.
        foreach (var s in list.Where(x => !x.IsDemo && x.Settings.ManageProcess)) TryAdopt(s, force: true);
        foreach (var s in list.Where(x => !x.IsDemo && x.Settings.ManageProcess && x.Settings.AutoStartWithManager && !Get(x.Id).IsRunning))
        {
            var err = await StartAsync(s);
            if (err is not null) log.LogWarning("Auto-start of {Name} failed: {Error}", s.Name, err);
        }
    }
}
