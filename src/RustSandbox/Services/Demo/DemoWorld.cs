using System.Collections.Concurrent;
using RustSandbox.Data;
using RustSandbox.Services.Rcon;

namespace RustSandbox.Services.Demo;

public sealed class SimPlayer
{
    public string SteamId { get; init; } = "";
    public string Name { get; init; } = "";
    public float X { get; set; }
    public float Z { get; set; }
    public float TargetX { get; set; }
    public float TargetZ { get; set; }
    public bool Online { get; set; }
    public float Health { get; set; } = 100;
    public double Skill { get; init; }
    public string FavWeapon { get; init; } = "";
    public int Ping { get; set; }
    public DateTime OnlineSinceUtc { get; set; }
}

public sealed record SimWeapon(string Name, float MinDist, float MaxDist, double HeadshotRate, int Weight, bool Ranged);

public sealed class DemoServerState
{
    public int ServerId { get; init; }
    public int WorldSize { get; set; }
    public Random Rng { get; init; } = new();
    public List<SimPlayer> Players { get; } = new();
    public List<(float X, float Z)> Hotspots { get; } = new();
    /// <summary>How far players wander around a hotspot. Small on custom maps, whose hotspots are known dry land.</summary>
    public int Spread { get; set; } = 180;
    /// <summary>The custom map the hotspots came from ("" = random hotspots).</summary>
    public string SpotsFrom { get; set; } = "";
    public DateTime StartedUtc { get; init; } = DateTime.UtcNow;
    public int Entities { get; set; } = 180_000;
    public object Lock { get; } = new();

    // Simulated Oxide permission system and Rust ban list for the demo.
    public Dictionary<string, (HashSet<string> Users, HashSet<string> Perms)> Groups { get; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["default"] = (new HashSet<string>(), new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "betterchat.use", "kits.default", "nteleportation.home", "nteleportation.tpr" }),
        ["admin"] = (new HashSet<string>(), new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "kits.admin", "removertool.admin", "gathermanager.admin", "nteleportation.admin" }),
        ["vip"] = (new HashSet<string>(), new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "kits.vip", "nteleportation.vip", "betterchat.vip" }),
    };
    public Dictionary<string, HashSet<string>> UserPerms { get; } = new();
    public List<(string Id, string Name, string Reason)> Bans { get; } = new()
    {
        ("76561198012345678", "Cheater_McGee", "Aimbot"),
        ("76561198087654321", "ToxicTim", "Harassment - 7 day ban"),
    };
    public static readonly string[] AllPerms =
    {
        "betterchat.use", "betterchat.vip", "gathermanager.admin", "kits.admin", "kits.default", "kits.vip",
        "nteleportation.admin", "nteleportation.home", "nteleportation.tpr", "nteleportation.vip",
        "removertool.admin", "removertool.normal", "rustsandboxtelemetry.ignore",
    };
}

/// <summary>
/// A tiny fake Rust world. Lets people explore the dashboard, console, live map and stats
/// before they have pointed the manager at a real server.
/// </summary>
public sealed class DemoWorld
{
    private readonly ConcurrentDictionary<int, DemoServerState> _states = new();
    private readonly ConcurrentDictionary<int, ConcurrentDictionary<DemoRconTransport, byte>> _transports = new();

