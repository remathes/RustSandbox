// Rust Sandbox — NPCs
// Puts more life in the world without a wall of settings. Pick a preset (scientists, heavies, bandits, raiders,
// elite guards, a wolf pack, bears...), say how many and where, and they spawn, wander and respawn on their own.
//   Monuments: add a preset to any monument by its name (every copy of it on the map gets the group).
//   Places:    stand somewhere in game and type /npcs add <preset> [count] [radius].
//   Roads:     raider patrols that walk along the roads.
//   Camps:     small bandit camps in the empty stretches between monuments (picked once per map).
// Gear is built in: each preset has outfits and weapons, one picked at random per NPC. No Kits plugin needed.
// Settings live in oxide/config/RustSandboxNpcs.json; the Rust Sandbox manager's NPCs page edits them for you.

using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Oxide.Core;
using Rust.Ai;
using Rust.Ai.Gen2.Nav;
using UnityEngine;
using UnityEngine.AI;

namespace Oxide.Plugins
{
    [Info("Rust Sandbox NPCs", "RustSandbox", "1.2.2")]
    [Description("Easy NPC spawns: presets with built-in gear at monuments, places you pick, road patrols and wild camps.")]
    public class RustSandboxNpcs : RustPlugin
    {
        private const string AdminPerm = "rustsandboxnpcs.admin";
        private const string ScientistRoam = "scientistnpc_roam";
        private const string ScientistHeavy = "scientistnpc_heavy";
        private const string CampfirePrefab = "assets/prefabs/deployable/campfire/campfire.prefab";

        #region Config

        private class Preset
        {
            [JsonProperty("Name")] public string Name = "";
            /// <summary>Scientist (a human NPC wearing the gear below) or Animal.</summary>
            [JsonProperty("Kind")] public string Kind = "Scientist";
            /// <summary>Scientist body: roam (normal) or heavy (Rust's heavy scientist).</summary>
            [JsonProperty("Body")] public string Body = "roam";
            [JsonProperty("Animals", ObjectCreationHandling = ObjectCreationHandling.Replace)] public List<string> Animals = new List<string>();
            /// <summary>0 = Rust's own health for that NPC.</summary>
            [JsonProperty("Health")] public float Health;
            [JsonProperty("Damage")] public float Damage = 1f;
            [JsonProperty("Accuracy")] public float Accuracy = 1f;
            [JsonProperty("Outfits", ObjectCreationHandling = ObjectCreationHandling.Replace)] public List<List<string>> Outfits = new List<List<string>>();
            [JsonProperty("Weapons", ObjectCreationHandling = ObjectCreationHandling.Replace)] public List<string> Weapons = new List<string>();
            /// <summary>Percent chance the NPC's weapon is in its body when it dies.</summary>
            [JsonProperty("WeaponDrop")] public int WeaponDrop = 10;
            [JsonProperty("KeepRustGear")] public bool KeepRustGear;

            [JsonIgnore] public bool IsAnimal => string.Equals(Kind, "Animal", StringComparison.OrdinalIgnoreCase);
        }

        private class Spot
        {
            [JsonProperty("Id")] public string Id = "";
            [JsonProperty("Enabled")] public bool Enabled = true;
            [JsonProperty("Preset")] public string Preset = "scientists";
            [JsonProperty("Count")] public int Count = 4;
            /// <summary>How far they wander from the middle. 0 = fit the monument.</summary>
            [JsonProperty("Radius")] public float Radius;
            [JsonProperty("RespawnMinutes")] public float RespawnMinutes = 10f;
            /// <summary>Monument prefab name (airfield_1, sphere_tank...). Every copy on the map gets the group.</summary>
            [JsonProperty("Monument")] public string Monument = "";
            /// <summary>"x y z" for a place picked in game (used when Monument is empty).</summary>
            [JsonProperty("Position")] public string Position = "";
            /// <summary>The map (size_seed) a Position belongs to; other maps skip it.</summary>
            [JsonProperty("Map")] public string Map = "";
            [JsonProperty("Label")] public string Label = "";
        }

        private class RoadSettings
        {
            [JsonProperty("Groups")] public int Groups = 3;
            [JsonProperty("Size")] public int Size = 3;
            [JsonProperty("Preset")] public string Preset = "raiders";
            [JsonProperty("RespawnMinutes")] public float RespawnMinutes = 15f;
        }

        private class CampSettings
        {
            /// <summary>Off, Few, Some or Many.</summary>
            [JsonProperty("Amount")] public string Amount = "Some";
            [JsonProperty("Preset")] public string Preset = "bandits";
            [JsonProperty("MinSize")] public int MinSize = 2;
            [JsonProperty("MaxSize")] public int MaxSize = 4;
            [JsonProperty("RespawnMinutes")] public float RespawnMinutes = 20f;
            [JsonProperty("Campfire")] public bool Campfire = true;
        }

        private class PluginConfig
        {
            [JsonProperty("Enabled")] public bool Enabled = true;
            /// <summary>Easy, Normal or Hard: scales every NPC's health, aim and damage together.</summary>
            [JsonProperty("Difficulty")] public string Difficulty = "Normal";
            // How far (metres) scientists notice players and shoot from. Rust's own range is the gun's short
            // "effective range", so out of it they just crouch and stand in combat without firing.
            [JsonProperty("EngageRange")] public float EngageRange = 45f;
            [JsonProperty("Presets", ObjectCreationHandling = ObjectCreationHandling.Replace)] public Dictionary<string, Preset> Presets = DefaultPresets();
            [JsonProperty("Spots", ObjectCreationHandling = ObjectCreationHandling.Replace)] public List<Spot> Spots = new List<Spot>();
            [JsonProperty("Roads")] public RoadSettings Roads = new RoadSettings();
            [JsonProperty("Camps")] public CampSettings Camps = new CampSettings();
        }

        private static List<string> L(params string[] items) => items.ToList();

