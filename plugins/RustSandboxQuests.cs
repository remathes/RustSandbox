// Rust Sandbox — Quests
// Quest givers stand where an admin puts them. Walk up, press E to talk, and a quest panel opens: pick a
// quest, accept it, and a tracker on the right of the screen shows your progress. Finished quests are
// handed in back at a quest giver for the rewards the admin chose (items, scrap, or console commands).
//
// Quest types: Gather (resources), Kill (scientists, animals, Bradley...), Loot (crates, optionally at a
// monument), Craft, Deliver (bring items to the giver), Swipe (keycard puzzles) and Visit (a monument).
//
// Install: copy this file to oxide/plugins (or carbon/plugins). Quests live in oxide/config/RustSandboxQuests.json
// and are easiest to edit on the Rust Sandbox "Quests" page. Admin chat commands:
//   /questgiver add <name>      place a quest giver where you stand, facing you
//   /questgiver remove          remove the quest giver you're looking at (or the nearest one)
//   /questgiver list            list quest givers
//   /questgiver move <id>       move a quest giver to where you stand
//   /questgiver look <id> <look>   change a quest giver's outfit (outpost_a, bandit_b, fishing_a, ...)
// Players type /quests to see their quest log, or /quests list for where quest givers and quest locations are.
// Quest givers and the places active quests send you are also marked on each player's map and compass.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Game.Rust.Cui;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("Rust Sandbox Quests", "RustSandbox", "1.3.2")]
    [Description("Quest givers you place in the world, quests with gather/kill/loot/craft/deliver/keycard/visit goals, a progress tracker and rewards.")]
    public class RustSandboxQuests : RustPlugin
    {
        #region Config

        private PluginConfig _config;

        private const string TypeGather = "Gather";
        private const string TypeKill = "Kill";
        private const string TypeLoot = "Loot";
        private const string TypeCraft = "Craft";
        private const string TypeDeliver = "Deliver";
        private const string TypeSwipe = "Swipe";
        private const string TypeVisit = "Visit";

        private class Reward
        {
            [JsonProperty("Item")] public string Item = "";
            [JsonProperty("Amount")] public int Amount = 1;
            [JsonProperty("Skin")] public ulong Skin;
        }

        private class Quest
        {
            [JsonProperty("Id")] public string Id = "";
            [JsonProperty("Title")] public string Title = "";
            [JsonProperty("Description")] public string Description = "";
            [JsonProperty("Enabled")] public bool Enabled = true;
            [JsonProperty("Giver (quest giver id, blank = every quest giver)")] public string Giver = "";
            [JsonProperty("Type (Gather, Kill, Loot, Craft, Deliver, Swipe, Visit)")] public string Type = TypeGather;
            [JsonProperty("Target (item / prefab names, comma separated, * wildcards; keycard colour for Swipe; monument for Visit)")] public string Target = "";
            [JsonProperty("Amount")] public int Amount = 1;
            [JsonProperty("Where (monument name, blank = anywhere)")] public string Where = "";
            [JsonProperty("Objective text (blank = automatic)")] public string Objective = "";
            [JsonProperty("Requires quest (id, blank = none)")] public string Requires = "";
            [JsonProperty("Repeatable")] public bool Repeatable;
            [JsonProperty("Cooldown before repeating (minutes)")] public float CooldownMinutes = 60f;
            [JsonProperty("Rewards")] public List<Reward> Rewards = new List<Reward>();
            [JsonProperty("Reward console commands ({steamid}, {name})")] public List<string> Commands = new List<string>();
        }

        private class PluginConfig
        {
            [JsonProperty("Return to a quest giver to claim rewards")] public bool ReturnToGiver = true;
            [JsonProperty("Max active quests per player")] public int MaxActive = 3;
            [JsonProperty("Show the quest tracker")] public bool Tracker = true;
            [JsonProperty("Tracker anchor (x y, 0-1 of the screen)")] public string TrackerAnchor = "1 0.5";
            [JsonProperty("Tracker top-left offset from the anchor (x y pixels)")] public string TrackerOffset = "-262 150";
            [JsonProperty("Announce quest completions to everyone")] public bool Announce;
            [JsonProperty("Show quest givers on the map and compass")] public bool MarkGivers = true;
            [JsonProperty("Quest giver marker style (Hover = small icon, name on hover; Pin = labelled pin with (!))")] public string GiverMarkerStyle = "Hover";
            [JsonProperty("Mark active quest locations on the map and compass")] public bool MarkQuests = true;
            [JsonProperty("Reset quest progress on map wipe")] public bool ResetOnWipe;
            [JsonProperty("Default quest giver look")] public string DefaultLook = "outpost_a";
            [JsonProperty("Chat prefix")] public string ChatPrefix = "<color=#f0b04a>[Quests]</color>";
            [JsonProperty("Chat icon (SteamID, 0 = server)")] public ulong ChatIcon = 0;
            [JsonProperty("Quests", ObjectCreationHandling = ObjectCreationHandling.Replace)] public List<Quest> Quests = DefaultQuests();
        }

        private static Reward R(string item, int amount) => new Reward { Item = item, Amount = amount };

        private static List<Quest> DefaultQuests() => new List<Quest>
        {
            new Quest
            {
                Id = "stone-cold-start", Title = "Stone Cold Start", Type = TypeGather, Target = "stones", Amount = 5000,
                Description = "Every base starts with a pile of rocks. Bring me proof you can swing a pickaxe.",
                Rewards = { R("metal.fragments", 500), R("scrap", 100) },
            },
            new Quest
            {
                Id = "lumberjack", Title = "Lumberjack", Type = TypeGather, Target = "wood", Amount = 10000,
                Description = "Winter's coming and the furnaces are hungry. Chop chop.",
                Rewards = { R("lowgradefuel", 100), R("scrap", 100) },
            },
            new Quest
            {
                Id = "tool-time", Title = "Tool Time", Type = TypeCraft, Target = "spear.wooden", Amount = 5,
                Description = "Show me you know your way around a workbench. Five wooden spears will do.",
                Rewards = { R("axe.salvaged", 1) },
            },
            new Quest
            {
                Id = "bear-hunter", Title = "Bear Hunter", Type = TypeKill, Target = "bear,polarbear", Amount = 3,
                Description = "Bears keep raiding the camp. Thin them out.",
                Rewards = { R("bow.compound", 1), R("arrow.hv", 40) },
            },
            new Quest
            {
                Id = "scientist-problem", Title = "Scientist Problem", Type = TypeKill, Target = "scientistnpc_*", Amount = 10,
                Description = "The scientists at the monuments are getting bold. Take ten of them out.",
                Rewards = { R("ammo.rifle", 128), R("scrap", 250) },
            },
            new Quest
            {
                Id = "monument-explorer", Title = "Monument Explorer", Type = TypeVisit, Target = "Airfield", Amount = 1,
                Description = "Scout the Airfield and come back alive.",
                Rewards = { R("scrap", 50), R("syringe.medical", 3) },
            },
            new Quest
            {
                Id = "launch-site-looter", Title = "Launch Site Looter", Type = TypeLoot, Target = "crate_normal,crate_normal_2,crate_elite,crate_tools", Amount = 15,
                Where = "Launch Site",
                Description = "Launch Site is full of crates nobody's using. Loot fifteen of them.",
                Rewards = { R("metal.refined", 50), R("scrap", 500) },
            },
            new Quest
            {
                Id = "red-card-run", Title = "Red Card Run", Type = TypeSwipe, Target = "red", Amount = 1,
                Requires = "launch-site-looter",
                Description = "The big doors open for a red keycard. Solve a red card puzzle anywhere on the map.",
                Rewards = { R("rifle.ak", 1), R("ammo.rifle", 128) },
            },
            new Quest
            {
                Id = "mushroom-hunt", Title = "Mushroom Hunt", Type = TypeDeliver, Target = "mushroom", Amount = 20,
                Repeatable = true, CooldownMinutes = 60,
                Description = "I'm making stew. Bring me twenty mushrooms and I'll patch you up.",
                Rewards = { R("largemedkit", 2), R("scrap", 40) },
            },
        };

        protected override void LoadDefaultConfig() => _config = new PluginConfig();

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                _config = Config.ReadObject<PluginConfig>();
                if (_config == null) throw new Exception("empty");
                if (_config.Quests == null) _config.Quests = new List<Quest>();
                var before = _config.Quests.Count;
                _config.Quests = Dedupe(_config.Quests, q => q.Id, DefaultQuests());
                if (_config.Quests.Count < before) Puts($"Removed {before - _config.Quests.Count} duplicate copies of the starter quests from the config.");
                foreach (var q in _config.Quests)
                {
                    if (q.Rewards == null) q.Rewards = new List<Reward>();
                    if (q.Commands == null) q.Commands = new List<string>();
                    if (string.IsNullOrEmpty(q.Id)) q.Id = Slug(q.Title);
                    if (q.Amount < 1) q.Amount = 1;
                    // 1.2.0 and earlier shipped Tool Time with a misspelled reward item (the real shortname is axe.salvaged).
                    foreach (var r in q.Rewards)
                        if (r != null && r.Item == "salvaged.axe") r.Item = "axe.salvaged";
                }
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

        private static string Slug(string s)
        {
            var slug = Regex.Replace((s ?? "").ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
            return slug.Length == 0 ? "quest-" + UnityEngine.Random.Range(1000, 9999) : slug;
        }

        private Quest FindQuest(string id) =>
            _config.Quests.FirstOrDefault(q => string.Equals(q.Id, id, StringComparison.OrdinalIgnoreCase));

        #endregion

        #region Lang

        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["Title"] = "QUESTS",
                ["LogTitle"] = "QUEST LOG",
                ["NoQuests"] = "{0} has no quests for you right now.",
                ["NoActive"] = "You have no active quests. Talk to a quest giver to pick one up.",
                ["Pick"] = "Pick a quest on the left.",
                ["Accept"] = "ACCEPT",
                ["Abandon"] = "ABANDON",
                ["Claim"] = "CLAIM REWARD",
                ["HandOver"] = "HAND OVER",
                ["Close"] = "CLOSE",
                ["Rewards"] = "REWARDS",
                ["Objective"] = "OBJECTIVE",
                ["StateAvailable"] = "New",
                ["StateActive"] = "{0}%",
                ["StateReady"] = "Ready!",
                ["StateDone"] = "Done",
                ["StateLocked"] = "Locked",
                ["StateCooldown"] = "{0}",
                ["LockedInfo"] = "Finish <color=#f0b04a>{0}</color> first.",
                ["CooldownInfo"] = "You can do this again in {0}.",
                ["DoneInfo"] = "You've finished this quest.",
                ["Accepted"] = "Quest accepted: <color=#f0b04a>{0}</color>",
                ["Abandoned"] = "Quest abandoned: {0}",
                ["TooMany"] = "You can only have {0} quests at a time. Finish or abandon one first.",
                ["Complete"] = "Quest complete: <color=#8fd16a>{0}</color>!",
                ["ReturnTo"] = "Return to a quest giver to claim your reward.",
                ["ReturnToName"] = "Return to {0}",
                ["Claimed"] = "Reward claimed for <color=#8fd16a>{0}</color>: {1}",
                ["Dropped"] = "Your inventory was full, so part of your reward was dropped at your feet.",
                ["NotEnough"] = "You need {0} {1} to hand over (you have {2}).",
                ["Announce"] = "{0} completed the quest <color=#8fd16a>{1}</color>!",
                ["TrackerHeader"] = "QUESTS",
                ["Hide"] = "Hide tracker",
                ["Show"] = "Show tracker",
                ["NoPermission"] = "Only admins can do that.",
                ["Inventory"] = "in your inventory",
                ["ClaimAtGiver"] = "Claim it from a quest giver.",
                ["ListGivers"] = "<color=#f0b04a>Quest givers</color> (also on your map and compass):",
                ["ListGiver"] = "  {0} - {1}, {2} {3}{4}",
                ["ListNoGivers"] = "There are no quest givers on this map yet.",
                ["ListActive"] = "<color=#f0b04a>Your quests:</color>",
                ["ListQuest"] = "  {0} - {1}",
                ["ListNoActive"] = "You have no active quests. Visit a quest giver to pick one up.",
                ["ListHint"] = "Type /quests to open your quest log.",
                ["GiverNew"] = "{0} new",
                ["GiverReady"] = "{0} to hand in",
                ["Location"] = "{0} · {1} · {2} {3}",
                ["HandInAt"] = "Hand in at {0} · {1} · {2} {3}",
            }, this);
        }

        private string Msg(string key, string userId, params object[] args)
        {
            var text = lang.GetMessage(key, this, userId);
            return args.Length == 0 ? text : string.Format(text, args);
        }

        private void Say(BasePlayer player, string text) => Player.Message(player, text, _config.ChatPrefix, _config.ChatIcon);

        #endregion

        #region Data

        private const string DataFile = "RustSandboxQuests";
        private const string GiversFile = "RustSandboxQuests_givers";

        private class ActiveQuest
        {
            public int Progress;
            public bool Complete;
            public DateTime Started;
            public List<ulong> Seen = new List<ulong>();
        }

        private class DoneQuest
        {
            public int Count;
            public DateTime Last;
        }

        private class PlayerData
        {
            public string Name = "";
            public bool HideTracker;
            public Dictionary<string, ActiveQuest> Active = new Dictionary<string, ActiveQuest>();
            public Dictionary<string, DoneQuest> Done = new Dictionary<string, DoneQuest>();
        }

        private class Completion
        {
            public DateTime At;
            public string Id = "";
            public string Name = "";
            public string Quest = "";
            public string Title = "";
        }

        private class StoredData
        {
            public Dictionary<ulong, PlayerData> Players = new Dictionary<ulong, PlayerData>();
            public List<Completion> History = new List<Completion>();
        }

        private class Giver
        {
            public string Id = "";
            public string Name = "";
            public string Look = "";
            public float X, Y, Z, Yaw;
            public string Map = "";
            // Clothing (with skins) worn instead of the look's own outfit. Empty = the look's outfit.
            public List<OutfitItem> Outfit = new List<OutfitItem>();
        }

        private class OutfitItem
        {
            public string Item = "";
            public ulong Skin;
        }

        private class GiverData
        {
            public int NextId = 1;
            public List<Giver> Givers = new List<Giver>();
        }

        private StoredData _data = new StoredData();
        private GiverData _givers = new GiverData();
        private bool _dirty;

        private void LoadData()
        {
            try { _data = Interface.Oxide.DataFileSystem.ReadObject<StoredData>(DataFile) ?? new StoredData(); }
            catch (Exception ex)
            {
                PrintError($"Couldn't read the data file ({ex.Message}); starting empty.");
                _data = new StoredData();
            }
            if (_data.Players == null) _data.Players = new Dictionary<ulong, PlayerData>();
            if (_data.History == null) _data.History = new List<Completion>();
            foreach (var p in _data.Players.Values)
            {
                if (p.Active == null) p.Active = new Dictionary<string, ActiveQuest>();
                if (p.Done == null) p.Done = new Dictionary<string, DoneQuest>();
                foreach (var a in p.Active.Values)
                    if (a.Seen == null) a.Seen = new List<ulong>();
            }

            try { _givers = Interface.Oxide.DataFileSystem.ReadObject<GiverData>(GiversFile) ?? new GiverData(); }
            catch (Exception ex)
            {
                PrintError($"Couldn't read the quest giver file ({ex.Message}); starting with none.");
                _givers = new GiverData();
            }
            if (_givers.Givers == null) _givers.Givers = new List<Giver>();
        }

        private void SaveData()
        {
            Interface.Oxide.DataFileSystem.WriteObject(DataFile, _data);
            _dirty = false;
        }

        private void SaveGivers() => Interface.Oxide.DataFileSystem.WriteObject(GiversFile, _givers);

        private PlayerData Data(BasePlayer player)
        {
            PlayerData d;
            if (!_data.Players.TryGetValue(player.userID, out d)) _data.Players[player.userID] = d = new PlayerData();
            d.Name = player.displayName;
            return d;
        }

        private PlayerData DataIfAny(ulong id)
        {
            PlayerData d;
            return _data.Players.TryGetValue(id, out d) ? d : null;
        }

        #endregion

        #region Lifecycle

        private void Init()
        {
            permission.RegisterPermission(AdminPerm, this);
            LoadData();
        }

        private void OnServerInitialized()
        {
            SpawnAllGivers();
            RefreshAllMarkers();
            timer.Every(30f, () => { if (_dirty) SaveData(); });
            timer.Every(2f, PollTick);
            foreach (var p in BasePlayer.activePlayerList) DrawTracker(p);
        }

        private void Unload()
        {
            foreach (var p in BasePlayer.activePlayerList)
            {
                Close(p);
                CuiHelper.DestroyUi(p, TrackerUi);
            }
            DespawnAllGivers();
            SaveData();
            _unloading = true;
            foreach (var p in BasePlayer.activePlayerList) p.SendMarkersToClient();
        }

        private void OnServerSave() { if (_dirty) SaveData(); }

        private void OnNewSave(string filename)
        {
            if (!_config.ResetOnWipe) return;
            foreach (var p in _data.Players.Values)
            {
                p.Active.Clear();
                p.Done.Clear();
            }
            SaveData();
            Puts("Map wipe: quest progress reset.");
        }

        private void OnPlayerSleepEnded(BasePlayer player)
        {
            if (player == null || !player.userID.IsSteamId()) return;
            DrawTracker(player);
            RefreshMarkers(player);
        }

        private void OnPlayerDisconnected(BasePlayer player, string reason)
        {
            if (player == null) return;
            _views.Remove(player.userID);
            _trackerQueued.Remove(player.userID);
            _shown.Remove(player.userID);
        }

        #endregion

        #region Quest givers (NPCs)

        private const string AdminPerm = "rustsandboxquests.admin";

        // The talking NPCs from Outpost, Bandit Camp and the fishing villages: they stand still, can't be hurt,
        // wave at passers-by and show the game's own "Talk" prompt. Pressing E is caught in OnNpcConversationStart.
        private static readonly string[] Looks = { "outpost_a", "outpost_b", "bandit_a", "bandit_b", "fishing_a", "fishing_b", "stables_a", "stables_b", "generic_a", "floatingcity_a" };

        private static string PrefabFor(string look) =>
            "assets/prefabs/npc/bandit/missionproviders/missionprovider_" + (look ?? "outpost_a").ToLowerInvariant() + ".prefab";

        private readonly Dictionary<string, NPCTalking> _spawned = new Dictionary<string, NPCTalking>();
        private readonly Dictionary<ulong, string> _npcToGiver = new Dictionary<ulong, string>();

        private static string MapKey() => World.Size + "_" + World.Seed;

        private bool OnThisMap(Giver g) => string.IsNullOrEmpty(g.Map) || g.Map == MapKey();

        private void SpawnAllGivers()
        {
            foreach (var g in _givers.Givers) SpawnGiver(g);
        }

        private const string ShopMarkerPrefab = "assets/prefabs/deployable/vendingmachine/vending_mapmarker.prefab";
        private readonly Dictionary<string, VendingMachineMapMarker> _shopMarkers = new Dictionary<string, VendingMachineMapMarker>();

        /// <summary>Hover style: a small shop-style map icon that shows the giver's name on hover (same for every player).</summary>
        private bool HoverMarkers => _config.MarkGivers && !string.Equals(_config.GiverMarkerStyle, "Pin", StringComparison.OrdinalIgnoreCase);

        private void KillShopMarker(Giver g)
        {
            VendingMachineMapMarker m;
            if (!_shopMarkers.TryGetValue(g.Id, out m)) return;
            _shopMarkers.Remove(g.Id);
            if (m != null && !m.IsDestroyed) m.Kill();
        }

        private void SpawnShopMarker(Giver g)
        {
            KillShopMarker(g);
            if (!HoverMarkers || !OnThisMap(g)) return;
            var m = GameManager.server.CreateEntity(ShopMarkerPrefab, PosOf(g)) as VendingMachineMapMarker;
            if (m == null) return;
            m.markerShopName = g.Name + " - quest giver";
            m.enableSaving = false;
            m.flags |= BaseEntity.Flags.Busy; // green "open" icon instead of grey "out of stock"
            m.Spawn();
            _shopMarkers[g.Id] = m;
        }

        private void DespawnAllGivers()
        {
            foreach (var m in _shopMarkers.Values)
                if (m != null && !m.IsDestroyed) m.Kill();
            _shopMarkers.Clear();
            foreach (var npc in _spawned.Values)
                if (npc != null && !npc.IsDestroyed) npc.Kill();
            _spawned.Clear();
            _npcToGiver.Clear();
        }

        private void DespawnGiver(Giver g)
        {
            KillShopMarker(g);
            NPCTalking npc;
            if (!_spawned.TryGetValue(g.Id, out npc)) return;
            _spawned.Remove(g.Id);
            if (npc == null) return;
            if (npc.net != null) _npcToGiver.Remove(npc.net.ID.Value);
            if (!npc.IsDestroyed) npc.Kill();
        }

        private void SpawnGiver(Giver g)
        {
            DespawnGiver(g);
            if (!OnThisMap(g)) return;
            var pos = new Vector3(g.X, g.Y, g.Z);
            var rot = Quaternion.Euler(0f, g.Yaw, 0f);
            var entity = GameManager.server.CreateEntity(PrefabFor(string.IsNullOrEmpty(g.Look) ? _config.DefaultLook : g.Look), pos, rot);
            if (!(entity is NPCTalking))
            {
                if (entity != null) entity.Kill();
                entity = GameManager.server.CreateEntity(PrefabFor("outpost_a"), pos, rot);
            }
            var npc = entity as NPCTalking;
            if (npc == null)
            {
                if (entity != null) entity.Kill();
                PrintError($"Couldn't spawn quest giver {g.Name}: the NPC prefab didn't load.");
                return;
            }
            npc.enableSaving = false;
            npc.Spawn();
            npc.displayName = g.Name;
            Dress(npc, g);
            _spawned[g.Id] = npc;
            if (npc.net != null) _npcToGiver[npc.net.ID.Value] = g.Id;
            SpawnShopMarker(g);

            // Mission NPCs drop a map marker for the game's own missions; these givers don't offer those.
            NextTick(() =>
            {
                var markers = new List<MapMarkerMissionProvider>();
                Vis.Entities(pos, 2f, markers);
                foreach (var m in markers)
                    if (m != null && !m.IsDestroyed) m.Kill();
            });
        }

        private void Dress(NPCTalking npc, Giver g)
        {
            if (g.Outfit == null || g.Outfit.Count == 0) return;
            var wear = npc.inventory.containerWear;
            foreach (var old in wear.itemList.ToList()) { old.RemoveFromContainer(); old.Remove(); }
            foreach (var o in g.Outfit)
            {
                var item = ItemManager.CreateByName(o.Item, 1, o.Skin);
                if (item != null && !item.MoveToContainer(wear)) item.Remove();
            }
            npc.SendNetworkUpdate();
        }

        // Copies what the player is wearing (skins included) onto a quest giver.
        private string SetOutfit(Giver g, BasePlayer from, bool clear)
        {
            if (clear) g.Outfit = new List<OutfitItem>();
            else
            {
                var worn = from.inventory.containerWear.itemList;
                if (worn.Count == 0) return "Put on the clothes you want them to wear first (skin them with /skins), then run this again.";
                g.Outfit = worn.Select(i => new OutfitItem { Item = i.info.shortname, Skin = i.skin }).ToList();
            }
            SaveGivers();
            SpawnGiver(g);
            return clear ? $"{g.Name} is back to the {(string.IsNullOrEmpty(g.Look) ? _config.DefaultLook : g.Look)} look's outfit."
                         : $"{g.Name} now wears your outfit ({g.Outfit.Count} items).";
        }

        private Giver GiverOf(BaseEntity npc)
        {
            if (npc == null || npc.net == null) return null;
            string id;
            return _npcToGiver.TryGetValue(npc.net.ID.Value, out id) ? FindGiver(id) : null;
        }

        private Giver FindGiver(string id) =>
            _givers.Givers.FirstOrDefault(g => string.Equals(g.Id, id, StringComparison.OrdinalIgnoreCase));

        private Giver AddGiver(BasePlayer at, string name)
        {
            var yaw = at.eyes != null ? at.eyes.rotation.eulerAngles.y + 180f : at.transform.eulerAngles.y + 180f;
            var g = new Giver
            {
                Id = "g" + _givers.NextId++,
                Name = string.IsNullOrEmpty(name) ? "Quest Giver" : name,
                Look = _config.DefaultLook,
                X = at.transform.position.x, Y = at.transform.position.y, Z = at.transform.position.z,
                Yaw = Mathf.Repeat(yaw, 360f),
                Map = MapKey(),
            };
            _givers.Givers.Add(g);
            SaveGivers();
            SpawnGiver(g);
            RefreshAllMarkers();
            return g;
        }

        private void RemoveGiver(Giver g)
        {
            DespawnGiver(g);
            _givers.Givers.Remove(g);
            SaveGivers();
            RefreshAllMarkers();
        }

        // Pressing E on one of our NPCs: show the quest panel instead of the game's conversation.
        private object OnNpcConversationStart(NPCTalking npc, BasePlayer player, ConversationData conversation)
        {
            var g = GiverOf(npc);
            if (g == null) return null;
            OpenGiver(player, g);
            return false;
        }

        // Fallback if a game update changes the talk flow: E while looking at a giver up close.
        private readonly Dictionary<ulong, float> _lastUse = new Dictionary<ulong, float>();

        private void OnPlayerInput(BasePlayer player, InputState input)
        {
            if (input == null || !input.WasJustPressed(BUTTON.USE) || _npcToGiver.Count == 0) return;
            if (_views.ContainsKey(player.userID)) return;
            RaycastHit hit;
            if (!Physics.Raycast(player.eyes.HeadRay(), out hit, 3f, Rust.Layers.Mask.Player_Server, QueryTriggerInteraction.Ignore)) return;
            var npc = hit.GetEntity() as NPCTalking;
            var g = GiverOf(npc);
            if (g == null) return;
            float last;
            var now = Time.realtimeSinceStartup;
            if (_lastUse.TryGetValue(player.userID, out last) && now - last < 0.6f) return;
            _lastUse[player.userID] = now;
            // Let the conversation hook open it first when it fires; open it here only if it didn't.
            timer.Once(0.3f, () =>
            {
                if (player != null && player.IsConnected && !_views.ContainsKey(player.userID)) OpenGiver(player, g);
            });
        }

        private bool IsAdmin(BasePlayer player) =>
            player != null && (player.IsAdmin || permission.UserHasPermission(player.UserIDString, AdminPerm));

        [ChatCommand("questgiver")]
        private void CmdGiver(BasePlayer player, string command, string[] args)
        {
            if (!IsAdmin(player)) { Say(player, Msg("NoPermission", player.UserIDString)); return; }
            var sub = args.Length > 0 ? args[0].ToLowerInvariant() : "";
            switch (sub)
            {
                case "add":
                {
                    var g = AddGiver(player, string.Join(" ", args.Skip(1)));
                    Say(player, $"Placed quest giver <color=#f0b04a>{g.Name}</color> ({g.Id}) at {Grid(player.transform.position)}. Step back to talk to them.");
                    return;
                }
                case "remove":
                {
                    var g = NearestGiver(player, 8f);
                    if (g == null) { Say(player, "No quest giver within 8 m. Stand next to the one to remove."); return; }
                    RemoveGiver(g);
                    Say(player, $"Removed quest giver {g.Name} ({g.Id}).");
                    return;
                }
                case "move":
                {
                    Giver g = args.Length > 1 ? FindGiver(args[1]) : null;
                    if (g == null) { Say(player, "Usage: /questgiver move <id> (moves them to where you stand; /questgiver list shows ids)"); return; }
                    var p = player.transform.position;
                    g.X = p.x; g.Y = p.y; g.Z = p.z;
                    g.Yaw = Mathf.Repeat((player.eyes != null ? player.eyes.rotation.eulerAngles.y : player.transform.eulerAngles.y) + 180f, 360f);
                    g.Map = MapKey();
                    SaveGivers();
                    SpawnGiver(g);
                    RefreshAllMarkers();
                    Say(player, $"Moved {g.Name} to {Grid(p)}. Step back to talk to them.");
                    return;
                }
                case "look":
                {
                    Giver g = args.Length > 1 ? FindGiver(args[1]) : null;
                    var look = args.Length > 2 ? args[2].ToLowerInvariant() : "";
                    if (g == null || !Looks.Contains(look)) { Say(player, "Usage: /questgiver look <id> <" + string.Join("|", Looks) + ">"); return; }
                    g.Look = look;
                    SaveGivers();
                    SpawnGiver(g);
                    Say(player, $"{g.Name} now uses the {look} look.");
                    return;
                }
                case "outfit":
                {
                    Giver g = args.Length > 1 ? FindGiver(args[1]) : null;
                    if (g == null) { Say(player, "Usage: /questgiver outfit <id> (copies what you're wearing, skins included) · /questgiver outfit <id> clear"); return; }
                    Say(player, SetOutfit(g, player, args.Length > 2 && args[2].ToLowerInvariant() == "clear"));
                    return;
                }
                case "list":
                {
                    if (_givers.Givers.Count == 0) { Say(player, "No quest givers yet. Use /questgiver add <name>."); return; }
                    Say(player, string.Join("\n", _givers.Givers.Select(g => $"{g.Id}: {g.Name} at {Grid(new Vector3(g.X, g.Y, g.Z))}{(OnThisMap(g) ? "" : " (another map)")}")));
                    return;
                }
                default:
                    Say(player, "/questgiver add <name> · move <id> · remove · list · look <id> <look> · outfit <id> [clear]");
                    return;
            }
        }

        private Giver NearestGiver(BasePlayer player, float within)
        {
            Giver best = null;
            var bestD = within;
            foreach (var g in _givers.Givers)
            {
                if (!OnThisMap(g)) continue;
                var d = Vector3.Distance(player.transform.position, new Vector3(g.X, g.Y, g.Z));
                if (d <= bestD) { bestD = d; best = g; }
            }
            return best;
        }

        #endregion

        #region Quest state

        private enum State { Available, Active, Ready, Done, Locked, Cooldown }

        private bool Offers(Giver g, Quest q) =>
            q.Enabled && (string.IsNullOrEmpty(q.Giver) || (g != null && string.Equals(q.Giver, g.Id, StringComparison.OrdinalIgnoreCase)));

        private State StateOf(BasePlayer player, PlayerData d, Quest q)
        {
            ActiveQuest a;
            if (d.Active.TryGetValue(q.Id, out a)) return IsReady(player, q, a) ? State.Ready : State.Active;
            if (!string.IsNullOrEmpty(q.Requires) && FindQuest(q.Requires) != null && !d.Done.ContainsKey(q.Requires)) return State.Locked;
            DoneQuest done;
            if (d.Done.TryGetValue(q.Id, out done))
            {
                if (!q.Repeatable) return State.Done;
                if (CooldownLeft(q, done) > TimeSpan.Zero) return State.Cooldown;
            }
            return State.Available;
        }

        private static TimeSpan CooldownLeft(Quest q, DoneQuest done) =>
            done.Last.AddMinutes(Math.Max(0, q.CooldownMinutes)) - DateTime.UtcNow;

        private bool IsReady(BasePlayer player, Quest q, ActiveQuest a) =>
            q.Type == TypeDeliver ? InventoryCount(player, q) >= q.Amount : a.Complete;

        private int ProgressOf(BasePlayer player, Quest q, ActiveQuest a) =>
            q.Type == TypeDeliver ? Math.Min(q.Amount, InventoryCount(player, q)) : Math.Min(q.Amount, a.Progress);

        private int InventoryCount(BasePlayer player, Quest q)
        {
            if (player == null || player.inventory == null) return 0;
            var total = 0;
            foreach (var def in ItemDefs(q.Target)) total += player.inventory.GetAmount(def.itemid);
            return total;
        }

        private static List<string> Tokens(string target) =>
            (target ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(t => t.Trim().ToLowerInvariant()).Where(t => t.Length > 0).ToList();

        private static List<ItemDefinition> ItemDefs(string target)
        {
            var list = new List<ItemDefinition>();
            foreach (var t in Tokens(target))
            {
                if (t.Contains("*"))
                {
                    var rx = Glob(t);
                    list.AddRange(ItemManager.itemList.Where(d => rx.IsMatch(d.shortname)));
                }
                else
                {
                    var def = ItemManager.FindItemDefinition(t);
                    if (def != null) list.Add(def);
                }
            }
            return list.Distinct().ToList();
        }

        private static readonly Dictionary<string, Regex> _globs = new Dictionary<string, Regex>();

        private static Regex Glob(string pattern)
        {
            Regex rx;
            if (!_globs.TryGetValue(pattern, out rx))
                _globs[pattern] = rx = new Regex("^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$", RegexOptions.IgnoreCase);
            return rx;
        }

        private static bool NameMatches(string target, string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            foreach (var t in Tokens(target))
            {
                if (t == "*") return true;
                if (t.Contains("*") ? Glob(t).IsMatch(name) : string.Equals(t, name, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static bool IsAnimal(BaseEntity e) =>
            e is BaseNpc || e is Rust.Ai.Gen2.BaseNPC2 || e is SimpleShark || e is BaseFishNPC;

        private static bool KillMatches(string target, BaseCombatEntity entity, BasePlayer killer)
        {
            var name = entity.ShortPrefabName;
            var victim = entity as BasePlayer;
            foreach (var t in Tokens(target))
            {
                switch (t)
                {
                    case "animal":
                    case "animals":
                        if (IsAnimal(entity)) return true;
                        continue;
                    case "npc":
                    case "npcs":
                        if (victim != null && (victim.IsNpc || !victim.userID.IsSteamId())) return true;
                        continue;
                    case "scientist":
                    case "scientists":
                        if (entity is ScientistNPC || (name != null && name.Contains("scientist"))) return true;
                        continue;
                    case "player":
                    case "players":
                        if (victim != null && !victim.IsNpc && victim.userID.IsSteamId() && victim != killer) return true;
                        continue;
                }
                if (NameMatches(t, name)) return true;
            }
            return false;
        }

        private static string NameOf(MonumentInfo m)
        {
            var phrase = m.displayPhrase != null ? m.displayPhrase.english : null;
            return !string.IsNullOrEmpty(phrase) ? phrase : m.name;
        }

        private static bool AtMonument(string where, Vector3 pos)
        {
            if (string.IsNullOrEmpty(where)) return true;
            var monuments = TerrainMeta.Path != null ? TerrainMeta.Path.Monuments : null;
            if (monuments == null) return false;
            foreach (var m in monuments)
            {
                if (m == null || NameOf(m).IndexOf(where, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (m.IsInBounds(pos)) return true;
            }
            return false;
        }

        private static string Grid(Vector3 pos)
        {
            try { return MapHelper.PositionToString(pos); }
            catch { return $"{pos.x:0}, {pos.z:0}"; }
        }

        #endregion

        #region Progress hooks

        /// <summary>Adds progress to each of the player's active quests of this type that the test accepts.</summary>
        private void Advance(BasePlayer player, string type, int amount, Vector3 pos, Func<Quest, bool> matches)
        {
            if (player == null || amount <= 0 || !player.userID.IsSteamId()) return;
            var d = DataIfAny(player.userID);
            if (d == null || d.Active.Count == 0) return;
            var changed = false;
            foreach (var kv in d.Active)
            {
                var a = kv.Value;
                if (a.Complete) continue;
                var q = FindQuest(kv.Key);
                if (q == null || q.Type != type || !AtMonument(q.Where, pos) || !matches(q)) continue;
                a.Progress = Math.Min(q.Amount, a.Progress + amount);
                changed = true;
                if (a.Progress >= q.Amount) Completed(player, q, a);
            }
            if (!changed) return;
            _dirty = true;
            QueueTracker(player);
        }

        private void Completed(BasePlayer player, Quest q, ActiveQuest a)
        {
            a.Complete = true;
            RefreshMarkers(player);
            PlaySound(player, "assets/prefabs/misc/xmas/presents/effects/unwrap.prefab");
            Say(player, Msg("Complete", player.UserIDString, q.Title));
            if (_config.ReturnToGiver) Say(player, Msg("ReturnTo", player.UserIDString));
            else NextTick(() => { if (player != null) Claim(player, q, null); });
        }

        private static bool HasActive(PlayerData d, string type, Func<string, Quest> find)
        {
            foreach (var kv in d.Active)
            {
                if (kv.Value.Complete) continue;
                var q = find(kv.Key);
                if (q != null && q.Type == type) return true;
            }
            return false;
        }

        private void Gathered(BasePlayer player, Item item, Vector3 pos)
        {
            if (player == null || item == null || item.info == null) return;
            var d = DataIfAny(player.userID);
            if (d == null || d.Active.Count == 0) return;
            var shortname = item.info.shortname;
            Advance(player, TypeGather, item.amount, pos, q => NameMatches(q.Target, shortname));
        }

        private void OnDispenserGathered(ResourceDispenser dispenser, BaseEntity entity, Item item) =>
            Gathered(entity as BasePlayer, item, dispenser != null ? dispenser.transform.position : Vector3.zero);

        private void OnDispenserBonusReceived(ResourceDispenser dispenser, BasePlayer player, Item item) =>
            Gathered(player, item, dispenser != null ? dispenser.transform.position : Vector3.zero);

        private void OnCollectiblePickedup(CollectibleEntity collectible, BasePlayer player, Item item) =>
            Gathered(player, item, collectible != null ? collectible.transform.position : Vector3.zero);

        private void OnGrowableGathered(GrowableEntity plant, Item item, BasePlayer player) =>
            Gathered(player, item, plant != null ? plant.transform.position : Vector3.zero);

        private void OnEntityDeath(BaseCombatEntity entity, HitInfo info)
        {
            if (entity == null || info == null) return;
            var killer = info.InitiatorPlayer;
            if (killer == null || killer == entity || !killer.userID.IsSteamId()) return;
            var d = DataIfAny(killer.userID);
            if (d == null || d.Active.Count == 0) return;
            var pos = entity.transform.position;
            Advance(killer, TypeKill, 1, pos, q => KillMatches(q.Target, entity, killer));
        }

        private void OnLootEntity(BasePlayer player, BaseEntity entity)
        {
            if (player == null || entity == null || entity.net == null || !(entity is StorageContainer)) return;
            var d = DataIfAny(player.userID);
            if (d == null || d.Active.Count == 0) return;
            var id = entity.net.ID.Value;
            var name = entity.ShortPrefabName;
            var pos = entity.transform.position;
            var changed = false;
            // Each container counts once per quest, so opening the same crate again doesn't.
            foreach (var kv in d.Active)
            {
                var a = kv.Value;
                if (a.Complete) continue;
                var q = FindQuest(kv.Key);
                if (q == null || q.Type != TypeLoot || !NameMatches(q.Target, name) || !AtMonument(q.Where, pos) || a.Seen.Contains(id)) continue;
                a.Seen.Add(id);
                if (a.Seen.Count > 500) a.Seen.RemoveRange(0, a.Seen.Count - 500);
                a.Progress = Math.Min(q.Amount, a.Progress + 1);
                changed = true;
                if (a.Progress >= q.Amount) Completed(player, q, a);
            }
            if (!changed) return;
            _dirty = true;
            QueueTracker(player);
        }

        private void OnItemCraftFinished(ItemCraftTask task, Item item, ItemCrafter crafter)
        {
            if (item == null || item.info == null || crafter == null) return;
            var player = crafter.owner;
            if (player == null) return;
            var shortname = item.info.shortname;
            Advance(player, TypeCraft, item.amount, player.transform.position, q => NameMatches(q.Target, shortname));
        }

        private static readonly Dictionary<string, int> CardLevels = new Dictionary<string, int>
        {
            ["green"] = 1, ["blue"] = 2, ["red"] = 3, ["any"] = 0, ["*"] = 0,
        };

        private void OnCardSwipe(CardReader reader, Keycard card, BasePlayer player)
        {
            if (reader == null || card == null || player == null) return;
            var item = card.GetItem();
            // The same test the game makes right after this hook: the right colour, in the swiper's hands, not broken.
            if (item == null || item.parent == null || item.parent.playerOwner != player || card.accessLevel != reader.accessLevel || item.conditionNormalized <= 0f) return;
            var level = reader.accessLevel;
            Advance(player, TypeSwipe, 1, reader.transform.position, q =>
            {
                int want;
                var t = (q.Target ?? "").Trim().ToLowerInvariant();
                if (t.Length == 0) return true;
                return CardLevels.TryGetValue(t, out want) && (want == 0 || want == level);
            });
        }

        /// <summary>Every couple of seconds: Visit quests (is the player at the monument?) and Deliver trackers (inventory counts).</summary>
        private void PollTick()
        {
            foreach (var player in BasePlayer.activePlayerList)
            {
                if (player == null || player.IsSleeping()) continue;
                var d = DataIfAny(player.userID);
                if (d == null || d.Active.Count == 0) continue;
                var pos = player.transform.position;
                if (HasActive(d, TypeVisit, FindQuest))
                    Advance(player, TypeVisit, 1, pos, q => AtMonument(q.Target, pos));
                foreach (var kv in d.Active)
                {
                    var q = FindQuest(kv.Key);
                    if (q == null || q.Type != TypeDeliver) continue;
                    var have = Math.Min(q.Amount, InventoryCount(player, q));
                    if (have == kv.Value.Progress) continue;
                    kv.Value.Progress = have;
                    QueueTracker(player);
                }
            }
        }

        #endregion

        #region Accept / abandon / claim

        private string Accept(BasePlayer player, Quest q, Giver g)
        {
            var d = Data(player);
            if (!Offers(g, q)) return null;
            if (StateOf(player, d, q) != State.Available) return null;
            if (d.Active.Count >= Math.Max(1, _config.MaxActive)) return Msg("TooMany", player.UserIDString, _config.MaxActive);
            d.Active[q.Id] = new ActiveQuest { Started = DateTime.UtcNow };
            _dirty = true;
            PlaySound(player, "assets/bundled/prefabs/fx/notice/item.select.fx.prefab");
            Say(player, Msg("Accepted", player.UserIDString, q.Title));
            QueueTracker(player);
            RefreshMarkers(player);
            return null;
        }

        private void Abandon(BasePlayer player, Quest q)
        {
            var d = Data(player);
            if (!d.Active.Remove(q.Id)) return;
            _dirty = true;
            Say(player, Msg("Abandoned", player.UserIDString, q.Title));
            QueueTracker(player);
            RefreshMarkers(player);
        }

        /// <summary>Pays out a finished quest. Deliver quests take the items first. Returns an error to show, or null.</summary>
        private string Claim(BasePlayer player, Quest q, Giver g)
        {
            var d = Data(player);
            ActiveQuest a;
            if (!d.Active.TryGetValue(q.Id, out a)) return null;
            if (q.Type == TypeDeliver)
            {
                var have = InventoryCount(player, q);
                if (have < q.Amount) return Msg("NotEnough", player.UserIDString, q.Amount, TargetName(q), have);
                TakeItems(player, q, q.Amount);
            }
            else if (!a.Complete) return null;

            d.Active.Remove(q.Id);
            DoneQuest done;
            if (!d.Done.TryGetValue(q.Id, out done)) d.Done[q.Id] = done = new DoneQuest();
            done.Count++;
            done.Last = DateTime.UtcNow;

            var got = new List<string>();
            foreach (var r in q.Rewards)
            {
                var def = ItemManager.FindItemDefinition(r.Item);
                if (def == null || r.Amount <= 0) continue;
                Give(player, def, r.Amount, r.Skin);
                got.Add($"{r.Amount:N0}x {ItemName(def)}");
            }
            foreach (var c in q.Commands)
            {
                if (string.IsNullOrWhiteSpace(c)) continue;
                var cmd = c.Replace("{steamid}", player.UserIDString).Replace("{name}", player.displayName.Replace("\"", ""));
                ConsoleSystem.Run(ConsoleSystem.Option.Server, cmd);
            }

            _data.History.Insert(0, new Completion { At = DateTime.UtcNow, Id = player.UserIDString, Name = player.displayName, Quest = q.Id, Title = q.Title });
            if (_data.History.Count > 300) _data.History.RemoveRange(300, _data.History.Count - 300);
            SaveData();

            PlaySound(player, "assets/prefabs/deployable/research table/effects/research-success.prefab");
            Say(player, Msg("Claimed", player.UserIDString, q.Title, got.Count > 0 ? string.Join(", ", got) : "-"));
            if (_config.Announce) Server.Broadcast(Msg("Announce", null, player.displayName, q.Title), _config.ChatPrefix, _config.ChatIcon);
            Interface.CallHook("OnSandboxQuestCompleted", player, q.Id, q.Title);
            QueueTracker(player);
            RefreshMarkers(player);
            return null;
        }

        private void TakeItems(BasePlayer player, Quest q, int amount)
        {
            var left = amount;
            foreach (var def in ItemDefs(q.Target))
            {
                if (left <= 0) break;
                var have = player.inventory.GetAmount(def.itemid);
                if (have <= 0) continue;
                var take = Math.Min(have, left);
                player.inventory.Take(null, def.itemid, take);
                left -= take;
            }
        }

        private void Give(BasePlayer player, ItemDefinition def, int amount, ulong skin)
        {
            var dropped = false;
            var left = amount;
            var stack = Math.Max(1, def.stackable);
            while (left > 0)
            {
                var n = Math.Min(stack, left);
                left -= n;
                var item = ItemManager.Create(def, n, skin);
                if (item == null) break;
                if (item.MoveToContainer(player.inventory.containerMain) || item.MoveToContainer(player.inventory.containerBelt)) continue;
                item.Drop(player.eyes.position, player.eyes.BodyForward() * 2f);
                dropped = true;
            }
            if (dropped) Say(player, Msg("Dropped", player.UserIDString));
        }

        private static string ItemName(ItemDefinition def) =>
            def.displayName != null && !string.IsNullOrEmpty(def.displayName.english) ? def.displayName.english : def.shortname;

        private static void PlaySound(BasePlayer player, string prefab)
        {
            if (player == null || player.net == null || player.net.connection == null) return;
            EffectNetwork.Send(new Effect(prefab, player, 0, Vector3.zero, Vector3.forward), player.net.connection);
        }

        #endregion

        #region Objective text

        private static readonly Dictionary<string, string> Pretty = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["scientistnpc_*"] = "scientists", ["scientist"] = "scientists", ["scientists"] = "scientists",
            ["scientistnpc_heavy"] = "heavy scientists", ["npc_tunneldweller"] = "tunnel dwellers", ["npc_underwaterdweller"] = "underwater dwellers",
            ["bear"] = "bears", ["polarbear"] = "polar bears", ["wolf"] = "wolves", ["wolf2"] = "wolves", ["boar"] = "boars", ["stag"] = "deer",
            ["chicken"] = "chickens", ["tiger"] = "tigers", ["panther"] = "panthers", ["crocodile"] = "crocodiles", ["simpleshark"] = "sharks",
            ["bradleyapc"] = "Bradley APCs", ["patrolhelicopter"] = "Patrol Helicopters", ["animal"] = "animals", ["animals"] = "animals",
            ["npc"] = "NPCs", ["player"] = "players", ["players"] = "players",
            ["crate_normal"] = "military crates", ["crate_normal_2"] = "crates", ["crate_elite"] = "elite crates", ["crate_tools"] = "tool boxes",
            ["codelockedhackablecrate"] = "locked crates", ["codelockedhackablecrate_oilrig"] = "oil rig locked crates", ["supply_drop"] = "supply drops",
            ["loot_barrel_1"] = "barrels", ["loot_barrel_2"] = "barrels", ["loot-barrel-1"] = "barrels", ["loot-barrel-2"] = "barrels", ["*barrel*"] = "barrels",
            ["crate_*"] = "crates", ["bradley_crate"] = "Bradley crates", ["heli_crate"] = "heli crates", ["crate_underwater_basic"] = "underwater crates",
            ["crate_underwater_advanced"] = "underwater crates", ["foodbox"] = "food boxes", ["crate_food_1"] = "food crates", ["crate_food_2"] = "food crates",
            ["crate_medical"] = "medical crates",
        };

        private static string TargetName(Quest q)
        {
            if (q.Type == TypeGather || q.Type == TypeCraft || q.Type == TypeDeliver)
            {
                var defs = ItemDefs(q.Target);
                if (defs.Count == 0) return q.Target;
                return string.Join(" or ", defs.Take(3).Select(ItemName)) + (defs.Count > 3 ? "..." : "");
            }
            var names = Tokens(q.Target).Select(t => { string p; return Pretty.TryGetValue(t, out p) ? p : t; }).Distinct().ToList();
            if (names.Count == 0) return "anything";
            // A mix of crate types reads better as one word than "military crates or crates or elite crates".
            if (q.Type == TypeLoot && names.Count > 2) return "crates";
            return string.Join(" or ", names);
        }

        private string ObjectiveText(Quest q, Giver g)
        {
            if (!string.IsNullOrEmpty(q.Objective)) return q.Objective;
            var n = q.Amount.ToString("N0", CultureInfo.InvariantCulture);
            var where = string.IsNullOrEmpty(q.Where) ? "" : " at " + q.Where;
            switch (q.Type)
            {
                case TypeGather: return $"Gather {n} {TargetName(q)}{where}";
                case TypeKill: return $"Kill {n} {TargetName(q)}{where}";
                case TypeLoot: return $"Loot {n} {TargetName(q)}{where}";
                case TypeCraft: return $"Craft {n} {TargetName(q)}";
                case TypeDeliver: return $"Bring {n} {TargetName(q)} to {(g != null && !string.IsNullOrEmpty(q.Giver) ? g.Name : "a quest giver")}";
                case TypeSwipe:
                {
                    var colour = string.IsNullOrEmpty(q.Target) || q.Target == "any" ? "" : q.Target.ToLowerInvariant() + " ";
                    return q.Amount == 1 ? $"Open a {colour}keycard door{where}" : $"Open {n} {colour}keycard doors{where}";
                }
                case TypeVisit: return $"Visit {q.Target}";
            }
            return $"{q.Type} {n} {q.Target}";
        }

        #endregion

        #region Quest panel

        private const string Root = "SandboxQuests.Root";
        private const string Main = Root + ".Main";

        // Players whose backdrop (the part that frees the mouse) is on screen. Redraws only replace the window inside
        // it: destroying and re-adding the backdrop made the game grab the mouse and re-centre it on every click.
        private readonly HashSet<ulong> _shown = new HashSet<ulong>();

        private void Close(BasePlayer player)
        {
            if (player == null) return;
            _shown.Remove(player.userID);
            CuiHelper.DestroyUi(player, Root);
        }


        // The first draw (the window together with the backdrop that frees the mouse) comes out blurry in game until
        // the window is drawn again, which is what clicking anything did. So the window is sent once more a moment
        // after it opens, unless something newer was drawn in the meantime.
        private readonly Dictionary<ulong, int> _draws = new Dictionary<ulong, int>();

        private void Drawn(BasePlayer player, CuiElementContainer ui, bool fresh, string root, string main)
        {
            int n;
            _draws.TryGetValue(player.userID, out n);
            _draws[player.userID] = ++n;
            if (!fresh) return;
            var again = new CuiElementContainer();
            again.AddRange(ui.Where(e => e.Name == main || e.Name != root && e.Parent != root));
            timer.Once(0.2f, () =>
            {
                int now;
                if (player == null || !player.IsConnected || !_draws.TryGetValue(player.userID, out now) || now != n) return;
                if (!StillOpen(player)) return;
                CuiHelper.DestroyUi(player, main);
                CuiHelper.AddUi(player, again);
            });
        }

        private bool StillOpen(BasePlayer player) => _shown.Contains(player.userID);

        private void Show(BasePlayer player, CuiElementContainer ui)
        {
            var fresh = _shown.Add(player.userID);
            CuiHelper.DestroyUi(player, fresh ? Root : Main);
            CuiHelper.AddUi(player, ui);
            Drawn(player, ui, fresh, Root, Main);
        }
        private const int PageSize = 8;

        private class View
        {
            public string GiverId; // null = the quest log
            public string Selected;
            public int Page;
            public string Notice;
        }

        private readonly Dictionary<ulong, View> _views = new Dictionary<ulong, View>();

        private void OpenGiver(BasePlayer player, Giver g)
        {
            if (player == null || !player.IsConnected) return;
            _views[player.userID] = new View { GiverId = g.Id };
            DrawPanel(player);
        }

        private void OpenLog(BasePlayer player)
        {
            if (player == null || !player.IsConnected) return;
            _views[player.userID] = new View { GiverId = null };
            DrawPanel(player);
        }

        [ChatCommand("quests")]
        private void CmdQuests(BasePlayer player, string command, string[] args)
        {
            if (args.Length > 0 && (args[0].Equals("list", StringComparison.OrdinalIgnoreCase) || args[0].Equals("where", StringComparison.OrdinalIgnoreCase)))
                ListLocations(player);
            else OpenLog(player);
        }

        [ConsoleCommand("sandboxquests.ui")]
        private void CcUi(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null) return;
            var action = arg.GetString(0);
            if (action == "tracker")
            {
                var d = Data(player);
                d.HideTracker = !d.HideTracker;
                _dirty = true;
                DrawTracker(player);
                View tv;
                if (_views.TryGetValue(player.userID, out tv)) DrawPanel(player);
                return;
            }
            View v;
            if (!_views.TryGetValue(player.userID, out v))
            {
                Close(player);
                return;
            }
            var g = v.GiverId != null ? FindGiver(v.GiverId) : null;
            var q = FindQuest(arg.GetString(1));
            v.Notice = null;
            switch (action)
            {
                case "close":
                    _views.Remove(player.userID);
                    Close(player);
                    return;
                case "select":
                    if (q != null) v.Selected = q.Id;
                    break;
                case "page":
                    v.Page = Math.Max(0, arg.GetInt(1));
                    break;
                case "accept":
                    if (q != null && g != null) v.Notice = Accept(player, q, g);
                    break;
                case "abandon":
                    if (q != null) Abandon(player, q);
                    break;
                case "claim":
                    if (q != null && CanClaimHere(q, g)) v.Notice = Claim(player, q, g);
                    break;
            }
            DrawPanel(player);
        }

        /// <summary>From the quest log only when rewards don't need a giver; at a giver, any giver unless the quest names one.</summary>
        private bool CanClaimHere(Quest q, Giver g)
        {
            if (g == null) return !_config.ReturnToGiver;
            return string.IsNullOrEmpty(q.Giver) || string.Equals(q.Giver, g.Id, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>The quests listed in the panel: a giver's offers (and anything ready to hand in), or the player's active quests.</summary>
        private List<Quest> Listed(BasePlayer player, PlayerData d, Giver g)
        {
            if (g == null)
                return d.Active.Keys.Select(FindQuest).Where(q => q != null).ToList();
            return _config.Quests.Where(q => Offers(g, q) || d.Active.ContainsKey(q.Id))
                .OrderBy(q => SortKey(StateOf(player, d, q))).ToList();
        }

        private static int SortKey(State s)
        {
            switch (s)
            {
                case State.Ready: return 0;
                case State.Active: return 1;
                case State.Available: return 2;
                case State.Cooldown: return 3;
                case State.Locked: return 4;
                default: return 5;
            }
        }

        private void DrawPanel(BasePlayer player)
        {
            View v;
            if (!_views.TryGetValue(player.userID, out v)) return;
            var id = player.UserIDString;
            var d = Data(player);
            var g = v.GiverId != null ? FindGiver(v.GiverId) : null;
            if (v.GiverId != null && g == null) { _views.Remove(player.userID); Close(player); return; }
            var quests = Listed(player, d, g);
            if (v.Selected == null || !quests.Any(q => q.Id == v.Selected)) v.Selected = quests.Count > 0 ? quests[0].Id : null;
            var pages = Math.Max(1, (quests.Count + PageSize - 1) / PageSize);
            v.Page = Math.Min(v.Page, pages - 1);

            var ui = new CuiElementContainer();
            if (!_shown.Contains(player.userID))
                ui.Add(new CuiPanel
                {
                    Image = { Color = "0 0 0 0.55" },
                    RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" },
                    CursorEnabled = true,
                }, "Overlay", Root);
            ui.Add(new CuiPanel
            {
                Image = { Color = "0.10 0.10 0.10 0.98" },
                RectTransform = { AnchorMin = "0.5 0.5", AnchorMax = "0.5 0.5", OffsetMin = "-390 -230", OffsetMax = "390 230" },
            }, Root, Main);

            // header
            Box(ui, Main, "0.07 0.07 0.07 1", 0, 414, 780, 460);
            Box(ui, Main, "0.94 0.69 0.29 1", 0, 412, 780, 414);
            Label(ui, Main, g != null ? $"{g.Name.ToUpperInvariant()}  <color=#9a9a9a>· {Msg("Title", id)}</color>" : Msg("LogTitle", id), 18, 18, 414, 560, 460);
            Button(ui, Main, d.HideTracker ? Msg("Show", id) : Msg("Hide", id), 11, "sandboxquests.ui tracker", "0.25 0.25 0.25 0.95", 590, 424, 730, 450);
            Button(ui, Main, "X", 16, "sandboxquests.ui close", "0.70 0.22 0.17 0.95", 740, 422, 772, 452);

            // quest list
            Box(ui, Main, "0.07 0.07 0.07 1", 12, 12, 272, 402);
            if (quests.Count == 0)
            {
                Label(ui, Main, g != null ? Msg("NoQuests", id, g.Name) : Msg("NoActive", id), 13, 24, 300, 262, 392, TextAnchor.UpperLeft, "0.75 0.75 0.75 1");
            }
            var y = 402f;
            foreach (var q in quests.Skip(v.Page * PageSize).Take(PageSize))
            {
                var state = StateOf(player, d, q);
                var selected = q.Id == v.Selected;
                y -= 44;
                var row = ButtonPanel(ui, Main, $"sandboxquests.ui select {q.Id}", selected ? "0.24 0.20 0.12 1" : "0.13 0.13 0.13 1", 16, y, 268, y + 40);
                if (selected) Box(ui, row, "0.94 0.69 0.29 1", 0, 0, 3, 40);
                Label(ui, row, q.Title, 13, 12, 20, 180, 38, TextAnchor.MiddleLeft, state == State.Done || state == State.Locked ? "0.6 0.6 0.6 1" : "1 1 1 0.95");
                Label(ui, row, TypeLabel(q.Type), 10, 12, 2, 180, 20, TextAnchor.MiddleLeft, "0.6 0.6 0.6 1");
                string badge, color;
                StateBadge(player, d, q, state, id, out badge, out color);
                Label(ui, row, badge, 11, 150, 0, 244, 40, TextAnchor.MiddleRight, color);
            }
            if (pages > 1)
            {
                if (v.Page > 0) Button(ui, Main, "<", 14, $"sandboxquests.ui page {v.Page - 1}", "0.2 0.2 0.2 1", 16, 16, 60, 40);
                Label(ui, Main, $"{v.Page + 1} / {pages}", 11, 60, 16, 228, 40, TextAnchor.MiddleCenter, "0.7 0.7 0.7 1");
                if (v.Page < pages - 1) Button(ui, Main, ">", 14, $"sandboxquests.ui page {v.Page + 1}", "0.2 0.2 0.2 1", 228, 16, 268, 40);
            }

            // details
            var sel = quests.FirstOrDefault(q => q.Id == v.Selected);
            if (sel != null) DrawDetails(ui, player, d, g, sel, v);
            else Label(ui, Main, Msg("Pick", id), 14, 290, 200, 770, 260, TextAnchor.MiddleCenter, "0.7 0.7 0.7 1");

            Show(player, ui);
        }

        private void DrawDetails(CuiElementContainer ui, BasePlayer player, PlayerData d, Giver g, Quest q, View v)
        {
            var id = player.UserIDString;
            var state = StateOf(player, d, q);
            Label(ui, Main, q.Title, 20, 290, 366, 770, 402);
            Label(ui, Main, q.Description, 13, 290, 290, 770, 366, TextAnchor.UpperLeft, "0.82 0.82 0.82 1");

            // objective + progress
            Label(ui, Main, Msg("Objective", id), 11, 290, 266, 770, 286, TextAnchor.MiddleLeft, "0.94 0.69 0.29 1");
            Label(ui, Main, ObjectiveText(q, g), 14, 290, 242, 770, 266);
            var place = LocationLine(player, d, q);
            if (place != null) Label(ui, Main, place, 11, 400, 266, 770, 286, TextAnchor.MiddleRight, "0.7 0.7 0.7 1");
            ActiveQuest a;
            if (d.Active.TryGetValue(q.Id, out a))
            {
                var p = ProgressOf(player, q, a);
                var frac = q.Amount > 0 ? Mathf.Clamp01((float)p / q.Amount) : 1f;
                Box(ui, Main, "0.2 0.2 0.2 1", 290, 226, 680, 236);
                if (frac > 0) Box(ui, Main, state == State.Ready ? "0.45 0.75 0.3 1" : "0.94 0.69 0.29 1", 290, 226, 290 + 390 * frac, 236);
                Label(ui, Main, $"{p:N0} / {q.Amount:N0}{(q.Type == TypeDeliver ? " " + Msg("Inventory", id) : "")}", 11, 686, 220, 770, 242, TextAnchor.MiddleRight, "0.8 0.8 0.8 1");
            }
            else if (state == State.Locked)
                Label(ui, Main, Msg("LockedInfo", id, FindQuest(q.Requires)?.Title ?? q.Requires), 12, 290, 220, 770, 240, TextAnchor.MiddleLeft, "0.8 0.5 0.4 1");
            else if (state == State.Cooldown)
                Label(ui, Main, Msg("CooldownInfo", id, FormatSpan(CooldownLeft(q, d.Done[q.Id]))), 12, 290, 220, 770, 240, TextAnchor.MiddleLeft, "0.7 0.7 0.7 1");
            else if (state == State.Done)
                Label(ui, Main, Msg("DoneInfo", id), 12, 290, 220, 770, 240, TextAnchor.MiddleLeft, "0.45 0.75 0.3 1");

            // rewards
            Label(ui, Main, Msg("Rewards", id), 11, 290, 192, 770, 212, TextAnchor.MiddleLeft, "0.94 0.69 0.29 1");
            var x = 290f;
            foreach (var r in q.Rewards.Take(7))
            {
                var def = ItemManager.FindItemDefinition(r.Item);
                if (def == null) continue;
                Box(ui, Main, "0.16 0.16 0.16 1", x, 108, x + 64, 188);
                ui.Add(new CuiElement
                {
                    Parent = Main,
                    Components =
                    {
                        new CuiImageComponent { ItemId = def.itemid, SkinId = r.Skin },
                        new CuiRectTransformComponent { AnchorMin = "0 0", AnchorMax = "0 0", OffsetMin = $"{x + 8} {128}", OffsetMax = $"{x + 56} {176}" },
                    },
                });
                Label(ui, Main, "x" + r.Amount.ToString("N0", CultureInfo.InvariantCulture), 11, x + 2, 108, x + 62, 126, TextAnchor.MiddleRight);
                x += 70;
            }
            if (q.Rewards.Count == 0 && q.Commands.Count == 0)
                Label(ui, Main, "-", 13, 290, 150, 400, 180, TextAnchor.MiddleLeft, "0.6 0.6 0.6 1");
            else if (q.Rewards.Count == 0)
                Label(ui, Main, "A special reward", 13, 290, 150, 600, 180, TextAnchor.MiddleLeft, "0.8 0.8 0.8 1");

            if (!string.IsNullOrEmpty(v.Notice))
                Label(ui, Main, v.Notice, 12, 290, 70, 770, 100, TextAnchor.MiddleLeft, "0.9 0.45 0.35 1");

            // buttons
            switch (state)
            {
                case State.Available:
                    if (g != null) Button(ui, Main, Msg("Accept", id), 15, $"sandboxquests.ui accept {q.Id}", "0.42 0.63 0.24 0.95", 600, 18, 770, 62);
                    break;
                case State.Active:
                    Button(ui, Main, Msg("Abandon", id), 12, $"sandboxquests.ui abandon {q.Id}", "0.35 0.18 0.15 0.95", 290, 18, 410, 52);
                    break;
                case State.Ready:
                    Button(ui, Main, Msg("Abandon", id), 12, $"sandboxquests.ui abandon {q.Id}", "0.35 0.18 0.15 0.95", 290, 18, 410, 52);
                    if (CanClaimHere(q, g))
                        Button(ui, Main, q.Type == TypeDeliver ? Msg("HandOver", id) : Msg("Claim", id), 15, $"sandboxquests.ui claim {q.Id}", "0.42 0.63 0.24 0.95", 600, 18, 770, 62);
                    else
                    {
                        var owner = !string.IsNullOrEmpty(q.Giver) ? FindGiver(q.Giver) : null;
                        Label(ui, Main, owner != null ? Msg("ReturnToName", id, owner.Name) : Msg("ClaimAtGiver", id), 12, 420, 18, 770, 52, TextAnchor.MiddleRight, "0.45 0.75 0.3 1");
                    }
                    break;
            }
        }

        private void StateBadge(BasePlayer player, PlayerData d, Quest q, State state, string id, out string text, out string color)
        {
            switch (state)
            {
                case State.Active:
                {
                    var a = d.Active[q.Id];
                    text = Msg("StateActive", id, q.Amount > 0 ? ProgressOf(player, q, a) * 100 / q.Amount : 0);
                    color = "0.94 0.69 0.29 1";
                    return;
                }
                case State.Ready: text = Msg("StateReady", id); color = "0.55 0.85 0.4 1"; return;
                case State.Done: text = Msg("StateDone", id); color = "0.5 0.5 0.5 1"; return;
                case State.Locked: text = Msg("StateLocked", id); color = "0.7 0.4 0.35 1"; return;
                case State.Cooldown: text = Msg("StateCooldown", id, FormatSpan(CooldownLeft(q, d.Done[q.Id]))); color = "0.6 0.6 0.6 1"; return;
                default: text = Msg("StateAvailable", id); color = "0.45 0.7 0.95 1"; return;
            }
        }

        private static string TypeLabel(string type)
        {
            switch (type)
            {
                case TypeGather: return "GATHER";
                case TypeKill: return "HUNT";
                case TypeLoot: return "LOOT";
                case TypeCraft: return "CRAFT";
                case TypeDeliver: return "DELIVER";
                case TypeSwipe: return "KEYCARD";
                case TypeVisit: return "EXPLORE";
                default: return (type ?? "").ToUpperInvariant();
            }
        }

        private static string FormatSpan(TimeSpan t) =>
            t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes}m" : $"{Math.Max(1, (int)Math.Ceiling(t.TotalMinutes))}m";

        #endregion

        #region Locations (map markers and /quests list)

        private bool _unloading;

        private class Spot
        {
            public Vector3 Pos;
            public string Name;
        }

        /// <summary>The monument a quest sends you to (its Visit target or its "Where"), nearest first if several match.</summary>
        private static Spot QuestSpot(Quest q, Vector3 from)
        {
            var name = q.Type == TypeVisit ? q.Target : q.Where;
            if (string.IsNullOrEmpty(name)) return null;
            var monuments = TerrainMeta.Path != null ? TerrainMeta.Path.Monuments : null;
            if (monuments == null) return null;
            Spot best = null;
            var bestD = float.MaxValue;
            foreach (var m in monuments)
            {
                if (m == null) continue;
                var mName = NameOf(m);
                if (mName.IndexOf(name, StringComparison.OrdinalIgnoreCase) < 0) continue;
                var center = m.transform.TransformPoint(m.Bounds.center);
                var dist = (center - from).sqrMagnitude;
                if (dist < bestD) { bestD = dist; best = new Spot { Pos = center, Name = mName }; }
            }
            return best;
        }

        private static Vector3 PosOf(Giver g) => new Vector3(g.X, g.Y, g.Z);

        private static string Distance(Vector3 from, Vector3 to)
        {
            var d = Vector3.Distance(new Vector3(from.x, 0, from.z), new Vector3(to.x, 0, to.z));
            return d >= 1000f ? (d / 1000f).ToString("0.0", CultureInfo.InvariantCulture) + " km" : Mathf.RoundToInt(d) + " m";
        }

        private static readonly string[] Directions = { "north", "north-east", "east", "south-east", "south", "south-west", "west", "north-west" };

        private static string Direction(Vector3 from, Vector3 to)
        {
            var dx = to.x - from.x;
            var dz = to.z - from.z;
            if (dx * dx + dz * dz < 25f) return "here";
            var angle = Mathf.Repeat(Mathf.Atan2(dx, dz) * Mathf.Rad2Deg, 360f);
            return Directions[Mathf.RoundToInt(angle / 45f) % 8];
        }

        /// <summary>For one giver: how many quests this player could pick up there and how many they could hand in.</summary>
        private void GiverCounts(BasePlayer player, PlayerData d, Giver g, out int fresh, out int ready)
        {
            fresh = ready = 0;
            foreach (var q in _config.Quests)
            {
                if (d != null && d.Active.ContainsKey(q.Id))
                {
                    if (CanClaimHere(q, g) && IsReady(player, q, d.Active[q.Id])) ready++;
                    continue;
                }
                if (Offers(g, q) && (d == null || StateOf(player, d, q) == State.Available)) fresh++;
            }
        }

        /// <summary>"Launch Site · K4 · 1.2 km south" for a quest's place, or where to hand it in once it's done.</summary>
        private string LocationLine(BasePlayer player, PlayerData d, Quest q)
        {
            var id = player.UserIDString;
            var from = player.transform.position;
            ActiveQuest a;
            if (d.Active.TryGetValue(q.Id, out a) && IsReady(player, q, a) && _config.ReturnToGiver)
            {
                var giver = !string.IsNullOrEmpty(q.Giver) ? FindGiver(q.Giver) : NearestGiverTo(from);
                if (giver != null && OnThisMap(giver))
                    return Msg("HandInAt", id, giver.Name, Grid(PosOf(giver)), Distance(from, PosOf(giver)), Direction(from, PosOf(giver)));
                return null;
            }
            var spot = QuestSpot(q, from);
            return spot == null ? null : Msg("Location", id, spot.Name, Grid(spot.Pos), Distance(from, spot.Pos), Direction(from, spot.Pos));
        }

        private Giver NearestGiverTo(Vector3 from)
        {
            Giver best = null;
            var bestD = float.MaxValue;
            foreach (var g in _givers.Givers)
            {
                if (!OnThisMap(g)) continue;
                var dist = (PosOf(g) - from).sqrMagnitude;
                if (dist < bestD) { bestD = dist; best = g; }
            }
            return best;
        }

        private void ListLocations(BasePlayer player)
        {
            var id = player.UserIDString;
            var from = player.transform.position;
            var d = Data(player);
            var lines = new List<string>();

            var givers = _givers.Givers.Where(OnThisMap).OrderBy(g => (PosOf(g) - from).sqrMagnitude).ToList();
            if (givers.Count == 0) lines.Add(Msg("ListNoGivers", id));
            else
            {
                lines.Add(Msg("ListGivers", id));
                foreach (var g in givers)
                {
                    int fresh, ready;
                    GiverCounts(player, d, g, out fresh, out ready);
                    var extra = new List<string>();
                    if (ready > 0) extra.Add("<color=#8fd16a>" + Msg("GiverReady", id, ready) + "</color>");
                    if (fresh > 0) extra.Add("<color=#7ab4f0>" + Msg("GiverNew", id, fresh) + "</color>");
                    lines.Add(Msg("ListGiver", id, g.Name, Grid(PosOf(g)), Distance(from, PosOf(g)), Direction(from, PosOf(g)),
                        extra.Count > 0 ? " (" + string.Join(", ", extra) + ")" : ""));
                }
            }

            var active = d.Active.Select(kv => FindQuest(kv.Key)).Where(q => q != null).ToList();
            if (active.Count == 0) lines.Add(Msg("ListNoActive", id));
            else
            {
                lines.Add(Msg("ListActive", id));
                foreach (var q in active)
                {
                    var a = d.Active[q.Id];
                    var where = LocationLine(player, d, q);
                    var status = IsReady(player, q, a) ? "<color=#8fd16a>" + Msg("StateReady", id) + "</color>" : $"{ProgressOf(player, q, a):N0}/{q.Amount:N0}";
                    lines.Add(Msg("ListQuest", id, q.Title, status + (where != null ? " · " + where : "")));
                }
            }
            lines.Add(Msg("ListHint", id));
            Say(player, string.Join("\n", lines));
        }

        private void RefreshMarkers(BasePlayer player)
        {
            if (player != null && player.IsConnected && (_config.MarkGivers || _config.MarkQuests)) player.SendMarkersToClient();
        }

        private void RefreshAllMarkers()
        {
            foreach (var p in BasePlayer.activePlayerList) RefreshMarkers(p);
        }

        // Added to the markers the game sends each player, so they show on that player's map and compass
        // without touching the markers they placed themselves.
        private void OnPlayerMarkersSend(BasePlayer player, ProtoBuf.MapNoteList list)
        {
            if (_unloading || player == null || list == null || list.notes == null) return;
            var d = DataIfAny(player.userID);
            if (_config.MarkGivers)
            {
                foreach (var g in _givers.Givers)
                {
                    if (!OnThisMap(g)) continue;
                    int fresh, ready;
                    GiverCounts(player, d, g, out fresh, out ready);
                    // Hover style already shows every giver; a pin only shows where this player has something to do:
                    // green "?" for a new quest, yellow "!" for a quest to hand in.
                    if (HoverMarkers && ready == 0 && fresh == 0) continue;
                    var label = ready > 0 ? "! " + g.Name : fresh > 0 ? "? " + g.Name : g.Name;
                    list.notes.Add(new ProtoBuf.MapNote
                    {
                        noteType = (int)BasePlayer.MapNoteType.PointOfInterest,
                        worldPosition = PosOf(g) + Vector3.up * 2f,
                        label = label,
                        colourIndex = ready > 0 ? 0 : fresh > 0 ? 2 : 5, // yellow, green, cyan
                        icon = 0,
                    });
                }
            }
            if (_config.MarkQuests && d != null)
            {
                foreach (var kv in d.Active)
                {
                    if (kv.Value.Complete) continue;
                    var q = FindQuest(kv.Key);
                    if (q == null || (q.Type == TypeDeliver && IsReady(player, q, kv.Value))) continue;
                    var spot = QuestSpot(q, player.transform.position);
                    if (spot == null) continue;
                    list.notes.Add(new ProtoBuf.MapNote
                    {
                        noteType = (int)BasePlayer.MapNoteType.PointOfInterest,
                        worldPosition = spot.Pos,
                        label = q.Title,
                        colourIndex = 1, // blue, so it isn't mistaken for a quest giver
                        icon = 0,
                    });
                }
            }
        }

        #endregion

        #region Tracker HUD

        private const string TrackerUi = "SandboxQuests.Tracker";
        private readonly HashSet<ulong> _trackerQueued = new HashSet<ulong>();

        /// <summary>Redraws at most twice a second per player, however fast progress arrives.</summary>
        private void QueueTracker(BasePlayer player)
        {
            if (player == null || !_trackerQueued.Add(player.userID)) return;
            var id = player.userID;
            timer.Once(0.5f, () =>
            {
                _trackerQueued.Remove(id);
                var p = BasePlayer.FindByID(id);
                if (p != null)
                {
                    DrawTracker(p);
                    View v;
                    if (_views.TryGetValue(id, out v)) DrawPanel(p);
                }
            });
        }

        private void DrawTracker(BasePlayer player)
        {
            if (player == null || !player.IsConnected) return;
            CuiHelper.DestroyUi(player, TrackerUi);
            var d = DataIfAny(player.userID);
            if (!_config.Tracker || d == null || d.HideTracker || d.Active.Count == 0) return;
            var id = player.UserIDString;
            var rows = d.Active.Select(kv => new KeyValuePair<Quest, ActiveQuest>(FindQuest(kv.Key), kv.Value)).Where(kv => kv.Key != null).ToList();
            if (rows.Count == 0) return;

            float ox, oy;
            ParsePair(_config.TrackerOffset, -262, 150, out ox, out oy);
            const float width = 250f;
            const float rowH = 44f;
            var height = 22 + rows.Count * rowH + 4;
            var ui = new CuiElementContainer();
            ui.Add(new CuiPanel
            {
                Image = { Color = "0.08 0.08 0.08 0.72" },
                RectTransform = { AnchorMin = _config.TrackerAnchor, AnchorMax = _config.TrackerAnchor, OffsetMin = $"{ox} {oy - height}", OffsetMax = $"{ox + width} {oy}" },
            }, "Hud", TrackerUi);
            Box(ui, TrackerUi, "0.94 0.69 0.29 0.9", 0, height - 2, width, height);
            Label(ui, TrackerUi, Msg("TrackerHeader", id), 11, 8, height - 22, width - 8, height - 2, TextAnchor.MiddleLeft, "0.94 0.69 0.29 1");
            var y = height - 22;
            foreach (var kv in rows)
            {
                var q = kv.Key;
                var a = kv.Value;
                var ready = IsReady(player, q, a);
                var p = ProgressOf(player, q, a);
                y -= rowH;
                Label(ui, TrackerUi, q.Title, 12, 8, y + 24, width - 8, y + 42, TextAnchor.MiddleLeft, ready ? "0.55 0.85 0.4 1" : "1 1 1 0.95");
                if (ready)
                {
                    var giver = !string.IsNullOrEmpty(q.Giver) ? FindGiver(q.Giver) : null;
                    var text = _config.ReturnToGiver ? (giver != null ? Msg("ReturnToName", id, giver.Name) : Msg("ReturnTo", id)) : Msg("StateReady", id);
                    Label(ui, TrackerUi, text, 10, 8, y + 6, width - 8, y + 24, TextAnchor.MiddleLeft, "0.75 0.9 0.65 1");
                }
                else
                {
                    var frac = q.Amount > 0 ? Mathf.Clamp01((float)p / q.Amount) : 0f;
                    Label(ui, TrackerUi, ObjectiveText(q, null), 10, 8, y + 10, width - 70, y + 25, TextAnchor.MiddleLeft, "0.75 0.75 0.75 1");
                    Label(ui, TrackerUi, $"{p:N0}/{q.Amount:N0}", 10, width - 70, y + 10, width - 8, y + 25, TextAnchor.MiddleRight, "0.85 0.85 0.85 1");
                    Box(ui, TrackerUi, "0.25 0.25 0.25 1", 8, y + 4, width - 8, y + 8);
                    if (frac > 0) Box(ui, TrackerUi, "0.94 0.69 0.29 1", 8, y + 4, 8 + (width - 16) * frac, y + 8);
                }
            }
            CuiHelper.AddUi(player, ui);
        }

        private static void ParsePair(string s, float dx, float dy, out float x, out float y)
        {
            var p = (s ?? "").Split(' ');
            if (p.Length < 2 || !float.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out x)
                || !float.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out y)) { x = dx; y = dy; }
        }

        #endregion

        #region Drawing helpers (pixels from the parent's bottom-left)

        private static void Box(CuiElementContainer c, string parent, string color, float x0, float y0, float x1, float y1)
        {
            c.Add(new CuiPanel
            {
                Image = { Color = color },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "0 0", OffsetMin = $"{x0} {y0}", OffsetMax = $"{x1} {y1}" },
            }, parent);
        }

        private static void Label(CuiElementContainer c, string parent, string text, int size, float x0, float y0, float x1, float y1,
            TextAnchor align = TextAnchor.MiddleLeft, string color = "1 1 1 0.95")
        {
            c.Add(new CuiLabel
            {
                Text = { Text = text ?? "", FontSize = size, Align = align, Color = color, Font = "robotocondensed-bold.ttf" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "0 0", OffsetMin = $"{x0} {y0}", OffsetMax = $"{x1} {y1}" },
            }, parent);
        }

        private static void Button(CuiElementContainer c, string parent, string text, int size, string command, string color, float x0, float y0, float x1, float y1)
        {
            c.Add(new CuiButton
            {
                Button = { Command = command, Color = color },
                Text = { Text = text, FontSize = size, Align = TextAnchor.MiddleCenter, Color = "1 1 1 0.95", Font = "robotocondensed-bold.ttf" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "0 0", OffsetMin = $"{x0} {y0}", OffsetMax = $"{x1} {y1}" },
            }, parent);
        }

        /// <summary>A clickable panel that other elements can sit inside; returns its name.</summary>
        private static string ButtonPanel(CuiElementContainer c, string parent, string command, string color, float x0, float y0, float x1, float y1)
        {
            return c.Add(new CuiButton
            {
                Button = { Command = command, Color = color },
                Text = { Text = "" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "0 0", OffsetMin = $"{x0} {y0}", OffsetMax = $"{x1} {y1}" },
            }, parent);
        }

        #endregion

        #region Manager commands (RCON)

        private static bool IsAdminArg(ConsoleSystem.Arg arg) => arg.Connection == null || arg.Connection.authLevel >= 2;

        private static BasePlayer FindPlayer(string idOrName)
        {
            ulong id;
            if (ulong.TryParse(idOrName, out id)) return BasePlayer.FindByID(id);
            return BasePlayer.activePlayerList.FirstOrDefault(p => p.displayName.IndexOf(idOrName ?? "", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        /// <summary>sandboxquests.summary [count] - quest givers, players' quests and recent completions for the manager.</summary>
        [ConsoleCommand("sandboxquests.summary")]
        private void CcSummary(ConsoleSystem.Arg arg)
        {
            if (!IsAdminArg(arg)) return;
            var count = Math.Max(1, Math.Min(300, arg.GetInt(0, 100)));
            var online = new HashSet<ulong>(BasePlayer.activePlayerList.Select(p => p.userID.Get()));
            arg.ReplyWith(JsonConvert.SerializeObject(new
            {
                version = Version.ToString(),
                returnToGiver = _config.ReturnToGiver,
                looks = Looks,
                givers = _givers.Givers.Select(g => new
                {
                    id = g.Id, name = g.Name, look = string.IsNullOrEmpty(g.Look) ? _config.DefaultLook : g.Look,
                    grid = Grid(new Vector3(g.X, g.Y, g.Z)), x = Math.Round(g.X, 1), y = Math.Round(g.Y, 1), z = Math.Round(g.Z, 1),
                    thisMap = OnThisMap(g), spawned = _spawned.ContainsKey(g.Id) && _spawned[g.Id] != null && !_spawned[g.Id].IsDestroyed,
                }),
                players = _data.Players.Select(kv => new
                {
                    id = kv.Key.ToString(), name = kv.Value.Name, online = online.Contains(kv.Key),
                    active = kv.Value.Active.Select(a =>
                    {
                        var q = FindQuest(a.Key);
                        return new { quest = a.Key, title = q != null ? q.Title : a.Key, progress = a.Value.Progress, goal = q != null ? q.Amount : 0, complete = a.Value.Complete };
                    }),
                    done = kv.Value.Done.Select(x => new { quest = x.Key, count = x.Value.Count, last = x.Value.Last }),
                }),
                history = _data.History.Take(count).Select(h => new { at = h.At, id = h.Id, name = h.Name, quest = h.Quest, title = h.Title }),
            }));
        }

        /// <summary>sandboxquests.giver.add &lt;steamid|name&gt; &lt;giver name...&gt; - place a quest giver where that player stands, facing them.</summary>
        [ConsoleCommand("sandboxquests.giver.add")]
        private void CcGiverAdd(ConsoleSystem.Arg arg)
        {
            if (!IsAdminArg(arg)) return;
            var player = FindPlayer(arg.GetString(0));
            if (player == null || !player.IsConnected) { arg.ReplyWith("That player isn't online."); return; }
            var name = arg.Args != null && arg.Args.Length > 1 ? string.Join(" ", arg.Args.Skip(1)) : "Quest Giver";
            var g = AddGiver(player, name);
            Say(player, $"A quest giver, <color=#f0b04a>{g.Name}</color>, was placed where you're standing. Step back to talk to them.");
            arg.ReplyWith($"Placed {g.Name} ({g.Id}) at {Grid(player.transform.position)}.");
        }

        /// <summary>sandboxquests.giver.rename &lt;id&gt; &lt;name...&gt;</summary>
        [ConsoleCommand("sandboxquests.giver.rename")]
        private void CcGiverRename(ConsoleSystem.Arg arg)
        {
            if (!IsAdminArg(arg)) return;
            var g = FindGiver(arg.GetString(0));
            var name = arg.Args != null && arg.Args.Length > 1 ? string.Join(" ", arg.Args.Skip(1)).Trim() : "";
            if (g == null || name.Length == 0) { arg.ReplyWith("Usage: sandboxquests.giver.rename <id> <name>"); return; }
            g.Name = name;
            SaveGivers();
            NPCTalking npc;
            if (_spawned.TryGetValue(g.Id, out npc) && npc != null) npc.displayName = name;
            SpawnShopMarker(g);
            RefreshAllMarkers();
            arg.ReplyWith($"Renamed {g.Id} to {name}.");
        }

        /// <summary>sandboxquests.giver.look &lt;id&gt; &lt;look&gt;</summary>
        [ConsoleCommand("sandboxquests.giver.look")]
        private void CcGiverLook(ConsoleSystem.Arg arg)
        {
            if (!IsAdminArg(arg)) return;
            var g = FindGiver(arg.GetString(0));
            var look = arg.GetString(1).ToLowerInvariant();
            if (g == null || !Looks.Contains(look)) { arg.ReplyWith("Usage: sandboxquests.giver.look <id> <" + string.Join("|", Looks) + ">"); return; }
            g.Look = look;
            SaveGivers();
            SpawnGiver(g);
            arg.ReplyWith($"{g.Name} now uses the {look} look.");
        }

        /// <summary>sandboxquests.giver.outfit &lt;id&gt; &lt;steamid|name|clear&gt; - dress a giver in what that player wears, or reset it.</summary>
        [ConsoleCommand("sandboxquests.giver.outfit")]
        private void CcGiverOutfit(ConsoleSystem.Arg arg)
        {
            if (!IsAdminArg(arg)) return;
            var g = FindGiver(arg.GetString(0));
            var who = arg.GetString(1);
            if (g == null || string.IsNullOrEmpty(who)) { arg.ReplyWith("Usage: sandboxquests.giver.outfit <id> <steamid|name|clear>"); return; }
            if (who.ToLowerInvariant() == "clear") { arg.ReplyWith(SetOutfit(g, null, true)); return; }
            var p = BasePlayer.Find(who);
            if (p == null) { arg.ReplyWith($"No online player matches \"{who}\"."); return; }
            arg.ReplyWith(SetOutfit(g, p, false));
        }

        /// <summary>sandboxquests.giver.move &lt;id&gt; &lt;steamid|name&gt; - move a quest giver to where that player stands.</summary>
        [ConsoleCommand("sandboxquests.giver.move")]
        private void CcGiverMove(ConsoleSystem.Arg arg)
        {
            if (!IsAdminArg(arg)) return;
            var g = FindGiver(arg.GetString(0));
            var player = FindPlayer(arg.GetString(1));
            if (g == null || player == null) { arg.ReplyWith("Usage: sandboxquests.giver.move <id> <player>"); return; }
            var p = player.transform.position;
            g.X = p.x; g.Y = p.y; g.Z = p.z;
            g.Yaw = Mathf.Repeat((player.eyes != null ? player.eyes.rotation.eulerAngles.y : player.transform.eulerAngles.y) + 180f, 360f);
            g.Map = MapKey();
            SaveGivers();
            SpawnGiver(g);
            RefreshAllMarkers();
            arg.ReplyWith($"Moved {g.Name} to {Grid(p)}.");
        }

        /// <summary>sandboxquests.giver.remove &lt;id&gt;</summary>
        [ConsoleCommand("sandboxquests.giver.remove")]
        private void CcGiverRemove(ConsoleSystem.Arg arg)
        {
            if (!IsAdminArg(arg)) return;
            var g = FindGiver(arg.GetString(0));
            if (g == null) { arg.ReplyWith("No quest giver with that id."); return; }
            RemoveGiver(g);
            arg.ReplyWith($"Removed {g.Name}.");
        }

        /// <summary>sandboxquests.reset &lt;steamid&gt; [quest id] - clear a player's progress (all quests, or one).</summary>
        [ConsoleCommand("sandboxquests.reset")]
        private void CcReset(ConsoleSystem.Arg arg)
        {
            if (!IsAdminArg(arg)) return;
            ulong id;
            PlayerData d;
            if (!ulong.TryParse(arg.GetString(0), out id) || !_data.Players.TryGetValue(id, out d)) { arg.ReplyWith("No quest data for that Steam ID."); return; }
            var quest = arg.GetString(1);
            if (string.IsNullOrEmpty(quest))
            {
                d.Active.Clear();
                d.Done.Clear();
            }
            else
            {
                d.Active.Remove(quest);
                d.Done.Remove(quest);
            }
            SaveData();
            var online = BasePlayer.FindByID(id);
            if (online != null) DrawTracker(online);
            arg.ReplyWith(string.IsNullOrEmpty(quest) ? $"Reset all quests for {d.Name}." : $"Reset {quest} for {d.Name}.");
        }

        #endregion
    }
}