    public static readonly SimWeapon[] Weapons =
    {
        new("Assault Rifle", 8, 190, 0.28, 30, true),
        new("LR-300 Assault Rifle", 8, 170, 0.26, 10, true),
        new("Bolt Action Rifle", 40, 420, 0.45, 9, true),
        new("L96 Rifle", 80, 620, 0.5, 3, true),
        new("M39 Rifle", 25, 260, 0.33, 6, true),
        new("Semi-Automatic Rifle", 8, 130, 0.22, 14, true),
        new("Thompson", 4, 60, 0.18, 8, true),
        new("Custom SMG", 4, 55, 0.16, 8, true),
        new("MP5A4", 4, 70, 0.18, 5, true),
        new("Pump Shotgun", 1, 14, 0.08, 9, true),
        new("Double Barrel Shotgun", 1, 10, 0.06, 5, true),
        new("Python Revolver", 4, 45, 0.25, 5, true),
        new("Semi-Automatic Pistol", 3, 35, 0.2, 7, true),
        new("Revolver", 3, 30, 0.2, 4, true),
        new("Hunting Bow", 4, 55, 0.25, 7, true),
        new("Crossbow", 4, 60, 0.3, 4, true),
        new("Compound Bow", 5, 70, 0.3, 2, true),
        new("M249", 15, 210, 0.18, 2, true),
        new("Nailgun", 2, 25, 0.12, 3, true),
        new("Eoka Pistol", 1, 8, 0.05, 2, true),
        new("Salvaged Sword", 1, 2.5f, 0.1, 2, false),
        new("Machete", 1, 2.5f, 0.1, 2, false),
        new("Rock", 1, 2, 0.1, 3, false),
        new("Wooden Spear", 1, 3, 0.1, 2, false),
    };

    private static readonly string[] Names =
    {
        "Bambi_Slayer", "xX_Raider_Xx", "NakedBob", "RockOnly", "TwigWarrior", "SulfurSam", "OilRigOllie", "Scrapper",
        "DoorCamper", "Grubby", "Zerg_Leader", "HazmatHank", "CompoundKing", "BeachBum", "LowGrade", "Fishing_Pro",
        "BagMan", "RoofCamper", "MetalFrags", "TeaDrinker", "SatchelSue", "C4Charlie", "BerryPicker", "Heli_Hunter",
        "BradleyBro", "GibsGary", "Tool_Cupboard", "PopUpPete", "Shotgun_Trap", "Rad_Town_Rob", "Outpost_Olly",
        "Bandit_Barry", "CargoCaptain", "LargeRigLarry", "TechTrashTom", "HQM_Hoarder", "Blueprint_Ben", "WipeDayWill",
        "Solo_Sally", "TrioTim", "DuoDan", "ClanCarl", "Hapis_Hero", "SnowBiome", "DesertDweller", "Swamp_Thing",
        "Lockpick", "Furnace_Fred", "Hemp_Harvester", "Horse_Girl", "Minicopter_Mike", "Scrap_Heli", "Tugboat_Tess",
        "Diver_Dave", "Vending_Vic", "Recycler_Rae", "Dome_Diver", "Launch_Lou", "Airfield_Al", "Sewer_Steve",
    };

    public static readonly string[] AnimalVictims = { "Bear", "Wolf", "Boar", "Stag", "Chicken", "Horse", "Polar Bear" };
    public static readonly string[] AnimalKillers = { "Bear", "Wolf", "Boar", "Polar Bear", "Shark" };
    public static readonly string[] EnvironmentCauses = { "Fall", "Cold", "Radiation", "Drowned", "Bleeding", "Hunger", "Thirst", "Heat" };
    public static readonly string[] Traps = { "Auto Turret", "Shotgun Trap", "Flame Turret", "Landmine", "Bear Trap", "SAM Site" };
    public static readonly string[] BodyParts = { "Head", "Chest", "Chest", "Chest", "Stomach", "Stomach", "Arm", "Arm", "Leg", "Leg", "Hand", "Foot" };

    public DemoServerState GetState(int serverId, int? worldSize = null)
    {
        var state = _states.GetOrAdd(serverId, id => Create(id, worldSize ?? 4000));
        if (worldSize is { } size) state.WorldSize = size;
        return state;
    }