        private static Dictionary<string, Preset> DefaultPresets() => new Dictionary<string, Preset>
        {
            ["scientists"] = new Preset
            {
                Name = "Scientist", Health = 150, Damage = 1f, Accuracy = 1f, WeaponDrop = 10,
                Outfits = { L("hazmatsuit_scientist"), L("hazmatsuit_scientist_peacekeeper") },
                Weapons = L("smg.mp5", "rifle.semiauto", "shotgun.pump", "rifle.lr300", "pistol.m92"),
            },
            ["heavies"] = new Preset
            {
                Name = "Heavy Scientist", Body = "heavy", Health = 300, Damage = 1f, Accuracy = 0.8f, WeaponDrop = 5,
                Outfits = { L("scientistsuit_heavy") },
                Weapons = L("lmg.m249", "rifle.ak", "shotgun.spas12"),
            },
            ["bandits"] = new Preset
            {
                Name = "Bandit", Health = 100, Damage = 0.8f, Accuracy = 0.8f, WeaponDrop = 15,
                Outfits =
                {
                    L("burlap.headwrap", "burlap.shirt", "burlap.trousers", "burlap.shoes"),
                    L("mask.bandana", "hoodie", "pants", "shoes.boots"),
                    L("hat.cap", "tshirt", "pants", "shoes.boots", "wood.armor.jacket"),
                    L("bucket.helmet", "jacket", "pants", "shoes.boots"),
                },
                Weapons = L("pistol.revolver", "pistol.semiauto", "shotgun.waterpipe", "shotgun.double", "smg.thompson"),
            },
            ["raiders"] = new Preset
            {
                Name = "Raider", Health = 140, Damage = 1f, Accuracy = 1f, WeaponDrop = 10,
                Outfits =
                {
                    L("metal.facemask", "metal.plate.torso", "roadsign.kilt", "hoodie", "pants", "shoes.boots", "tactical.gloves"),
                    L("coffeecan.helmet", "roadsign.jacket", "roadsign.kilt", "hoodie", "pants", "shoes.boots"),
                    L("riot.helmet", "roadsign.jacket", "hoodie", "pants", "shoes.boots"),
                },
                Weapons = L("rifle.ak", "rifle.semiauto", "smg.thompson", "smg.2", "shotgun.pump", "rifle.m39"),
            },
            ["elite"] = new Preset
            {
                Name = "Elite Guard", Health = 250, Damage = 1.2f, Accuracy = 1.3f, WeaponDrop = 5,
                Outfits =
                {
                    L("heavy.plate.helmet", "heavy.plate.jacket", "heavy.plate.pants", "shoes.boots", "tactical.gloves"),
                    L("metal.facemask", "metal.plate.torso", "roadsign.kilt", "hoodie", "pants", "shoes.boots", "tactical.gloves"),
                },
                Weapons = L("rifle.lr300", "rifle.ak", "lmg.m249", "rifle.m39"),
            },
            ["wolfpack"] = new Preset { Name = "Wolf", Kind = "Animal", Animals = L("wolf2") },
            ["bears"] = new Preset { Name = "Bear", Kind = "Animal", Animals = L("bear") },
            ["boars"] = new Preset { Name = "Boar", Kind = "Animal", Animals = L("boar") },
            ["bigcats"] = new Preset { Name = "Big cat", Kind = "Animal", Animals = L("panther", "tiger") },
        };

        private PluginConfig _config;

