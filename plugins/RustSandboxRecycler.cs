// Rust Sandbox — Repair Recycler
// One deployable item that places a recycler with a repair bench joined to it in an L (Corner layout, default),
// or a recycler alone with a Repair panel beside its menu (Single layout). Rust can't load custom models from a
// plugin, so the corner station is the real recycler and real repair bench, parented together so they act as one.
// Get one with /recycler craft, or an admin can /recycler give <player>. Settings are in the config file.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Game.Rust.Cui;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("Rust Sandbox Repair Recycler", "RustSandbox", "1.2.0")]
    [Description("A deployable recycler with a built-in repair bench, placed from one item.")]
    public class RustSandboxRecycler : RustPlugin
    {
        private const string AdminPerm = "rustsandboxrecycler.admin";
        private const string CraftPerm = "rustsandboxrecycler.craft";
        private const string BaseItem = "box.repair.bench";
        private const string DefaultPrefab = "assets/bundled/prefabs/static/recycler_static.prefab";
        private const string BenchPrefab = "assets/prefabs/deployable/repair bench/repairbench_deployed.prefab";
        private const string RepairEffect = "assets/bundled/prefabs/fx/repairbench/itemrepair.prefab";
        private const string Ui = "SandboxRecycler.Panel";

        #region Config

        private class Cost
        {
            [JsonProperty("Item shortname")] public string ShortName;
            [JsonProperty("Amount")] public int Amount;
        }

        private class PluginConfig
        {
            [JsonProperty("Item name")] public string ItemName = "Repair Recycler";
            [JsonProperty("Item skin ID (0 = plain repair bench icon)")] public ulong SkinId;
            [JsonProperty("Players can craft it with /recycler craft")] public bool AllowCraft = true;
            [JsonProperty("Crafting needs the rustsandboxrecycler.craft permission")] public bool CraftNeedsPermission;
            [JsonProperty("Craft cost", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<Cost> CraftCost = new List<Cost>
            {
                new Cost { ShortName = "metal.fragments", Amount = 500 },
                new Cost { ShortName = "gears", Amount = 10 },
                new Cost { ShortName = "scrap", Amount = 150 },
            };
            [JsonProperty("Most each player can have placed (0 = no limit)")] public int MaxPerPlayer = 2;
            [JsonProperty("Only the owner, their team and building-authorised players can use it")] public bool Private = true;
            [JsonProperty("Always powered (ignore the power grid)")] public bool AlwaysPowered = true;
            [JsonProperty("Repair mode: Default (vanilla repair bench) or Full (back to brand new, no max condition lost)")] public string RepairMode = "Default";
            [JsonProperty("Default mode: max condition lost per repair (vanilla 0.2)")] public float ConditionLoss = 0.2f;
            [JsonProperty("Recycler output % (0 = vanilla for that recycler)")] public float RecycleOutputPercent;
            [JsonProperty("Recycler seconds per item (0 = vanilla for that recycler)")] public float RecycleSeconds;
            [JsonProperty("Must know the blueprint to repair (vanilla true)")] public bool MustKnowBlueprint = true;
            [JsonProperty("Repair panel position: pixels right of screen centre")] public float PanelX = 193f;
            [JsonProperty("Repair panel position: pixels up from the bottom")] public float PanelY = 430f;
            [JsonProperty("Recycler prefab (blank = find it automatically)")] public string Prefab = "";
            [JsonProperty("Layout: Corner (repair bench joined to the recycler in an L) or Single (recycler with a Repair panel)")] public string Layout = "Corner";
            [JsonProperty("Corner: bench side of the recycler (Left or Right)")] public string BenchSide = "Left";
            [JsonProperty("Corner: move the bench forward/back in metres (+ = forward)")] public float BenchNudgeForward;
            [JsonProperty("Corner: move the bench sideways in metres (+ = away from the recycler)")] public float BenchNudgeSide;
            [JsonProperty("Corner: bench turn in degrees (90 = L shape)")] public float BenchTurn = 90f;
        }

        private PluginConfig _cfg;

        protected override void LoadDefaultConfig() => Config.WriteObject(new PluginConfig(), true);

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                _cfg = Config.ReadObject<PluginConfig>() ?? new PluginConfig();
                if (_cfg.CraftCost == null) _cfg.CraftCost = new List<Cost>();
                SaveConfig();
            }
            catch (Exception ex)
            {
                PrintError($"Couldn't read the config file ({ex.Message}); using defaults and leaving the file untouched.");
                _cfg = new PluginConfig();
            }
        }

        protected override void SaveConfig() => Config.WriteObject(_cfg, true);

        private bool Corner => !string.Equals(_cfg.Layout, "Single", StringComparison.OrdinalIgnoreCase);

        private bool FullRepair => string.Equals(_cfg.RepairMode, "Full", StringComparison.OrdinalIgnoreCase);

        #endregion

        #region Data

        // Placed recyclers by network id. Position is kept too, so a station can be found again if its id changes.
        private class Station
        {
            public ulong Owner;
            public float X, Y, Z;
        }

        private class StoredData
        {
            public Dictionary<ulong, Station> Stations = new Dictionary<ulong, Station>();
        }

        private const string DataFile = "RustSandboxRecycler";
        private StoredData _data = new StoredData();
        private readonly Dictionary<ulong, Recycler> _live = new Dictionary<ulong, Recycler>();
        private string _prefab;

        private void LoadData()
        {
            try { _data = Interface.Oxide.DataFileSystem.ReadObject<StoredData>(DataFile) ?? new StoredData(); }
            catch { _data = new StoredData(); }
            if (_data.Stations == null) _data.Stations = new Dictionary<ulong, Station>();
        }

        private void SaveData() => Interface.Oxide.DataFileSystem.WriteObject(DataFile, _data);

        private bool IsStation(BaseEntity e) => e is Recycler && e.net != null && _live.ContainsKey(e.net.ID.Value);

        // The tracked recycler for either part of a station (the recycler itself or its joined repair bench).
        private Recycler StationOf(BaseEntity e)
        {
            if (e == null) return null;
            if (IsStation(e)) return (Recycler)e;
            var parent = e is RepairBench ? e.GetParentEntity() : null;
            return parent != null && IsStation(parent) ? (Recycler)parent : null;
        }

        private static RepairBench BenchOf(Recycler rec) =>
            rec?.children?.OfType<RepairBench>().FirstOrDefault(b => b != null && !b.IsDestroyed);

        private void Track(Recycler rec, ulong owner)
        {
            var p = rec.transform.position;
            _data.Stations[rec.net.ID.Value] = new Station { Owner = owner, X = p.x, Y = p.y, Z = p.z };
            _live[rec.net.ID.Value] = rec;
            SaveData();
        }

        private void Untrack(ulong id)
        {
            _live.Remove(id);
            if (_data.Stations.Remove(id)) SaveData();
        }

        private int CountOwned(ulong owner) => _data.Stations.Values.Count(s => s.Owner == owner);

        #endregion

        #region Lifecycle

        private void Init()
        {
            permission.RegisterPermission(AdminPerm, this);
            permission.RegisterPermission(CraftPerm, this);
            LoadData();
        }

        private void OnServerInitialized()
        {
            _prefab = FindPrefab();
            if (_prefab == null) PrintError("Couldn't find a recycler prefab. Set \"Recycler prefab\" in the config.");

            // Reattach to the recyclers placed before a restart.
            var recyclers = BaseNetworkable.serverEntities.OfType<Recycler>().ToList();
            var fixedUp = new Dictionary<ulong, Station>();
            foreach (var kv in _data.Stations)
            {
                var rec = recyclers.FirstOrDefault(r => r.net != null && r.net.ID.Value == kv.Key)
                          ?? recyclers.FirstOrDefault(r => r.net != null && !fixedUp.ContainsKey(r.net.ID.Value)
                                                           && (r.transform.position - new Vector3(kv.Value.X, kv.Value.Y, kv.Value.Z)).sqrMagnitude < 0.25f);
                if (rec == null) continue;
                fixedUp[rec.net.ID.Value] = kv.Value;
                _live[rec.net.ID.Value] = rec;
                if (_cfg.AlwaysPowered) KeepPowered(rec);
            }
            if (fixedUp.Count != _data.Stations.Count || fixedUp.Keys.Except(_data.Stations.Keys).Any())
            {
                _data.Stations = fixedUp;
                SaveData();
            }
        }

        private void Unload()
        {
            foreach (var p in BasePlayer.activePlayerList) CuiHelper.DestroyUi(p, Ui);
        }

        private void OnNewSave(string filename)
        {
            _data = new StoredData();
            _live.Clear();
            SaveData();
        }

        private string FindPrefab()
        {
            if (!string.IsNullOrEmpty(_cfg.Prefab)) return _cfg.Prefab;
            if (GameManager.server.FindPrefab(DefaultPrefab) != null) return DefaultPrefab;
            var found = GameManifest.Current?.entities?
                .Where(p => p.Contains("recycler") && p.EndsWith(".prefab") && !p.Contains("config"))
                .OrderBy(p => p.Contains("static") ? 0 : 1)
                .FirstOrDefault();
            if (found != null) Puts($"Using recycler prefab {found}");
            return found;
        }

        #endregion

        #region Item

        private bool IsStationItem(Item item) =>
            item != null && item.info.shortname == BaseItem
            && (item.name == _cfg.ItemName || (_cfg.SkinId != 0 && item.skin == _cfg.SkinId));

        private Item CreateItem(int amount = 1)
        {
            var item = ItemManager.CreateByName(BaseItem, Math.Max(1, amount), _cfg.SkinId);
            if (item == null) return null;
            item.name = _cfg.ItemName;
            item.MarkDirty();
            return item;
        }

        private void Give(BasePlayer player, int amount = 1)
        {
            var item = CreateItem(amount);
            if (item == null) return;
            player.GiveItem(item);
        }

        #endregion

        #region Placing

        private object CanBuild(Planner planner, Construction prefab, Construction.Target target)
        {
            var item = planner?.GetItem();
            if (!IsStationItem(item)) return null;
            var player = planner.GetOwnerPlayer();
            if (player == null) return null;
            if (_prefab == null)
            {
                player.ChatMessage("The Repair Recycler isn't working on this server right now (no recycler prefab).");
                return false;
            }
            if (_cfg.MaxPerPlayer > 0 && !IsAdmin(player) && CountOwned(player.userID) >= _cfg.MaxPerPlayer)
            {
                player.ChatMessage($"You already have {_cfg.MaxPerPlayer} {_cfg.ItemName}s placed. Pick one up first.");
                return false;
            }
            return null;
        }

        // The item places as a repair bench (its placement rules and ghost); right after, swap the bench for a recycler.
        private void OnEntityBuilt(Planner planner, GameObject go)
        {
            var item = planner?.GetItem();
            if (!IsStationItem(item)) return;
            var player = planner.GetOwnerPlayer();
            var bench = go.ToBaseEntity();
            if (player == null || bench == null) return;
            var pos = bench.transform.position;
            var rot = bench.transform.rotation;
            var owner = player.userID.Get();

            NextTick(() =>
            {
                if (bench != null && !bench.IsDestroyed) bench.Kill();
                var rec = SpawnRecycler(pos, rot, owner);
                if (rec != null && Corner) SpawnBench(rec);
                if (rec != null && !HasRoom(rec))
                {
                    // Try the other three facings before giving up.
                    for (int i = 0; i < 3 && !HasRoom(rec); i++) rec.transform.rotation *= Quaternion.Euler(0f, 90f, 0f);
                    if (!HasRoom(rec))
                    {
                        rec.Kill();
                        rec = null;
                        if (player != null && player.IsConnected) player.ChatMessage($"There isn't enough room for the {_cfg.ItemName} there. It's back in your inventory.");
                    }
                    else SendMoved(rec);
                }
                if (rec == null)
                {
                    if (player != null) Give(player);
                    return;
                }
                Track(rec, owner);
                if (player != null && player.IsConnected)
                    player.ChatMessage(Corner
                        ? $"{_cfg.ItemName} placed. Recycle at the recycler and repair at the bench."
                        : $"{_cfg.ItemName} placed. Open it to recycle, or use the Repair panel on the right to fix items.");
            });
        }

        private Recycler SpawnRecycler(Vector3 pos, Quaternion rot, ulong owner)
        {
            var rec = GameManager.server.CreateEntity(_prefab, pos, rot) as Recycler;
            if (rec == null) return null;
            rec.OwnerID = owner;
            rec.Spawn();
            if (_cfg.AlwaysPowered) KeepPowered(rec);
            return rec;
        }

        // The bench sits beside the recycler, turned 90 degrees so the two make an L. It's parented to the recycler,
        // so it moves, rotates and is removed with it.
        private RepairBench SpawnBench(Recycler rec)
        {
            var bench = GameManager.server.CreateEntity(BenchPrefab, rec.transform.position, rec.transform.rotation) as RepairBench;
            if (bench == null) { PrintWarning("Couldn't create the repair bench for a corner station."); return null; }
            var rb = rec.bounds;
            var side = string.Equals(_cfg.BenchSide, "Right", StringComparison.OrdinalIgnoreCase) ? 1f : -1f;
            var turn = Quaternion.Euler(0f, _cfg.BenchTurn, 0f);
            // The bench's own half-width along the recycler's x axis once it's turned.
            var bb = bench.bounds;
            var turnedExtents = turn * bb.extents;
            var benchHalfX = Mathf.Abs(turnedExtents.x);
            var benchHalfZ = Mathf.Abs(turnedExtents.z);
            var x = rb.center.x + side * (rb.extents.x + benchHalfX + 0.05f + _cfg.BenchNudgeSide);
            var z = rb.center.z + rb.extents.z - benchHalfZ + _cfg.BenchNudgeForward;
            bench.OwnerID = rec.OwnerID;
            bench.SetParent(rec);
            bench.transform.localPosition = new Vector3(x, 0f, z);
            bench.transform.localRotation = turn;
            bench.Spawn();
            return bench;
        }

        private static readonly int RoomMask = LayerMask.GetMask("Construction", "Deployed", "World", "Default");

        // The recycler is bigger than the bench it replaced, so make sure it isn't inside a wall or another deployable.
        private bool HasRoom(Recycler rec)
        {
            var bench = BenchOf(rec);
            return FitsAt(rec, rec) && (bench == null || FitsAt(bench, rec));
        }

        private bool FitsAt(BaseEntity ent, Recycler rec)
        {
            var b = ent.bounds;
            var lift = 0.2f;
            var half = new Vector3(Mathf.Max(0.05f, b.extents.x - 0.1f), Mathf.Max(0.05f, b.extents.y - lift), Mathf.Max(0.05f, b.extents.z - 0.1f));
            var centre = ent.transform.TransformPoint(b.center + new Vector3(0f, lift * 0.5f + 0.05f, 0f));
            var hits = Physics.OverlapBox(centre, half, ent.transform.rotation, RoomMask, QueryTriggerInteraction.Ignore);
            foreach (var c in hits)
            {
                var e = c.ToBaseEntity();
                // The bench this replaced is killed this frame but its collider lingers until the frame ends.
                if (e == rec || (e != null && (e.IsDestroyed || e.GetParentEntity() == rec))) continue;
                return false;
            }
            return true;
        }

        private void KeepPowered(Recycler rec)
        {
            if (rec == null || rec.IsDestroyed || rec.HasFlag(BaseEntity.Flags.Reserved11)) return;
            using (var scope = rec.StartSetFlags(BaseEntity.FlagsUpdateMode.SendNetworkUpdate_Flags))
                scope.Set(BaseEntity.Flags.Reserved11, true);
        }

        private void OnEntityKill(Recycler rec)
        {
            if (rec?.net == null || !_live.ContainsKey(rec.net.ID.Value)) return;
            foreach (var p in Looters(rec)) CuiHelper.DestroyUi(p, Ui);
            var bench = BenchOf(rec);
            if (bench != null)
            {
                foreach (var p in Looters(bench)) CuiHelper.DestroyUi(p, Ui);
                NextTick(() => { if (bench != null && !bench.IsDestroyed) bench.Kill(); });
            }
            Untrack(rec.net.ID.Value);
        }

        // The joined bench can't be hurt, decay or be picked up on its own; the station is picked up as a whole.
        private object OnEntityTakeDamage(RepairBench bench, HitInfo info) => StationOf(bench) != null ? (object)true : null;

        private object CanPickupEntity(BasePlayer player, RepairBench bench) => StationOf(bench) != null ? (object)false : null;

        #endregion

        #region Using

        private bool IsAdmin(BasePlayer p) => p.IsAdmin || permission.UserHasPermission(p.UserIDString, AdminPerm);

        private bool IsOwner(BasePlayer p, BaseEntity e) => e.OwnerID == p.userID.Get() || IsAdmin(p);

        private bool CanUse(BasePlayer p, BaseEntity e)
        {
            if (!_cfg.Private || IsOwner(p, e)) return true;
            var team = RelationshipManager.ServerInstance?.FindPlayersTeam(e.OwnerID);
            if (team != null && team.members.Contains(p.userID.Get())) return true;
            var priv = e.GetBuildingPrivilege();
            return priv != null && priv.IsAuthed(p);
        }

        private object CanLootEntity(BasePlayer player, StorageContainer container)
        {
            var station = StationOf(container);
            if (station == null || CanUse(player, station)) return null;
            player.ChatMessage($"This {_cfg.ItemName} belongs to someone else.");
            return false;
        }

        private object OnRecyclerToggle(Recycler rec, BasePlayer player)
        {
            if (!IsStation(rec)) return null;
            if (!CanUse(player, rec)) return false;
            if (_cfg.AlwaysPowered) KeepPowered(rec);
            // The vanilla switch runs right after this hook; adjust speed/output once it has started.
            if (!rec.IsOn() && (_cfg.RecycleOutputPercent > 0 || _cfg.RecycleSeconds > 0)) NextTick(() => ApplyRecycleSettings(rec));
            NextTick(() => { foreach (var p in Looters(rec)) DrawPanel(p, rec); });
            return null;
        }

        private void OnLootEntity(BasePlayer player, BaseEntity entity)
        {
            var rec = StationOf(entity);
            if (rec == null) return;
            if (_cfg.AlwaysPowered) KeepPowered(rec);
            NextTick(() => DrawPanel(player, rec));
        }

        private void OnLootEntityEnd(BasePlayer player, BaseCombatEntity entity)
        {
            if (StationOf(entity) != null) CuiHelper.DestroyUi(player, Ui);
        }

        private void OnPlayerDisconnected(BasePlayer player) => CuiHelper.DestroyUi(player, Ui);

        private void OnItemAddedToContainer(ItemContainer container, Item item) => RedrawFor(container);

        private void OnItemRemovedFromContainer(ItemContainer container, Item item) => RedrawFor(container);

        private readonly HashSet<ulong> _pendingRedraw = new HashSet<ulong>();

        private void RedrawFor(ItemContainer container)
        {
            var rec = container?.entityOwner as Recycler;
            if (rec == null || !IsStation(rec) || !_pendingRedraw.Add(rec.net.ID.Value)) return;
            NextTick(() =>
            {
                if (rec == null || rec.net == null) return;
                _pendingRedraw.Remove(rec.net.ID.Value);
                foreach (var p in Looters(rec)) DrawPanel(p, rec);
            });
        }

        private static IEnumerable<BasePlayer> Looters(BaseEntity ent) =>
            BasePlayer.activePlayerList.Where(p => p != null && p.inventory?.loot?.entitySource == ent);

        private Recycler LootedStation(BasePlayer player) => StationOf(player?.inventory?.loot?.entitySource);

        #endregion

        #region Repair

        private class RepairInfo
        {
            public Item Item;
            public string Problem;
            public List<KeyValuePair<ItemDefinition, int>> Cost = new List<KeyValuePair<ItemDefinition, int>>();
            public bool CanAfford = true;
        }

        // Same checks and cost as the vanilla repair bench (RepairBench.RepairAnItem), so the panel can explain itself.
        private RepairInfo GetRepairInfo(BasePlayer player, Recycler rec) => GetRepairInfo(player, rec.inventory.GetSlot(0));

        private RepairInfo GetRepairInfo(BasePlayer player, Item slotItem)
        {
            var info = new RepairInfo { Item = slotItem };
            var item = info.Item;
            if (item == null) { info.Problem = "Put a damaged item in the first input slot."; return info; }
            var bp = item.info.Blueprint;
            if (bp == null || !item.info.condition.enabled || !item.info.condition.repairable) { info.Problem = "This item can't be repaired."; return info; }
            var target = FullRepair ? item.info.condition.max : item.maxCondition;
            if (item.condition >= target) { info.Problem = "Already at full condition."; return info; }
            if (_cfg.MustKnowBlueprint)
            {
                var def = item.info.isRedirectOf != null ? item.info.isRedirectOf : item.info;
                if (!player.blueprints.HasUnlocked(def) && (def.Blueprint == null || def.Blueprint.isResearchable))
                { info.Problem = "You need to learn this blueprint to repair it."; return info; }
            }
            // Full repair is charged on the whole missing bar (including lost max condition), at the vanilla 20% rate.
            var fraction = FullRepair ? RepairBench.RepairCostFraction(item.condition, target) : RepairBench.RepairCostFraction(item);
            var list = new List<ItemAmount>();
            RepairBench.GetRepairCostList(bp, list);
            foreach (var a in list)
            {
                if (a.itemDef.category == ItemCategory.Component) continue;
                var amount = Mathf.CeilToInt(a.amount * fraction);
                if (amount <= 0) continue;
                info.Cost.Add(new KeyValuePair<ItemDefinition, int>(a.itemDef, amount));
                if (player.inventory.GetAmount(a.itemDef.itemid) < amount) info.CanAfford = false;
            }
            return info;
        }

        [ConsoleCommand("sandboxrecycler.repair")]
        private void CmdRepair(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            var rec = LootedStation(player);
            if (rec == null) return;
            if (rec.IsOn()) { player.ChatMessage("Switch the recycler off first, or it will recycle the item."); return; }
            var info = GetRepairInfo(player, rec);
            if (info.Problem != null || !info.CanAfford) { DrawPanel(player, rec); return; }
            var item = info.Item;
            if (FullRepair) RepairFully(player, rec, info);
            else
            {
                var loss = _cfg.ConditionLoss;
                var mod = item.info.GetComponent<ItemModRepair>();
                if (mod != null) loss = mod.conditionLost;
                var before = item.condition;
                RepairBench.RepairAnItem(item, player, rec, loss, _cfg.MustKnowBlueprint);
                if (item.condition <= before) player.ChatMessage("That item couldn't be repaired.");
            }
            DrawPanel(player, rec);
        }

        private bool _repairing;

        // Full repair mode at a corner station's bench: replace the vanilla repair with a full one.
        private object OnItemRepair(BasePlayer player, Item item)
        {
            if (_repairing || !FullRepair || player == null) return null;
            var bench = player.inventory?.loot?.entitySource as RepairBench;
            if (bench == null || StationOf(bench) == null) return null;
            var info = GetRepairInfo(player, item);
            if (info.Problem == null && info.CanAfford) RepairFully(player, bench, info);
            return false;
        }

        private void RepairFully(BasePlayer player, BaseEntity rec, RepairInfo info)
        {
            var item = info.Item;
            _repairing = true;
            try { if (Interface.CallHook("OnItemRepair", player, item) != null) return; }
            finally { _repairing = false; }
            foreach (var c in info.Cost) player.inventory.Take(null, c.Key.itemid, c.Value);
            item.maxCondition = item.info.condition.max;
            item.condition = item.maxCondition;
            item.MarkDirty();
            var mod = item.info.GetComponent<ItemModRepair>();
            Effect.server.Run(mod != null && mod.successEffect != null && mod.successEffect.isValid ? mod.successEffect.resourcePath : RepairEffect, rec, 0u, Vector3.zero, Vector3.zero);
        }

        #endregion

        #region Recycling speed and output

        // Recycler keeps its efficiency in a private field read once when it starts, and runs RecycleThink on a repeating
        // invoke, so to change either we restart the invoke with our timing right after the vanilla start.
        private static readonly FieldInfo EfficiencyField = typeof(Recycler).GetField("lastFetchedEfficiency", BindingFlags.Instance | BindingFlags.NonPublic);
        private static readonly MethodInfo ThinkMethod = typeof(Recycler).GetMethod("RecycleThink", BindingFlags.Instance | BindingFlags.NonPublic);

        private void ApplyRecycleSettings(Recycler rec)
        {
            if (rec == null || rec.IsDestroyed || !rec.IsOn()) return;
            if (_cfg.RecycleOutputPercent > 0 && EfficiencyField != null)
                EfficiencyField.SetValue(rec, _cfg.RecycleOutputPercent / 100f);
            if (_cfg.RecycleSeconds > 0 && ThinkMethod != null)
            {
                var think = (Action)Delegate.CreateDelegate(typeof(Action), rec, ThinkMethod);
                rec.CancelInvoke(think);
                rec.InvokeRepeating(think, _cfg.RecycleSeconds, _cfg.RecycleSeconds);
            }
        }

        private string RecycleStatsText(Recycler rec)
        {
            float eff, dur;
            rec.GetRecyclerStats(out eff, out dur);
            if (_cfg.RecycleOutputPercent > 0) eff = _cfg.RecycleOutputPercent / 100f;
            if (_cfg.RecycleSeconds > 0) dur = _cfg.RecycleSeconds;
            return $"Recycler: {Mathf.RoundToInt(eff * 100f)}% output, {dur:0.#}s per item";
        }

        private static void SendMoved(Recycler rec)
        {
            rec.SendNetworkUpdateImmediate();
            BenchOf(rec)?.SendNetworkUpdateImmediate();
        }

        [ConsoleCommand("sandboxrecycler.rotate")]
        private void CmdRotate(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            var rec = LootedStation(player);
            if (rec == null || !IsOwner(player, rec)) return;
            var start = rec.transform.rotation;
            for (int i = 0; i < 3; i++)
            {
                rec.transform.rotation *= Quaternion.Euler(0f, 90f, 0f);
                if (HasRoom(rec)) { SendMoved(rec); return; }
            }
            rec.transform.rotation = start;
            player.ChatMessage("There's no room to turn it here.");
        }

        [ConsoleCommand("sandboxrecycler.pickup")]
        private void CmdPickup(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            var rec = LootedStation(player);
            if (rec == null || !IsOwner(player, rec)) return;
            if (rec.IsOn()) { player.ChatMessage("Switch the recycler off first."); return; }
            player.EndLooting();
            CuiHelper.DestroyUi(player, Ui);
            var bench = BenchOf(rec);
            var items = rec.inventory.itemList.ToList();
            if (bench != null) items.AddRange(bench.inventory.itemList);
            foreach (var item in items)
            {
                item.RemoveFromContainer();
                if (!item.MoveToContainer(player.inventory.containerMain) && !item.MoveToContainer(player.inventory.containerBelt))
                    item.Drop(player.eyes.position, player.eyes.BodyForward() * 2f);
            }
            var id = rec.net.ID.Value;
            if (bench != null && !bench.IsDestroyed) bench.Kill();
            rec.Kill();
            Untrack(id);
            Give(player);
            player.ChatMessage($"Picked up your {_cfg.ItemName}.");
        }

        #endregion

        #region Panel

        private void DrawPanel(BasePlayer player, Recycler rec)
        {
            if (player == null || !player.IsConnected || rec == null || rec.IsDestroyed) return;
            CuiHelper.DestroyUi(player, Ui);
            var looted = player.inventory?.loot?.entitySource;
            if (looted == null || StationOf(looted) != rec) return;
            var owner = IsOwner(player, rec);
            if (BenchOf(rec) != null) { DrawCornerPanel(player, rec, looted is RepairBench, owner); return; }

            var info = GetRepairInfo(player, rec);
            const float w = 380f, h = 150f;
            var ui = new CuiElementContainer();
            ui.Add(new CuiPanel
            {
                Image = { Color = "0.10 0.10 0.10 0.92" },
                RectTransform = { AnchorMin = "0.5 0", AnchorMax = "0.5 0", OffsetMin = $"{_cfg.PanelX} {_cfg.PanelY}", OffsetMax = $"{_cfg.PanelX + w} {_cfg.PanelY + h}" },
            }, "Hud.Menu", Ui);

            ui.Add(new CuiPanel { Image = { Color = "0.35 0.55 0.25 0.9" }, RectTransform = { AnchorMin = "0 1", AnchorMax = "1 1", OffsetMin = "0 -24", OffsetMax = "0 0" } }, Ui);
            Label(ui, "REPAIR", 13, "8 -24", "200 0", "0 1", "0 1", TextAnchor.MiddleLeft, "1 1 1 0.95", true);
            Label(ui, _cfg.ItemName.ToUpperInvariant(), 10, "-200 -24", "-8 0", "1 1", "1 1", TextAnchor.MiddleRight, "1 1 1 0.6", false);

            string line1, line2 = "", line2Color = "0.85 0.85 0.85 0.85";
            if (info.Item == null) line1 = info.Problem;
            else
            {
                var pct = info.Item.maxCondition > 0 ? Mathf.RoundToInt(info.Item.condition / info.Item.maxCondition * 100f) : 0;
                line1 = info.Item.info.condition.enabled
                    ? $"{info.Item.info.displayName.english}  ·  condition {pct}%"
                    : info.Item.info.displayName.english;
                if (info.Problem != null) { line2 = info.Problem; line2Color = "1 0.75 0.4 0.95"; }
                else if (info.Cost.Count == 0) line2 = "Costs: nothing";
                else
                {
                    line2 = "Costs: " + string.Join(", ", info.Cost.Select(c => $"{c.Value} {c.Key.displayName.english}"));
                    if (!info.CanAfford) { line2 += "  (you don't have enough)"; line2Color = "1 0.5 0.45 0.95"; }
                }
                if (info.Problem == null && rec.IsOn()) { line2 = "Switch the recycler off to repair."; line2Color = "1 0.75 0.4 0.95"; }
            }
            Label(ui, line1, 12, "10 -54", "-10 -28", "0 1", "1 1", TextAnchor.MiddleLeft, "1 1 1 0.95", false);
            Label(ui, line2, 11, "10 -80", "-10 -54", "0 1", "1 1", TextAnchor.UpperLeft, line2Color, false);
            Label(ui, (FullRepair ? "Full repair: restores max condition" : "Vanilla repair") + "   ·   " + RecycleStatsText(rec), 10, "10 -104", "-10 -84", "0 1", "1 1", TextAnchor.MiddleLeft, "1 1 1 0.5", false);

            var ready = info.Item != null && info.Problem == null && info.CanAfford && !rec.IsOn();
            Button(ui, "REPAIR", ready ? "sandboxrecycler.repair" : "", "8 8", "128 38", ready ? "0.35 0.55 0.25 1" : "0.25 0.25 0.25 0.9", ready ? "1 1 1 1" : "1 1 1 0.35");
            if (owner)
            {
                Button(ui, "ROTATE", "sandboxrecycler.rotate", $"{w - 214} 8", $"{w - 114} 38", "0.28 0.28 0.28 0.95", "1 1 1 0.9");
                Button(ui, "PICK UP", "sandboxrecycler.pickup", $"{w - 108} 8", $"{w - 8} 38", "0.45 0.22 0.18 0.95", "1 1 1 0.9");
            }
            CuiHelper.AddUi(player, ui);
        }

        // Corner stations use the real bench for repairs, so the panel only carries the settings line and owner buttons.
        private void DrawCornerPanel(BasePlayer player, Recycler rec, bool atBench, bool owner)
        {
            const float w = 380f, h = 82f;
            var ui = new CuiElementContainer();
            ui.Add(new CuiPanel
            {
                Image = { Color = "0.10 0.10 0.10 0.92" },
                RectTransform = { AnchorMin = "0.5 0", AnchorMax = "0.5 0", OffsetMin = $"{_cfg.PanelX} {_cfg.PanelY}", OffsetMax = $"{_cfg.PanelX + w} {_cfg.PanelY + h}" },
            }, "Hud.Menu", Ui);
            ui.Add(new CuiPanel { Image = { Color = "0.35 0.55 0.25 0.9" }, RectTransform = { AnchorMin = "0 1", AnchorMax = "1 1", OffsetMin = "0 -24", OffsetMax = "0 0" } }, Ui);
            Label(ui, _cfg.ItemName.ToUpperInvariant(), 13, "8 -24", "-8 0", "0 1", "1 1", TextAnchor.MiddleLeft, "1 1 1 0.95", true);
            var line = atBench
                ? (FullRepair ? "Full repair: items come back brand new, max condition restored" : "Vanilla repair (same as a normal repair bench)")
                : RecycleStatsText(rec);
            Label(ui, line, 11, "10 -48", "-10 -26", "0 1", "1 1", TextAnchor.MiddleLeft, "1 1 1 0.75", false);
            if (owner)
            {
                Button(ui, "ROTATE", "sandboxrecycler.rotate", $"{w - 214} 8", $"{w - 114} 32", "0.28 0.28 0.28 0.95", "1 1 1 0.9");
                Button(ui, "PICK UP", "sandboxrecycler.pickup", $"{w - 108} 8", $"{w - 8} 32", "0.45 0.22 0.18 0.95", "1 1 1 0.9");
            }
            CuiHelper.AddUi(player, ui);
        }

        private static void Label(CuiElementContainer ui, string text, int size, string min, string max, string aMin, string aMax, TextAnchor align, string color, bool bold)
        {
            ui.Add(new CuiLabel
            {
                Text = { Text = text ?? "", FontSize = size, Align = align, Color = color, Font = bold ? "robotocondensed-bold.ttf" : "robotocondensed-regular.ttf" },
                RectTransform = { AnchorMin = aMin, AnchorMax = aMax, OffsetMin = min, OffsetMax = max },
            }, Ui);
        }

        private static void Button(CuiElementContainer ui, string text, string command, string min, string max, string color, string textColor)
        {
            ui.Add(new CuiButton
            {
                Button = { Command = command, Color = color },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "0 0", OffsetMin = min, OffsetMax = max },
                Text = { Text = text, FontSize = 12, Align = TextAnchor.MiddleCenter, Color = textColor, Font = "robotocondensed-bold.ttf" },
            }, Ui);
        }

        #endregion

        #region Getting one

        private string CostText() =>
            _cfg.CraftCost.Count == 0 ? "free"
                : string.Join(", ", _cfg.CraftCost.Select(c => $"{c.Amount} {ItemManager.FindItemDefinition(c.ShortName)?.displayName.english ?? c.ShortName}"));

        [ChatCommand("recycler")]
        private void CmdRecycler(BasePlayer player, string command, string[] args)
        {
            var sub = args.Length > 0 ? args[0].ToLowerInvariant() : "";
            if (sub == "craft") { Craft(player); return; }
            if (sub == "give")
            {
                if (!IsAdmin(player)) { player.ChatMessage("Only admins can give the Repair Recycler."); return; }
                var target = args.Length > 1 ? FindPlayer(args[1]) : player;
                if (target == null) { player.ChatMessage($"No online player matches \"{args[1]}\"."); return; }
                int amount;
                if (args.Length < 3 || !int.TryParse(args[2], out amount)) amount = 1;
                Give(target, amount);
                player.ChatMessage($"Gave {amount} {_cfg.ItemName} to {target.displayName}.");
                return;
            }

            var lines = new List<string>
            {
                $"<size=16><color=#8fd16a>{_cfg.ItemName}</color></size>",
                "One item that places a recycler with a repair bench built in. Place it like a repair bench, then open it: recycle as normal, or put a damaged item in the first slot and press Repair.",
            };
            if (_cfg.AllowCraft) lines.Add($"<color=#ffd479>/recycler craft</color>  make one for {CostText()}");
            if (_cfg.MaxPerPlayer > 0) lines.Add($"You can have {_cfg.MaxPerPlayer} placed (you have {CountOwned(player.userID.Get())}). Pick one up from its panel to move it.");
            if (IsAdmin(player)) lines.Add("<color=#ffd479>/recycler give [player] [amount]</color>  admin: give one");
            player.ChatMessage(string.Join("\n", lines));
        }

        private void Craft(BasePlayer player)
        {
            if (!_cfg.AllowCraft && !IsAdmin(player)) { player.ChatMessage("Crafting the Repair Recycler is turned off on this server."); return; }
            if (_cfg.CraftNeedsPermission && !IsAdmin(player) && !permission.UserHasPermission(player.UserIDString, CraftPerm))
            { player.ChatMessage("You don't have permission to craft the Repair Recycler."); return; }

            var missing = new List<string>();
            foreach (var c in _cfg.CraftCost)
            {
                var def = ItemManager.FindItemDefinition(c.ShortName);
                if (def == null) continue;
                var have = player.inventory.GetAmount(def.itemid);
                if (have < c.Amount) missing.Add($"{c.Amount - have} {def.displayName.english}");
            }
            if (missing.Count > 0) { player.ChatMessage($"You need {string.Join(", ", missing)} more to make a {_cfg.ItemName}."); return; }
            foreach (var c in _cfg.CraftCost)
            {
                var def = ItemManager.FindItemDefinition(c.ShortName);
                if (def != null && c.Amount > 0) player.inventory.Take(null, def.itemid, c.Amount);
            }
            Give(player);
            Effect.server.Run(RepairEffect, player.transform.position);
            player.ChatMessage($"Made a {_cfg.ItemName}. It's in your inventory: place it like a repair bench.");
        }

        private static BasePlayer FindPlayer(string nameOrId)
        {
            ulong id;
            if (ulong.TryParse(nameOrId, out id)) return BasePlayer.FindByID(id);
            var lower = nameOrId.ToLowerInvariant();
            return BasePlayer.activePlayerList.FirstOrDefault(p => p.displayName.ToLowerInvariant() == lower)
                   ?? BasePlayer.activePlayerList.FirstOrDefault(p => p.displayName.ToLowerInvariant().Contains(lower));
        }

        // RCON / server console: sandboxrecycler.give <steamid or name> [amount]
        [ConsoleCommand("sandboxrecycler.give")]
        private void CcGive(ConsoleSystem.Arg arg)
        {
            if (arg.Connection != null && (arg.Player() == null || !IsAdmin(arg.Player()))) return;
            if (!arg.HasArgs()) { arg.ReplyWith("Usage: sandboxrecycler.give <steamid or name> [amount]"); return; }
            var target = FindPlayer(arg.GetString(0));
            if (target == null) { arg.ReplyWith($"No online player matches \"{arg.GetString(0)}\"."); return; }
            var amount = Math.Max(1, arg.GetInt(1, 1));
            Give(target, amount);
            target.ChatMessage($"You got {amount} {_cfg.ItemName}. Place it like a repair bench.");
            arg.ReplyWith($"Gave {amount} {_cfg.ItemName} to {target.displayName}.");
        }

        // RCON: sandboxrecycler.summary  — how many are placed and by whom.
        [ConsoleCommand("sandboxrecycler.summary")]
        private void CcSummary(ConsoleSystem.Arg arg)
        {
            if (arg.Connection != null) return;
            var byOwner = _data.Stations.Values.GroupBy(s => s.Owner)
                .Select(g => $"{covalence.Players.FindPlayerById(g.Key.ToString())?.Name ?? g.Key.ToString()}: {g.Count()}");
            arg.ReplyWith($"{_data.Stations.Count} placed. " + string.Join(", ", byOwner));
        }

        #endregion
    }
}