    /// <summary>The server's state, with players kept on dry land when it runs a custom map that lists land spots (e.g. Maui).</summary>
    public DemoServerState GetState(ServerInstance server)
    {
        var st = server.Settings;
        var state = GetState(server.Id, st.WorldSize);
        var mapFile = st.MapMode == MapMode.CustomMap ? st.CustomMapFile ?? "" : "";
        if (state.SpotsFrom == mapFile) return state;
        lock (state.Lock)
        {
            var map = CustomMaps.Find(mapFile);
            var spots = map is null ? null : CustomMaps.Details(map)?.LandSpots.Where(a => a.Length >= 2).Select(a => (a[0], a[1])).ToList();
            state.SpotsFrom = mapFile;
            if (spots is not { Count: > 0 }) return state;
            state.Hotspots.Clear();
            state.Hotspots.AddRange(spots);
            state.Spread = 60;
            var rng = state.Rng;
            foreach (var p in state.Players)
            {
                var spot = spots[rng.Next(spots.Count)];
                p.X = p.TargetX = spot.Item1 + rng.Next(-40, 40);
                p.Z = p.TargetZ = spot.Item2 + rng.Next(-40, 40);
            }
        }
        return state;
    }

    public IEnumerable<DemoServerState> States => _states.Values;

    private static DemoServerState Create(int serverId, int worldSize)
    {
        var rng = new Random(serverId * 7919 + 17);
        var state = new DemoServerState { ServerId = serverId, WorldSize = worldSize, Rng = rng, StartedUtc = DateTime.UtcNow.AddHours(-rng.Next(2, 30)) };
        var half = worldSize * 0.36f;
        for (var i = 0; i < 14; i++)
            state.Hotspots.Add(((float)(rng.NextDouble() * 2 - 1) * half, (float)(rng.NextDouble() * 2 - 1) * half));

        var weights = Weapons.Where(w => w.Ranged).ToArray();
        for (var i = 0; i < Names.Length; i++)
        {
            var spot = state.Hotspots[rng.Next(state.Hotspots.Count)];
            var p = new SimPlayer
            {
                SteamId = (76561198000000000UL + (ulong)(serverId * 100000 + i * 7919 + 13)).ToString(),
                Name = Names[i],
                Skill = Math.Pow(rng.NextDouble(), 1.6),
                FavWeapon = weights[rng.Next(weights.Length)].Name,
                X = spot.X + (float)(rng.NextDouble() * 300 - 150),
                Z = spot.Z + (float)(rng.NextDouble() * 300 - 150),
                Online = rng.NextDouble() < 0.6,
                Ping = rng.Next(18, 140),
                OnlineSinceUtc = DateTime.UtcNow.AddMinutes(-rng.Next(1, 240)),
            };
            p.TargetX = p.X;
            p.TargetZ = p.Z;
            state.Players.Add(p);
        }
        return state;
    }

    public IRconTransport CreateTransport(int serverId)
    {
        var t = new DemoRconTransport(this, serverId);
        return t;
    }

    internal void Register(DemoRconTransport t) =>
        _transports.GetOrAdd(t.ServerId, _ => new()).TryAdd(t, 0);

    internal void Unregister(DemoRconTransport t)
    {
        if (_transports.TryGetValue(t.ServerId, out var set)) set.TryRemove(t, out _);
    }

    /// <summary>Push a fake log/chat line to anyone connected to this demo server.</summary>
    public void Emit(int serverId, string type, string message)
    {
        if (!_transports.TryGetValue(serverId, out var set)) return;
        foreach (var t in set.Keys) t.Push(type, message);
    }

    // ---------------------------------------------------------------- event generation

    public static SimWeapon PickWeapon(Random rng, SimPlayer? shooter)
    {
        if (shooter is not null && rng.NextDouble() < 0.35)
        {
            var fav = Weapons.FirstOrDefault(w => w.Name == shooter.FavWeapon);
            if (fav is not null) return fav;
        }
        var total = Weapons.Sum(w => w.Weight);
        var roll = rng.Next(total);
        foreach (var w in Weapons)
        {
            if (roll < w.Weight) return w;
            roll -= w.Weight;
        }
        return Weapons[0];
    }

    private static float Clamp(float v, int worldSize) => Math.Clamp(v, -worldSize / 2f + 20, worldSize / 2f - 20);