        protected override void LoadDefaultConfig() => _config = new PluginConfig();

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                _config = Config.ReadObject<PluginConfig>();
                if (_config == null) throw new Exception("empty");
            }
            catch (Exception ex)
            {
                PrintError($"Config file was invalid ({ex.Message}); using defaults.");
                _config = new PluginConfig();
            }
            if (_config.Presets == null || _config.Presets.Count == 0) _config.Presets = DefaultPresets();
            if (_config.Spots == null) _config.Spots = new List<Spot>();
            if (_config.Roads == null) _config.Roads = new RoadSettings();
            if (_config.Camps == null) _config.Camps = new CampSettings();
            foreach (var s in _config.Spots)
                if (string.IsNullOrEmpty(s.Id)) s.Id = NewId();
            SaveConfig();
        }

        protected override void SaveConfig() => Config.WriteObject(_config, true);

        private string NewId()
        {
            string id;
            do id = "s" + UnityEngine.Random.Range(1000, 99999);
            while (_config != null && _config.Spots.Any(x => x.Id == id));
            return id;
        }

        private float DiffHealth => _config.Difficulty == "Easy" ? 0.7f : _config.Difficulty == "Hard" ? 1.4f : 1f;
        private float DiffDamage => _config.Difficulty == "Easy" ? 0.6f : _config.Difficulty == "Hard" ? 1.3f : 1f;
        private float DiffAim => _config.Difficulty == "Easy" ? 0.7f : _config.Difficulty == "Hard" ? 1.4f : 1f;

        #endregion

        #region Data (camp spots, picked once per map)

        private class CampData
        {
            public string Map = "";
            public List<string> Points = new List<string>();
        }

        private CampData _camps = new CampData();

        private static string MapKey => World.Size + "_" + World.Seed;

        private static string Vec(Vector3 v) => $"{v.x:0.0} {v.y:0.0} {v.z:0.0}";

        private static bool TryVec(string s, out Vector3 v)
        {
            v = Vector3.zero;
            if (string.IsNullOrWhiteSpace(s)) return false;
            var p = s.Split(' ');
            float x, y, z;
            if (p.Length != 3 || !float.TryParse(p[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out x)
                || !float.TryParse(p[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out y)
                || !float.TryParse(p[2], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out z)) return false;
            v = new Vector3(x, y, z);
            return true;
        }

        #endregion

        #region Groups

        private class Group
        {
            public string Kind;       // spot, road, camp
            public string SpotId;
            public string Label;
            public string PresetId;
            public Preset Preset;
            public Vector3 Home;
            public float Radius;
            public int Count;
            public float RespawnSeconds;
            public readonly List<BaseCombatEntity> Alive = new List<BaseCombatEntity>();
            public readonly List<float> Pending = new List<float>();
            public int Failures;
            public float NextTry;
            public BaseEntity Prop;
            // Road patrols walk from point to point along one road.
            public Vector3[] Road;
            public int RoadIndex;
            public int RoadDir = 1;
            public float NextRoadMove;
        }

        private class NpcInfo
        {
            public Group Group;
            public string Weapon;
        }

        private readonly List<Group> _groups = new List<Group>();
        private readonly Dictionary<ulong, NpcInfo> _npcs = new Dictionary<ulong, NpcInfo>();
        private readonly List<string> _warnings = new List<string>();
        private Timer _tick;
        private float _readyAt;

        private void Init()
        {
            permission.RegisterPermission(AdminPerm, this);
            try { _camps = Interface.Oxide.DataFileSystem.ReadObject<CampData>(Name + "_camps") ?? new CampData(); }
            catch { _camps = new CampData(); }
        }

        private void OnServerInitialized()
        {
            CheckServerSettings();
            // Give Rust a moment to finish its navmesh before the first NPCs go down.
            _readyAt = Time.realtimeSinceStartup + 15f;
            _diagUntil = Time.realtimeSinceStartup + 1200f;
            Build();
            _tick = timer.Every(2f, Tick);
        }

        private void Unload()
        {
            _tick?.Destroy();
            KillAll();
        }

        private void CheckServerSettings()
        {
            _warnings.Clear();
            if (AiManager.nav_disable) _warnings.Add("nav_disable is true: NPCs can't walk. Set aimanager.nav_disable false.");
            if (!ConVar.AI.think) _warnings.Add("ai.think is false: NPCs won't move or fight. Set ai.think true.");
            if (!ConVar.AI.npc_enable) _warnings.Add("ai.npc_enable is false: Rust won't run NPCs. Set ai.npc_enable true.");
            if (!AiManager.nav_wait) _warnings.Add("nav_wait is false: the server can start before its navmesh is ready. Set aimanager.nav_wait true.");
            foreach (var w in _warnings) PrintWarning(w);
        }

        private void KillAll()
        {
            foreach (var g in _groups)
            {
                foreach (var e in g.Alive)
                    if (e != null && !e.IsDestroyed) e.Kill();
                if (g.Prop != null && !g.Prop.IsDestroyed) g.Prop.Kill();
            }
            _groups.Clear();
            _npcs.Clear();
        }

        /// <summary>(Re)creates every group from the config. Existing NPCs are removed first.</summary>
        private void Build()
        {
            KillAll();
            WriteMonumentList();
            if (!_config.Enabled) return;

            foreach (var spot in _config.Spots)
            {
                if (!spot.Enabled) continue;
                Preset preset;
                if (!_config.Presets.TryGetValue(spot.Preset ?? "", out preset)) { PrintWarning($"Spot {spot.Id}: there's no preset called '{spot.Preset}'."); continue; }
                if (!string.IsNullOrEmpty(spot.Monument))
                {
                    foreach (var m in Monuments().Where(x => MonumentKey(x) == spot.Monument))
                        _groups.Add(NewGroup("spot", spot.Id, spot.Label.Length > 0 ? spot.Label : MonumentName(m), spot.Preset, preset, m.transform.position,
                            spot.Radius > 0 ? spot.Radius : MonumentRadius(m), spot.Count, spot.RespawnMinutes));
                }
                else
                {
                    Vector3 pos;
                    if (spot.Map != MapKey || !TryVec(spot.Position, out pos)) continue;
                    _groups.Add(NewGroup("spot", spot.Id, spot.Label.Length > 0 ? spot.Label : Grid(pos), spot.Preset, preset, pos,
                        spot.Radius > 0 ? spot.Radius : 25f, spot.Count, spot.RespawnMinutes));
                }
            }

            BuildRoads();
            BuildCamps();
        }

        private Group NewGroup(string kind, string spotId, string label, string presetId, Preset preset, Vector3 home, float radius, int count, float respawnMinutes) => new Group
        {
            Kind = kind, SpotId = spotId, Label = label, PresetId = presetId, Preset = preset, Home = home,
            Radius = Mathf.Clamp(radius, 5f, 200f), Count = Mathf.Clamp(count, 1, 30), RespawnSeconds = Mathf.Max(0.5f, respawnMinutes) * 60f,
        };

        private void BuildRoads()
        {
            var r = _config.Roads;
            Preset preset;
            if (r.Groups <= 0 || !_config.Presets.TryGetValue(r.Preset ?? "", out preset) || TerrainMeta.Path == null) return;
            var roads = TerrainMeta.Path.Roads.Where(x => x?.Path?.Points != null && x.Path.Points.Length > 20).ToList();
            if (roads.Count == 0) return;
            var monuments = Monuments().Select(m => m.transform.position).ToList();
            var used = new List<Vector3>();
            for (int i = 0, tries = 0; i < r.Groups && tries < 200; tries++)
            {
                var road = roads[UnityEngine.Random.Range(0, roads.Count)];
                var index = UnityEngine.Random.Range(5, road.Path.Points.Length - 5);
                var p = road.Path.Points[index];
                if (monuments.Any(m => (m - p).sqrMagnitude < 120f * 120f) || used.Any(u => (u - p).sqrMagnitude < 400f * 400f)) continue;
                used.Add(p);
                var g = NewGroup("road", null, "Road patrol near " + Grid(p), r.Preset, preset, p, 20f, r.Size, r.RespawnMinutes);
                g.Road = road.Path.Points;
                g.RoadIndex = index;
                g.RoadDir = UnityEngine.Random.value < 0.5f ? 1 : -1;
                _groups.Add(g);
                i++;
            }
        }

        private int CampCount
        {
            get
            {
                switch ((_config.Camps.Amount ?? "").ToLower())
                {
                    case "few": return 4;
                    case "some": return 8;
                    case "many": return 14;
                    default: return 0;
                }
            }
        }

        private void BuildCamps()
        {
            var c = _config.Camps;
            Preset preset;
            var want = CampCount;
            if (want <= 0 || !_config.Presets.TryGetValue(c.Preset ?? "", out preset)) return;
            if (_camps.Map != MapKey) _camps = new CampData { Map = MapKey };
            var points = new List<Vector3>();
            foreach (var s in _camps.Points)
            {
                Vector3 v;
                if (TryVec(s, out v)) points.Add(v);
            }
            if (points.Count < want)
            {
                points.AddRange(FindCampSpots(want - points.Count, points));
                _camps.Points = points.Select(Vec).ToList();
                Interface.Oxide.DataFileSystem.WriteObject(Name + "_camps", _camps);
            }
            foreach (var p in points.Take(want))
            {
                var size = UnityEngine.Random.Range(Mathf.Max(1, c.MinSize), Mathf.Max(c.MinSize, c.MaxSize) + 1);
                var g = NewGroup("camp", null, "Camp at " + Grid(p), c.Preset, preset, p, 18f, size, c.RespawnMinutes);
                if (c.Campfire) g.Prop = SpawnCampfire(p);
                _groups.Add(g);
            }
        }

        private List<Vector3> FindCampSpots(int count, List<Vector3> existing)
        {
            var found = new List<Vector3>();
            var half = World.Size / 2f - 250f;
            if (half <= 0 || TerrainMeta.HeightMap == null) return found;
            var monuments = Monuments().Select(m => m.transform.position).ToList();
            var mask = LayerMask.GetMask("Construction", "Deployed");
            for (var tries = 0; tries < 1500 && found.Count < count; tries++)
            {
                var p = new Vector3(UnityEngine.Random.Range(-half, half), 0f, UnityEngine.Random.Range(-half, half));
                p.y = TerrainMeta.HeightMap.GetHeight(p);
                if (TerrainMeta.WaterMap != null && TerrainMeta.WaterMap.GetHeight(p) > p.y - 0.5f) continue;
                if (TerrainMeta.HeightMap.GetSlope(p) > 20f) continue;
                if (monuments.Any(m => (m - p).sqrMagnitude < 200f * 200f)) continue;
                if (existing.Concat(found).Any(o => (o - p).sqrMagnitude < 350f * 350f)) continue;
                if (Physics.CheckSphere(p, 90f, mask, QueryTriggerInteraction.Ignore)) continue; // someone's base
                Vector3 nav;
                if (NavReady)
                {
                    if (!OnNavmesh(p, 4f, out nav)) continue;
                    p = nav;
                }
                found.Add(p);
            }
            return found;
        }

        // A saved camp spot NPCs can't stand on (picked before the navmesh was ready, or the map changed) moves to a new one.
        private bool MoveCamp(Group g)
        {
            var others = _groups.Where(o => o != g && o.Kind == "camp").Select(o => o.Home).ToList();
            var spot = FindCampSpots(1, others);
            if (spot.Count == 0) return false;
            var old = g.Label;
            var p = spot[0];
            var key = Vec(g.Home);
            var i = _camps.Points.IndexOf(key);
            if (i >= 0) _camps.Points[i] = Vec(p); else _camps.Points.Add(Vec(p));
            Interface.Oxide.DataFileSystem.WriteObject(Name + "_camps", _camps);
            g.Home = p;
            g.Label = "Camp at " + Grid(p);
            g.Failures = 0;
            g.NextTry = 0f;
            if (g.Prop != null && !g.Prop.IsDestroyed) { g.Prop.Kill(); g.Prop = SpawnCampfire(p); }
            Puts($"{old} had no walkable ground, moved it to {Grid(p)}.");
            return true;
        }

        private BaseEntity SpawnCampfire(Vector3 p)
        {
            var fire = GameManager.server.CreateEntity(CampfirePrefab, p) as BaseOven;
            if (fire == null) return null;
            fire.enableSaving = false;
            fire.Spawn();
            using (var flags = fire.StartSetFlags(BaseEntity.FlagsUpdateMode.SendNetworkUpdate))
                flags.Set(BaseEntity.Flags.On, true); // looks lit; it isn't fuelled, so nothing cooks
            return fire;
        }

        #endregion

        #region Monuments

        private static IEnumerable<MonumentInfo> Monuments()
        {
            if (TerrainMeta.Path == null) return Enumerable.Empty<MonumentInfo>();
            return TerrainMeta.Path.Monuments.Where(m => m != null && !m.IsSafeZone && m.shouldDisplayOnMap);
        }

        private static string MonumentKey(MonumentInfo m)
        {
            var path = m.name ?? "";
            var slash = path.LastIndexOf('/');
            if (slash >= 0) path = path.Substring(slash + 1);
            if (path.EndsWith(".prefab")) path = path.Substring(0, path.Length - 7);
            return path.ToLower();
        }

        private static string MonumentName(MonumentInfo m)
        {
            var phrase = m.displayPhrase?.english;
            if (!string.IsNullOrEmpty(phrase)) return phrase.Trim();
            var key = MonumentKey(m).Replace('_', ' ');
            return key.Length > 0 ? char.ToUpper(key[0]) + key.Substring(1) : "Monument";
        }

        private static float MonumentRadius(MonumentInfo m)
        {
            var e = m.Bounds.extents;
            var r = Mathf.Max(e.x, e.z) * 0.6f;
            return Mathf.Clamp(r <= 0 ? 40f : r, 20f, 90f);
        }

        private static string Grid(Vector3 pos)
        {
            try { return MapHelper.PositionToString(pos); }
            catch { return $"{pos.x:0} {pos.z:0}"; }
        }

        private class MonumentEntry
        {
            public string Key;
            public string Name;
            public int Copies;
            public List<string> Grids = new List<string>();
        }

        private List<MonumentEntry> MonumentList()
        {
            return Monuments()
                .GroupBy(MonumentKey)
                .Select(g => new MonumentEntry
                {
                    Key = g.Key, Name = MonumentName(g.First()), Copies = g.Count(),
                    Grids = g.Select(m => Grid(m.transform.position)).ToList(),
                })
                .OrderBy(x => x.Name)
                .ToList();
        }

        // Saved so the manager can show the monument list while the server is off.
        private void WriteMonumentList()
        {
            Interface.Oxide.DataFileSystem.WriteObject(Name + "_monuments", new Dictionary<string, object>
            {
                ["Map"] = MapKey,
                ["Monuments"] = MonumentList(),
            });
        }

        #endregion

        #region Spawning

        private static readonly Dictionary<string, string> _prefabs = new Dictionary<string, string>();

        /// <summary>Finds the full prefab path for an NPC or animal name (wolf2, bear, scientistnpc_roam...).</summary>
        private static string PrefabFor(string name)
        {
            string path;
            if (_prefabs.TryGetValue(name, out path)) return path;
            var want = "/" + name.ToLower() + ".prefab";
            path = GameManifest.pathToGuid.Keys.FirstOrDefault(p => p.StartsWith("assets/rust.ai/agents/", StringComparison.OrdinalIgnoreCase) && p.EndsWith(want, StringComparison.OrdinalIgnoreCase))
                ?? GameManifest.pathToGuid.Keys.FirstOrDefault(p => p.EndsWith(want, StringComparison.OrdinalIgnoreCase));
            _prefabs[name] = path;
            return path;
        }

        private static int _humanAgentType = -1;

        /// <summary>Rust's own navmesh (RustNav) covers the whole map. Unity's navmesh is only used when ai.useunitynavmesh is on.</summary>
        private static RustNavmesh RustNavReady()
        {
            if (ConVar.AI.useUnityNavmesh) return null;
            var nav = RustNavigation.Instance;
            if (nav == null || !nav.IsDefaultNavmeshBuilt()) return null;
            var mesh = nav.DefaultNavmesh;
            return mesh != null && mesh.IsValid() ? mesh : null;
        }

        private static bool NavReady => ConVar.AI.useUnityNavmesh || RustNavReady() != null;

        private static bool OnNavmesh(Vector3 p, float range, out Vector3 result)
        {
            result = p;
            var rustNav = RustNavReady();
            if (rustNav != null)
            {
                NavHit navHit;
                if (!rustNav.SamplePosition(new NavVector3(p), out navHit, new Vector3(range, range, range))) return false;
                result = navHit.position.Value;
                return true;
            }
            if (!ConVar.AI.useUnityNavmesh) return false;
            NavMeshHit hit;
            bool ok;
            if (_humanAgentType >= 0)
                ok = NavMesh.SamplePosition(p, out hit, range, new NavMeshQueryFilter { agentTypeID = _humanAgentType, areaMask = NavMesh.AllAreas });
            else
                ok = NavMesh.SamplePosition(p, out hit, range, NavMesh.AllAreas);
            if (!ok) return false;
            result = hit.position;
            return true;
        }

        private static Vector3? FindPoint(Vector3 home, float radius)
        {
            for (var i = 0; i < 15; i++)
            {
                var off = UnityEngine.Random.insideUnitCircle * radius * (i < 10 ? 1f : 0.3f);
                var p = home + new Vector3(off.x, 0f, off.y);
                if (TerrainMeta.HeightMap != null)
                {
                    var ground = TerrainMeta.HeightMap.GetHeight(p);
                    p.y = Mathf.Max(ground, home.y);
                    if (TerrainMeta.WaterMap != null && TerrainMeta.WaterMap.GetHeight(p) > ground + 0.3f && home.y < ground + 1f) continue;
                }
                Vector3 nav;
                if (OnNavmesh(p, 6f, out nav)) return nav;
            }
            return null;
        }

        private BaseCombatEntity SpawnOne(Group g)
        {
            var pos = FindPoint(g.Home, g.Radius);
            if (!pos.HasValue) return null;
            var rot = Quaternion.Euler(0f, UnityEngine.Random.Range(0f, 360f), 0f);
            return g.Preset.IsAnimal ? SpawnAnimal(g, pos.Value, rot) : SpawnScientist(g, pos.Value, rot);
        }

        private BaseCombatEntity SpawnScientist(Group g, Vector3 pos, Quaternion rot)
        {
            var preset = g.Preset;
            var path = PrefabFor(preset.Body == "heavy" ? ScientistHeavy : ScientistRoam) ?? PrefabFor(ScientistRoam);
            if (path == null) { Warn("Couldn't find Rust's scientist prefab."); return null; }
            var npc = GameManager.server.CreateEntity(path, pos, rot) as HumanNPC;
            if (npc == null) return null;
            npc.enableSaving = false;
            if (_humanAgentType < 0 && npc.NavAgent != null) _humanAgentType = npc.NavAgent.agentTypeID;

            var weapon = "";
            var gear = !preset.KeepRustGear && (preset.Outfits.Count > 0 || preset.Weapons.Count > 0);
            if (gear) npc.loadouts = new PlayerInventoryProperties[0];
            npc.Spawn();
            if (!string.IsNullOrEmpty(preset.Name)) npc.displayName = preset.Name;

            if (gear)
            {
                npc.inventory.Strip();
                if (preset.Outfits.Count > 0)
                    foreach (var shortname in preset.Outfits[UnityEngine.Random.Range(0, preset.Outfits.Count)])
                        Give(npc.inventory.containerWear, shortname);
                if (preset.Weapons.Count > 0)
                {
                    weapon = preset.Weapons[UnityEngine.Random.Range(0, preset.Weapons.Count)];
                    var item = Give(npc.inventory.containerBelt, weapon);
                    var gun = item?.GetHeldEntity() as BaseProjectile;
                    if (gun != null && gun.primaryMagazine != null) gun.primaryMagazine.contents = gun.primaryMagazine.capacity;
                }
            }
            else
            {
                var held = npc.inventory.containerBelt.GetSlot(0);
                if (held != null) weapon = held.info.shortname;
            }

            var health = (preset.Health > 0 ? preset.Health : npc.MaxHealth()) * DiffHealth;
            npc.startHealth = health;
            npc._maxHealth = health;
            npc.InitializeHealth(health, health);
            npc.aimConeScale = npc.aimConeScale / Mathf.Max(0.2f, preset.Accuracy * DiffAim);

            _npcs[npc.net.ID.Value] = new NpcInfo { Group = g, Weapon = weapon };
            timer.Once(0.5f, () => SetupBrain(npc, g));
            return npc;
        }

        private Item Give(ItemContainer container, string shortname)
        {
            var def = ItemManager.FindItemDefinition(shortname);
            if (def == null) { Warn($"There's no item called '{shortname}' (check the preset's outfits and weapons)."); return null; }
            var item = ItemManager.Create(def, 1);
            if (item.MoveToContainer(container)) return item;
            item.Remove();
            return null;
        }

        private void SetupBrain(HumanNPC npc, Group g)
        {
            if (npc == null || npc.IsDestroyed || npc.Brain == null) return;
            try
            {
                npc.EquipWeapon(true);
                var nav = npc.Brain.Navigator;
                if (nav != null)
                {
                    nav.MaxRoamDistanceFromHome = g.Radius;
                    nav.BestRoamPointMaxDistance = g.Radius;
                }
                npc.Brain.Events?.Memory?.Position.Set(g.Home, 4);
            }
            catch (Exception ex) { Warn("NPC setup failed: " + ex.Message); }
            timer.Once(0.5f, () => SetRange(npc));
        }

        private bool _rangeLogged;
        private float _diagUntil;
        private readonly Dictionary<ulong, float> _diagNext = new Dictionary<ulong, float>();

        // For 20 minutes after loading, what each scientist with a player target is doing goes to the log, so a
        // fight that goes wrong (not shooting from range) can be read back afterwards.
        private void Diagnose(Group g)
        {
            foreach (var e in g.Alive)
            {
                var npc = e as HumanNPC;
                if (npc == null || npc.IsDestroyed || npc.Brain == null || npc.net == null) continue;
                try
                {
                    var brain = npc.Brain;
                    var target = brain.Events?.Memory?.Entity.Get(brain.Events.CurrentInputMemorySlot) as BasePlayer;
                    if (target == null || target.IsNpc) continue;
                    float next;
                    var id = npc.net.ID.Value;
                    if (_diagNext.TryGetValue(id, out next) && Time.realtimeSinceStartup < next) continue;
                    _diagNext[id] = Time.realtimeSinceStartup + 4f;
                    var dir = (target.CenterPoint() - npc.eyes.position).normalized;
                    var gun = npc.GetAttackEntity() as BaseProjectile;
                    var agent = brain.Navigator?.Agent;
                    Puts($"[fight] {npc.displayName} {Grid(npc.transform.position)} state={brain.CurrentState?.StateType} " +
                         $"dist={Vector3.Distance(npc.transform.position, target.transform.position):0.0} engage={npc.EngagementRange():0} " +
                         $"los={brain.Senses.Memory.IsLOS(target)} facing={Vector3.Dot(npc.eyes.BodyForward(), dir):0.00} aimed={npc.targetAimedDuration:0.0} " +
                         $"held={npc.GetHeldEntity()?.ShortPrefabName ?? "none"} ammo={(gun != null ? gun.primaryMagazine.contents.ToString() : "-")} " +
                         $"navmesh={(agent != null && agent.enabled ? agent.isOnNavMesh.ToString() : "off")} moving={brain.Navigator?.Moving} " +
                         $"faceOverride={brain.Navigator?.IsOverridingFacingDirection} ducked={npc.modelState?.ducked}");
                }
                catch (Exception ex) { Warn("Fight log failed: " + ex.Message); }
            }
        }

        // Rust only lets a scientist fire within its gun's engagement range (effective range x 2 x the brain's
        // multiplier). For player guns that is short, so from further off they sit in combat ducking up and down.
        private void SetRange(HumanNPC npc)
        {
            if (npc == null || npc.IsDestroyed || npc.Brain == null) return;
            var brain = npc.Brain;
            var want = Mathf.Clamp(_config.EngageRange, 5f, 150f);
            var before = npc.EngagementRange();
            if (before > 0.1f && before < want) brain.AttackRangeMultiplier *= want / before;
            if (brain.SenseRange < want) brain.SenseRange = want;
            if (brain.TargetLostRange < want * 1.5f) brain.TargetLostRange = want * 1.5f;
            if (!_rangeLogged)
            {
                _rangeLogged = true;
                Puts($"Scientists engage at {npc.EngagementRange():0} m (Rust gave {before:0} m with {npc.GetAttackEntity()?.ShortPrefabName ?? "no weapon"}), notice players at {brain.SenseRange:0} m.");
            }
        }

        private BaseCombatEntity SpawnAnimal(Group g, Vector3 pos, Quaternion rot)
        {
            var preset = g.Preset;
            if (preset.Animals.Count == 0) return null;
            var name = preset.Animals[UnityEngine.Random.Range(0, preset.Animals.Count)];
            var path = PrefabFor(name);
            if (path == null) { Warn($"There's no animal called '{name}'."); return null; }
            var animal = GameManager.server.CreateEntity(path, pos, rot) as BaseCombatEntity;
            if (animal == null) return null;
            animal.enableSaving = false;
            animal.Spawn();
            var health = (preset.Health > 0 ? preset.Health : animal.MaxHealth()) * DiffHealth;
            animal._maxHealth = health;
            animal.InitializeHealth(health, health);
            _npcs[animal.net.ID.Value] = new NpcInfo { Group = g };
            return animal;
        }

        private readonly HashSet<string> _warned = new HashSet<string>();

        private void Warn(string msg)
        {
            if (_warned.Add(msg)) PrintWarning(msg);
        }

        #endregion

        #region Tick

        private void Tick()
        {
            if (!_config.Enabled || Time.realtimeSinceStartup < _readyAt) return;
            var now = Time.realtimeSinceStartup;
            var spawned = 0;
            foreach (var g in _groups)
            {
                // Bodies left: each dead one comes back after the respawn time.
                for (var i = g.Alive.Count - 1; i >= 0; i--)
                {
                    var e = g.Alive[i];
                    if (e != null && !e.IsDestroyed && !e.IsDead()) continue;
                    g.Alive.RemoveAt(i);
                    g.Pending.Add(now + g.RespawnSeconds);
                }
                g.Pending.RemoveAll(t => t <= now);

                var missing = g.Count - g.Alive.Count - g.Pending.Count;
                if (missing > 0 && spawned < 4 && now >= g.NextTry)
                {
                    var e = SpawnOne(g);
                    if (e != null) { g.Alive.Add(e); g.Failures = 0; spawned++; }
                    else
                    {
                        g.Failures++;
                        g.NextTry = now + Mathf.Min(300f, 5f * g.Failures);
                        if (g.Kind == "camp" && g.Failures >= 3 && g.Alive.Count == 0 && NavReady && MoveCamp(g)) continue;
                        if (g.Failures == 10) Warn($"Can't find walkable ground for {g.Label} ({Grid(g.Home)}). Try a bigger radius or another spot.");
                    }
                }

                if (g.Road != null) MoveAlongRoad(g, now);
                Wander(g);
                if (now < _diagUntil) Diagnose(g);
            }
        }

        // Scientists only roam where Rust placed roam points (inside monuments). Everywhere else this keeps them strolling.
        private void Wander(Group g)
        {
            foreach (var e in g.Alive)
            {
                var npc = e as HumanNPC;
                if (npc != null)
                {
                    var brain = npc.Brain;
                    if (brain?.Navigator == null || brain.CurrentState == null || brain.PathFinder == null) continue;
                    var state = brain.CurrentState.StateType;
                    if (state != AIState.Idle && state != AIState.Roam) continue;
                    if (brain.Navigator.Moving || UnityEngine.Random.value > 0.15f) continue;
                    var dest = brain.PathFinder.GetRandomPositionAround(g.Home, 2f, g.Radius);
                    brain.Navigator.SetDestination(dest, BaseNavigator.NavigationSpeed.Slow);
                    continue;
                }
                // Animals roam on their own; one that wanders far off is swapped for a fresh one at home.
                if (e != null && !e.IsDestroyed && (e.transform.position - g.Home).sqrMagnitude > Mathf.Pow(Mathf.Max(150f, g.Radius * 4f), 2) && e.SecondsSinceAttacked > 60f)
                    e.Kill();
            }
        }

        private void MoveAlongRoad(Group g, float now)
        {
            if (now < g.NextRoadMove) return;
            g.NextRoadMove = now + 30f;
            foreach (var e in g.Alive)
            {
                var npc = e as HumanNPC;
                var st = npc?.Brain?.CurrentState?.StateType;
                if (st.HasValue && st.Value != AIState.Idle && st.Value != AIState.Roam) return; // fighting: stay put
            }
            // About 40 m further along, turning round at the ends.
            var from = g.Road[g.RoadIndex];
            var i = g.RoadIndex;
            while (true)
            {
                var next = i + g.RoadDir;
                if (next < 0 || next >= g.Road.Length) { g.RoadDir = -g.RoadDir; break; }
                i = next;
                if ((g.Road[i] - from).sqrMagnitude > 40f * 40f) break;
            }
            g.RoadIndex = i;
            g.Home = g.Road[i];
            foreach (var e in g.Alive)
            {
                var npc = e as HumanNPC;
                if (npc?.Brain?.Events?.Memory == null) continue;
                npc.Brain.Events.Memory.Position.Set(g.Home, 4);
                npc.Brain.Navigator?.SetDestination(npc.Brain.PathFinder.GetRandomPositionAround(g.Home, 1f, 6f), BaseNavigator.NavigationSpeed.Slow);
            }
        }

        #endregion

        #region Hooks

        private object OnEntityTakeDamage(BaseCombatEntity entity, HitInfo info)
        {
            var attacker = info?.Initiator;
            if (attacker == null || attacker.net == null) return null;
            NpcInfo ni;
            if (!_npcs.TryGetValue(attacker.net.ID.Value, out ni)) return null;
            var scale = ni.Group.Preset.Damage * DiffDamage;
            if (Math.Abs(scale - 1f) > 0.001f) info.damageTypes.ScaleAll(scale);
            return null;
        }

        private object OnCorpsePopulate(HumanNPC npc, NPCPlayerCorpse corpse)
        {
            NpcInfo ni;
            if (npc == null || npc.net == null || corpse == null || !_npcs.TryGetValue(npc.net.ID.Value, out ni)) return null;
            if (string.IsNullOrEmpty(ni.Weapon) || UnityEngine.Random.Range(0, 100) >= ni.Group.Preset.WeaponDrop) return null;
            var weapon = ni.Weapon;
            NextTick(() =>
            {
                if (corpse == null || corpse.IsDestroyed || corpse.containers == null || corpse.containers.Length == 0) return;
                var item = ItemManager.CreateByName(weapon);
                if (item == null) return;
                if (item.hasCondition) item.condition = item.maxCondition * UnityEngine.Random.Range(0.2f, 0.6f);
                if (!item.MoveToContainer(corpse.containers[0])) item.Remove();
            });
            return null;
        }

        private void OnEntityKill(BaseNetworkable entity)
        {
            if (entity?.net != null) _npcs.Remove(entity.net.ID.Value);
        }

        #endregion

        #region Commands

        private bool IsAdmin(BasePlayer player) => player != null && (player.IsAdmin || permission.UserHasPermission(player.UserIDString, AdminPerm));

        [ChatCommand("npcs")]
        private void NpcsCommand(BasePlayer player, string command, string[] args)
        {
            if (!IsAdmin(player)) { player.ChatMessage("Only admins can manage NPC spawns."); return; }
            var sub = args.Length > 0 ? args[0].ToLower() : "";
            switch (sub)
            {
                case "add":
                {
                    if (args.Length < 2 || !_config.Presets.ContainsKey(args[1].ToLower()))
                    {
                        Say(player, "Usage: /npcs add <preset> [count] [radius] [respawn minutes]\nPresets: " + string.Join(", ", _config.Presets.Keys));
                        return;
                    }
                    int count;
                    float radius, respawn;
                    var spot = new Spot
                    {
                        Id = NewId(), Preset = args[1].ToLower(),
                        Count = args.Length > 2 && int.TryParse(args[2], out count) ? Mathf.Clamp(count, 1, 30) : 4,
                        Radius = args.Length > 3 && float.TryParse(args[3], out radius) ? Mathf.Clamp(radius, 5f, 200f) : 25f,
                        RespawnMinutes = args.Length > 4 && float.TryParse(args[4], out respawn) ? Mathf.Max(0.5f, respawn) : 10f,
                        Position = Vec(player.transform.position), Map = MapKey,
                        Label = _config.Presets[args[1].ToLower()].Name + " at " + Grid(player.transform.position),
                    };
                    Vector3 nav;
                    if (!OnNavmesh(player.transform.position, 8f, out nav))
                        Say(player, "<color=#ffb347>Careful:</color> there's no walkable ground for NPCs right here. They'll try nearby spots within the radius.");
                    _config.Spots.Add(spot);
                    SaveConfig();
                    Build();
                    Say(player, $"Added {spot.Count} {spot.Preset} here (spot {spot.Id}, {spot.Radius:0} m, back {spot.RespawnMinutes:0.#} min after dying).");
                    return;
                }
                case "remove":
                case "delete":
                {
                    Spot spot = null;
                    if (args.Length > 1) spot = _config.Spots.FirstOrDefault(x => x.Id.Equals(args[1], StringComparison.OrdinalIgnoreCase));
                    else spot = NearestSpot(player, 40f);
                    if (spot == null) { Say(player, "No spot found. Use /npcs list to see them, then /npcs remove <id>."); return; }
                    _config.Spots.Remove(spot);
                    SaveConfig();
                    Build();
                    Say(player, $"Removed spot {spot.Id} ({spot.Label}).");
                    return;
                }
                case "list":
                {
                    var list = ListFor(player);
                    var lines = new List<string>();
                    for (var i = 0; i < list.Count && i < 15; i++)
                    {
                        var g = list[i];
                        lines.Add($"<color=#7bd88f>{i + 1}</color>. {g.Label} ({Grid(g.Home)}): {g.PresetId} {AliveCount(g)}/{g.Count} alive, {Vector3.Distance(g.Home, player.transform.position):0} m away");
                    }
                    Say(player, lines.Count == 0 ? "No NPC groups yet." : $"{_groups.Count} NPC groups, {_groups.Sum(AliveCount)} NPCs alive. Nearest:\n" + string.Join("\n", lines) + "\n/npcs go <number> takes you there, /npcs show marks them on screen.");
                    return;
                }
                case "go":
                case "tp":
                {
                    var list = ListFor(player);
                    int n;
                    if (args.Length < 2 || !int.TryParse(args[1], out n) || n < 1 || n > list.Count) { Say(player, "Usage: /npcs go <number from /npcs list>"); return; }
                    var g = list[n - 1];
                    var alive = g.Alive.FirstOrDefault(e => e != null && !e.IsDestroyed && !e.IsDead());
                    var target = alive != null ? alive.transform.position : g.Home;
                    // Land well outside their sight range (scientists spot and shoot from 100 m or more).
                    var dist = 0f;
                    float wantDist;
                    if (args.Length > 2 && float.TryParse(args[2], out wantDist)) dist = Mathf.Clamp(wantDist, 10f, 500f);
                    if (dist <= 0f) dist = g.Preset != null && g.Preset.IsAnimal ? 60f : 160f;
                    var off = (player.transform.position - target).WithY(0);
                    off = off.sqrMagnitude < 1f ? Vector3.forward : off.normalized;
                    var land = target + off * dist;
                    if (TerrainMeta.HeightMap != null) land.y = TerrainMeta.HeightMap.GetHeight(land) + 0.5f;
                    if (TerrainMeta.WaterMap != null && TerrainMeta.WaterMap.GetHeight(land) > land.y)
                    {
                        // Water on this side: try the other sides.
                        for (var turn = 1; turn < 8; turn++)
                        {
                            var dir = Quaternion.Euler(0f, turn * 45f, 0f) * off;
                            var p2 = target + dir * dist;
                            p2.y = TerrainMeta.HeightMap.GetHeight(p2) + 0.5f;
                            if (TerrainMeta.WaterMap.GetHeight(p2) <= p2.y) { land = p2; break; }
                        }
                    }
                    player.Teleport(land);
                    Say(player, $"Took you to {g.Label} ({Grid(g.Home)}), {AliveCount(g)}/{g.Count} alive. They're about {dist:0} m ahead. /npcs go {n} <metres> lands closer or further.");
                    return;
                }
                case "show":
                {
                    var seconds = 30f;
                    float want;
                    if (args.Length > 1 && float.TryParse(args[1], out want)) seconds = Mathf.Clamp(want, 5f, 300f);
                    var shown = ShowNpcs(player, seconds);
                    Say(player, shown == 0 ? "No NPCs from this plugin within 1 km of you. Try /npcs list to find the nearest group." : $"Marked {shown} NPCs within 1 km for {seconds:0} s (green = scientists, orange = animals).");
                    return;
                }
                case "presets":
                    Say(player, "Presets: " + string.Join(", ", _config.Presets.Select(kv => $"{kv.Key} ({kv.Value.Name})")));
                    return;
                case "respawn":
                case "reset":
                    Build();
                    _readyAt = 0f;
                    Say(player, "All NPCs removed; they're spawning again.");
                    return;
                default:
                    Say(player, "/npcs add <preset> [count] [radius] [respawn min]  spawn a group where you stand\n/npcs list  every group, nearest first, with grid and how many are alive\n/npcs go <number> [metres]  teleport near a group from the list (160 m off by default)\n/npcs show [seconds]  mark NPCs near you on screen\n/npcs remove [id]  remove the nearest spot (or by id)\n/npcs presets  preset names\n/npcs respawn  clear and respawn every NPC\nMonuments, road patrols and camps are set on the manager's NPCs page.");
                    if (_warnings.Count > 0) Say(player, "<color=#ffb347>" + string.Join("\n", _warnings) + "</color>");
                    return;
            }
        }

        private List<Group> ListFor(BasePlayer player) =>
            _groups.OrderBy(x => (x.Home - player.transform.position).sqrMagnitude).ToList();

        private static int AliveCount(Group g) => g.Alive.Count(e => e != null && !e.IsDestroyed && !e.IsDead());

        /// <summary>Draws a label over every NPC of ours within 1 km (ddraw only works for admins, so non-admin holders of the permission get the flag for a moment).</summary>
        private int ShowNpcs(BasePlayer player, float seconds)
        {
            var temp = !player.IsAdmin;
            if (temp) { player.SetPlayerFlag(BasePlayer.PlayerFlags.IsAdmin, true); player.SendNetworkUpdateImmediate(); }
            var shown = 0;
            try
            {
                foreach (var g in _groups)
                {
                    var color = g.Preset != null && g.Preset.IsAnimal ? new Color(1f, 0.6f, 0.2f) : new Color(0.48f, 0.85f, 0.56f);
                    foreach (var e in g.Alive)
                    {
                        if (e == null || e.IsDestroyed || e.IsDead()) continue;
                        var d = Vector3.Distance(e.transform.position, player.transform.position);
                        if (d > 1000f) continue;
                        player.SendConsoleCommand("ddraw.text", seconds, color, e.transform.position + Vector3.up * 2.2f, $"<size=16>{g.PresetId} ({d:0} m)</size>");
                        player.SendConsoleCommand("ddraw.arrow", seconds, color, e.transform.position + Vector3.up * 12f, e.transform.position + Vector3.up * 2.6f, 1.2f);
                        shown++;
                    }
                }
            }
            finally
            {
                if (temp) { player.SetPlayerFlag(BasePlayer.PlayerFlags.IsAdmin, false); player.SendNetworkUpdateImmediate(); }
            }
            return shown;
        }

        private Spot NearestSpot(BasePlayer player, float within)
        {
            Spot best = null;
            var bestDist = within * within;
            foreach (var s in _config.Spots)
            {
                Vector3 v;
                if (s.Map != MapKey || !TryVec(s.Position, out v)) continue;
                var d = (v - player.transform.position).sqrMagnitude;
                if (d < bestDist) { bestDist = d; best = s; }
            }
            return best;
        }

        private static void Say(BasePlayer player, string msg) => player.ChatMessage("<color=#7bd88f>NPCs</color>: " + msg);

        [ConsoleCommand("sandboxnpcs.status")]
        private void StatusCommand(ConsoleSystem.Arg arg)
        {
            if (arg.Connection != null && !IsAdmin(arg.Player())) return;
            var status = new Dictionary<string, object>
            {
                ["version"] = Version.ToString(),
                ["enabled"] = _config.Enabled,
                ["difficulty"] = _config.Difficulty,
                ["map"] = MapKey,
                ["warnings"] = _warnings,
                ["alive"] = _groups.Sum(g => g.Alive.Count),
                ["groups"] = _groups.Select(g => new Dictionary<string, object>
                {
                    ["kind"] = g.Kind, ["spot"] = g.SpotId, ["label"] = g.Label, ["preset"] = g.PresetId,
                    ["alive"] = AliveCount(g), ["count"] = g.Count, ["grid"] = Grid(g.Home),
                }).ToList(),
                ["monuments"] = MonumentList(),
            };
            arg.ReplyWith(JsonConvert.SerializeObject(status));
        }

        [ConsoleCommand("sandboxnpcs.respawn")]
        private void RespawnCommand(ConsoleSystem.Arg arg)
        {
            if (arg.Connection != null && !IsAdmin(arg.Player())) return;
            Build();
            _readyAt = 0f;
            arg.ReplyWith("NPCs cleared; respawning.");
        }

        #endregion
    }
}
