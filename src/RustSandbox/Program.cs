using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting.WindowsServices;
using RustSandbox.Components;
using RustSandbox.Data;
using RustSandbox.Services;
using RustSandbox.Services.Demo;

// Installed as a Windows service the working folder is System32, so anchor the app to its
// own folder and keep the database, settings and SteamCMD in ProgramData instead.
var runningAsService = WindowsServiceHelpers.IsWindowsService();
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = runningAsService ? AppContext.BaseDirectory : null,
});
builder.Services.AddWindowsService(o => o.ServiceName = "RustSandbox");

AppPaths.DataDir = builder.Configuration["Manager:DataDir"] is { Length: > 0 } dataDir ? dataDir
    : runningAsService ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RustSandbox")
    : null;
if (AppPaths.DataDir is not null)
{
    LegacyRename.MoveDataFolder(AppPaths.DataDir);
    Directory.CreateDirectory(AppPaths.DataDir);
    builder.Configuration.AddJsonFile(AppPaths.SettingsFile!, optional: true, reloadOnChange: false);
}

// A relative database path is relative to the data folder (or the app folder), never the working folder.
var dbConn = new SqliteConnectionStringBuilder(builder.Configuration.GetConnectionString("Default") ?? "Data Source=rustsandbox.db");
if (!Path.IsPathRooted(dbConn.DataSource) && dbConn.DataSource != ":memory:")
    dbConn.DataSource = Path.Combine(AppPaths.DataDir ?? builder.Environment.ContentRootPath, dbConn.DataSource);
if (dbConn.DataSource != ":memory:") LegacyRename.MoveDatabase(dbConn.DataSource);
builder.Configuration["ConnectionStrings:Default"] = dbConn.ConnectionString;

builder.Services.AddRazorComponents().AddInteractiveServerComponents();

builder.Services.AddDbContextFactory<AppDbContext>(o =>
    o.UseSqlite(dbConn.ConnectionString));

// ---- auth: a single admin account from configuration, cookie session
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.LoginPath = "/login";
        o.LogoutPath = "/account/logout";
        o.ExpireTimeSpan = TimeSpan.FromDays(14);
        o.SlidingExpiration = true;
        o.Cookie.Name = "rsm.auth";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Lax;
    });
builder.Services.AddAuthorization(o =>
    o.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddHttpClient("discord", c => c.Timeout = TimeSpan.FromSeconds(10));
builder.Services.AddHttpClient("downloads", c =>
{
    c.Timeout = TimeSpan.FromMinutes(10);
    c.DefaultRequestHeaders.UserAgent.ParseAdd("RustSandbox/1.0");
});

// ---- core services (singletons: shared live state across all browser sessions)
builder.Services.AddSingleton<LiveEvents>();
builder.Services.AddSingleton<LiveWorldStore>();
builder.Services.AddSingleton<DemoWorld>();
builder.Services.AddSingleton<ServerConnectionManager>();
builder.Services.AddSingleton<DiscordNotifier>();
builder.Services.AddSingleton<TelemetryService>();
builder.Services.AddSingleton<ProcessManager>();
builder.Services.AddSingleton<ServerService>();
builder.Services.AddSingleton<StatsService>();
builder.Services.AddSingleton<HeatmapService>();
builder.Services.AddSingleton<SeedLibrary>();
builder.Services.AddSingleton<SchedulerService>();
builder.Services.AddSingleton<ServerMonitorService>();
builder.Services.AddSingleton<UpdateService>();
builder.Services.AddSingleton<PluginService>();
builder.Services.AddSingleton<PermissionService>();
builder.Services.AddSingleton<TweaksService>();
builder.Services.AddSingleton<BundledPluginService>();
builder.Services.AddSingleton<SkillsService>();
builder.Services.AddSingleton<DailyService>();
builder.Services.AddSingleton<QuestsService>();
builder.Services.AddSingleton<LootService>();
builder.Services.AddSingleton<WeatherService>();
builder.Services.AddSingleton<NpcsService>();
builder.Services.AddSingleton<EventsService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ServerMonitorService>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<SchedulerService>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<UpdateService>());
builder.Services.AddHostedService<DemoSimulator>();
builder.Services.AddHostedService<MapImageWatcher>();
builder.Services.AddHostedService<LegacyPluginMigration>();

// ---- per-circuit UI state
builder.Services.AddScoped<ToastService>();
builder.Services.AddScoped<ServerContext>();

var app = builder.Build();

// ---- database + first-run data
using (var scope = app.Services.CreateScope())
{
    var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
    await using var db = await factory.CreateDbContextAsync();
    await db.Database.EnsureCreatedAsync();
    await SchemaUpgrade.ApplyAsync(db, app.Logger);
    await SeedData.EnsureAsync(db, app.Configuration);
    // Hosted custom maps default to the manager's Public URL (Preferences), so players can download them.
    var publicUrl = (await db.AppSettings.FindAsync("PublicUrl"))?.Value ?? app.Configuration["Manager:PublicUrl"];
    if (!string.IsNullOrWhiteSpace(publicUrl)) CustomMaps.DefaultHostUrl = publicUrl.Trim().TrimEnd('/');
}

var authUser = app.Configuration["Auth:Username"] ?? "admin";
var authPass = app.Configuration["Auth:Password"] ?? "changeme";
if (authPass == "changeme")
    app.Logger.LogWarning("Using the default password. Set Auth:Password in {File} or the Auth__Password environment variable.", AppPaths.SettingsFile ?? "appsettings.json");

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/error", createScopeForErrors: true);
}

app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

