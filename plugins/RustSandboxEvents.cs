// Rust Sandbox — Events
// Server events players can take part in. First up: the Treasure Hunt.
//
// A hunt starts at the Airfield: anyone who walks in gets a Treasure Compass (a renamed compass)
// and a riddle pointing at the next monument. Held in hand, the detector beeps faster the closer you are to
// the clue's hidden spot. Reach it and the detector recalibrates to the next clue. After the last clue a
// Golden Statue crate appears; the first player to open it wins the prize and the hunt ends.
//
// Train Heist: a self-driving loot train with scientist guards runs the rail loop; board it, clear the guards
// and hack the locked crate on the last wagon before the train reaches the end of its run.
//
// Start them with "sandboxevents.hunt.start" / "sandboxevents.heist.start" (RCON / F1 as admin) or from the
// Rust Sandbox "Events" page.
// Settings and the prize list are in oxide/config/RustSandboxEvents.json. Players type /hunt for their clue.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Game.Rust.Cui;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("Rust Sandbox Events", "RustSandbox", "1.7.3")]
    [Description("Server events: a treasure hunt across the map's monuments, and a train heist on a self-driving loot train.")]
    public class RustSandboxEvents : RustPlugin
    {
        #region Config

        private PluginConfig _config;

        private class PrizeItem
        {
            [JsonProperty("Item")] public string Item = "";
            [JsonProperty("Min")] public int Min = 1;
            [JsonProperty("Max")] public int Max = 1;
            [JsonProperty("Weight")] public float Weight = 1f;
        }

        private class HuntSettings
        {
            [JsonProperty("Clues (monuments to visit before the statue)")] public int Clues = 4;
            [JsonProperty("Time limit (minutes)")] public float Minutes = 60f;
            [JsonProperty("Start at (monument name)")] public string StartAt = "Airfield";
            [JsonProperty("Start area radius (meters)")] public float StartRadius = 90f;
            [JsonProperty("How close you must get to a clue (meters)")] public float FindRadius = 12f;
            [JsonProperty("Detector shows the distance within (meters)")] public float ShowDistance = 60f;
            [JsonProperty("Mark the clue's monument on the compass and map")] public bool CompassMarker = true;
            [JsonProperty("Grid hint after this many minutes on a clue (0 = never)")] public float HintMinutes = 5f;
            [JsonProperty("Prize rolls")] public int PrizeRolls = 3;
            [JsonProperty("Trophy item (shortname)")] public string TrophyItem = "trophy";
            [JsonProperty("Trophy name")] public string TrophyName = "Golden Statue";
            [JsonProperty("Detector item (shortname)")] public string DetectorItem = "compass";
            [JsonProperty("Detector name")] public string DetectorName = "Treasure Compass";
            [JsonProperty("Prize items", ObjectCreationHandling = ObjectCreationHandling.Replace)] public List<PrizeItem> Prizes = DefaultPrizes();
        }

        private class HeistSettings
        {
            [JsonProperty("Guards (scientists on the train)")] public int Guards = 6;
            [JsonProperty("Of those, heavy scientists")] public int HeavyGuards = 2;
            [JsonProperty("Wagons behind the locomotive")] public int Wagons = 2;
            [JsonProperty("Time limit (minutes)")] public float Minutes = 30f;
            [JsonProperty("Crate hack time (minutes)")] public float HackMinutes = 5f;
            [JsonProperty("Train speed (1 = slow, 2 = medium, 3 = fast)")] public int Speed = 1;
            [JsonProperty("Locomotive prefab")] public string LocomotivePrefab = "assets/content/vehicles/trains/locomotive/locomotive.entity.prefab";
            [JsonProperty("Wagon prefab")] public string WagonPrefab = "assets/content/vehicles/trains/wagons/trainwagona.entity.prefab";
            [JsonProperty("Guard seat prefab")] public string SeatPrefab = "assets/bundled/prefabs/static/chair.invisible.static.prefab";
            [JsonProperty("Crate prefab")] public string CratePrefab = "assets/prefabs/deployable/chinooklockedcrate/codelockedhackablecrate.prefab";
            [JsonProperty("Scientist prefab")] public string ScientistPrefab = "assets/rust.ai/agents/npcplayer/humannpc/scientist/scientistnpc_roam.prefab";
            [JsonProperty("Heavy scientist prefab")] public string HeavyPrefab = "assets/rust.ai/agents/npcplayer/humannpc/scientist/scientistnpc_heavy.prefab";
        }

        private class PluginConfig
        {
            [JsonProperty("Treasure hunt")] public HuntSettings Hunt = new HuntSettings();
            [JsonProperty("Train heist")] public HeistSettings Heist = new HeistSettings();
            [JsonProperty("Chat prefix")] public string ChatPrefix = "<color=#ffd479>[Treasure Hunt]</color>";
            [JsonProperty("Chat icon (SteamID, 0 = server)")] public ulong ChatIcon = 0;
        }

        private static PrizeItem P(string item, int min, int max = 0, float weight = 1f) =>
            new PrizeItem { Item = item, Min = min, Max = max <= 0 ? min : max, Weight = weight };

        private static List<PrizeItem> DefaultPrizes() => new List<PrizeItem>
        {
            P("rifle.ak", 1), P("rifle.lr300", 1), P("rifle.l96", 1, 1, 0.6f), P("smg.mp5", 1), P("lmg.m249", 1, 1, 0.4f),
            P("rocket.launcher", 1, 1, 0.6f), P("ammo.rocket.basic", 2, 4, 0.6f), P("explosive.timed", 1, 2, 0.5f), P("explosive.satchel", 2, 4),
            P("heavy.plate.helmet", 1), P("keycard_red", 1), P("supply.signal", 1), P("scrap", 1000, 1500),
        };

        protected override void LoadDefaultConfig() => _config = new PluginConfig();

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                _config = Config.ReadObject<PluginConfig>();
                if (_config == null) throw new Exception("empty");
                if (_config.Hunt == null) _config.Hunt = new HuntSettings();
                if (_config.Hunt.Prizes == null) _config.Hunt.Prizes = DefaultPrizes();
                var before = _config.Hunt.Prizes.Count;
                _config.Hunt.Prizes = Dedupe(_config.Hunt.Prizes, JsonConvert.SerializeObject, DefaultPrizes());
                if (_config.Hunt.Prizes.Count < before) Puts($"Removed {before - _config.Hunt.Prizes.Count} duplicate treasure hunt prizes from the config.");
                if (_config.Heist == null) _config.Heist = new HeistSettings();
                SaveConfig();
            }
            catch (Exception ex)
            {
                PrintError($"Couldn't read the config file ({ex.Message}); using defaults and leaving the file untouched.");
                _config = new PluginConfig();
            }
        }

        protected override void SaveConfig() => Config.WriteObject(_config, true);

        /// <summary>
        /// Older versions appended the built-in defaults to this list every time the config loaded, so it could hold
        /// several copies. Keeps one per key, preferring a copy that was edited (differs from the default).
        /// </summary>
        private static List<T> Dedupe<T>(List<T> list, Func<T, string> key, List<T> defaults)
        {
            var original = new Dictionary<string, string>();
            foreach (var d in defaults)
            {
                var k = key(d) ?? "";
                if (!original.ContainsKey(k)) original[k] = JsonConvert.SerializeObject(d);
            }
            var result = new List<T>();
            var index = new Dictionary<string, int>();
            foreach (var item in list)
            {
                var k = key(item) ?? "";
                int at;
                if (!index.TryGetValue(k, out at))
                {
                    index[k] = result.Count;
                    result.Add(item);
                    continue;
                }
                string d;
                if (original.TryGetValue(k, out d) && JsonConvert.SerializeObject(result[at]) == d) result[at] = item;
            }
            return result;
        }

        #endregion

        #region Riddles

        // Keyed by a word in the monument's name. Several riddles each, picked at random.
        private static readonly Dictionary<string, string[]> Riddles = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["Launch Site"] = new[] { "Where rockets once rose and the gantry stands tall, the next clue waits near the tower's cold wall.", "A countdown that never reached zero. Look where the giant never flew." },
            ["Military Tunnel"] = new[] { "Soldiers dug deep where the hillside is bored; follow the tunnels the scientists ward.", "Barbed wire and bunkers, a road through the stone. The next clue hides where the scientists roam." },
            ["Power Plant"] = new[] { "Cooling towers breathe where the turbines once spun. Find where the current and the clue become one.", "Smokestacks stand guard over silent machines. The next clue is humming where power was seen." },
            ["Water Treatment"] = new[] { "Round pools of grey where the dirty becomes clean; search by the tanks where the filters have been.", "What the city drank was cleaned in these bowls. The next clue floats near the settling holes." },
            ["Train Yard"] = new[] { "Carriages rest where the tracks start to braid. The next clue waits in the shunting yard's shade.", "A crane over rails and a ladder to climb. The engines are idle; the clue's on the line." },
            ["Satellite Dish"] = new[] { "Great ears turned skyward still listen for home. The next clue is caught in their dish made of chrome.", "They spoke to the stars, now they whisper to rust. Search where the big dishes gather their dust." },
            ["Dome"] = new[] { "A sphere on stilts where the brave climb up high. The next clue waits under the ball in the sky.", "Climb if you dare to the globe in the air; the next clue sits under the round iron lair." },
            ["Sewer Branch"] = new[] { "Down where the city once flushed what it made, the next clue lies hidden in pipes and in shade.", "Follow your nose to the pipes and the grate. The next clue is waiting where the waters were late." },
            ["Junkyard"] = new[] { "Crushed cars in towers and a magnet that swings. The next clue's buried in broken old things.", "Where wrecks go to die in a rusting array, the next clue is hiding in scrap and decay." },
            ["Harbor"] = new[] { "Containers stacked high where the big cranes still lean. The next clue waits by the docks, out of sight, unseen.", "Ships brought the cargo, the cranes set it down. Search by the water at the edge of the town." },
            ["Lighthouse"] = new[] { "A tower that once kept the sailors from harm. Climb to the light by the sea's rocky arm.", "Its beam has gone dark but it still marks the shore. The next clue waits by the lighthouse door." },
            ["Gas Station"] = new[] { "Pumps that ran dry and a shop with no clerk. The next clue waits where the cars used to park.", "Fill up your tank? Not here, not today. The next clue's hidden where drivers would stay." },
            ["Supermarket"] = new[] { "Aisles stripped bare and a trolley left out. The next clue's on special; go look about.", "Nothing's for sale but the clue's in stock. Search where the shoppers once used to flock." },
            ["Mining Outpost"] = new[] { "A shed and a drill where the miners once dug. The next clue is waiting near the old water jug.", "Small camp of the miners, a tower of wood. The next clue is hidden where the workers once stood." },
            ["Excavator"] = new[] { "A metal colossus that eats up the ground. The next clue's in the pit where its great arm swings round.", "The biggest machine on the island stands here. Search the deep pit where its buckets appear." },
            ["Arctic Research"] = new[] { "Where scientists shivered and studied the cold, the next clue is frozen, so search and be bold.", "Snow on the labs and ice on the rails. The next clue is waiting where the cold wind wails." },
            ["Missile Silo"] = new[] { "A launch that was hidden far under the snow. The next clue's above where the missiles would go.", "Bunkers and doors in the frost and the stone. The next clue waits where the warheads were known." },
            ["Ferry Terminal"] = new[] { "Ferries once docked here to carry folk out. The next clue waits by the terminal's spout.", "No boats are leaving; the ticket booth's bare. The next clue is waiting for you over there." },
            ["Military Base"] = new[] { "Fences and barracks the army forgot. The next clue is hiding inside the old lot.", "An abandoned base with its watchtowers high. The next clue waits under its watchful eye." },
            ["Fishing Village"] = new[] { "Nets on the jetty and huts on the sand. The next clue is caught where the fishermen land.", "Boats on the water and planks on the pier. The next clue's been hooked and it's hiding near here." },
            ["Ranch"] = new[] { "Hay bales and horses and fences of wood. The next clue's in the barn where the cattle once stood.", "A farm with a stable where riders would rest. The next clue is waiting in the horses' nest." },
            ["Barn"] = new[] { "A big red barn where the hay's piled up high. The next clue's in the loft, so give it a try.", "Where farmers kept feed and the horses would stay, the next clue is hidden in straw and in hay." },
            ["Ziggurat"] = new[] { "Stone steps in the jungle that climb to the sun. The next clue's at the temple, so go and have fun.", "Vines on old blocks where the ancients once prayed. The next clue's in the jungle, under the shade." },
            ["Quarry"] = new[] { "A great hole was dug for the stone and the ore. The next clue waits where the diggers dug more.", "Machines that once crushed all the rock into fine. The next clue is waiting at the quarry's mine." },
            ["Cabin"] = new[] { "Little old cabins where nobody stays. The next clue waits by the porch in the haze.", "Wooden huts empty and doors on the swing. The next clue's in the cabins, so search everything." },
            ["Oasis"] = new[] { "Water and palms where the desert is dry. The next clue's at the oasis, under the sky.", "In sand and in heat there's a pool and some shade. The next clue is waiting where the travellers stayed." },
        };

        #endregion

        #region Lang

        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["Started"] = "A treasure hunt has begun! Head to <color=#ffd479>{0}</color> ({1}) to pick up your Treasure Compass. First to find the <color=#ffd479>{2}</color> wins epic loot! You have {3} minutes.",
                ["Joined"] = "You picked up a <color=#ffd479>Treasure Compass</color>. Hold it in your hand: it beeps faster the closer you get.",
                ["Clue"] = "<color=#ffd479>Clue {0}/{1}:</color> {2}",
                ["Found"] = "<color=#7bd88f>Clue {0} found!</color> Your compass recalibrated.",
                ["FinalClue"] = "<color=#ffd479>Last clue!</color> The {0} is close: an <color=#ffd479>elite crate</color> under purple smoke. Your compass is tuned to it.",
                ["Hint"] = "<color=#9aa0a6>Hint:</color> look around grid <color=#ffd479>{0}</color>.",
                ["StatueHint"] = "<color=#9aa0a6>Hint:</color> the {0} is at <color=#ffd479>{1}</color>.",
                ["Winner"] = "<color=#ffd479>{0}</color> found the {1} at {2}! The treasure hunt is over.",
                ["TimeUp"] = "Nobody found the {0} in time. The treasure hunt is over.",
                ["Stopped"] = "The treasure hunt was stopped by an admin.",
                ["Reminder"] = "The treasure hunt is on! Pick up a Treasure Compass at <color=#ffd479>{0}</color> ({1}). {2} minutes left.",
                ["NoHunt"] = "There's no treasure hunt right now. Watch chat for the next one!",
                ["Status"] = "You're on clue {0}/{1}. {2} minutes left.",
                ["NotJoined"] = "Go to <color=#ffd479>{0}</color> ({1}) to pick up your Treasure Compass.",
                ["Done"] = "You've reached the end. Find the {0}!",
                ["Meter"] = "TREASURE COMPASS",
                ["HeistStarted"] = "<color=#ff6b5b>Train heist!</color> A loot train guarded by {0} scientists is running the tracks near <color=#ffd479>{1}</color>. Board it and hack the locked crate on the last wagon. It leaves in {2} minutes.",
                ["HeistWhere"] = "The loot train is near <color=#ffd479>{0}</color>. {1} guard(s) left, {2} minutes until it leaves.",
                ["HeistHacking"] = "<color=#ffd479>{0}</color> is hacking the train crate! It unlocks in {1}.",
                ["HeistWon"] = "<color=#ffd479>{0}</color> cracked the loot train crate! The train heist is over.",
                ["HeistGone"] = "The loot train left with its cargo. Nobody cracked the crate this time.",
                ["HeistStopped"] = "The train heist was stopped by an admin.",
                ["HeistNone"] = "There's no train heist right now.",
                ["HeistDriverDown"] = "The loot train's driver is down! The train is rolling to a stop near <color=#ffd479>{0}</color>.",
                ["MeterClue"] = "Clue {0}/{1}",
                ["MeterStatue"] = "Statue",
            }, this);
        }

        private string Msg(string key, string userId, params object[] args)
        {
            var text = lang.GetMessage(key, this, userId);
            return args.Length == 0 ? text : string.Format(text, args);
        }

        private void Say(BasePlayer player, string text) => Player.Message(player, text, _config.ChatPrefix, _config.ChatIcon);
        private void Broadcast(string text) => Server.Broadcast(text, _config.ChatPrefix, _config.ChatIcon);

        #endregion

        #region Hunt state

        private class Stage
        {
            public string Monument;
            public Vector3 Center;
            public Vector3 Spot;
            public string Riddle;
        }

        private class Hunter
        {
            public int Stage;          // index of the stage the player is looking for
            public float StageStart;   // when they got this clue
            public bool Hinted;
            public float NextBeep;
            public string MeterKey;
        }

        private class Hunt
        {
            public Vector3 StartPos;
            public string StartName;
            public List<Stage> Stages = new List<Stage>();
            public float EndsAt;
            public LootContainer Statue;
            public Dictionary<ulong, Hunter> Hunters = new Dictionary<ulong, Hunter>();
            public float NextReminder;
            public float NextSmoke;
        }

        private Hunt _hunt;
        private Timer _tick;

        private static string Grid(Vector3 pos)
        {
            try { return MapHelper.PositionToString(pos); }
            catch { return $"{pos.x:0}, {pos.z:0}"; }
        }

        private static string NameOf(MonumentInfo m)
        {
            var phrase = m.displayPhrase != null ? m.displayPhrase.english : null;
            return !string.IsNullOrEmpty(phrase) ? phrase : m.name;
        }

        private static string RiddleFor(string name)
        {
            foreach (var kv in Riddles)
                if (name.IndexOf(kv.Key, StringComparison.OrdinalIgnoreCase) >= 0)
                    return kv.Value[UnityEngine.Random.Range(0, kv.Value.Length)];
            return null;
        }

        private const int SurfaceMask = Rust.Layers.Mask.Terrain | Rust.Layers.Mask.World | Rust.Layers.Mask.Default | Rust.Layers.Mask.Construction | Rust.Layers.Mask.Tree;
        private const int BlockerMask = Rust.Layers.Mask.World | Rust.Layers.Mask.Default | Rust.Layers.Mask.Construction | Rust.Layers.Mask.Deployed | Rust.Layers.Mask.Tree;

        /// <summary>
        /// A random spot inside a monument that a player can actually stand on: open ground (the first thing
        /// a ray from the sky hits is terrain, so not under a roof or a machine), with room around it, and dry.
        /// </summary>
        private static Vector3 SpotIn(MonumentInfo m)
        {
            var center = m.transform.TransformPoint(m.Bounds.center);
            var ext = m.Bounds.extents;
            var radius = Mathf.Clamp(Mathf.Min(ext.x, ext.z) * 0.6f, 8f, 40f);
            var fallback = center;
            fallback.y = TerrainMeta.HeightMap != null ? TerrainMeta.HeightMap.GetHeight(center) : center.y;
            for (var attempt = 0; attempt < 100; attempt++)
            {
                // Inside the monument first; small ones (a lighthouse on a cliff) often have no open ground,
                // so then try a ring around it rather than ending up inside a building.
                Vector2 offset;
                if (attempt < 40) offset = UnityEngine.Random.insideUnitCircle * radius;
                else
                {
                    var dir = UnityEngine.Random.insideUnitCircle.normalized;
                    offset = dir * UnityEngine.Random.Range(radius, radius + 10f + (attempt - 40) * 0.8f);
                }
                var top = new Vector3(center.x + offset.x, center.y + 200f, center.z + offset.y);
                RaycastHit hit;
                if (!Physics.Raycast(top, Vector3.down, out hit, 400f, SurfaceMask, QueryTriggerInteraction.Ignore)) continue;
                if (((1 << hit.collider.gameObject.layer) & Rust.Layers.Mask.Terrain) == 0) continue; // a roof, rock or machine
                if (hit.normal.y < 0.7f) continue;                                                     // too steep to stand on
                var p = hit.point;
                if (Physics.CheckSphere(p + Vector3.up * 1.3f, 1.1f, BlockerMask, QueryTriggerInteraction.Ignore)) continue;
                if (WaterLevel.Test(p + Vector3.up * 0.5f, false, false)) continue;
                return p;
            }
            return fallback;
        }

        #endregion

        #region Start / stop

        private string StartHunt(int clues, float minutes)
        {
            if (_hunt != null) return "A treasure hunt is already running.";
            var monuments = TerrainMeta.Path != null ? TerrainMeta.Path.Monuments : null;
            if (monuments == null || monuments.Count == 0) return "This map has no monuments.";

            var cfg = _config.Hunt;
            clues = Mathf.Clamp(clues > 0 ? clues : cfg.Clues, 1, 10);
            minutes = Mathf.Clamp(minutes > 0 ? minutes : cfg.Minutes, 5f, 600f);

            var usable = monuments.Where(m => m != null && !m.IsSafeZone && RiddleFor(NameOf(m)) != null).ToList();
            var start = monuments.FirstOrDefault(m => m != null && NameOf(m).IndexOf(cfg.StartAt ?? "", StringComparison.OrdinalIgnoreCase) >= 0)
                        ?? usable.OrderByDescending(m => m.Bounds.size.sqrMagnitude).FirstOrDefault();
            if (start == null) return "Couldn't find a monument to start at.";
            var route = usable.Where(m => m != start).OrderBy(_ => UnityEngine.Random.value).Take(clues).ToList();
            if (route.Count == 0) return "Not enough monuments with riddles on this map.";

            _hunt = new Hunt
            {
                StartPos = start.transform.TransformPoint(start.Bounds.center),
                StartName = NameOf(start),
                EndsAt = Time.realtimeSinceStartup + minutes * 60f,
            };
            foreach (var m in route)
                _hunt.Stages.Add(new Stage { Monument = NameOf(m), Center = m.transform.TransformPoint(m.Bounds.center), Spot = SpotIn(m), Riddle = RiddleFor(NameOf(m)) });
            _hunt.NextReminder = Time.realtimeSinceStartup + 300f;

            Broadcast(Msg("Started", null, _hunt.StartName, Grid(_hunt.StartPos), cfg.TrophyName, Mathf.RoundToInt(minutes)));
            Puts($"Treasure hunt started at {_hunt.StartName}: " + string.Join(" -> ", _hunt.Stages.Select(s => $"{s.Monument} ({Grid(s.Spot)})")));
            _tick?.Destroy();
            _tick = timer.Every(0.2f, Tick);
            return $"Treasure hunt started at {_hunt.StartName} with {_hunt.Stages.Count} clues and {Mathf.RoundToInt(minutes)} minutes.";
        }

        private void EndHunt(string message)
        {
            if (_hunt == null) return;
            _tick?.Destroy();
            _tick = null;
            if (!string.IsNullOrEmpty(message)) Broadcast(message);
            foreach (var id in _hunt.Hunters.Keys.ToList())
            {
                var player = BasePlayer.FindByID(id) ?? BasePlayer.FindSleeping(id);
                if (player == null) continue;
                RemoveDetectors(player);
                if (player.IsConnected) CuiHelper.DestroyUi(player, MeterUi);
            }
            if (_hunt.Statue != null && !_hunt.Statue.IsDestroyed) _hunt.Statue.Kill();
            var hunters = _hunt.Hunters.Keys.ToList();
            _hunt = null;
            // Take the compass marker away again.
            foreach (var id in hunters)
            {
                var player = BasePlayer.FindByID(id);
                if (player != null && player.IsConnected) player.SendMarkersToClient();
            }
        }

        private void Unload()
        {
            EndHunt(null);
            EndHeist(null);
        }

        #endregion

        #region Tick: joining, detector beeps, finding clues

        private void Tick()
        {
            if (_hunt == null) return;
            var now = Time.realtimeSinceStartup;
            var cfg = _config.Hunt;
            if (now >= _hunt.EndsAt)
            {
                EndHunt(Msg("TimeUp", null, cfg.TrophyName));
                return;
            }
            if (_hunt.Statue != null && !_hunt.Statue.IsDestroyed && now >= _hunt.NextSmoke)
            {
                // One supply-signal plume lasts minutes and can't be cancelled, so refresh slowly: the column stays
                // up during the hunt and only a little is left to fade after someone wins.
                _hunt.NextSmoke = now + 60f;
                Effect.server.Run("assets/bundled/prefabs/fx/smoke_signal_full.prefab", _hunt.Statue.transform.position + Vector3.up * 0.5f);
            }
            if (now >= _hunt.NextReminder)
            {
                _hunt.NextReminder = now + 300f;
                Broadcast(Msg("Reminder", null, _hunt.StartName, Grid(_hunt.StartPos), Mathf.CeilToInt((_hunt.EndsAt - now) / 60f)));
            }

            foreach (var player in BasePlayer.activePlayerList)
            {
                if (player == null || player.IsDead() || player.IsSleeping() || !player.userID.IsSteamId()) continue;
                Hunter h;
                if (!_hunt.Hunters.TryGetValue(player.userID, out h))
                {
                    var flat = player.transform.position - _hunt.StartPos;
                    flat.y = 0f;
                    if (flat.magnitude <= cfg.StartRadius) Join(player);
                    continue;
                }
                var target = Target(h);
                if (target == null) continue;

                var to = target.Value - player.transform.position;
                var dist = new Vector2(to.x, to.z).magnitude;
                if (h.Stage < _hunt.Stages.Count && dist <= cfg.FindRadius && Mathf.Abs(to.y) < 20f)
                {
                    Advance(player, h, now);
                    continue;
                }
                if (cfg.HintMinutes > 0 && !h.Hinted && now - h.StageStart > cfg.HintMinutes * 60f)
                {
                    h.Hinted = true;
                    Say(player, h.Stage < _hunt.Stages.Count ? Msg("Hint", player.UserIDString, Grid(target.Value)) : Msg("StatueHint", player.UserIDString, cfg.TrophyName, Grid(target.Value)));
                }

                var holding = IsDetector(player.GetActiveItem());
                if (!holding)
                {
                    if (h.MeterKey != null) { h.MeterKey = null; CuiHelper.DestroyUi(player, MeterUi); }
                    continue;
                }
                // Signal: 5 bars, and a beep every 3 s far away down to 4 a second right on top of it.
                var strength = 1f - Mathf.Clamp01(dist / 500f);
                var bars = Mathf.Clamp(Mathf.CeilToInt(strength * 5f), 0, 5);
                var heading = Heading(player, target.Value);
                var shown = dist <= cfg.ShowDistance ? Mathf.RoundToInt(dist) : -1;
                var key = bars + "|" + heading + "|" + shown;
                if (key != h.MeterKey) { h.MeterKey = key; DrawMeter(player, h, bars, heading, shown); }
                if (now >= h.NextBeep)
                {
                    h.NextBeep = now + Mathf.Lerp(0.25f, 3f, Mathf.Clamp01(dist / 400f));
                    PlaySound(player, "assets/prefabs/tools/detonator/effects/unpress.prefab");
                }
            }
        }

        /// <summary>Where the player's detector points: their current clue, or the statue once all clues are found.</summary>
        private Vector3? Target(Hunter h)
        {
            if (h.Stage < _hunt.Stages.Count) return _hunt.Stages[h.Stage].Spot;
            if (_hunt.Statue != null && !_hunt.Statue.IsDestroyed) return _hunt.Statue.transform.position;
            return _hunt.Stages[_hunt.Stages.Count - 1].Spot;
        }

        private void Join(BasePlayer player)
        {
            var h = new Hunter { Stage = 0, StageStart = Time.realtimeSinceStartup };
            _hunt.Hunters[player.userID] = h;
            GiveDetector(player);
            RefreshMarker(player);
            Say(player, Msg("Joined", player.UserIDString));
            Say(player, Msg("Clue", player.UserIDString, 1, _hunt.Stages.Count, _hunt.Stages[0].Riddle));
            PlaySound(player, "assets/prefabs/locks/keypad/effects/lock.code.updated.prefab");
        }

        private void Advance(BasePlayer player, Hunter h, float now)
        {
            var cfg = _config.Hunt;
            h.Stage++;
            h.StageStart = now;
            h.Hinted = false;
            h.MeterKey = null;
            PlaySound(player, "assets/prefabs/locks/keypad/effects/lock.code.updated.prefab");
            Say(player, Msg("Found", player.UserIDString, h.Stage));
            RefreshMarker(player);
            if (h.Stage < _hunt.Stages.Count)
            {
                Say(player, Msg("Clue", player.UserIDString, h.Stage + 1, _hunt.Stages.Count, _hunt.Stages[h.Stage].Riddle));
                return;
            }
            // Last clue reached: the statue appears at that spot (once), and the detector points at it.
            if (_hunt.Statue == null) SpawnStatue(_hunt.Stages[_hunt.Stages.Count - 1].Spot);
            Say(player, Msg("FinalClue", player.UserIDString, cfg.TrophyName));
        }

        #endregion

        #region Compass marker

        // The hunt's marker is added to the list the game sends to the player, so it shows on their compass
        // and map without touching their own saved markers. It sits on the monument, not the hidden spot:
        // the compass gets you there, the detector finds the exact place.
        private void OnPlayerMarkersSend(BasePlayer player, ProtoBuf.MapNoteList list)
        {
            if (_hunt == null || player == null || list?.notes == null || !_config.Hunt.CompassMarker) return;
            Hunter h;
            if (!_hunt.Hunters.TryGetValue(player.userID, out h)) return;
            var last = _hunt.Stages.Count - 1;
            var stage = _hunt.Stages[Math.Min(h.Stage, last)];
            list.notes.Add(new ProtoBuf.MapNote
            {
                noteType = (int)BasePlayer.MapNoteType.PointOfInterest,
                worldPosition = stage.Center,
                label = h.Stage <= last ? "Clue " + (h.Stage + 1) : "Statue",
                colourIndex = 2,
                icon = 0,
            });
        }

        private void RefreshMarker(BasePlayer player)
        {
            if (player != null && player.IsConnected && _config.Hunt.CompassMarker) player.SendMarkersToClient();
        }

        #endregion

        #region Detector, statue and prize

        private bool IsDetector(Item item) =>
            item != null && item.info != null && item.info.shortname == _config.Hunt.DetectorItem && item.name == _config.Hunt.DetectorName;

        private static List<Item> Items(BasePlayer player)
        {
            var list = new List<Item>();
            player.inventory.GetAllItems(list);
            return list;
        }

        private void GiveDetector(BasePlayer player)
        {
            if (Items(player).Any(IsDetector)) return;
            var item = ItemManager.CreateByName(_config.Hunt.DetectorItem, 1);
            if (item == null) return;
            item.name = _config.Hunt.DetectorName;
            if (!item.MoveToContainer(player.inventory.containerBelt) && !item.MoveToContainer(player.inventory.containerMain))
                item.Drop(player.eyes.position, player.eyes.BodyForward() * 2f);
        }

        private void RemoveDetectors(BasePlayer player)
        {
            foreach (var item in Items(player).Where(IsDetector).ToList())
            {
                item.RemoveFromContainer();
                item.Remove();
            }
        }

        private void SpawnStatue(Vector3 pos)
        {
            var entity = GameManager.server.CreateEntity("assets/bundled/prefabs/radtown/crate_elite.prefab", pos + Vector3.up * 0.2f, Quaternion.Euler(0f, UnityEngine.Random.Range(0f, 360f), 0f));
            var crate = entity as LootContainer;
            if (crate == null) { entity?.Kill(); return; }
            // Starts empty and never refills: it only ever holds the prize.
            crate.initialLootSpawn = false;
            crate.minSecondsBetweenRefresh = 0f;
            crate.maxSecondsBetweenRefresh = 0f;
            crate.Spawn();
            crate.inventory.Clear();
            ItemManager.DoRemoves();
            FillPrize(crate);
            _hunt.Statue = crate;
        }

        private void FillPrize(LootContainer crate)
        {
            var cfg = _config.Hunt;
            var trophy = ItemManager.CreateByName(cfg.TrophyItem, 1);
            if (trophy != null)
            {
                trophy.name = cfg.TrophyName;
                trophy.MoveToContainer(crate.inventory);
            }
            var pool = cfg.Prizes.Where(p => p.Weight > 0 && ItemManager.FindItemDefinition(p.Item) != null).ToList();
            for (var i = 0; i < cfg.PrizeRolls && pool.Count > 0; i++)
            {
                var total = pool.Sum(p => p.Weight);
                var r = UnityEngine.Random.value * total;
                var pick = pool[pool.Count - 1];
                foreach (var p in pool)
                {
                    r -= p.Weight;
                    if (r <= 0) { pick = p; break; }
                }
                var item = ItemManager.CreateByName(pick.Item, UnityEngine.Random.Range(Math.Max(1, pick.Min), Math.Max(pick.Min, pick.Max) + 1));
                if (item != null && !item.MoveToContainer(crate.inventory)) item.Remove();
            }
        }

        private void OnHuntLoot(BasePlayer player, BaseEntity entity)
        {
            if (_hunt == null || player == null || entity == null || _hunt.Statue == null || entity != _hunt.Statue) return;
            var where = _hunt.Stages[_hunt.Stages.Count - 1].Monument;
            PlaySound(player, "assets/prefabs/misc/xmas/presents/effects/unwrap.prefab");
            Puts($"{player.displayName} won the treasure hunt.");
            // The crate stays for the winner to empty; it's no longer the event's.
            _hunt.Statue = null;
            EndHunt(Msg("Winner", null, player.displayName, _config.Hunt.TrophyName, where));
        }

        private static void PlaySound(BasePlayer player, string prefab)
        {
            if (player?.net?.connection == null) return;
            EffectNetwork.Send(new Effect(prefab, player, 0, Vector3.zero, Vector3.forward), player.net.connection);
        }

        #endregion

        #region Signal meter

        private const string MeterUi = "SandboxEvents.Meter";

        /// <summary>Which way the target is from where the player is looking, in eight steps.</summary>
        private static string Heading(BasePlayer player, Vector3 target)
        {
            var forward = player.eyes.HeadForward();
            forward.y = 0f;
            var to = target - player.transform.position;
            var dy = to.y;
            to.y = 0f;
            if (to.sqrMagnitude < 9f)
            {
                if (dy > 3f) return "ABOVE " + Mathf.RoundToInt(dy) + " m";
                if (dy < -3f) return "BELOW " + Mathf.RoundToInt(-dy) + " m";
                return "HERE";
            }
            if (forward.sqrMagnitude < 0.001f) return "AHEAD";
            var angle = Vector3.SignedAngle(forward, to, Vector3.up);
            var a = Mathf.Abs(angle);
            var side = angle > 0 ? "RIGHT" : "LEFT";
            if (a <= 22.5f) return "AHEAD";
            if (a <= 67.5f) return "AHEAD-" + side;
            if (a <= 112.5f) return side;
            if (a <= 157.5f) return "BEHIND-" + side;
            return "BEHIND";
        }

        private void DrawMeter(BasePlayer player, Hunter h, int bars, string heading, int distance)
        {
            var id = player.UserIDString;
            var ui = new CuiElementContainer();
            ui.Add(new CuiPanel
            {
                Image = { Color = "0.08 0.08 0.08 0.75" },
                RectTransform = { AnchorMin = "0.5 0", AnchorMax = "0.5 0", OffsetMin = "-130 104", OffsetMax = "130 166" },
            }, "Hud", MeterUi);
            var vertical = heading.StartsWith("ABOVE") || heading.StartsWith("BELOW");
            var where = heading == "HERE" ? "<color=#7bd88f>RIGHT HERE</color>"
                : vertical ? "<color=#ffd479>" + heading + "</color>"
                : "<color=#ffffff>" + heading + "</color>" + (distance >= 0 ? "   <color=#ffd479>" + distance + " m</color>" : "");
            ui.Add(new CuiLabel
            {
                Text = { Text = where, FontSize = 12, Align = TextAnchor.MiddleCenter, Color = "1 1 1 0.95", Font = "robotocondensed-bold.ttf" },
                RectTransform = { AnchorMin = "0 0.32", AnchorMax = "1 0.66" },
            }, MeterUi);
            var stage = h.Stage < _hunt.Stages.Count ? Msg("MeterClue", id, h.Stage + 1, _hunt.Stages.Count) : Msg("MeterStatue", id);
            ui.Add(new CuiLabel
            {
                Text = { Text = Msg("Meter", id) + "   <color=#9aa0a6>" + stage + "</color>", FontSize = 10, Align = TextAnchor.MiddleCenter, Color = "1 0.85 0.4 1", Font = "robotocondensed-bold.ttf" },
                RectTransform = { AnchorMin = "0 0.66", AnchorMax = "1 1" },
            }, MeterUi);
            for (var i = 0; i < 5; i++)
            {
                var lit = i < bars;
                var color = lit ? (bars >= 5 ? "0.95 0.35 0.2 1" : bars >= 3 ? "1 0.8 0.25 1" : "0.45 0.85 0.35 1") : "0.3 0.3 0.3 0.9";
                var x0 = -60 + i * 25;
                ui.Add(new CuiPanel
                {
                    Image = { Color = color },
                    RectTransform = { AnchorMin = "0.5 0", AnchorMax = "0.5 0", OffsetMin = $"{x0} 6", OffsetMax = $"{x0 + 20} {10 + i * 2}" },
                }, MeterUi);
            }
            CuiHelper.DestroyUi(player, MeterUi);
            CuiHelper.AddUi(player, ui);
        }

        #endregion

        #region Train heist

        private class Heist
        {
            public TrainEngine Engine;
            public readonly List<TrainCar> Cars = new List<TrainCar>();
            public HackableLockedCrate Crate;
            public readonly List<ScientistNPC> Guards = new List<ScientistNPC>();
            public readonly Dictionary<ScientistNPC, KeyValuePair<TrainCar, Vector3>> GuardSpots = new Dictionary<ScientistNPC, KeyValuePair<TrainCar, Vector3>>();
            public readonly List<BaseEntity> Seats = new List<BaseEntity>();
            public Timer Ride;
            public float EndsAt;
            public float NextUpdate;
            public float NextThrottle;
            public bool HackAnnounced;
            public bool Won;
            public bool KeepRunningWas;
            public bool ChangedKeepRunning;
            public ScientistNPC Driver;
            public bool HadDriver;
            public bool DriverDownAnnounced;
            public Vector3 LastPos;
            public float LastMoved;
            public bool Reverse;
            public string Log;
        }

        private Heist _heist;
        private Timer _heistTick;

        private static TrainEngine.EngineSpeeds Throttle(int speed, bool reverse = false)
        {
            if (reverse) return speed >= 3 ? TrainEngine.EngineSpeeds.Rev_Hi : speed == 2 ? TrainEngine.EngineSpeeds.Rev_Med : TrainEngine.EngineSpeeds.Rev_Lo;
            return speed >= 3 ? TrainEngine.EngineSpeeds.Fwd_Hi : speed == 2 ? TrainEngine.EngineSpeeds.Fwd_Med : TrainEngine.EngineSpeeds.Fwd_Lo;
        }

        /// <summary>
        /// Finds a stretch of track the whole train fits on: every track is scanned every 10 m and a random
        /// straight, empty stretch is picked. Above-ground track is preferred; gentle bends and tunnels are
        /// only used when nothing better fits. On failure, <paramref name="why"/> says what was checked.
        /// </summary>
        private static bool FindTrainSpot(float needed, out TrainTrackSpline track, out float locoDist, out string why)
        {
            track = null;
            locoDist = 0f;
            var all = UnityEngine.Object.FindObjectsByType<TrainTrackSpline>(FindObjectsSortMode.None)
                .Where(t => t != null && !t.isStation && t.GetLength() > needed + 20f)
                .ToList();
            int straight = 0, blocked = 0;
            foreach (var aboveGround in new[] { true, false })
            foreach (var maxBend in new[] { 4f, 8f })
            {
                var spots = new List<KeyValuePair<TrainTrackSpline, float>>();
                foreach (var t in all)
                {
                    if (t.aboveGroundSpawn != aboveGround) continue;
                    for (var d = needed + 10f; d <= t.GetLength() - 10f; d += 10f)
                        if (TrackIsStraight(t, d - needed - 5f, d + 5f, maxBend)) spots.Add(new KeyValuePair<TrainTrackSpline, float>(t, d));
                }
                straight += spots.Count;
                // Checking for parked vehicles costs physics queries, so only a sample of spots is tried.
                for (var i = 0; i < 40 && spots.Count > 0; i++)
                {
                    var k = UnityEngine.Random.Range(0, spots.Count);
                    var spot = spots[k];
                    spots.RemoveAt(k);
                    if (TrackIsClear(spot.Key, spot.Value - needed - 5f, spot.Value + 10f)) { track = spot.Key; locoDist = spot.Value; why = null; return true; }
                    blocked++;
                }
            }
            why = all.Count == 0
                ? $"no track on this map is longer than {needed + 20f:0} m"
                : $"checked {all.Count} tracks: {straight} long-enough stretches, {blocked} of those tried had something on the track";
            return false;
        }

        /// <summary>Distance from a car's root to its front and rear couplings, read from the prefab.</summary>
        private static bool CouplingOffsets(string prefab, out float front, out float rear)
        {
            front = rear = 0f;
            var go = GameManager.server.FindPrefab(prefab);
            var car = go == null ? null : go.GetComponent<TrainCar>();
            if (car == null || car.frontCoupling == null || car.rearCoupling == null) return false;
            front = go.transform.InverseTransformPoint(car.frontCoupling.position).z;
            rear = -go.transform.InverseTransformPoint(car.rearCoupling.position).z;
            return true;
        }

        /// <summary>Cars placed on a bend overlap and get deleted by the game, so trains only spawn on straight track.</summary>
        private static bool TrackIsStraight(TrainTrackSpline track, float from, float to, float maxBend = 4f)
        {
            from = Mathf.Max(0f, from);
            to = Mathf.Min(track.GetLength() - 1f, to);
            var first = (track.GetPosition(from + 1f) - track.GetPosition(from)).normalized;
            for (var d = from + 5f; d <= to; d += 5f)
            {
                var dir = (track.GetPosition(d + 1f) - track.GetPosition(d)).normalized;
                if (Vector3.Angle(first, dir) > maxBend) return false;
            }
            return true;
        }

        /// <summary>No other vehicles (parked trains, workcarts) along this stretch of track.</summary>
        private static bool TrackIsClear(TrainTrackSpline track, float from, float to)
        {
            for (var d = Mathf.Max(0f, from); d <= Mathf.Min(track.GetLength(), to); d += 6f)
                if (Physics.CheckSphere(track.GetPosition(d) + Vector3.up * 2f, 5f, Rust.Layers.Mask.Vehicle_World, QueryTriggerInteraction.Ignore))
                    return false;
            return true;
        }

        private TrainCar SpawnCar(string prefab, TrainTrackSpline track, float dist, Vector3 forward)
        {
            Vector3 tangent;
            var pos = track.GetPositionAndTangent(dist, forward, out tangent);
            var entity = GameManager.server.CreateEntity(prefab, pos + Vector3.up * 0.1f, Quaternion.LookRotation(tangent));
            var car = entity as TrainCar;
            if (car == null) { entity?.Kill(); return null; }
            car.Spawn();
            return car;
        }

        private string StartHeist(int guards, float minutes, bool test = false, int attempt = 1)
        {
            if (_heist != null) return "A train heist is already running.";
            var cfg = _config.Heist;
            guards = Mathf.Clamp(guards >= 0 ? guards : cfg.Guards, 0, 30);
            minutes = Mathf.Clamp(minutes > 0 ? minutes : cfg.Minutes, 2f, 180f);

            float locoFront, locoRear, wagonFront, wagonRear;
            if (!CouplingOffsets(cfg.LocomotivePrefab, out locoFront, out locoRear)) return "Couldn't load the locomotive prefab: " + cfg.LocomotivePrefab;
            if (!CouplingOffsets(cfg.WagonPrefab, out wagonFront, out wagonRear)) return "Couldn't load the wagon prefab: " + cfg.WagonPrefab;
            var wagons = Mathf.Clamp(cfg.Wagons, 1, 5);
            const float gap = 2.5f; // cars that overlap when spawned are deleted by the game; couplings close the gap
            var needed = locoFront + locoRear + wagons * (wagonFront + wagonRear + gap);
            TrainTrackSpline track;
            float locoDist;
            string why;
            if (!FindTrainSpot(needed, out track, out locoDist, out why))
                return $"Couldn't find a clear stretch of track for the train ({why}).";

            // The locomotive sits near the far end of the track; wagons line up behind it.
            var forward = (track.GetPosition(locoDist + 1f) - track.GetPosition(locoDist)).normalized;
            var heist = new Heist { EndsAt = Time.realtimeSinceStartup + minutes * 60f, KeepRunningWas = ConVar.vehicle.trainskeeprunning };
            var engine = SpawnCar(cfg.LocomotivePrefab, track, locoDist, forward) as TrainEngine;
            if (engine == null) return "The locomotive prefab isn't a train engine.";
            heist.Engine = engine;
            heist.Cars.Add(engine);
            var dist = locoDist - locoRear;
            TrainCar previous = engine;
            for (var i = 0; i < wagons; i++)
            {
                dist -= wagonFront + gap;
                var wagon = SpawnCar(cfg.WagonPrefab, track, dist, forward);
                if (wagon == null) break;
                previous.coupling.rearCoupling.TryCouple(wagon.coupling.frontCoupling, true);
                heist.Cars.Add(wagon);
                previous = wagon;
                dist -= wagonRear;
            }

            // The prize: a hackable locked crate riding on the last wagon.
            var last = heist.Cars[heist.Cars.Count - 1];
            var crateEntity = GameManager.server.CreateEntity(cfg.CratePrefab, last.transform.TransformPoint(new Vector3(0f, DeckHeight(last), -2f)), last.transform.rotation);
            var crate = crateEntity as HackableLockedCrate;
            if (crate != null)
            {
                crate.Spawn();
                // It's the Chinook's crate, built to fall: with physics on it slides off a moving train.
                var body = crate.GetComponent<Rigidbody>();
                if (body != null) { body.isKinematic = true; body.useGravity = false; }
                // Loot crates delete themselves when "the ground goes missing"; on a moving wagon it always does.
                foreach (var c in crate.GetComponentsInChildren<GroundWatch>()) UnityEngine.Object.Destroy(c);
                foreach (var c in crate.GetComponentsInChildren<DestroyOnGroundMissing>()) UnityEngine.Object.Destroy(c);
                crate.SetParent(last, true, true);
                heist.Crate = crate;
            }
            else crateEntity?.Kill();

            // Guards sit on invisible chairs fixed to the wagon decks, so they ride smoothly and can't wander off.
            var heavies = Mathf.Clamp(cfg.HeavyGuards, 0, guards);
            for (var i = 0; i < guards; i++)
            {
                var car = heist.Cars[1 + i % Math.Max(1, heist.Cars.Count - 1)];
                var slot = i / Math.Max(1, heist.Cars.Count - 1);
                var side = (slot % 2 == 0 ? 1f : -1f) * 0.9f;
                var along = 3f - (slot / 2) * 2.5f;
                var local = new Vector3(side, DeckHeight(car), along);
                var npc = SpawnGuard(heist, car, local, side > 0, i < heavies);
                if (npc != null) heist.Guards.Add(npc);
            }

            // A scientist drives: a seated driver keeps the train running legitimately, and players can stop the
            // train by taking the driver out. Only if the seat can't be used do we fall back to the server setting.
            heist.Driver = SpawnDriver(engine);
            heist.HadDriver = heist.Driver != null;
            if (heist.Driver == null)
            {
                ConVar.vehicle.trainskeeprunning = true;
                heist.ChangedKeepRunning = true;
            }
            heist.LastPos = engine.transform.position;
            heist.LastMoved = Time.realtimeSinceStartup;
            // A locomotive needs diesel; its fuel tank is a child entity, ready a moment after spawning.
            NextTick(() =>
            {
                if (engine == null || engine.IsDestroyed) return;
                (engine.GetFuelSystem() as EntityFuelSystem)?.AddFuel(200);
                engine.engineController.FinishStartingEngine();
                engine.SetThrottle(Throttle(cfg.Speed));
            });
            _heist = heist;
            _heistTick?.Destroy();
            _heistTick = timer.Every(1f, HeistTick);
            heist.Ride = timer.Every(0.1f, () => RideGuards(heist));

            var where = Grid(engine.transform.position);
            Puts($"Train heist started at {where}: {heist.Cars.Count} cars, {heist.Guards.Count} guards, crate {(heist.Crate != null ? "on" : "MISSING")} (attempt {attempt}).");
            // The game deletes cars that spawn overlapping something a moment later; check before announcing.
            var wantGuards = guards;
            timer.Once(0.6f, () =>
            {
                if (_heist != heist) return;
                var broken = heist.Cars.Any(c => c == null || c.IsDestroyed) || heist.Crate == null || heist.Crate.IsDestroyed;
                if (broken)
                {
                    Puts($"Train heist at {where} lost a car while spawning; trying another stretch of track.");
                    EndHeist(null);
                    if (attempt < 3) StartHeist(wantGuards, minutes, test, attempt + 1);
                    else PrintWarning("Couldn't spawn a complete heist train after 3 tries.");
                    return;
                }
                if (!test) Broadcast(Msg("HeistStarted", null, heist.Guards.Count, Grid(heist.Engine.transform.position), Mathf.RoundToInt(minutes)));
            });
            return $"Train heist started near {where} with {heist.Guards.Count} guards and {Mathf.RoundToInt(minutes)} minutes.";
        }

        private void HeistTick()
        {
            var h = _heist;
            if (h == null) return;
            var now = Time.realtimeSinceStartup;
            if (h.Engine == null || h.Engine.IsDestroyed)
            {
                EndHeist(h.Won ? null : Msg("HeistGone", null));
                return;
            }
            if (!h.Won && now >= h.EndsAt)
            {
                EndHeist(Msg("HeistGone", null));
                return;
            }
            if (h.Won && now >= h.EndsAt)
            {
                EndHeist(null);
                return;
            }
            // A killed NPC's entity is removed, so "had a driver" is remembered separately.
            var driverAlive = h.HadDriver ? h.Driver != null && !h.Driver.IsDestroyed && !h.Driver.IsDead() && h.Driver.isMounted : h.ChangedKeepRunning;
            if (!driverAlive && h.HadDriver)
            {
                // Players took out the driver: the train rolls to a stop and stays there.
                if (!h.DriverDownAnnounced)
                {
                    h.DriverDownAnnounced = true;
                    h.Engine.SetThrottle(TrainEngine.EngineSpeeds.Zero);
                    if (!h.Won) Broadcast(Msg("HeistDriverDown", null, Grid(h.Engine.transform.position)));
                }
            }
            else if (!h.Won)
            {
                // A train that hasn't moved for 10 seconds hit the end of the line (or something on it): reverse.
                if ((h.Engine.transform.position - h.LastPos).sqrMagnitude > 1f)
                {
                    h.LastPos = h.Engine.transform.position;
                    h.LastMoved = now;
                }
                else if (now - h.LastMoved > 10f)
                {
                    h.Reverse = !h.Reverse;
                    h.LastMoved = now;
                    h.Engine.SetThrottle(Throttle(_config.Heist.Speed, h.Reverse));
                }
            }
            // Keep it moving while the driver lives: re-apply the throttle if the engine stalled or a player braked.
            if (driverAlive && !h.Won && now >= h.NextThrottle)
            {
                h.NextThrottle = now + 5f;
                var fuel = h.Engine.GetFuelSystem() as EntityFuelSystem;
                if (fuel != null && fuel.GetFuelAmount() < 20) fuel.AddFuel(100);
                if (h.Engine.engineController.IsOff) h.Engine.engineController.FinishStartingEngine();
                if (h.Engine.CurThrottleSetting == TrainEngine.EngineSpeeds.Zero) h.Engine.SetThrottle(Throttle(_config.Heist.Speed, h.Reverse));
            }
            h.Guards.RemoveAll(g => g == null || g.IsDestroyed || g.IsDead());
            if (h.Crate != null && h.Crate.IsBeingHacked() && !h.HackAnnounced)
            {
                h.HackAnnounced = true;
                var hacker = BasePlayer.FindByID(h.Crate.originalHackerPlayerId);
                Broadcast(Msg("HeistHacking", null, hacker != null ? hacker.displayName : "Someone", FormatMinutes(_config.Heist.HackMinutes)));
            }
            if (now >= h.NextUpdate)
            {
                h.NextUpdate = now + 300f;
                if (!h.Won) Broadcast(Msg("HeistWhere", null, Grid(h.Engine.transform.position), h.Guards.Count, Mathf.CeilToInt((h.EndsAt - now) / 60f)));
            }
        }

        /// <summary>Height of a wagon's floor above its root, found with a ray from above (falls back to wagon A's).</summary>
        private static float DeckHeight(TrainCar car)
        {
            RaycastHit hit;
            var top = car.transform.position + Vector3.up * 12f;
            if (Physics.Raycast(top, Vector3.down, out hit, 20f, ~0, QueryTriggerInteraction.Ignore) && hit.GetEntity() == car)
                return hit.point.y - car.transform.position.y + 0.05f;
            return 2.9f;
        }

        private ScientistNPC SpawnDriver(TrainEngine engine)
        {
            var seat = engine.mountPoints != null && engine.mountPoints.Count > 0 ? engine.mountPoints[0].mountable : null;
            if (seat == null) return null;
            var npc = GameManager.server.CreateEntity(_config.Heist.ScientistPrefab, seat.transform.position, seat.transform.rotation) as ScientistNPC;
            if (npc == null) return null;
            npc.Spawn();
            if (npc.Brain != null && npc.Brain.Navigator != null)
            {
                npc.Brain.Navigator.CanUseNavMesh = false;
                npc.Brain.Navigator.SetNavMeshEnabled(false);
            }
            seat.MountPlayer(npc);
            if (npc.isMounted) return npc;
            npc.Kill();
            return null;
        }

        private ScientistNPC SpawnGuard(Heist heist, TrainCar car, Vector3 local, bool facingRight, bool heavy)
        {
            var cfg = _config.Heist;
            var facing = car.transform.rotation * Quaternion.Euler(0f, facingRight ? 90f : -90f, 0f);
            var npcEntity = GameManager.server.CreateEntity(heavy ? cfg.HeavyPrefab : cfg.ScientistPrefab, car.transform.TransformPoint(local), facing);
            var npc = npcEntity as ScientistNPC;
            if (npc == null) { npcEntity?.Kill(); return null; }
            npc.Spawn();
            if (npc.Brain != null && npc.Brain.Navigator != null)
            {
                npc.Brain.Navigator.CanUseNavMesh = false;
                npc.Brain.Navigator.SetNavMeshEnabled(false);
            }
            var seat = GameManager.server.CreateEntity(cfg.SeatPrefab, car.transform.TransformPoint(local), facing) as BaseMountable;
            if (seat != null)
            {
                seat.Spawn();
                seat.SetParent(car, true, true);
                heist.Seats.Add(seat);
                seat.MountPlayer(npc);
                if (npc.isMounted) return npc;
            }
            // No seat: fall back to putting the guard back on its spot every tick.
            heist.GuardSpots[npc] = new KeyValuePair<TrainCar, Vector3>(car, local);
            return npc;
        }

        private static void RideGuards(Heist h)
        {
            foreach (var kv in h.GuardSpots)
            {
                var npc = kv.Key;
                var car = kv.Value.Key;
                if (npc == null || npc.IsDestroyed || npc.IsDead() || car == null || car.IsDestroyed) continue;
                var spot = car.transform.TransformPoint(kv.Value.Value);
                if ((npc.transform.position - spot).sqrMagnitude < 0.04f) continue;
                npc.ServerPosition = spot;
            }
        }

        private static string FormatMinutes(float minutes) =>
            minutes >= 1f ? $"{Mathf.RoundToInt(minutes)} min" : $"{Mathf.RoundToInt(minutes * 60f)} s";

        // The crate unlocks after the configured hack time instead of the global 15 minutes.
        private void OnCrateHack(HackableLockedCrate crate)
        {
            if (_heist == null || crate == null || crate != _heist.Crate) return;
            var wanted = Mathf.Clamp(_config.Heist.HackMinutes * 60f, 10f, HackableLockedCrate.requiredHackSeconds);
            NextTick(() =>
            {
                if (crate != null && !crate.IsDestroyed) crate.hackSeconds = HackableLockedCrate.requiredHackSeconds - wanted;
            });
        }

        private void OnLootEntity(BasePlayer player, BaseEntity entity)
        {
            OnHuntLoot(player, entity);
            OnHeistLoot(player, entity);
        }

        private void OnHeistLoot(BasePlayer player, BaseEntity entity)
        {
            var h = _heist;
            if (h == null || h.Won || player == null || entity == null || entity != h.Crate) return;
            h.Won = true;
            Broadcast(Msg("HeistWon", null, player.displayName));
            Puts($"{player.displayName} won the train heist.");
            // Give them two minutes to empty it, then the train and any guards go away.
            h.EndsAt = Time.realtimeSinceStartup + 120f;
        }

        private void EndHeist(string message)
        {
            var h = _heist;
            if (h == null) return;
            _heist = null;
            _heistTick?.Destroy();
            _heistTick = null;
            h.Ride?.Destroy();
            if (!string.IsNullOrEmpty(message)) Broadcast(message);
            foreach (var g in h.Guards) if (g != null && !g.IsDestroyed) g.Kill();
            foreach (var seat in h.Seats) if (seat != null && !seat.IsDestroyed) seat.Kill();
            if (h.Crate != null && !h.Crate.IsDestroyed) h.Crate.Kill();
            foreach (var car in h.Cars) if (car != null && !car.IsDestroyed) car.Kill();
            if (h.ChangedKeepRunning) ConVar.vehicle.trainskeeprunning = h.KeepRunningWas;
            if (h.Driver != null && !h.Driver.IsDestroyed) h.Driver.Kill();
        }

        // ---- self-test: put {"Run": true} in oxide/data/RustSandboxEvents_selftest.json and reload the plugin.

        private class SelfTest { public bool Run; public string Result = ""; }

        private void OnServerInitialized()
        {
            SelfTest test;
            try { test = Interface.Oxide.DataFileSystem.ReadObject<SelfTest>("RustSandboxEvents_selftest"); }
            catch { return; }
            if (test == null || !test.Run) return;
            test.Run = false;
            Interface.Oxide.DataFileSystem.WriteObject("RustSandboxEvents_selftest", test);
            timer.Once(3f, RunSelfTest);
        }

        private void RunSelfTest()
        {
            // Which wagon and seat prefabs exist, and how high the floor/roof of each wagon is.
            var candidates = new[]
            {
                "assets/content/vehicles/trains/wagons/trainwagona.entity.prefab", "assets/content/vehicles/trains/wagons/trainwagonb.entity.prefab",
                "assets/content/vehicles/trains/wagons/trainwagonc.entity.prefab", "assets/content/vehicles/trains/wagons/trainwagond.entity.prefab",
                "assets/content/vehicles/trains/wagons/trainwagonunloadable.entity.prefab", "assets/content/vehicles/trains/wagons/trainwagonunloadableloot.entity.prefab",
                "assets/content/vehicles/trains/wagons/trainwagonunloadablefuel.entity.prefab", "assets/content/vehicles/trains/caboose/traincaboose.entity.prefab",
                "assets/bundled/prefabs/static/chair.invisible.static.prefab", "assets/prefabs/vehicle/seats/passengerchair.prefab",
            };
            RunHeistSelfTest();
        }

        /// <summary>Spawns each wagon type in turn on a clear track and reports what a ray from above hits.</summary>
        private void ProbeWagons(List<string> prefabs, int index)
        {
            if (index >= prefabs.Count) { timer.Once(1f, RunHeistSelfTest); return; }
            TrainTrackSpline track;
            float d;
            string why;
            if (!FindTrainSpot(60f, out track, out d, out why)) { Puts("SELFTEST probe: no track, " + why); return; }
            var fwd = (track.GetPosition(d + 1f) - track.GetPosition(d)).normalized;
            var car = SpawnCar(prefabs[index], track, d, fwd);
            timer.Once(1.5f, () =>
            {
                if (car == null || car.IsDestroyed) { Puts($"SELFTEST probe {prefabs[index]}: car gone"); ProbeWagons(prefabs, index + 1); return; }
                var hits = new List<string>();
                foreach (var along in new[] { -4f, 0f, 4f })
                {
                    var top = car.transform.position + car.transform.forward * along + Vector3.up * 12f;
                    RaycastHit hit;
                    if (Physics.Raycast(top, Vector3.down, out hit, 20f, ~0, QueryTriggerInteraction.Ignore))
                        hits.Add($"{along:+0;-0;0}m:{hit.point.y - car.transform.position.y:0.00}({(hit.GetEntity() == car ? "car" : hit.collider.name)})");
                }
                Puts($"SELFTEST probe {prefabs[index].Substring(prefabs[index].LastIndexOf('/') + 1)}: surface above root " + string.Join(" ", hits));
                car.Kill();
                timer.Once(1.5f, () => ProbeWagons(prefabs, index + 1));
            });
        }

        private void RunHeistSelfTest()
        {
            var cfg = _config.Heist;
            var tracks = UnityEngine.Object.FindObjectsByType<TrainTrackSpline>(FindObjectsSortMode.None);
            Puts($"SELFTEST tracks={tracks.Length} aboveGround={tracks.Count(t => t.aboveGroundSpawn)} longest={(tracks.Length == 0 ? 0 : tracks.Max(t => t.GetLength())):0}m");
            foreach (var prefab in new[] { cfg.LocomotivePrefab, cfg.WagonPrefab, cfg.CratePrefab, cfg.ScientistPrefab, cfg.HeavyPrefab })
                Puts($"SELFTEST prefab {(GameManager.server.FindPrefab(prefab) != null ? "OK     " : "MISSING")} {prefab}");
            var result = StartHeist(4, 3f, test: true);
            Puts("SELFTEST start: " + result);
            timer.Once(2.5f, () => FollowSelfTest());
        }

        private void FollowSelfTest()
        {
            if (_heist == null) { Puts("SELFTEST no heist running"); return; }
            var h = _heist;
            var start = h.Engine.transform.position;
            timer.Once(1f, () => Puts("SELFTEST after 1s: " + string.Join(", ", h.Cars.Select((c, i) => $"car{i}={(c == null || c.IsDestroyed ? "GONE" : "ok")}")) + $" crate={(h.Crate == null || h.Crate.IsDestroyed ? "GONE" : "ok")} guards={h.Guards.Count(g => g != null && !g.IsDestroyed)}"));
            for (var i = 1; i <= 8; i++)
            {
                var n = i;
                timer.Once(n * 5f, () =>
                {
                    if (_heist != h || h.Engine == null || h.Engine.IsDestroyed) { Puts($"SELFTEST t={n * 5}s engine gone"); return; }
                    var guardInfo = string.Join(" ", h.Guards.Where(g => g != null && !g.IsDestroyed).Select(g => $"{(g.isMounted ? "seated" : "loose")}({Vector3.Distance(g.transform.position, h.Engine.transform.position):0}m{(g.IsDead() ? ",dead" : "")})"));
                    Puts($"SELFTEST t={n * 5}s moved={Vector3.Distance(start, h.Engine.transform.position):0}m speed={h.Engine.GetLocalVelocity().magnitude:0.0} throttle={h.Engine.CurThrottleSetting} engineOn={h.Engine.engineController.IsOn} cars={h.Cars.Count(c => c != null && !c.IsDestroyed)} coupled={h.Cars.Count(c => c != null && c.coupling.IsCoupled)} crate={(h.Crate != null && !h.Crate.IsDestroyed ? (h.Crate.GetParentEntity() != null ? $"onboard({Vector3.Distance(h.Crate.transform.position, h.Cars[h.Cars.Count - 1].transform.position):0.0}m)" : "loose") : "none")} guards=[{guardInfo}] driver={(!h.HadDriver ? "none(setting)" : h.Driver == null || h.Driver.IsDestroyed || h.Driver.IsDead() ? "down" : h.Driver.isMounted ? "seated" : "loose")} grid={Grid(h.Engine.transform.position)}");
                    if (n == 4 && h.Driver != null && !h.Driver.IsDestroyed) { Puts("SELFTEST killing the driver"); h.Driver.Die(); }
                    if (n == 8) EndHeist(null);
                });
            }
        }

        [ConsoleCommand("sandboxevents.heist.start")]
        private void CcHeistStart(ConsoleSystem.Arg arg)
        {
            if (!IsAdminArg(arg)) return;
            arg.ReplyWith(StartHeist(arg.GetInt(0, -1), arg.GetFloat(1, 0f)));
        }

        [ConsoleCommand("sandboxevents.heist.stop")]
        private void CcHeistStop(ConsoleSystem.Arg arg)
        {
            if (!IsAdminArg(arg)) return;
            if (_heist == null) { arg.ReplyWith("No train heist is running."); return; }
            EndHeist(Msg("HeistStopped", null));
            arg.ReplyWith("Train heist stopped.");
        }

        #endregion

        #region Commands

        [ChatCommand("hunt")]
        private void CmdHunt(BasePlayer player, string command, string[] args)
        {
            if (_hunt == null) { Say(player, Msg("NoHunt", player.UserIDString)); return; }
            var left = Mathf.CeilToInt((_hunt.EndsAt - Time.realtimeSinceStartup) / 60f);
            Hunter h;
            // Admins only: /hunt tp jumps next to your current clue (or the statue), for testing.
            if (args.Length > 0 && args[0].ToLowerInvariant() == "tp")
            {
                if (!player.IsAdmin) return;
                if (!_hunt.Hunters.TryGetValue(player.userID, out h)) { Join(player); h = _hunt.Hunters[player.userID]; }
                var target = Target(h);
                if (target == null) return;
                player.Teleport(target.Value + new Vector3(3f, 1.5f, 0f));
                Say(player, "Teleported next to " + (h.Stage < _hunt.Stages.Count ? "clue " + (h.Stage + 1) + " (" + _hunt.Stages[h.Stage].Monument + ")" : "the " + _config.Hunt.TrophyName) + ".");
                return;
            }
            if (!_hunt.Hunters.TryGetValue(player.userID, out h))
            {
                Say(player, Msg("NotJoined", player.UserIDString, _hunt.StartName, Grid(_hunt.StartPos)));
                return;
            }
            if (h.Stage < _hunt.Stages.Count)
            {
                Say(player, Msg("Status", player.UserIDString, h.Stage + 1, _hunt.Stages.Count, left));
                Say(player, Msg("Clue", player.UserIDString, h.Stage + 1, _hunt.Stages.Count, _hunt.Stages[h.Stage].Riddle));
            }
            else Say(player, Msg("Done", player.UserIDString, _config.Hunt.TrophyName));
            GiveDetector(player);
        }

        private static bool IsAdminArg(ConsoleSystem.Arg arg) => arg.Connection == null || arg.Connection.authLevel >= 2;

        /// <summary>sandboxevents.hunt.start [clues] [minutes]</summary>
        [ConsoleCommand("sandboxevents.hunt.start")]
        private void CcStart(ConsoleSystem.Arg arg)
        {
            if (!IsAdminArg(arg)) return;
            arg.ReplyWith(StartHunt(arg.GetInt(0, 0), arg.GetFloat(1, 0f)));
        }

        [ConsoleCommand("sandboxevents.hunt.stop")]
        private void CcStop(ConsoleSystem.Arg arg)
        {
            if (!IsAdminArg(arg)) return;
            if (_hunt == null) { arg.ReplyWith("No treasure hunt is running."); return; }
            EndHunt(Msg("Stopped", null));
            arg.ReplyWith("Treasure hunt stopped.");
        }

        private object HeistJson()
        {
            var h = _heist;
            if (h == null || h.Engine == null || h.Engine.IsDestroyed) return null;
            var hacker = h.Crate != null && h.Crate.originalHackerPlayerId != 0 ? BasePlayer.FindByID(h.Crate.originalHackerPlayerId) : null;
            return new
            {
                grid = Grid(h.Engine.transform.position),
                minutesLeft = Math.Max(0, Math.Round((h.EndsAt - Time.realtimeSinceStartup) / 60f, 1)),
                guards = h.Guards.Count(g => g != null && !g.IsDestroyed && !g.IsDead()),
                hacking = h.Crate != null && h.Crate.IsBeingHacked(),
                hacker = hacker != null ? hacker.displayName : null,
                won = h.Won,
                driver = h.HadDriver && h.Driver != null && !h.Driver.IsDestroyed && !h.Driver.IsDead(),
            };
        }

        /// <summary>sandboxevents.status - JSON for the manager.</summary>
        [ConsoleCommand("sandboxevents.status")]
        private void CcStatus(ConsoleSystem.Arg arg)
        {
            if (!IsAdminArg(arg)) return;
            if (_hunt == null) { arg.ReplyWith(JsonConvert.SerializeObject(new { version = Version.ToString(), hunt = (object)null, heist = HeistJson() })); return; }
            var now = Time.realtimeSinceStartup;
            arg.ReplyWith(JsonConvert.SerializeObject(new
            {
                version = Version.ToString(),
                hunt = new
                {
                    start = _hunt.StartName,
                    startGrid = Grid(_hunt.StartPos),
                    minutesLeft = Math.Max(0, Math.Round((_hunt.EndsAt - now) / 60f, 1)),
                    statueSpawned = _hunt.Statue != null,
                    stages = _hunt.Stages.Select(s => new { monument = s.Monument, grid = Grid(s.Spot) }),
                    hunters = _hunt.Hunters.Select(kv =>
                    {
                        var p = BasePlayer.FindByID(kv.Key);
                        return new { id = kv.Key.ToString(), name = p != null ? p.displayName : kv.Key.ToString(), stage = kv.Value.Stage, online = p != null };
                    }).OrderByDescending(x => x.stage),
                },
                heist = HeistJson(),
            }));
        }

        #endregion
    }
}
