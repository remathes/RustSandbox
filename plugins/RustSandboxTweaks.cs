// Rust Sandbox — Tweaks
// Gather rates, stack sizes, smelting/cooking speed, inventory sorting, furnace auto-split and a remove tool,
// controlled from the Rust Sandbox web app.
// Don't edit the config by hand: the manager rewrites it every time you press Apply on "Rates & stacks".
// Unloading the plugin puts every stack size and cooking value back to vanilla.

using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Game.Rust.Cui;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("Rust Sandbox Tweaks", "RustSandbox", "1.4.1")]
    [Description("Gather rates, stack sizes, smelting, inventory sorting and furnace auto-split controlled from Rust Sandbox.")]
    public class RustSandboxTweaks : RustPlugin
    {
        // Property names match the manager's GameTweaks class, so it can write this file directly.
        private class Settings
        {
            public bool Enabled = false;
            public double GatherRate = 1;
            public Dictionary<string, double> GatherByItem = new Dictionary<string, double>();
            public double SourceHits = 1;
            public double SourcePickups = 1;
            public double SourceCrops = 1;
            public double SourceQuarries = 1;
            public double StackRate = 1;
            public Dictionary<string, double> StackByCategory = new Dictionary<string, double>();
            public Dictionary<string, int> StackOverrides = new Dictionary<string, int>();
            public double SmeltSpeed = 1;
            public double SmeltOutput = 1;
            public double CookSpeed = 1;
            public double FuelUse = 1;
            public Dictionary<string, double> FuelUseByOven = new Dictionary<string, double>();
            public double Charcoal = 1;
            public bool Sorting = false;
            public float SortButtonX = 193f;
            public float SortButtonY = 18f;
            public bool FurnaceSplit = false;
            public bool RemoveTool = false;
            public double RemoveRefund = 0.5;
            public float RemoveSeconds = 60f;
        }

        private class CookDefault
        {
            public ItemDefinition Def;
            public ItemModCookable Mod;
            public float CookTime;
            public float Amount;
        }

        private Settings _cfg;
        private bool _applied;
        private readonly Dictionary<ItemDefinition, int> _defaultStacks = new Dictionary<ItemDefinition, int>();
        private readonly List<CookDefault> _defaultCook = new List<CookDefault>();

        private bool Active { get { return _cfg != null && _cfg.Enabled && _applied; } }

        // ------------------------------------------------------------------ config

        protected override void LoadDefaultConfig()
        {
            Config.WriteObject(new Settings(), true);
        }

        // This plugin used to be called RustServerManagerTweaks (the project was renamed to Rust Sandbox).
        // The first time it loads, copy the old config and data files over so nothing is lost.
        private const string LegacyName = "RustServerManagerTweaks";

        // Runs before Oxide loads the config, so it works on the files directly. A marker file makes it
        // a one-time copy: the old files win once, even if this plugin already created empty new ones.
        private void MigrateLegacyFiles(params string[] dataSuffixes)
        {
            const string newName = "RustSandboxTweaks";
            var marker = newName + "_migrated";
            try
            {
                var data = Interface.Oxide.DataFileSystem;
                if (data.ExistsDatafile(marker)) return;
                var oldConfig = new Oxide.Core.Configuration.DynamicConfigFile(Interface.Oxide.ConfigDirectory + "/" + LegacyName + ".json");
                var copied = false;
                if (oldConfig.Exists())
                {
                    var newConfig = new Oxide.Core.Configuration.DynamicConfigFile(Interface.Oxide.ConfigDirectory + "/" + newName + ".json");
                    newConfig.WriteObject(oldConfig.ReadObject<Newtonsoft.Json.Linq.JToken>());
                    Puts("Copied the settings from " + LegacyName + ".json.");
                    copied = true;
                }
                foreach (var suffix in dataSuffixes)
                {
                    var from = LegacyName + suffix;
                    if (!data.ExistsDatafile(from)) continue;
                    data.WriteObject(newName + suffix, data.ReadObject<Newtonsoft.Json.Linq.JToken>(from));
                    Puts("Copied the data from " + from + ".json.");
                    copied = true;
                }
                data.WriteObject(marker, new Dictionary<string, object> { ["from"] = LegacyName, ["copied"] = copied, ["at"] = DateTime.UtcNow });
            }
            catch (Exception ex)
            {
                PrintWarning("Couldn't copy the old " + LegacyName + " files: " + ex.Message);
            }
        }

        protected override void LoadConfig()
        {
            MigrateLegacyFiles("_NoAutoSplit");
            base.LoadConfig();
            try
            {
                _cfg = Config.ReadObject<Settings>();
            }
            catch (Exception ex)
            {
                // Never overwrite a config we can't read — the manager owns it.
                _cfg = null;
                PrintError("Couldn't read the config, tweaks are off: " + ex.Message);
            }
            if (_cfg != null)
            {
                if (_cfg.GatherByItem == null) _cfg.GatherByItem = new Dictionary<string, double>();
                if (_cfg.StackByCategory == null) _cfg.StackByCategory = new Dictionary<string, double>();
                if (_cfg.StackOverrides == null) _cfg.StackOverrides = new Dictionary<string, int>();
                if (_cfg.FuelUseByOven == null) _cfg.FuelUseByOven = new Dictionary<string, double>();
            }
        }

        // ------------------------------------------------------------------ lifecycle

        private void OnServerInitialized()
        {
            CaptureDefaults();
            if (_cfg == null || !_cfg.Enabled)
            {
                Puts("Loaded — tweaks are turned off in the manager.");
                return;
            }
            ApplyStacks();
            ApplyCooking();
            _applied = true;
            Puts(string.Format("Applied: gather {0:0.##}x, stacks {1:0.##}x ({2} item overrides), smelt speed {3:0.##}x, output {4:0.##}x, cooking {5:0.##}x, fuel use {6:0.##}x.",
                _cfg.GatherRate, _cfg.StackRate, _cfg.StackOverrides.Count, _cfg.SmeltSpeed, _cfg.SmeltOutput, _cfg.CookSpeed, _cfg.FuelUse));
        }

        private void Unload()
        {
            foreach (var p in BasePlayer.activePlayerList)
            {
                CuiHelper.DestroyUi(p, SortUi);
                CuiHelper.DestroyUi(p, RemoveUi);
            }
            RestoreDefaults();
        }

        private void CaptureDefaults()
        {
            if (_defaultStacks.Count > 0) return;
            foreach (var def in ItemManager.itemList)
            {
                if (def == null) continue;
                _defaultStacks[def] = def.stackable;
                var cook = def.GetComponent<ItemModCookable>();
                if (cook != null)
                    _defaultCook.Add(new CookDefault { Def = def, Mod = cook, CookTime = cook.cookTime, Amount = cook.amountOfBecome });
            }
        }

        private void RestoreDefaults()
        {
            foreach (var kv in _defaultStacks)
                if (kv.Key != null) kv.Key.stackable = kv.Value;
            foreach (var c in _defaultCook)
            {
                if (c.Mod == null) continue;
                SetCook(c, c.CookTime, c.Amount);
            }
            _applied = false;
        }

        // ------------------------------------------------------------------ stacks

        private void ApplyStacks()
        {
            foreach (var kv in _defaultStacks)
            {
                var def = kv.Key;
                if (def == null) continue;
                var vanilla = kv.Value;
                int exact;
                if (_cfg.StackOverrides.TryGetValue(def.shortname, out exact) && exact > 0)
                {
                    def.stackable = exact;
                    continue;
                }
                // Items that don't stack in vanilla (guns, tools, clothes) stay that way unless overridden.
                if (vanilla <= 1) continue;
                double mult;
                if (!_cfg.StackByCategory.TryGetValue(def.category.ToString(), out mult)) mult = _cfg.StackRate;
                def.stackable = (int)Math.Max(1, Math.Min(1000000000, Math.Round(vanilla * mult)));
            }
        }

        // ------------------------------------------------------------------ smelting & cooking

        private static bool IsSmeltable(ItemDefinition def)
        {
            return def.category == ItemCategory.Resources;
        }

        private void ApplyCooking()
        {
            foreach (var c in _defaultCook)
            {
                if (c.Mod == null || c.Def == null) continue;
                if (c.CookTime <= 0) continue;
                if (IsSmeltable(c.Def))
                {
                    SetCook(c, (float)Math.Max(0.05, c.CookTime / Math.Max(0.01, _cfg.SmeltSpeed)),
                                (float)Math.Max(1, Math.Round(c.Amount * _cfg.SmeltOutput)));
                }
                else if (c.Def.category == ItemCategory.Food)
                {
                    SetCook(c, (float)Math.Max(0.05, c.CookTime / Math.Max(0.01, _cfg.CookSpeed)), c.Amount);
                }
            }
        }

        // Ovens read a copy of the cooking settings (ItemDefinition.ItemModCookable, a CookableItemInfo made at startup),
        // not the ItemModCookable component itself, so both have to change.
        private static void SetCook(CookDefault c, float cookTime, float amount)
        {
            c.Mod.cookTime = cookTime;
            c.Mod.amountOfBecome = amount;
            var info = c.Def.ItemModCookable;
            if (info != null)
            {
                info.cookTime = cookTime;
                info.amountOfBecome = amount;
            }
        }

        private double FuelUseFor(BaseOven oven)
        {
            double use;
            if (_cfg.FuelUseByOven.TryGetValue(oven.ShortPrefabName, out use)) return use;
            return _cfg.FuelUse;
        }

        private object OnFuelConsume(BaseOven oven, Item fuel, ItemModBurnable burnable)
        {
            if (!Active || oven == null || fuel == null || burnable == null) return null;
            var use = FuelUseFor(oven);
            if (Math.Abs(use - 1) < 0.001 && Math.Abs(_cfg.Charcoal - 1) < 0.001) return null;

            // Same as vanilla BaseOven.ConsumeFuel, with charcoal and fuel duration scaled.
            if (oven.allowByproductCreation && burnable.byproductItem != null && UnityEngine.Random.Range(0f, 1f) > burnable.byproductChance)
            {
                var amount = (int)Math.Round(burnable.byproductAmount * _cfg.Charcoal);
                if (amount > 0)
                {
                    var byproduct = ItemManager.Create(burnable.byproductItem, amount);
                    if (byproduct != null && !byproduct.MoveToContainer(oven.inventory))
                        byproduct.Drop(oven.transform.position + Vector3.up, Vector3.zero);
                }
            }
            if (fuel.amount <= 1)
            {
                fuel.Remove();
                return true;
            }
            fuel.UseItem(1);
            fuel.fuel = burnable.fuelAmount / (float)Math.Max(0.05, use);
            fuel.MarkDirty();
            return true;
        }

        // ------------------------------------------------------------------ gather

        private double RateFor(string shortname)
        {
            double r;
            return _cfg.GatherByItem.TryGetValue(shortname, out r) ? r : _cfg.GatherRate;
        }

        private void Scale(Item item, double source)
        {
            if (!Active || item == null || item.info == null) return;
            var m = RateFor(item.info.shortname) * source;
            if (Math.Abs(m - 1) < 0.001) return;
            item.amount = (int)Math.Max(1, Math.Round(item.amount * m));
        }

        private void OnDispenserGather(ResourceDispenser dispenser, BaseEntity entity, Item item) { Scale(item, _cfg == null ? 1 : _cfg.SourceHits); }
        private void OnDispenserBonus(ResourceDispenser dispenser, BaseEntity entity, Item item) { Scale(item, _cfg == null ? 1 : _cfg.SourceHits); }
        private void OnGrowableGathered(GrowableEntity plant, Item item, BasePlayer player) { Scale(item, _cfg == null ? 1 : _cfg.SourceCrops); }
        private void OnQuarryGather(MiningQuarry quarry, Item item) { Scale(item, _cfg == null ? 1 : _cfg.SourceQuarries); }
        private void OnExcavatorGather(ExcavatorArm arm, Item item) { Scale(item, _cfg == null ? 1 : _cfg.SourceQuarries); }
        private void OnSurveyGather(SurveyCharge charge, Item item) { Scale(item, _cfg == null ? 1 : _cfg.SourceQuarries); }

        private void OnCollectiblePickup(CollectibleEntity collectible, BasePlayer player)
        {
            if (!Active || collectible == null || collectible.itemList == null) return;
            foreach (var ia in collectible.itemList)
            {
                if (ia == null || ia.itemDef == null) continue;
                var m = RateFor(ia.itemDef.shortname) * _cfg.SourcePickups;
                if (Math.Abs(m - 1) < 0.001) continue;
                ia.amount = (float)Math.Max(1, Math.Round(ia.amount * m));
            }
        }

        // ------------------------------------------------------------------ inventory sorting
        // Works whether or not custom rates are on: a Sort button while looting boxes, barrels, crates,
        // stashes, the tool cupboard and backpacks, plus /sort (or "sort.inventory") for your own inventory.
        // Containers whose slots have a job (furnaces, vending machines, lockers, recyclers...) are skipped.

        private const string SortUi = "SandboxTweaks.Sort";

        private bool SortingOn { get { return _cfg != null && _cfg.Sorting; } }

        // Rust's own category order: gear first, then the stuff you craft with.
        private static readonly ItemCategory[] SortOrder =
        {
            ItemCategory.Weapon, ItemCategory.Ammunition, ItemCategory.Tool, ItemCategory.Medical, ItemCategory.Attire,
            ItemCategory.Food, ItemCategory.Resources, ItemCategory.Component, ItemCategory.Construction, ItemCategory.Items,
            ItemCategory.Electrical, ItemCategory.Traps, ItemCategory.Misc, ItemCategory.Fun,
        };

        private static int CategoryRank(ItemCategory c)
        {
            var i = Array.IndexOf(SortOrder, c);
            return i < 0 ? SortOrder.Length : i;
        }

        private static bool IsSortable(BaseEntity entity)
        {
            if (entity == null) return false;
            if (entity is VendingMachine || entity is ShopFront || entity is DropBox || entity is Locker || entity is BaseOven
                || entity is Recycler || entity is ResearchTable || entity is ItemBasedFlowRestrictor) return false;
            return entity is BoxStorage || entity is LootContainer || entity is StashContainer || entity is DroppedItemContainer
                || entity is BuildingPrivlidge || entity is Fridge || entity.GetType() == typeof(StorageContainer);
        }

        private static bool IsBackpack(Item item)
        {
            return item != null && item.contents != null && item.info != null && item.info.GetComponent<ItemModBackpack>() != null;
        }

        private void OnLootEntity(BasePlayer player, BaseEntity entity)
        {
            if (!SortingOn || player == null || !IsSortable(entity)) return;
            NextTick(() => ShowSortButtons(player));
        }

        private void OnLootItem(BasePlayer player, Item item)
        {
            if (!SortingOn || player == null || !IsBackpack(item)) return;
            NextTick(() => ShowSortButtons(player));
        }

        private void OnPlayerLootEnd(PlayerLoot loot)
        {
            var player = loot == null ? null : loot.GetComponent<BasePlayer>();
            if (player != null) CuiHelper.DestroyUi(player, SortUi);
        }

        private void ShowSortButtons(BasePlayer player)
        {
            if (player == null || !player.IsConnected || player.inventory.loot.containers.Count == 0) return;
            CuiHelper.DestroyUi(player, SortUi);
            var x = _cfg.SortButtonX;
            var y = _cfg.SortButtonY;
            // A 2x2 grid between the hotbar and the health/water/food bars (about 214 wide).
            var ui = new CuiElementContainer();
            ui.Add(new CuiPanel
            {
                Image = { Color = "0 0 0 0" },
                RectTransform = { AnchorMin = "0.5 0", AnchorMax = "0.5 0", OffsetMin = x + " " + y, OffsetMax = (x + 214) + " " + (y + 60) },
            }, "Overlay", SortUi);
            SortButton(ui, 0, 1, "SORT BOX", "sort.loot", "0.42 0.63 0.24 0.9");
            SortButton(ui, 1, 1, "QUICK STACK", "sort.quickstack", "0.25 0.45 0.65 0.9");
            SortButton(ui, 0, 0, "TAKE ALL", "sort.takeall", "0.25 0.45 0.65 0.9");
            SortButton(ui, 1, 0, "SORT MINE", "sort.inventory", "0.30 0.30 0.30 0.9");
            CuiHelper.AddUi(player, ui);
        }

        /// <summary>One button of the 2x2 grid: col 0/1 (left/right), row 0/1 (bottom/top).</summary>
        private static void SortButton(CuiElementContainer ui, int col, int row, string text, string command, string color)
        {
            ui.Add(new CuiButton
            {
                Button = { Command = command, Color = color },
                Text = { Text = text, FontSize = 12, Align = TextAnchor.MiddleCenter, Color = "1 1 1 0.95", Font = "robotocondensed-bold.ttf" },
                RectTransform =
                {
                    AnchorMin = (col * 0.5f) + " " + (row * 0.5f), AnchorMax = ((col + 1) * 0.5f) + " " + ((row + 1) * 0.5f),
                    OffsetMin = (col == 0 ? "0" : "3") + " " + (row == 0 ? "0" : "2"), OffsetMax = (col == 0 ? "-3" : "0") + " " + (row == 0 ? "-2" : "0"),
                },
            }, SortUi);
        }

        /// <summary>The containers the player is looting, if it's one the buttons work on and it isn't locked.</summary>
        private static List<ItemContainer> LootTargets(BasePlayer player)
        {
            var loot = player.inventory.loot;
            var ok = (loot.entitySource != null && IsSortable(loot.entitySource)) || IsBackpack(loot.itemSource);
            if (!ok) return new List<ItemContainer>();
            return loot.containers.Where(c => c != null && !c.IsLocked()).ToList();
        }

        /// <summary>Merge partial stacks and order a container by category, then name. Returns how many slots it uses now.</summary>
        private static int SortContainer(ItemContainer container, BasePlayer player)
        {
            if (container == null || container.itemList == null || container.itemList.Count == 0) return 0;
            if (container.IsLocked()) return container.itemList.Count;
            var items = container.itemList
                .OrderBy(i => CategoryRank(i.info.category))
                .ThenBy(i => i.info.displayName != null ? i.info.displayName.english : i.info.shortname)
                .ThenBy(i => i.skin)
                .ThenByDescending(i => i.amount)
                .ToList();
            foreach (var item in items) item.RemoveFromContainer();
            foreach (var item in items)
            {
                // Everything came out of this container, so it fits back in; stacking only frees space.
                if (item.MoveToContainer(container, -1, true)) continue;
                if (item.MoveToContainer(container, -1, false)) continue;
                if (player != null) player.GiveItem(item);
                else item.Drop(container.dropPosition, Vector3.zero);
            }
            container.MarkDirty();
            return container.itemList.Count;
        }

        [ConsoleCommand("sort.loot")]
        private void CmdSortLoot(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (!SortingOn || player == null) return;
            foreach (var c in LootTargets(player)) SortContainer(c, player);
        }

        // Quick stack: move everything from your main inventory that the box already has a stack of.
        // The hotbar is left alone so your tools and weapons never go in by accident.
        [ConsoleCommand("sort.quickstack")]
        private void CmdQuickStack(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (!SortingOn || player == null || player.IsDead()) return;
            foreach (var box in LootTargets(player))
            {
                var kinds = new HashSet<int>(box.itemList.Select(i => i.info.itemid));
                foreach (var item in player.inventory.containerMain.itemList.ToList())
                {
                    if (!kinds.Contains(item.info.itemid)) continue;
                    item.MoveToContainer(box, -1, true);
                }
            }
        }

        // Take all: move everything from the box into your inventory until it's full.
        [ConsoleCommand("sort.takeall")]
        private void CmdTakeAll(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (!SortingOn || player == null || player.IsDead()) return;
            foreach (var box in LootTargets(player))
            {
                foreach (var item in box.itemList.ToList())
                {
                    if (item.MoveToContainer(player.inventory.containerMain, -1, true)) continue;
                    if (!item.MoveToContainer(player.inventory.containerBelt, -1, true)) return;
                }
            }
        }

        [ConsoleCommand("sort.inventory")]
        private void CmdSortInventory(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (SortingOn && player != null) SortPlayer(player);
        }

        [ChatCommand("sort")]
        private void ChatSort(BasePlayer player, string command, string[] args)
        {
            if (!SortingOn) { player.ChatMessage("Inventory sorting is turned off on this server."); return; }
            SortPlayer(player);
            player.ChatMessage("Inventory sorted. Your hotbar and clothes weren't touched. Tip: press F1 and type  bind j sort.inventory  to sort with a key.");
        }

        private static void SortPlayer(BasePlayer player)
        {
            if (player == null || player.inventory == null || player.IsDead()) return;
            SortContainer(player.inventory.containerMain, player);
            foreach (var worn in player.inventory.containerWear.itemList.ToList())
                if (IsBackpack(worn)) SortContainer(worn.contents, player);
        }

        // ------------------------------------------------------------------ furnace auto-split
        // When a player drops something an oven cooks into it, spread it evenly over the empty input slots
        // and the slots already holding the same item. Only player moves count, so conveyors are left alone.

        private const string SplitOptOutFile = "RustSandboxTweaks_NoAutoSplit";
        private HashSet<ulong> _noSplit;
        private BasePlayer _mover;
        private readonly HashSet<BaseOven> _splitQueued = new HashSet<BaseOven>();
        private bool _splitting;

        private bool SplitOn { get { return _cfg != null && _cfg.FurnaceSplit; } }

        private HashSet<ulong> NoSplit
        {
            get
            {
                if (_noSplit == null)
                {
                    try { _noSplit = Interface.Oxide.DataFileSystem.ReadObject<HashSet<ulong>>(SplitOptOutFile); } catch { }
                    if (_noSplit == null) _noSplit = new HashSet<ulong>();
                }
                return _noSplit;
            }
        }

        private void CanMoveItem(Item item, PlayerInventory inventory)
        {
            if (!SplitOn || inventory == null) return;
            _mover = inventory.baseEntity;
            NextTick(() => _mover = null);
        }

        private void OnItemAddedToContainer(ItemContainer container, Item item)
        {
            if (!SplitOn || _splitting || _mover == null || container == null || item == null) return;
            var oven = container.entityOwner as BaseOven;
            if (oven == null || oven.inputSlots < 2 || !oven.IsMaterialInput(item)) return;
            if (item.position < oven._inputSlotIndex || item.position >= oven._inputSlotIndex + oven.inputSlots) return;
            if (NoSplit.Contains(_mover.userID)) return;
            if (!_splitQueued.Add(oven)) return;
            var itemId = item.info.itemid;
            var skin = item.skin;
            NextTick(() =>
            {
                _splitQueued.Remove(oven);
                if (oven != null && !oven.IsDestroyed) SplitEvenly(oven, itemId, skin);
            });
        }

        private void SplitEvenly(BaseOven oven, int itemId, ulong skin)
        {
            var inv = oven.inventory;
            if (inv == null) return;
            var slots = new List<int>();
            var stacks = new List<Item>();
            for (var i = oven._inputSlotIndex; i < oven._inputSlotIndex + oven.inputSlots; i++)
            {
                var slot = inv.GetSlot(i);
                if (slot == null) slots.Add(i);
                else if (slot.info.itemid == itemId && slot.skin == skin) { slots.Add(i); stacks.Add(slot); }
            }
            if (stacks.Count == 0 || slots.Count < 2) return;
            var total = stacks.Sum(x => x.amount);
            var used = Math.Min(slots.Count, total);
            if (stacks.Count == used && stacks.All(x => Math.Abs(x.amount - total / used) <= 1)) return;

            _splitting = true;
            try
            {
                // Pool everything into one item, then cut it into even pieces, one per slot.
                var pool = stacks[0];
                pool.RemoveFromContainer();
                for (var i = 1; i < stacks.Count; i++)
                {
                    stacks[i].RemoveFromContainer();
                    stacks[i].Remove();
                }
                pool.amount = total;
                var each = total / used;
                var extra = total % used;
                for (var i = 0; i < used; i++)
                {
                    var amount = each + (i < extra ? 1 : 0);
                    var piece = i == used - 1 ? pool : pool.SplitItem(amount);
                    if (piece == null) continue;
                    if (!piece.MoveToContainer(inv, slots[i], false) && !piece.MoveToContainer(inv))
                        piece.Drop(oven.transform.position + Vector3.up, Vector3.zero);
                }
                inv.MarkDirty();
            }
            finally
            {
                _splitting = false;
            }
        }

        [ChatCommand("autosplit")]
        private void ChatAutoSplit(BasePlayer player, string command, string[] args)
        {
            if (!SplitOn) { player.ChatMessage("Furnace auto-split is turned off on this server."); return; }
            if (NoSplit.Remove(player.userID)) player.ChatMessage("Furnace auto-split turned ON: ore you put in a furnace is spread evenly over its slots.");
            else
            {
                NoSplit.Add(player.userID);
                player.ChatMessage("Furnace auto-split turned OFF for you. Type /autosplit to turn it back on.");
            }
            Interface.Oxide.DataFileSystem.WriteObject(SplitOptOutFile, NoSplit);
        }

        // ------------------------------------------------------------------ remove tool
        // /remove turns on remove mode for a minute: hitting your own building pieces and deployables with a
        // Hammer removes them and refunds part of the cost (deployables come back as the item). You must be
        // authorised on the tool cupboard covering it, or, with no cupboard, be the owner or their teammate.

        private const string RemoveUi = "SandboxTweaks.Remove";
        private readonly Dictionary<ulong, float> _removeUntil = new Dictionary<ulong, float>();
        private Timer _removeTimer;

        private bool RemoveOn { get { return _cfg != null && _cfg.RemoveTool; } }

        [ChatCommand("remove")]
        private void ChatRemove(BasePlayer player, string command, string[] args)
        {
            if (!RemoveOn) { player.ChatMessage("The remove tool is turned off on this server."); return; }
            if (_removeUntil.Remove(player.userID))
            {
                CuiHelper.DestroyUi(player, RemoveUi);
                player.ChatMessage("Remove mode off.");
                return;
            }
            var seconds = Mathf.Clamp(_cfg.RemoveSeconds, 10f, 600f);
            _removeUntil[player.userID] = Time.realtimeSinceStartup + seconds;
            player.ChatMessage($"Remove mode on for {Mathf.RoundToInt(seconds)} seconds: hit your building pieces or deployables with a <color=#ffd479>Hammer</color> to remove them ({Math.Round(_cfg.RemoveRefund * 100)}% refund on building pieces). Type /remove again to stop.");
            DrawRemoveUi(player, seconds);
            if (_removeTimer == null || _removeTimer.Destroyed) _removeTimer = timer.Every(1f, RemoveTick);
        }

        private void RemoveTick()
        {
            var now = Time.realtimeSinceStartup;
            foreach (var kv in _removeUntil.ToList())
            {
                var player = BasePlayer.FindByID(kv.Key);
                var left = kv.Value - now;
                if (player == null || !player.IsConnected || left <= 0f)
                {
                    _removeUntil.Remove(kv.Key);
                    if (player != null && player.IsConnected)
                    {
                        CuiHelper.DestroyUi(player, RemoveUi);
                        player.ChatMessage("Remove mode off.");
                    }
                    continue;
                }
                DrawRemoveUi(player, left);
            }
            if (_removeUntil.Count == 0) { _removeTimer?.Destroy(); _removeTimer = null; }
        }

        private static void DrawRemoveUi(BasePlayer player, float secondsLeft)
        {
            var ui = new CuiElementContainer();
            ui.Add(new CuiPanel
            {
                Image = { Color = "0.7 0.18 0.14 0.85" },
                RectTransform = { AnchorMin = "0.5 1", AnchorMax = "0.5 1", OffsetMin = "-150 -112", OffsetMax = "150 -84" },
            }, "Hud", RemoveUi);
            ui.Add(new CuiLabel
            {
                Text = { Text = "REMOVE MODE  ·  hit with a Hammer  ·  " + Mathf.CeilToInt(secondsLeft) + "s", FontSize = 13, Align = TextAnchor.MiddleCenter, Color = "1 1 1 0.95", Font = "robotocondensed-bold.ttf" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" },
            }, RemoveUi);
            CuiHelper.DestroyUi(player, RemoveUi);
            CuiHelper.AddUi(player, ui);
        }

        private object OnHammerHit(BasePlayer player, HitInfo info)
        {
            if (!RemoveOn || player == null || info == null || !_removeUntil.ContainsKey(player.userID)) return null;
            var entity = info.HitEntity as BaseCombatEntity;
            if (entity == null || entity.IsDestroyed) return null;
            var problem = WhyCantRemove(player, entity);
            if (problem != null)
            {
                player.ChatMessage(problem);
                return true;
            }
            RefundRemoved(player, entity);
            entity.Kill(BaseNetworkable.DestroyMode.Gib);
            // Using the tool keeps the mode going.
            _removeUntil[player.userID] = Mathf.Max(_removeUntil[player.userID], Time.realtimeSinceStartup + 15f);
            return true;
        }

        private static string WhyCantRemove(BasePlayer player, BaseCombatEntity entity)
        {
            if (entity is BasePlayer || entity is BaseNpc) return "You can't remove that.";
            var block = entity as BuildingBlock;
            var removable = block != null || entity is DecayEntity || entity.pickup.itemTarget != null;
            if (!removable) return "You can't remove that.";

            var privilege = entity.GetBuildingPrivilege(entity.WorldSpaceBounds());
            if (privilege != null)
            {
                if (!privilege.IsAuthed(player)) return "You need to be authorised on the tool cupboard here to remove that.";
            }
            else
            {
                var owner = entity.OwnerID;
                var mine = owner == player.userID || (owner != 0 && player.Team != null && player.Team.members.Contains(owner));
                if (!mine) return "You can only remove things you or your team placed.";
            }

            var storage = entity as StorageContainer;
            if (storage != null && storage.inventory != null && storage.inventory.itemList.Count > 0) return "Empty it first.";
            return null;
        }

        private void RefundRemoved(BasePlayer player, BaseCombatEntity entity)
        {
            var block = entity as BuildingBlock;
            if (block != null)
            {
                var share = Mathf.Clamp01((float)_cfg.RemoveRefund);
                if (share <= 0f || block.currentGrade == null) return;
                foreach (var cost in block.currentGrade.CostToBuild())
                {
                    var amount = Mathf.FloorToInt(cost.amount * share);
                    if (amount > 0 && cost.itemDef != null) player.GiveItem(ItemManager.Create(cost.itemDef, amount));
                }
                return;
            }
            if (entity.pickup.itemTarget != null)
                player.GiveItem(ItemManager.Create(entity.pickup.itemTarget, Math.Max(1, entity.pickup.itemCount), entity.skinID));
        }

        private void OnPlayerDisconnected(BasePlayer player, string reason)
        {
            if (player != null) _removeUntil.Remove(player.userID);
        }

        // ------------------------------------------------------------------ manager commands (RCON)

        [ConsoleCommand("sandboxtweaks.items")]
        private void CmdItems(ConsoleSystem.Arg arg)
        {
            if (arg.Connection != null && arg.Connection.authLevel < 2) return;
            CaptureDefaults();
            var list = new List<Dictionary<string, object>>(_defaultStacks.Count);
            foreach (var kv in _defaultStacks)
            {
                var def = kv.Key;
                if (def == null) continue;
                var row = new Dictionary<string, object>
                {
                    { "s", def.shortname },
                    { "n", def.displayName != null && !string.IsNullOrEmpty(def.displayName.english) ? def.displayName.english : def.shortname },
                    { "c", def.category.ToString() },
                    { "d", kv.Value },
                    { "now", def.stackable },
                };
                var cook = _defaultCook.Find(c => c.Def == def);
                if (cook != null && cook.Mod != null && cook.Mod.becomeOnCooked != null)
                {
                    row["cookTo"] = cook.Mod.becomeOnCooked.shortname;
                    row["cookTime"] = cook.CookTime;
                    row["cookAmount"] = cook.Amount;
                }
                list.Add(row);
            }
            arg.ReplyWith(JsonConvert.SerializeObject(list));
        }

        [ConsoleCommand("sandboxtweaks.status")]
        private void CmdStatus(ConsoleSystem.Arg arg)
        {
            if (arg.Connection != null && arg.Connection.authLevel < 2) return;
            arg.ReplyWith(_cfg == null ? "config unreadable" : Active ? "applied" : "off");
        }
    }
}
