# Rust Sandbox

A self-hosted web app (ASP.NET Core 8 + Blazor Server, C#) for running one or many Rust game servers from a browser.

![stack](https://img.shields.io/badge/.NET-8.0-512BD4) ![ui](https://img.shields.io/badge/UI-Blazor%20Server-5C2D91) ![db](https://img.shields.io/badge/DB-SQLite-003B57)

## What it does

| Area | Highlights |
|---|---|
| **All servers** | Every server at a glance: status lamp, players, FPS, entities, uptime, player-count sparkline. A switcher in the sidebar keeps your place when you hop between servers. |
| **Dashboard** | Live gauges (players, FPS, entities), an hour of performance history, the last 24 h of combat (deadliest weapons, causes of death, kill distances, what players hunt), a live kill feed, who's online and a personal "You, last 24 hours" panel. |
| **Console** | Live WebRCON log with filters (commands, chat, warnings & errors) and search. Command autocomplete (Tab), ↑/↓ history, one-click quick commands you can add your own to, and a chat panel to talk as SERVER. |
| **Players** | Online list with ping, health and time online; kick/ban with reasons; copy Steam ID; jump to them on the live map; "everyone seen" with kills/deaths; full per-player stats. |
| **Live map** | Zoom/pan map with live player dots (smoothly animated), recent kills drawn as ✕ with killer→victim lines, Rust grid references, monument labels, and heat maps for *where players spend time*, *PvP kills*, *deaths* or *scientist kills* over 1 h – a whole wipe. Click a dot to open that player's stats. |
| **Combat stats** | Sortable leaderboard (kills, deaths, K/D, headshot %, longest kill, PvE, favourite weapon), weapon table with shots fired and accuracy, kill-distance histogram, kills by hour of day, what killed players, what players killed, body-part hits, daily/hourly activity. |
| **Gathering** | Who gathers what: wood, stone, metal, sulfur and HQ ore, berries, mushrooms, hemp, crops, animal parts and fish. Totals per resource type, most gathered items, where it comes from (trees, nodes, pickups, farming, animals, fishing), gathered per day and a sortable top-gatherers table. Each player's stats show what they gathered too. |
| **Per-player stats** | K/D, headshot rate, average & longest kill, **favourite weapon (most kills)** vs **most used weapon (most shots)**, accuracy, nemesis, favourite target, *what they killed* and *what killed them*, per-weapon breakdown, recent fights. |
| **Map generator** | Random seeds (crypto RNG), roll six to compare, size slider with size advice, labelled preview, PNG export, a saved-seed library with favourites, RustMaps link, and "send this seed to a server". |
| **Schedules** | Daily / weekly / every N minutes / first Thursday (forced wipe) / once: restarts with an in-game countdown that finishes *on time*, map or full wipes with optional new seed, SteamCMD updates, announcements and command lists. Run any task on demand. |
| **Custom maps** | Maui, a real-terrain island made in RustEdit, ships with the app and is the default map for the demo and new servers. Upload your own `.map` files to the map library and pick one per server: the manager hosts the file for players to download, sets `server.levelurl` and copies the map into the server folder, and shows the map's own image on the live map. |
| **World options** | Shape procedural maps with Rust's own generator settings: biome mix, starter/medium/hard zones, roads, rivers, power lines, rails, underwater labs and a monument checklist. Applies on the next map wipe. |
| **Server settings** | Everything to launch and manage an instance: hostname, description, images, tags, ports, RCON, map mode (procedural / custom map from the library or a URL / Barren / Hapis / Craggy / Savas), seed & size, PvE, radiation, stability, decay, save interval, tick rate, FPS limit, EAC, custom convars, extra args, framework, branch, auto-restart, auto-start. Shows the exact launch command. |
| **Hosting (optional)** | When the server runs on the same machine: start/stop/restart with countdowns, crash auto-restart (with crash-loop protection), SteamCMD install/update, one-click Oxide or Carbon install, one-click plugin install, safe wipes (files are moved to `wipe-backups/`, not deleted). |
| **Dashboard controls** | Start, stop, restart (with countdown), map wipe and full wipe in one panel, next to a live heat map of the server and the installed vs latest Rust build and Oxide/Carbon version. |
| **Updates** | Checks every 30 min for new Rust builds (Steam) and Oxide/Carbon releases (GitHub). Shows "Update available" on the dashboard and server list, writes to the console and posts to Discord. **Update now** warns players, saves and stops the server, updates Rust through SteamCMD, reinstalls Oxide/Carbon to match, and starts it again. Optional fully automatic updates. SteamCMD is downloaded automatically if you don't have it. |
| **Skills** | Eleven skills that level up as you play (Mining, Woodcutting, Combat, Gardening, Electrician, Survival, Mechanic, Fishing, Scrapper, Cooking, Companion), each with perks, plus a solo-friendly XP bonus. Leaderboards and each player's levels in the manager. |
| **Daily rewards** | A daily spin for a random item with rarity tiers, streak bonuses and a spinning reel. Edit the prize pool in the manager. |
| **Quests** | Quest givers you place around the map (with outfits and skins), quests to gather, hunt, loot, craft, deliver, swipe keycards or explore, a progress tracker and rewards you pick from pictures of every item. |
| **Loot** | Every crate, barrel and NPC body with Rust's own items and the real chance of each one, worked out from the game's loot tables. Keep Rust's loot, replace it with your own list or add to it, each item with an amount and a chance %. Plus a loot multiplier, scrap per crate and how many crates Patrol Heli and Bradley drop. |
| **Weather** | Random weather with Rust's own sky and fades: an on/off, a weather mix (mostly clear, varied, stormy or custom %), "change it now" buttons and a `/weather` forecast for players. |
| **NPCs** | More life in the world without a wall of settings: presets (scientists, heavies, bandits, raiders, elite guards, wolf packs, bears, big cats) with built-in random outfits and weapons, no Kits plugin. Add them to any monument from a list, send raider patrols along the roads and scatter bandit camps in the empty places. One Easy / Normal / Hard switch. Set on the manager's **NPCs** page. |
| **Pets** | `/pet` calls a companion that follows you, fights with you, carries a backpack and fetches loot (dropped items, crates, barrels, bodies, plants) with a key. Pick a wolf, tiger or panther (fighters) or a bear, polar bear, boar or crocodile (tanks). `/pet roar` buffs you, and a downed pet gets back up with a bandage, syringe or medkit. Made for solo play. |
| **Events** | A treasure hunt across the map's monuments and a train heist on a self-driving loot train, with an in-game HUD for running events and locked-crate timers. |
| **Rates & stacks** | Built-in gather rates (1x–10x presets, per resource, per source: hits, pickups, farming, quarries), optional "[10x]" tag in the server name, stack sizes per category or per item (item list read live from your server with vanilla sizes), smelt speed/output, food cooking speed, wood use per oven and charcoal. Applied live by the bundled `RustSandboxTweaks` plugin; turning it off restores vanilla values. |
| **Plugins** | Installed list with version, author and loaded/not-loaded status; turn plugins on/off (disabled ones are moved to `plugins/disabled` so they stay off), reload, delete; edit each plugin's JSON config with validation, a `.bak` backup and automatic reload; search uMod and install in one click; upload `.cs` files; check uMod for newer versions and update one or all. |
| **Groups & permissions** | Create/delete Oxide or Carbon groups, add/remove members, and tick plugin permissions per group or per player (grouped by plugin, with "from a group" hints). |
| **Player admin** | Ban by Steam ID (online or offline, permanent or timed), ban list with unban, mute/unmute chat, make moderator/owner or remove the role, kick. Owners/moderators listed from `users.cfg` for local servers. |
| **Notifications** | Discord webhook per server for online/offline, crash, restart and wipe — plus optional long-range kill posts. |
| **Quality of life** | Ctrl/⌘+K command palette (jump anywhere, or type `>status` to run a command), toasts that say exactly what happened, a demo server with simulated data so you can explore before connecting a real one, mobile-friendly layout. |

## Quick start

Requirements: [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

```bash
cd src/RustSandbox
dotnet run
```

Open <http://localhost:5080> and sign in with **admin / changeme**. You'll land on a **Demo Server** full of simulated players, kills and positions — every screen works with it. Remove it later from its *Settings → Danger zone*.

> **Change the password** before exposing the app: edit `Auth:Password` in `appsettings.json` or set the `Auth__Password` environment variable.

### Publish for a server

```bash
dotnet publish src/RustSandbox -c Release -o ./publish
cd publish && ./RustSandbox          # Linux
RustSandbox.exe                      # Windows
```

Settings live in `appsettings.json` next to the binary:

| Key | Default | Meaning |
|---|---|---|
| `Urls` | `http://0.0.0.0:5080` | Where the web UI listens |
| `Auth:Username` / `Auth:Password` | `admin` / `changeme` | Sign-in |
| `ConnectionStrings:Default` | `Data Source=rustsandbox.db` | SQLite file |
| `Manager:PublicUrl` | `http://localhost:5080` | Address game servers use to reach the manager (can also be set in *Preferences*) |
| `Manager:CreateDemoServer` | `true` | Create the demo server on first run |
| `Manager:PositionSampleSeconds` | `30` | How often positions are stored for heat maps |
| `Manager:RetentionDays` | `30` | Days of positions to keep (kills are kept 4× longer) |

### Windows installer

`installer\build-installer.cmd` (or `installer\build-installer.ps1 -Version 1.2.0`) builds `installer\output\RustSandbox-Setup-<version>.exe`. It needs the .NET 8 SDK and [Inno Setup 6](https://jrsoftware.org/isdl.php) (`winget install JRSoftware.InnoSetup`).

The installer copies a self-contained build (no .NET needed on the target PC) to `C:\Program Files\Rust Sandbox`, registers the **RustSandbox** Windows service (starts with Windows, restarts if it crashes) and adds a Start Menu shortcut that opens the dashboard. The first install asks for the web port, username and password.

The database, `appsettings.json` overrides and SteamCMD live in `C:\ProgramData\RustSandbox` (readable by Administrators only). Upgrades keep it; uninstall asks before deleting it. To bring an existing database across, stop the service, copy `rustsandbox.db` into that folder and start the service again. Set `Manager:DataDir` to use that layout without the service.

Put it behind a reverse proxy with HTTPS (Caddy, nginx, IIS) if it's reachable from the internet. WebSockets must be allowed for Blazor.

## Adding a server

**Hosted elsewhere** (GPortal, Nitrado, a VPS…): *Add a server → Connect to an existing server*, enter IP, RCON port and RCON password, press *Test connection*. The server must run with `+rcon.web 1` (the default on modern Rust).

**On this machine**: *Add a server → Run a server on this machine*, pick an install folder and the path to SteamCMD. Then in *Settings → Hosting* press **Install / update Rust**, optionally **Install Oxide/Carbon**, and **Start** from the server's header bar.

## Combat stats, heat maps and the live map

Vanilla RCON doesn't expose kills, distances or positions, so the manager ships a small plugin: [`plugins/RustSandboxTelemetry.cs`](plugins/RustSandboxTelemetry.cs) (Oxide/uMod or Carbon).

1. Open the server's **Plugin & telemetry** page.
2. Set the address the game server can reach the manager on (LAN/public IP — not `localhost` unless both run on the same box).
3. Either press **Install on this server** (managed servers), or download the `.cs` and the `.json` config and drop them into `oxide/plugins` and `oxide/config` (`carbon/plugins`, `carbon/configs` for Carbon).
4. The status turns green within seconds.

The plugin batches events every 5 s and posts them to `POST /api/telemetry/{serverId}` with a per-server `X-Api-Key`. It keeps events and backs off if the manager is down. Make sure the manager's port is reachable from the game server (firewall!).

It records: every player death (killer, weapon, distance, body part, headshot, positions, cause for environmental deaths), players killing scientists/animals/heli/Bradley, shots fired and hits per weapon, what players gather (plugin 1.5.0+), positions, and connects/disconnects.

## Game plugins included

The manager ships these Oxide/Carbon plugins and installs each one with a button on the **Telemetry** page (servers on this machine), or you can download the `.cs` from there.

| Plugin | What players get | Commands |
|---|---|---|
| `RustSandboxTelemetry` | Feeds combat, gathering and position stats to the manager. | – |
| `RustSandboxTweaks` | Gather rates, stack sizes, smelting and cooking speed, inventory sorting, furnace auto-split, remove tool. | `/sort`, `/autosplit`, `/remove` |
| `RustSandboxEventHud` | HUD tiles for airdrops, Patrol Heli, Bradley, Cargo, Chinook and oil rigs, locked-crate timers, a radiation detector and a compass/grid tile. | `/eventhud` |
| `RustSandboxSkills` | Eleven skills with levels and perks. | `/skills` |
| `RustSandboxDaily` | A daily reward spin with streaks. | `/daily` |
| `RustSandboxEvents` | Treasure hunt and train heist events. | `/hunt` |
| `RustSandboxQuests` | Quest givers, quests, tracker and rewards. | `/quests`, admin `/questgiver` |
| `RustSandboxHelp` | A `/help` guide with a page per system, shown to new players. | `/help` |
| `RustSandboxRecycler` | **Repair Recycler**: one deployable item that places a recycler and repair bench joined in an L. Optional full repair, recycler speed and output (vanilla by default). | `/recycler`, `/recycler craft` |
| `RustSandboxSkins` | A `/skins` picker for clothes, weapons, tools and deployables. Shows the skins each player owns on Steam plus Facepunch's free ones, because Facepunch's server rules forbid handing out paid skins people don't own. | `/skins` |
| `RustSandboxLoot` | Custom loot per crate, barrel and NPC type (edited on the manager's **Loot** page). Anything you don't change keeps Rust's own loot. | console `sandboxloot.refill` |
| `RustSandboxWeather` | Random weather mix, forecast and "change it now" (set on the manager's **Weather** page). Off = Rust's own weather. | `/weather`, admin `/weather storm` |
| `RustSandboxNpcs` | NPC spawns from presets with built-in gear: monuments, places picked in game, road patrols and wild camps (set on the manager's **NPCs** page). | `/npcs` (admin) |
| `RustSandboxPet` | A pet per player (wolf, tiger, panther, bear, polar bear, boar or crocodile): keeps pace, tanks enemies off you, fights, roars to buff you, carries a backpack and fetches loot. Middle mouse fetches or attacks what you look at. Downed pets are revived with meds. | `/pet`, `/pet choose`, `/pet roar`, `/pet patrol`, `/pet bag`, `/pet keys` |

Each plugin's settings are in its config file (`oxide/config/<plugin>.json`) and most are also editable in the manager.

## Custom maps

Pick **Custom map** in *Settings → World* and choose a map from the library, or upload a `.map` file (RustEdit maps or ones from map sites, up to 300 MB). Maps that come with the app live in `maps/` (see [maps/README.md](maps/README.md)); uploaded ones go to the data folder's `maps` folder. Players download the map from the manager at `/mapfiles/<name>.map`, so friends outside your network need the manager's port (5080, TCP) forwarded and the *Map download address* set to your public IP, e.g. `http://203.0.113.5:5080`. You can also paste a direct map URL hosted somewhere else instead. Maps with RustEdit IO or spawners need the Oxide.Ext.RustEdit extension on the server.

## About the map previews

The map generator and the live map background are drawn in the browser from the seed and size with a procedural approximation (coastline, biomes north→south, rivers, ring road, monument markers). They're consistent for a given seed and good for comparing the *feel* of seeds, but they are **not** Facepunch's world generator — use the RustMaps link to see the real terrain, or paste an exported map image URL into *Settings → World → Live map image* to use it as the live-map background. Player positions, kills and grid references are always exact.

## Project layout

```
src/RustSandbox/
  Program.cs                 DI, auth, telemetry + download endpoints
  Data/                      EF Core entities, ServerSettings, seed data
  Services/
    Rcon/                    WebRCON client and reply parsers
    ServerConnectionManager  one RCON connection per server, console/chat buffers
    ServerMonitorService     polling (serverinfo/playerlist), up/down alerts, retention
    ProcessManager           local RustDedicated process, SteamCMD, Oxide/Carbon, wipes
    SchedulerService         scheduled tasks
    TelemetryService         plugin ingest, live positions
    StatsService             combat analytics
    HeatmapService           heat-map aggregation
    Demo/                    simulated demo world
  Components/                Blazor layout, pages and shared components
  wwwroot/                   CSS, map/heat-map JavaScript
maps/                             maps that ship with the app (Maui)
plugins/RustSandboxTelemetry.cs   stats plugin
plugins/RustSandboxTweaks.cs      rates, stacks & smelting
plugins/RustSandbox*.cs           the other game plugins (see "Game plugins included")
```

## Notes & limits

- One admin account (from configuration). Everyone signed in shares the same view.
- Plugin files, configs and installs need the server to run on this machine. For servers hosted elsewhere the Plugins page can still load, unload and reload over RCON, and groups/permissions/bans work fully over RCON.
- Update checks use api.steamcmd.net (Steam build ids), the GitHub releases API (Oxide/Carbon) and umod.org's search API (plugins). If one is unreachable the dashboard shows the error and everything else keeps working.
- Mute uses Rust's `mutechat`/`unmutechat` commands.
- On Linux, SteamCMD needs 32-bit libraries (`sudo apt install lib32gcc-s1`).
- Starting/stopping servers you don't host on this machine isn't possible over RCON; *Stop* sends `quit` and *Restart* uses Rust's `restart` command, so your host must bring the server back.
- Carbon on Linux has to be launched through Carbon's `environment.sh`; the built-in process manager targets Windows and Oxide-on-Linux best.
- If the manager restarts, it loses track of server processes it started (they keep running); stop them from the console with `quit` or restart the server manually.

## Changes

See [CHANGELOG.md](CHANGELOG.md).

## License

Free to use, learn from and change under the [MIT License](LICENSE). Rust is a trademark of Facepunch Studios; this project is not affiliated with them.