    /// <summary>Generate one plausible death event involving the given (online) players.</summary>
    public KillEvent GenerateKill(DemoServerState s, IReadOnlyList<SimPlayer> pool, DateTime time)
    {
        var rng = s.Rng;
        var vi = rng.Next(pool.Count);
        var ki = pool.Count > 1 ? (vi + 1 + rng.Next(pool.Count - 1)) % pool.Count : vi;
        var victim = pool[vi];
        var killer = pool[ki];

        var k = new KillEvent { ServerId = s.ServerId, Timestamp = time };
        var roll = rng.NextDouble();

        void PlayerVictim(SimPlayer v) { k.VictimId = v.SteamId; k.VictimName = v.Name; k.VictimType = ActorType.Player; k.VictimX = v.X; k.VictimZ = v.Z; }
        void PlayerKiller(SimPlayer p) { k.KillerId = p.SteamId; k.KillerName = p.Name; k.KillerType = ActorType.Player; }
        void Shot(SimPlayer shooter, bool vsPlayer)
        {
            // Better players win more fights and take longer shots.
            var w = PickWeapon(rng, shooter);
            k.Weapon = w.Name;
            var t = Math.Pow(rng.NextDouble(), 1.8 - shooter.Skill);
            k.Distance = (float)Math.Round(w.MinDist + (w.MaxDist - w.MinDist) * t, 1);
            k.IsHeadshot = rng.NextDouble() < w.HeadshotRate * (0.7 + shooter.Skill * 0.6) * (vsPlayer ? 1 : 0.7);
            k.BodyPart = k.IsHeadshot ? "Head" : BodyParts[1 + rng.Next(BodyParts.Length - 1)];
            var angle = rng.NextDouble() * Math.PI * 2;
            k.KillerX = Clamp(k.VictimX + (float)(Math.Cos(angle) * k.Distance), s.WorldSize);
            k.KillerZ = Clamp(k.VictimZ + (float)(Math.Sin(angle) * k.Distance), s.WorldSize);
            shooter.X = k.KillerX;
            shooter.Z = k.KillerZ;
        }

        if (roll < 0.46)
        {
            // PvP — skill decides who wins more often
            if (rng.NextDouble() < 0.5 + (victim.Skill - killer.Skill) * 0.6) (killer, victim) = (victim, killer);
            PlayerVictim(victim); PlayerKiller(killer); Shot(killer, true);
        }
        else if (roll < 0.58)
        {
            PlayerKiller(killer);
            k.VictimType = ActorType.Scientist; k.VictimName = rng.NextDouble() < 0.2 ? "Heavy Scientist" : "Scientist";
            var spot = s.Hotspots[rng.Next(s.Hotspots.Count)];
            k.VictimX = spot.X + rng.Next(-60, 60); k.VictimZ = spot.Z + rng.Next(-60, 60);
            Shot(killer, false);
        }
        else if (roll < 0.73)
        {
            PlayerKiller(killer);
            k.VictimType = ActorType.Animal; k.VictimName = AnimalVictims[rng.Next(AnimalVictims.Length)];
            k.VictimX = Clamp(killer.X + rng.Next(-s.Spread * 5 / 6, s.Spread * 5 / 6), s.WorldSize); k.VictimZ = Clamp(killer.Z + rng.Next(-s.Spread * 5 / 6, s.Spread * 5 / 6), s.WorldSize);
            Shot(killer, false);
        }
        else if (roll < 0.81)
        {
            PlayerVictim(victim);
            k.KillerType = ActorType.Environment; k.DamageType = EnvironmentCauses[rng.Next(EnvironmentCauses.Length)]; k.KillerName = k.DamageType;
        }
        else if (roll < 0.88)
        {
            PlayerVictim(victim);
            k.KillerType = ActorType.Scientist; k.KillerName = rng.NextDouble() < 0.25 ? "Heavy Scientist" : "Scientist";
            k.Weapon = rng.NextDouble() < 0.6 ? "Assault Rifle" : "MP5A4";
            k.Distance = (float)Math.Round(10 + rng.NextDouble() * 70, 1);
            k.BodyPart = BodyParts[rng.Next(BodyParts.Length)]; k.IsHeadshot = k.BodyPart == "Head";
        }
        else if (roll < 0.93)
        {
            PlayerVictim(victim);
            k.KillerType = ActorType.Animal; k.KillerName = AnimalKillers[rng.Next(AnimalKillers.Length)];
            k.Distance = 1.5f; k.DamageType = "Bite";
        }
        else if (roll < 0.95)
        {
            PlayerVictim(victim);
            k.KillerType = ActorType.Helicopter; k.KillerName = "Patrol Helicopter"; k.Weapon = rng.NextDouble() < 0.7 ? "Heli Minigun" : "Heli Rocket";
            k.Distance = (float)Math.Round(40 + rng.NextDouble() * 120, 1);
        }
        else if (roll < 0.97)
        {
            PlayerVictim(victim);
            k.KillerType = ActorType.Trap; k.KillerName = Traps[rng.Next(Traps.Length)]; k.Weapon = k.KillerName;
            k.Distance = (float)Math.Round(2 + rng.NextDouble() * 25, 1);
        }
        else if (roll < 0.985)
        {
            PlayerVictim(victim);
            k.KillerType = ActorType.Self; k.KillerId = victim.SteamId; k.KillerName = victim.Name; k.DamageType = "Suicide";
        }
        else if (roll < 0.993)
        {
            PlayerKiller(killer);
            k.VictimType = ActorType.Helicopter; k.VictimName = "Patrol Helicopter";
            k.VictimX = Clamp(killer.X + rng.Next(-s.Spread * 2 / 3, s.Spread * 2 / 3), s.WorldSize); k.VictimZ = Clamp(killer.Z + rng.Next(-s.Spread * 2 / 3, s.Spread * 2 / 3), s.WorldSize);
            k.Weapon = rng.NextDouble() < 0.7 ? "Assault Rifle" : "M249"; k.Distance = (float)Math.Round(60 + rng.NextDouble() * 140, 1);
            k.KillerX = killer.X; k.KillerZ = killer.Z;
        }
        else
        {
            PlayerKiller(killer);
            k.VictimType = ActorType.Bradley; k.VictimName = "Bradley APC";
            var spot = s.Hotspots[0];
            k.VictimX = spot.X; k.VictimZ = spot.Z;
            k.Weapon = rng.NextDouble() < 0.6 ? "Rocket Launcher" : "HV Rocket"; k.Distance = (float)Math.Round(20 + rng.NextDouble() * 60, 1);
            k.KillerX = killer.X; k.KillerZ = killer.Z;
        }

        if (k.KillerType != ActorType.Player || k.VictimType != ActorType.Player)
        {
            if (k.KillerX == 0 && k.KillerZ == 0) { k.KillerX = k.VictimX; k.KillerZ = k.VictimZ; }
        }
        return k;
    }