// ---- account endpoints
app.MapPost("/account/login", async (HttpContext ctx, [FromForm] string username, [FromForm] string password, [FromForm] string? returnUrl) =>
{
    static bool Same(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    // Constant-time compare on both fields, and always evaluate both.
    var okUser = Same(username ?? "", authUser);
    var okPass = Same(password ?? "", authPass);
    if (!(okUser & okPass))
    {
        await Task.Delay(Random.Shared.Next(400, 900)); // slow down guessing
        return Results.Redirect($"/login?error=1&returnUrl={Uri.EscapeDataString(returnUrl ?? "/")}");
    }

    var identity = new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, authUser) }, CookieAuthenticationDefaults.AuthenticationScheme);
    await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity),
        new AuthenticationProperties { IsPersistent = true });
    var target = !string.IsNullOrEmpty(returnUrl) && returnUrl.StartsWith('/') && !returnUrl.StartsWith("//") ? returnUrl : "/";
    return Results.LocalRedirect(target);
}).AllowAnonymous();

app.MapPost("/account/logout", async (HttpContext ctx) =>
{
    await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Redirect("/login");
});

// ---- telemetry from the in-game plugin (authenticated by per-server API key)
app.MapPost("/api/telemetry/{serverId:int}", async (int serverId, TelemetryBatch batch, HttpRequest req, TelemetryService telemetry, CancellationToken ct) =>
{
    if (!await telemetry.AuthorizeAsync(serverId, req.Headers["X-Api-Key"].ToString(), ct))
        return Results.Unauthorized();
    await telemetry.IngestAsync(serverId, batch, ct);
    return Results.Ok(new { ok = true });
}).AllowAnonymous().DisableAntiforgery();

app.MapGet("/api/health", () => Results.Ok(new { status = "ok", time = DateTime.UtcNow })).AllowAnonymous();

// ---- plugin downloads (signed-in users only)
app.MapGet("/api/plugin", () =>
    Results.File(Encoding.UTF8.GetBytes(ProcessManager.ReadPluginSource()), "text/plain", ProcessManager.PluginFileName));

foreach (var bundled in new[] { BundledPlugin.EventHud, BundledPlugin.Skills, BundledPlugin.Daily, BundledPlugin.Events, BundledPlugin.Quests, BundledPlugin.Help, BundledPlugin.Recycler, BundledPlugin.Skins, BundledPlugin.Loot, BundledPlugin.Pet, BundledPlugin.Weather, BundledPlugin.Npcs })
    app.MapGet(bundled.DownloadPath, () => Results.File(Encoding.UTF8.GetBytes(bundled.ReadSource()), "text/plain", bundled.FileName));

app.MapGet("/api/servers/{id:int}/plugin-config", async (int id, ServerService servers, HttpRequest req, IConfiguration cfg) =>
{
    var s = await servers.GetAsync(id);
    if (s is null) return Results.NotFound();
    var url = await servers.GetAppSettingAsync("PublicUrl") ?? cfg["Manager:PublicUrl"];
    if (string.IsNullOrWhiteSpace(url)) url = $"{req.Scheme}://{req.Host}";
    return Results.File(Encoding.UTF8.GetBytes(ProcessManager.PluginConfigJson(s, url)), "application/json", ProcessManager.PluginConfigFileName);
});

// ---- high-res map rendered on the server with world.rendermap (map_<size>_<seed>.png in the install folder)
app.MapGet("/api/servers/{id:int}/rendered-map", async (int id, ServerService servers) =>
{
    var s = await servers.GetAsync(id);
    var file = s is null ? null : MapImages.FindRendered(s, fresh: true);
    if (file is null) return Results.NotFound();
    return Results.File(file.FullName, "image/png", lastModified: file.LastWriteTimeUtc, enableRangeProcessing: false);
});

// Small cached JPEG of the rendered map for the server list.
app.MapGet("/api/servers/{id:int}/map-thumb", async (int id, ServerService servers, HttpContext ctx) =>
{
    var s = await servers.GetAsync(id);
    var thumb = s is null ? null : await MapImages.ThumbnailAsync(s);
    if (thumb is null) return Results.NotFound();
    ctx.Response.Headers.CacheControl = "private, max-age=86400";
    return Results.File(thumb, "image/jpeg");
});

// ---- hosted custom maps: Rust and its players download these, so no sign-in
app.MapGet($"/{CustomMaps.DownloadPrefix}/{{file}}", (string file) =>
{
    if (CustomMaps.Find(file) is { } map)
        return Results.File(map.FullPath, "application/octet-stream", map.FileName, enableRangeProcessing: true);
    // The live-map picture and details that go with a map (Name.jpg / Name.png / Name.json).
    var ext = Path.GetExtension(file).ToLowerInvariant();
    if (ext is ".jpg" or ".png" or ".json" && CustomMaps.Find(Path.ChangeExtension(file, ".map")) is { } owner)
    {
        var side = Path.ChangeExtension(owner.FullPath, ext);
        if (File.Exists(side)) return Results.File(side, ext switch { ".jpg" => "image/jpeg", ".png" => "image/png", _ => "application/json" });
    }
    return Results.NotFound();
}).AllowAnonymous();

// ---- item icons from the Rust game folder (Bundles\items\<shortname>.png)
app.MapGet("/api/servers/{id:int}/item-icon/{shortname}", async (int id, string shortname, ServerService servers, HttpContext ctx) =>
{
    var s = await servers.GetAsync(id);
    var path = s is null ? null : ItemCatalog.IconPath(s.Settings, shortname);
    if (path is null) return Results.NotFound();
    ctx.Response.Headers.CacheControl = "private, max-age=86400";
    return Results.File(path, "image/png");
});

app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

app.Run();
