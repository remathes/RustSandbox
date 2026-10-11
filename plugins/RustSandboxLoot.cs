// Rust Sandbox — Loot
// Custom loot for each crate, barrel and NPC body type. Anything you don't set up keeps Rust's own (vanilla) loot.
// For each type you can Replace its loot or Add to it: list items with an amount range and the chance (%)
// each one shows up, plus the fewest and most items it holds. Also: a loot multiplier, scrap per crate, and how
// many crates Patrol Heli and Bradley drop.
// The manager's Loot page edits the config and shows Rust's own loot table for every crate, with the chance
// of each item, worked out from the game's loot tables (sandboxloot.export).

using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Oxide.Core;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("Rust Sandbox Loot", "RustSandbox", "1.1.0")]
    [Description("Custom loot tables per crate type, with Rust's own loot as the default.")]
    public class RustSandboxLoot : RustPlugin
    {
        #region Config

        private class LootItem
        {
            public string Item = "";
            public int Min = 1;
            public int Max = 1;
            [JsonProperty("Chance")] public double Chance = 100;
            public bool Blueprint;
            public ulong Skin;
        }

        private class CrateConfig
        {
            [JsonProperty("Mode")] public string Mode = "Vanilla";
            public int MinItems = 1;
            public int MaxItems = 6;
            public int Scrap = -1;   // -1 = Rust's own amount
            [JsonProperty("Items", ObjectCreationHandling = ObjectCreationHandling.Replace)] public List<LootItem> Items = new List<LootItem>();
        }

        private class PluginConfig
        {
            public bool Enabled = true;
            [JsonProperty("KeepScrap")] public bool KeepScrap = true;
            public float LootMultiplier = 1f;   // stackable items (and scrap) in crates and NPC bodies
            public int HeliCrates = -1;         // -1 = Rust's own
            public int BradleyCrates = -1;
            [JsonProperty("Crates", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public Dictionary<string, CrateConfig> Crates = new Dictionary<string, CrateConfig>();
        }

        private PluginConfig _cfg;

        protected override void LoadDefaultConfig() => Config.WriteObject(new PluginConfig(), true);

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                _cfg = Config.ReadObject<PluginConfig>() ?? new PluginConfig();
                if (_cfg.Crates == null) _cfg.Crates = new Dictionary<string, CrateConfig>();
                foreach (var c in _cfg.Crates.Values)
                {
                    if (c.Items == null) c.Items = new List<LootItem>();
                    c.Items.RemoveAll(i => i == null || string.IsNullOrEmpty(i.Item));
                    foreach (var i in c.Items)
                    {
                        if (i.Min < 1) i.Min = 1;
                        if (i.Max < i.Min) i.Max = i.Min;
                    }
                }
                SaveConfig();
            }
            catch (Exception ex)
            {
                PrintError($"Couldn't read the config file ({ex.Message}); using Rust's own loot everywhere and leaving the file untouched.");
                _cfg = new PluginConfig { Enabled = false };
            }
        }

        protected override void SaveConfig() => Config.WriteObject(_cfg, true);

        private CrateConfig EntryFor(string shortName)
        {
            CrateConfig cc;
            return _cfg != null && shortName != null && _cfg.Crates.TryGetValue(shortName, out cc) ? cc : null;
        }

        private static bool IsCustom(CrateConfig cc)
        {
            if (cc == null) return false;
            var mode = (cc.Mode ?? "").ToLowerInvariant();
            return (mode == "replace" || mode == "add") && cc.Items.Count > 0;
        }

        private bool Multiplying => Math.Abs(_cfg.LootMultiplier - 1f) > 0.001f && _cfg.LootMultiplier > 0;

        private bool NeedsWork(CrateConfig cc) => IsCustom(cc) || (cc != null && cc.Scrap >= 0) || Multiplying;

        #endregion

        #region Filling crates

        // Rust fills the crate first (so the refresh timer and scrap work as normal); then the custom table goes in.
        private void OnLootSpawn(LootContainer container)
        {
            if (_cfg == null || !_cfg.Enabled || container == null) return;
            var cc = EntryFor(container.ShortPrefabName);
            if (!NeedsWork(cc)) return;
            NextTick(() =>
            {
                if (container == null || container.IsDestroyed) return;
                After(container.inventory, cc);
            });
        }

        // NPC bodies (scientists, scarecrows…): Rust fills the body right after this hook, so ours runs a tick later.
        private void OnCorpsePopulate(BaseEntity npc, LootableCorpse corpse)
        {
            if (_cfg == null || !_cfg.Enabled || npc == null || corpse == null) return;
            var cc = EntryFor(npc.ShortPrefabName);
            if (!NeedsWork(cc)) return;
            NextTick(() =>
            {
                if (corpse == null || corpse.IsDestroyed || corpse.containers == null || corpse.containers.Length == 0) return;
                After(corpse.containers[0], cc);
            });
        }

        private void OnEntitySpawned(PatrolHelicopter heli)
        {
            if (_cfg != null && _cfg.Enabled && heli != null && _cfg.HeliCrates >= 0) heli.maxCratesToSpawn = _cfg.HeliCrates;
        }

        private void OnEntitySpawned(BradleyAPC apc)
        {
            if (_cfg != null && _cfg.Enabled && apc != null && _cfg.BradleyCrates >= 0) apc.maxCratesToSpawn = _cfg.BradleyCrates;
        }

        private static void RemoveWhere(ItemContainer inv, Func<Item, bool> which)
        {
            foreach (var item in inv.itemList.Where(which).ToList())
            {
                item.RemoveFromContainer();
                item.Remove();
            }
            ItemManager.DoRemoves();
        }

        // Custom list, then the crate's scrap, then the multiplier.
        private void After(ItemContainer inv, CrateConfig cc)
        {
            if (inv == null) return;
            if (IsCustom(cc)) Fill(inv, cc);
            var scrapSet = cc != null && cc.Scrap >= 0;
            if (scrapSet)
            {
                RemoveWhere(inv, i => i.info.shortname == "scrap");
                if (cc.Scrap > 0) AddAmount(inv, ItemManager.FindItemDefinition("scrap"), cc.Scrap, 0);
            }
            if (Multiplying) Multiply(inv, _cfg.LootMultiplier, scrapSet);
        }

        private static void Multiply(ItemContainer inv, float mult, bool skipScrap)
        {
            foreach (var item in inv.itemList.ToList())
            {
                if (item.info.stackable <= 1 || item.IsBlueprint() || (skipScrap && item.info.shortname == "scrap")) continue;
                var target = Math.Max(1, Mathf.RoundToInt(item.amount * mult));
                var max = Math.Max(1, item.MaxStackable());
                if (target <= max) item.amount = target;
                else
                {
                    item.amount = max;
                    AddAmount(inv, item.info, target - max, item.skin);
                }
                item.MarkDirty();
            }
        }

        // Adds the amount in full stacks; returns false once the container is full.
        private static bool AddAmount(ItemContainer inv, ItemDefinition def, int amount, ulong skin)
        {
            if (def == null) return true;
            while (amount > 0)
            {
                var stack = Math.Min(amount, Math.Max(1, def.stackable));
                var item = ItemManager.Create(def, stack, skin);
                if (item == null) return true;
                item.OnVirginSpawn();
                if (!item.MoveToContainer(inv)) { item.Remove(); return false; }
                amount -= stack;
            }
            return true;
        }

        private void Fill(ItemContainer inv, CrateConfig cc)
        {
            if (cc.Mode.Equals("replace", StringComparison.OrdinalIgnoreCase))
                RemoveWhere(inv, i => !(_cfg.KeepScrap && i.info.shortname == "scrap"));
            foreach (var pick in Roll(cc))
            {
                var def = ItemManager.FindItemDefinition(pick.Item);
                if (def == null) continue;
                var amount = UnityEngine.Random.Range(pick.Min, pick.Max + 1);
                if (pick.Blueprint)
                {
                    var bp = ItemManager.CreateByName("blueprintbase");
                    if (bp == null) continue;
                    bp.blueprintTarget = def.itemid;
                    if (!bp.MoveToContainer(inv)) { bp.Remove(); break; }
                    continue;
                }
                if (!AddAmount(inv, def, amount, pick.Skin)) break;
            }
        }

        // Each item rolls its own chance. Then the count is pushed up to MinItems (picking more, likelier items first)
        // or trimmed to MaxItems.
        private static List<LootItem> Roll(CrateConfig cc)
        {
            var picked = cc.Items.Where(i => UnityEngine.Random.Range(0f, 100f) < i.Chance).ToList();
            var rest = cc.Items.Except(picked).ToList();
            while (picked.Count < cc.MinItems && rest.Count > 0)
            {
                var total = rest.Sum(i => Math.Max(0.01, i.Chance));
                var r = UnityEngine.Random.Range(0f, (float)total);
                var choice = rest[rest.Count - 1];
                foreach (var i in rest)
                {
                    r -= (float)Math.Max(0.01, i.Chance);
                    if (r <= 0) { choice = i; break; }
                }
                picked.Add(choice);
                rest.Remove(choice);
            }
            if (cc.MaxItems > 0 && picked.Count > cc.MaxItems)
                picked = picked.OrderBy(_ => UnityEngine.Random.value).Take(cc.MaxItems).ToList();
            return picked;
        }

        #endregion

        #region Rust's own loot tables

        private class ItemOdds
        {
            public double P;
            public int Min = int.MaxValue;
            public int Max;
            public bool Bp;
        }

        private readonly Dictionary<LootSpawn, Dictionary<ItemDefinition, ItemOdds>> _memo = new Dictionary<LootSpawn, Dictionary<ItemDefinition, ItemOdds>>();

        // The chance one call to this loot table gives at least one of each item, following Rust's weighted categories.
        private Dictionary<ItemDefinition, ItemOdds> OddsOf(LootSpawn spawn, int depth = 0)
        {
            Dictionary<ItemDefinition, ItemOdds> result;
            if (spawn == null) return new Dictionary<ItemDefinition, ItemOdds>();
            if (_memo.TryGetValue(spawn, out result)) return result;
            result = new Dictionary<ItemDefinition, ItemOdds>();
            _memo[spawn] = result;
            if (depth > 20) return result;
            var subs = spawn.subSpawn?.Where(e => e.category != null && e.weight > 0).ToList();
            if (subs != null && subs.Count > 0)
            {
                double total = subs.Sum(e => e.weight);
                foreach (var e in subs)
                {
                    var times = 1 + Math.Max(0, e.extraSpawns);
                    foreach (var kv in OddsOf(e.category, depth + 1))
                    {
                        ItemOdds o;
                        if (!result.TryGetValue(kv.Key, out o)) result[kv.Key] = o = new ItemOdds();
                        o.P += e.weight / total * (1 - Math.Pow(1 - kv.Value.P, times));
                        o.Min = Math.Min(o.Min, kv.Value.Min);
                        o.Max = Math.Max(o.Max, kv.Value.Max * times);
                        o.Bp |= kv.Value.Bp;
                    }
                }
            }
            else if (spawn.items != null)
            {
                foreach (var i in spawn.items)
                {
                    if (i?.itemDef == null) continue;
                    ItemOdds o;
                    if (!result.TryGetValue(i.itemDef, out o)) result[i.itemDef] = o = new ItemOdds();
                    o.P = 1;
                    var min = Mathf.Max(1, Mathf.RoundToInt(i.amount));
                    var max = i.maxAmount > i.amount ? Mathf.RoundToInt(i.maxAmount) : min;
                    o.Min = Math.Min(o.Min, min);
                    o.Max = Math.Max(o.Max, max);
                    o.Bp |= i.itemDef.spawnAsBlueprint;
                }
            }
            return result;
        }

        private Dictionary<ItemDefinition, ItemOdds> CrateOdds(LootContainer c) =>
            SlotOdds(c.LootSpawnSlots, c.lootDefinition, c.maxDefinitionsToSpawn);

        private Dictionary<ItemDefinition, ItemOdds> SlotOdds(LootContainer.LootSpawnSlot[] slots, LootSpawn lootDefinition, int maxDefinitions)
        {
            var none = new Dictionary<ItemDefinition, double>();
            var info = new Dictionary<ItemDefinition, ItemOdds>();
            Action<LootSpawn, double, int> add = (def, prob, times) =>
            {
                foreach (var kv in OddsOf(def))
                {
                    double n;
                    if (!none.TryGetValue(kv.Key, out n)) n = 1;
                    none[kv.Key] = n * Math.Pow(1 - prob * kv.Value.P, times);
                    ItemOdds o;
                    if (!info.TryGetValue(kv.Key, out o)) info[kv.Key] = o = new ItemOdds();
                    o.Min = Math.Min(o.Min, kv.Value.Min);
                    o.Max = Math.Max(o.Max, kv.Value.Max);
                    o.Bp |= kv.Value.Bp;
                }
            };
            if (slots != null && slots.Length > 0)
            {
                foreach (var slot in slots)
                    if (slot.definition != null && slot.numberToSpawn > 0) add(slot.definition, Mathf.Clamp01(slot.probability), slot.numberToSpawn);
            }
            else if (lootDefinition != null)
                add(lootDefinition, 1, Math.Max(1, maxDefinitions));
            foreach (var kv in none) info[kv.Key].P = 1 - kv.Value;
            return info;
        }

        private static readonly Dictionary<string, string> Names = new Dictionary<string, string>
        {
            ["crate_normal"] = "Military crate",
            ["crate_normal_2"] = "Wooden crate",
            ["crate_elite"] = "Elite crate",
            ["crate_tools"] = "Tool box",
            ["crate_basic"] = "Small crate",
            ["crate_mine"] = "Mine crate",
            ["crate_medical"] = "Medical crate",
            ["crate_normal_2_medical"] = "Medical crate (small)",
            ["crate_food_1"] = "Food crate",
            ["crate_food_2"] = "Food crate (large)",
            ["crate_normal_2_food"] = "Food crate (small)",
            ["foodbox"] = "Food box",
            ["crate_underwater_basic"] = "Underwater crate",
            ["crate_underwater_advanced"] = "Underwater crate (advanced)",
            ["crate_ammunition"] = "Ammo crate",
            ["crate_fuel"] = "Fuel crate",
            ["supply_drop"] = "Supply drop",
            ["heli_crate"] = "Patrol Heli crate",
            ["bradley_crate"] = "Bradley crate",
            ["codelockedhackablecrate"] = "Locked crate",
            ["codelockedhackablecrate_oilrig"] = "Locked crate (oil rig)",
            ["loot_barrel_1"] = "Barrel (blue)",
            ["loot_barrel_2"] = "Barrel (yellow)",
            ["loot-barrel-1"] = "Barrel (road, blue)",
            ["loot-barrel-2"] = "Barrel (road, yellow)",
            ["oil_barrel"] = "Oil barrel",
            ["minecart"] = "Mine cart",
            ["vehicle_parts"] = "Vehicle parts",
            ["trash-pile-1"] = "Trash pile",
            ["scientistnpc_roam"] = "Scientist (roaming)",
            ["scientistnpc_patrol"] = "Scientist (patrol)",
            ["scientistnpc_heavy"] = "Heavy scientist",
            ["scientistnpc_oilrig"] = "Scientist (oil rig)",
            ["scientistnpc_cargo"] = "Scientist (cargo ship)",
            ["scientistnpc_excavator"] = "Scientist (excavator)",
            ["scientistnpc_junkpile_pistol"] = "Scientist (junk pile)",
            ["scientistnpc_full_any"] = "Scientist (monument)",
            ["npc_tunneldweller"] = "Tunnel dweller",
            ["npc_underwaterdweller"] = "Underwater dweller",
            ["scarecrow"] = "Scarecrow",
        };

        private static string Pretty(string shortName)
        {
            string n;
            if (Names.TryGetValue(shortName, out n)) return n;
            var words = shortName.Replace('-', ' ').Replace('_', ' ').Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            return string.Join(" ", words.Select(w => char.ToUpperInvariant(w[0]) + w.Substring(1)));
        }

        private string _exportCache;

        // Every loot container prefab in the game, by short name.
        private string BuildExport()
        {
            if (_exportCache != null) return _exportCache;
            var crates = new SortedDictionary<string, LootContainer>();
            var npcs = new SortedDictionary<string, BaseEntity>();
            int heliDefault = -1, bradleyDefault = -1;
            var paths = GameManifest.Current?.entities ?? new string[0];
            foreach (var path in paths)
            {
                var p = path.ToLowerInvariant();
                if (p.EndsWith("/patrolhelicopter.prefab") || p.EndsWith("/bradleyapc.prefab") || p.Contains("scientist") || p.Contains("npc_") || p.Contains("scarecrow"))
                {
                    GameObject g;
                    try { g = GameManager.server.FindPrefab(path); }
                    catch { g = null; }
                    if (g != null)
                    {
                        var heli = g.GetComponent<PatrolHelicopter>();
                        if (heli != null && heliDefault < 0) heliDefault = heli.maxCratesToSpawn;
                        var apc = g.GetComponent<BradleyAPC>();
                        if (apc != null && bradleyDefault < 0) bradleyDefault = apc.maxCratesToSpawn;
                        var npc = (BaseEntity)g.GetComponent<HumanNPC>() ?? g.GetComponent<ScarecrowNPC>();
                        var npcName = System.IO.Path.GetFileNameWithoutExtension(path);
                        if (npc != null && !npcs.ContainsKey(npcName)) npcs[npcName] = npc;
                    }
                }
                if (!(p.Contains("loot") || p.Contains("crate") || p.Contains("barrel") || p.Contains("supply_drop") || p.Contains("trash") || p.Contains("minecart") || p.Contains("foodbox")))
                    continue;
                GameObject go;
                try { go = GameManager.server.FindPrefab(path); }
                catch { continue; }
                var c = go == null ? null : go.GetComponent<LootContainer>();
                if (c == null) continue;
                var shortName = System.IO.Path.GetFileNameWithoutExtension(path);
                if (!crates.ContainsKey(shortName)) crates[shortName] = c;
            }
            var live = BaseNetworkable.serverEntities.OfType<BaseEntity>().Where(e => e is LootContainer || e is HumanNPC || e is ScarecrowNPC)
                .GroupBy(c => c.ShortPrefabName).ToDictionary(g => g.Key, g => g.Count());
            var list = new List<object>();
            Action<string, string, int, int, Dictionary<ItemDefinition, ItemOdds>> addRow = (id, kind, slots, scrap, odds) =>
            {
                if (odds.Count == 0) return;
                int n;
                live.TryGetValue(id, out n);
                list.Add(new Dictionary<string, object>
                {
                    ["id"] = id,
                    ["name"] = Pretty(id),
                    ["kind"] = kind,
                    ["onMap"] = n,
                    ["slots"] = slots,
                    ["scrap"] = scrap,
                    ["items"] = odds.OrderByDescending(o => o.Value.P).Select(o => new Dictionary<string, object>
                    {
                        ["s"] = o.Key.shortname,
                        ["n"] = o.Key.displayName?.english ?? o.Key.shortname,
                        ["chance"] = Math.Round(o.Value.P, 5),
                        ["min"] = o.Value.Min == int.MaxValue ? 1 : o.Value.Min,
                        ["max"] = Math.Max(1, o.Value.Max),
                        ["bp"] = o.Value.Bp,
                    }).ToList(),
                });
            };
            foreach (var kv in crates) addRow(kv.Key, "crate", kv.Value.inventorySlots, kv.Value.scrapAmount, CrateOdds(kv.Value));
            foreach (var kv in npcs)
            {
                var human = kv.Value as HumanNPC;
                var crow = kv.Value as ScarecrowNPC;
                var slots = human != null ? human.LootSpawnSlots : crow != null ? crow.LootSpawnSlots : null;
                addRow(kv.Key, "npc", 0, 0, SlotOdds(slots, null, 0));
            }
            _exportCache = JsonConvert.SerializeObject(new { version = Version.ToString(), heliCrates = heliDefault, bradleyCrates = bradleyDefault, crates = list });
            return _exportCache;
        }

        private void OnServerInitialized()
        {
            // Saved so the manager can show the tables while the server is off.
            try { Interface.Oxide.DataFileSystem.GetFile("RustSandboxLoot_vanilla").WriteObject(JsonConvert.DeserializeObject(BuildExport())); }
            catch (Exception ex) { PrintWarning($"Couldn't save Rust's loot tables for the manager: {ex.Message}"); }
            var custom = _cfg.Enabled ? _cfg.Crates.Values.Count(IsCustom) : 0;
            Puts(custom == 0 ? "Every crate uses Rust's own loot table." : $"Custom loot on {custom} crate/NPC type(s); the rest use Rust's own loot.");
            if (_cfg.Enabled && Multiplying) Puts($"Loot multiplier: {_cfg.LootMultiplier}x.");
            if (_cfg.Enabled && (_cfg.HeliCrates >= 0 || _cfg.BradleyCrates >= 0))
            {
                // Already flying / driving: apply now.
                foreach (var e in BaseNetworkable.serverEntities.OfType<PatrolHelicopter>().ToList()) OnEntitySpawned(e);
                foreach (var e in BaseNetworkable.serverEntities.OfType<BradleyAPC>().ToList()) OnEntitySpawned(e);
            }
        }

        #endregion

        #region Commands

        // sandboxloot.export — Rust's loot table for every crate type, as JSON (chance is 0-1 per crate).
        [ConsoleCommand("sandboxloot.export")]
        private void CcExport(ConsoleSystem.Arg arg)
        {
            if (arg.Connection != null && arg.Connection.authLevel < 2) return;
            arg.ReplyWith(BuildExport());
        }

        // sandboxloot.refill [crate short name | all] — re-roll the loot in crates already on the map, so changes show now.
        [ConsoleCommand("sandboxloot.refill")]
        private void CcRefill(ConsoleSystem.Arg arg)
        {
            if (arg.Connection != null && arg.Connection.authLevel < 2) return;
            var which = arg.GetString(0, "all").ToLowerInvariant();
            var n = 0;
            foreach (var c in BaseNetworkable.serverEntities.OfType<LootContainer>().ToList())
            {
                if (c == null || c.IsDestroyed || c.inventory == null) continue;
                if (which != "all" && c.ShortPrefabName != which) continue;
                // Leave crates someone is looting right now, and locked crates mid-hack.
                if (c.IsOpen()) continue;
                if (c is HackableLockedCrate) continue;
                c.SpawnLoot();
                n++;
            }
            arg.ReplyWith($"Re-rolled the loot in {n} container(s).");
        }

        #endregion
    }
}