    /// <summary>Move players toward hotspots; occasionally pick a new destination.</summary>
    public static void Step(DemoServerState s, double seconds, IEnumerable<SimPlayer>? who = null)
    {
        var rng = s.Rng;
        foreach (var p in who ?? s.Players.Where(p => p.Online))
        {
            var dx = p.TargetX - p.X;
            var dz = p.TargetZ - p.Z;
            var dist = MathF.Sqrt(dx * dx + dz * dz);
            if (dist < 15 || rng.NextDouble() < 0.01)
            {
                var spot = s.Hotspots[rng.Next(s.Hotspots.Count)];
                p.TargetX = Clamp(spot.X + rng.Next(-s.Spread, s.Spread), s.WorldSize);
                p.TargetZ = Clamp(spot.Z + rng.Next(-s.Spread, s.Spread), s.WorldSize);
                continue;
            }
            var speed = MathF.Min(dist, (float)(4 + rng.NextDouble() * 4) * (float)seconds); // ~walking/running pace
            p.X = Clamp(p.X + dx / dist * speed + (float)(rng.NextDouble() * 4 - 2), s.WorldSize);
            p.Z = Clamp(p.Z + dz / dist * speed + (float)(rng.NextDouble() * 4 - 2), s.WorldSize);
            p.Health = Math.Clamp(p.Health + (float)(rng.NextDouble() * 6 - 2.5), 8, 100);
        }
    }
}
