// Rust Sandbox — Skins
// /skins opens a skin picker: pick one of your items (clothes, weapons, tools, deployables), then pick any approved
// Rust skin for it. The skin list comes from the game itself (Facepunch's skins and every approved workshop skin),
// so there's nothing to download or keep up to date. Each player's game draws the skin.
// By default players only see skins they own on Steam (plus Facepunch's free ones), the same check Rust's repair
// bench makes: since 7 August 2025 Facepunch's server rules forbid giving players paid skins they don't own.

using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Game.Rust.Cui;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("Rust Sandbox Skins", "RustSandbox", "1.1.0")]
    [Description("A skin picker for players' clothes, weapons and items, using every approved Rust skin.")]
    public class RustSandboxSkins : RustPlugin
    {
        private const string UsePerm = "rustsandboxskins.use";
        private const string Ui = "SandboxSkins.Root";
        private const int Cols = 8, Rows = 4, PerPage = Cols * Rows;

        #region Config

        private class PluginConfig
        {
            [JsonProperty("Everyone can use /skins (false = needs the rustsandboxskins.use permission)")] public bool Everyone = true;
            [JsonProperty("Skin IDs nobody can pick", ObjectCreationHandling = ObjectCreationHandling.Replace)] public List<ulong> Blocked = new List<ulong>();
            [JsonProperty("Item shortnames that can't be skinned", ObjectCreationHandling = ObjectCreationHandling.Replace)] public List<string> BlockedItems = new List<string>();
            [JsonProperty("Only skins the player owns (Facepunch's rules forbid giving out paid skins; false = every approved skin, at your own risk)")] public bool OwnedOnly = true;
        }

        private PluginConfig _cfg;

        protected override void LoadDefaultConfig() => Config.WriteObject(new PluginConfig(), true);

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                _cfg = Config.ReadObject<PluginConfig>() ?? new PluginConfig();
                if (_cfg.Blocked == null) _cfg.Blocked = new List<ulong>();
                if (_cfg.BlockedItems == null) _cfg.BlockedItems = new List<string>();
                SaveConfig();
            }
            catch (Exception ex)
            {
                PrintError($"Couldn't read the config file ({ex.Message}); using defaults and leaving the file untouched.");
                _cfg = new PluginConfig();
            }
        }

        protected override void SaveConfig() => Config.WriteObject(_cfg, true);

        #endregion

        #region Skin list

        private class SkinEntry
        {
            public ulong Id;
            public string Name;
            public int InvId;                    // Steam inventory definition id, what ownership is checked against
            public SteamInventoryItem Unlock;    // Facepunch skins: knows about free / DLC unlocks
        }

        private readonly Dictionary<int, List<SkinEntry>> _cache = new Dictionary<int, List<SkinEntry>>();

        // Facepunch's own skins (ItemSkinDirectory) plus every approved workshop skin the game knows about. Skins that
        // turn the item into a different item (redirects) are left out; the repair bench handles those itself.
        private List<SkinEntry> SkinsFor(ItemDefinition def)
        {
            List<SkinEntry> list;
            if (_cache.TryGetValue(def.itemid, out list)) return list;
            list = new List<SkinEntry>();
            var seen = new HashSet<ulong>();
            if (def.skins != null)
                foreach (var s in def.skins)
                {
                    var inv = s.invItem as ItemSkin;
                    if (inv != null && inv.Redirect != null) continue;
                    var id = (ulong)s.id;
                    if (id == 0 || !seen.Add(id)) continue;
                    list.Add(new SkinEntry { Id = id, Name = string.IsNullOrEmpty(s.name) ? id.ToString() : s.name, InvId = s.id, Unlock = s.invItem });
                }
            try
            {
                var approved = def.skins2;
                if (approved != null)
                    foreach (var s in approved)
                    {
                        // Same id the repair bench stamps on the item (ItemDefinition.FindSkin).
                        var id = s == null ? 0 : (s.WorkshopDownload != 0 ? s.WorkshopDownload : s.WorkshopId);
                        if (id == 0 || !seen.Add(id)) continue;
                        list.Add(new SkinEntry { Id = id, Name = s.Name, InvId = s.DefinitionId });
                    }
            }
            catch (Exception ex)
            {
                PrintWarning($"Couldn't read the approved skins for {def.shortname}: {ex.Message}");
            }
            list.RemoveAll(s => _cfg.Blocked.Contains(s.Id));
            list = list.OrderBy(s => s.Name).ToList();
            // Steam's skin list can arrive after the server starts; don't remember an empty answer.
            if (ApprovedCount() > 0) _cache[def.itemid] = list;
            return list;
        }

        // The Steam items a player owns, sent by their game when they join.
        private static HashSet<int> OwnedIds(BasePlayer p)
        {
            var set = new HashSet<int>();
            try
            {
                var items = p.blueprints?.steamInventory?.Items;
                if (items != null) foreach (var i in items) if (i != null) set.Add(i.DefinitionId);
            }
            catch { }
            return set;
        }

        // Same rule as PlayerBlueprints.CheckSkinOwnership, with the owned list worked out once per menu draw.
        private bool Owns(BasePlayer p, SkinEntry s, HashSet<int> owned)
        {
            if (!_cfg.OwnedOnly || s.Id == 0) return true;
            if (!p.DefaultSkinAccess) return p.AllSkinsUnlocked;
            if (s.InvId != 0 && owned.Contains(s.InvId)) return true;
            try { return s.Unlock != null && s.Unlock.HasUnlocked(p); }
            catch { return false; }
        }

        private List<SkinEntry> Available(BasePlayer p, ItemDefinition def, HashSet<int> owned)
        {
            var all = SkinsFor(def);
            return _cfg.OwnedOnly ? all.Where(s => Owns(p, s, owned)).ToList() : all;
        }

        private bool Skinnable(BasePlayer p, Item item, HashSet<int> owned) =>
            item != null && !_cfg.BlockedItems.Contains(item.info.shortname) && Available(p, item.info, owned).Count > 0;

        #endregion

        #region Lifecycle and commands

        private void Init() => permission.RegisterPermission(UsePerm, this);

        private static int ApprovedCount()
        {
            try { return PlatformService.Instance?.ItemDefinitions?.Count() ?? 0; }
            catch { return 0; }
        }

        private void OnServerInitialized()
        {
            var n = ApprovedCount();
            if (n > 0) Puts($"{n} approved skins available.");
            else PrintWarning("Steam's approved skin list hasn't loaded yet, so only Facepunch's own skins show for now. It's picked up automatically once it loads.");
        }

        private void Unload()
        {
            foreach (var p in BasePlayer.activePlayerList) CuiHelper.DestroyUi(p, Ui);
        }

        private void OnPlayerDisconnected(BasePlayer player)
        {
            CuiHelper.DestroyUi(player, Ui);
            _state.Remove(player.userID.Get());
        }

        private bool CanUse(BasePlayer p) => _cfg.Everyone || p.IsAdmin || permission.UserHasPermission(p.UserIDString, UsePerm);

        private class State
        {
            public ulong ItemUid;
            public int Page;
            public int ItemPage;
            public string Search = "";
        }

        private readonly Dictionary<ulong, State> _state = new Dictionary<ulong, State>();

        private State StateOf(BasePlayer p)
        {
            State s;
            if (!_state.TryGetValue(p.userID.Get(), out s)) _state[p.userID.Get()] = s = new State();
            return s;
        }

        [ChatCommand("skins")]
        private void CmdSkins(BasePlayer player, string command, string[] args)
        {
            if (!CanUse(player)) { player.ChatMessage("You don't have permission to use /skins."); return; }
            var s = StateOf(player);
            s.Page = 0;
            s.Search = "";
            // Start on the item in your hands, if it can be skinned.
            var held = player.GetActiveItem();
            if (Skinnable(player, held, OwnedIds(player))) s.ItemUid = held.uid.Value;
            else if (FindItem(player, s.ItemUid) == null) s.ItemUid = 0;
            Draw(player);
        }

        [ConsoleCommand("sandboxskins.ui")]
        private void CcUi(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !CanUse(player)) return;
            var s = StateOf(player);
            switch (arg.GetString(0))
            {
                case "close":
                    CuiHelper.DestroyUi(player, Ui);
                    return;
                case "item":
                    s.ItemUid = arg.GetULong(1);
                    s.Page = 0;
                    break;
                case "page":
                    s.Page = Math.Max(0, s.Page + arg.GetInt(1));
                    break;
                case "itempage":
                    s.ItemPage = Math.Max(0, s.ItemPage + arg.GetInt(1));
                    break;
                case "search":
                    s.Search = arg.HasArgs(2) ? string.Join(" ", arg.Args.Skip(1)).Trim() : "";
                    s.Page = 0;
                    break;
                case "apply":
                    Apply(player, FindItem(player, s.ItemUid), arg.GetULong(1));
                    break;
            }
            Draw(player);
        }

        private static IEnumerable<Item> AllItems(BasePlayer p) =>
            p.inventory.containerWear.itemList
                .Concat(p.inventory.containerBelt.itemList)
                .Concat(p.inventory.containerMain.itemList);

        private static Item FindItem(BasePlayer p, ulong uid) =>
            uid == 0 ? null : AllItems(p).FirstOrDefault(i => i.uid.Value == uid);

        private void Apply(BasePlayer player, Item item, ulong skin)
        {
            if (item == null) return;
            if (skin != 0 && !Available(player, item.info, OwnedIds(player)).Any(s => s.Id == skin)) return;
            if (Interface.CallHook("OnSandboxSkinApply", player, item, skin) != null) return;
            item.skin = skin;
            item.MarkDirty();
            var held = item.GetHeldEntity();
            if (held != null)
            {
                held.skinID = skin;
                held.SendNetworkUpdate();
            }
            // Worn clothing is drawn from the player's own network update.
            if (item.parent == player.inventory.containerWear) player.SendNetworkUpdate();
        }

        #endregion

        #region UI

        private void Draw(BasePlayer player)
        {
            CuiHelper.DestroyUi(player, Ui);
            var s = StateOf(player);
            var owned = OwnedIds(player);
            var items = AllItems(player).Where(i => Skinnable(player, i, owned)).ToList();
            var item = items.FirstOrDefault(i => i.uid.Value == s.ItemUid);
            if (item == null && items.Count > 0) { item = items[0]; s.ItemUid = item.uid.Value; }

            var ui = new CuiElementContainer();
            ui.Add(new CuiPanel
            {
                Image = { Color = "0 0 0 0.6" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" },
                CursorEnabled = true,
                KeyboardEnabled = true,
            }, "Overlay", Ui);
            var main = Ui + ".Main";
            ui.Add(new CuiPanel
            {
                Image = { Color = "0.10 0.10 0.10 0.98" },
                RectTransform = { AnchorMin = "0.5 0.5", AnchorMax = "0.5 0.5", OffsetMin = "-470 -270", OffsetMax = "470 270" },
            }, Ui, main);

            // Header
            ui.Add(new CuiPanel { Image = { Color = "0.35 0.55 0.25 0.9" }, RectTransform = { AnchorMin = "0 1", AnchorMax = "1 1", OffsetMin = "0 -36", OffsetMax = "0 0" } }, main);
            Text(ui, main, "SKINS", 16, "12 -36", "200 0", "0 1", "0 1", TextAnchor.MiddleLeft, true);
            Text(ui, main, _cfg.OwnedOnly ? "Pick an item on the left, then a skin. Shows the skins you own on Steam." : "Pick an item on the left, then a skin. Any approved Rust skin works, owned or not.", 11, "120 -36", "-60 0", "0 1", "1 1", TextAnchor.MiddleLeft, false, "1 1 1 0.7");
            Button(ui, main, "✕", "sandboxskins.ui close", "-36 -36", "0 0", "1 1", "1 1", "0.6 0.2 0.15 0.9");

            // Item list (left): 3 columns of icons.
            const int itemCols = 3, itemRows = 6, itemsPerPage = itemCols * itemRows;
            var maxItemPage = Math.Max(0, (items.Count - 1) / itemsPerPage);
            if (s.ItemPage > maxItemPage) s.ItemPage = maxItemPage;
            Text(ui, main, "YOUR ITEMS", 11, "12 -60", "220 -40", "0 1", "0 1", TextAnchor.MiddleLeft, true, "1 1 1 0.6");
            if (items.Count == 0)
                Text(ui, main, _cfg.OwnedOnly ? "You don't own skins for anything you're carrying." : "Nothing you're carrying can be skinned.", 11, "12 -120", "220 -64", "0 1", "0 1", TextAnchor.UpperLeft, false, "1 1 1 0.7");
            var shown = items.Skip(s.ItemPage * itemsPerPage).Take(itemsPerPage).ToList();
            for (int i = 0; i < shown.Count; i++)
            {
                var it = shown[i];
                float x = 12 + (i % itemCols) * 70, y = -64 - (i / itemCols) * 70;
                var selected = it.uid.Value == s.ItemUid;
                Icon(ui, main, it.info.itemid, it.skin, $"{x} {y - 64}", $"{x + 64} {y}", selected ? "0.35 0.55 0.25 0.9" : "0.2 0.2 0.2 0.9", "sandboxskins.ui item " + it.uid.Value);
                if (it.parent == player.inventory.containerWear)
                    Text(ui, main, "worn", 9, $"{x + 2} {y - 64}", $"{x + 62} {y - 50}", "0 1", "0 1", TextAnchor.LowerRight, false, "1 1 1 0.6");
            }
            if (maxItemPage > 0)
            {
                Button(ui, main, "▲", "sandboxskins.ui itempage -1", "12 26", "104 50", "0 0", "0 0", s.ItemPage > 0 ? "0.28 0.28 0.28 0.95" : "0.18 0.18 0.18 0.6");
                Button(ui, main, "▼", "sandboxskins.ui itempage 1", "116 26", "208 50", "0 0", "0 0", s.ItemPage < maxItemPage ? "0.28 0.28 0.28 0.95" : "0.18 0.18 0.18 0.6");
            }
            ui.Add(new CuiPanel { Image = { Color = "1 1 1 0.08" }, RectTransform = { AnchorMin = "0 0", AnchorMax = "0 1", OffsetMin = "226 12", OffsetMax = "227 -44" } }, main);

            if (item != null) DrawSkins(ui, main, item, s, Available(player, item.info, owned));
            CuiHelper.AddUi(player, ui);
        }

        private void DrawSkins(CuiElementContainer ui, string main, Item item, State s, List<SkinEntry> all)
        {
            var list = string.IsNullOrEmpty(s.Search) ? all
                : all.Where(k => k.Name.IndexOf(s.Search, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            var maxPage = Math.Max(0, list.Count / PerPage);
            if (s.Page > maxPage) s.Page = maxPage;

            Text(ui, main, $"{item.info.displayName.english.ToUpperInvariant()}  ·  {all.Count} skins", 11, "240 -60", "-260 -40", "0 1", "1 1", TextAnchor.MiddleLeft, true, "1 1 1 0.6");

            // Search box
            var box = main + ".Search";
            ui.Add(new CuiPanel { Image = { Color = "0.2 0.2 0.2 0.95" }, RectTransform = { AnchorMin = "1 1", AnchorMax = "1 1", OffsetMin = "-250 -62", OffsetMax = "-12 -40" } }, main, box);
            ui.Add(new CuiElement
            {
                Parent = box,
                Components =
                {
                    new CuiInputFieldComponent { Text = s.Search, FontSize = 11, Align = TextAnchor.MiddleLeft, Command = "sandboxskins.ui search", CharsLimit = 40, Color = "1 1 1 0.9" },
                    new CuiRectTransformComponent { AnchorMin = "0 0", AnchorMax = "1 1", OffsetMin = "6 0", OffsetMax = "-6 0" },
                },
            });
            if (string.IsNullOrEmpty(s.Search))
                Text(ui, box, "Search skins…", 11, "6 0", "-6 0", "0 0", "1 1", TextAnchor.MiddleLeft, false, "1 1 1 0.3");

            // Grid: the first cell on page one is the default (no skin).
            var cells = new List<SkinEntry>();
            if (s.Page == 0 && string.IsNullOrEmpty(s.Search)) cells.Add(new SkinEntry { Id = 0, Name = "Default" });
            var skip = s.Page * PerPage - (string.IsNullOrEmpty(s.Search) && s.Page > 0 ? 1 : 0);
            cells.AddRange(list.Skip(Math.Max(0, skip)).Take(PerPage - cells.Count));
            const float cw = 84f, ch = 100f;
            for (int i = 0; i < cells.Count; i++)
            {
                var k = cells[i];
                float x = 240 + (i % Cols) * (cw + 4), y = -70 - (i / Cols) * (ch + 4);
                var on = item.skin == k.Id;
                Icon(ui, main, item.info.itemid, k.Id, $"{x} {y - cw}", $"{x + cw} {y}", on ? "0.35 0.55 0.25 0.9" : "0.2 0.2 0.2 0.9", "sandboxskins.ui apply " + k.Id);
                Text(ui, main, k.Name, 9, $"{x} {y - ch}", $"{x + cw} {y - cw}", "0 1", "0 1", TextAnchor.UpperCenter, false, on ? "0.6 0.9 0.5 1" : "1 1 1 0.75");
            }
            if (cells.Count == 0)
                Text(ui, main, "No skins match that search.", 12, "240 -120", "-12 -70", "0 1", "1 1", TextAnchor.UpperLeft, false, "1 1 1 0.7");

            // Pages
            Text(ui, main, $"Page {s.Page + 1} of {maxPage + 1}", 11, "-200 12", "200 40", "0.5 0", "0.5 0", TextAnchor.MiddleCenter, false, "1 1 1 0.7");
            Button(ui, main, "◀ PREV", "sandboxskins.ui page -1", "-330 12", "-210 40", "0.5 0", "0.5 0", s.Page > 0 ? "0.28 0.28 0.28 0.95" : "0.18 0.18 0.18 0.6");
            Button(ui, main, "NEXT ▶", "sandboxskins.ui page 1", "210 12", "330 40", "0.5 0", "0.5 0", s.Page < maxPage ? "0.28 0.28 0.28 0.95" : "0.18 0.18 0.18 0.6");
        }

        private static void Icon(CuiElementContainer ui, string parent, int itemId, ulong skin, string min, string max, string bg, string command)
        {
            var cell = CuiHelper.GetGuid();
            ui.Add(new CuiButton
            {
                Button = { Command = command, Color = bg },
                RectTransform = { AnchorMin = "0 1", AnchorMax = "0 1", OffsetMin = min, OffsetMax = max },
                Text = { Text = "" },
            }, parent, cell);
            ui.Add(new CuiElement
            {
                Parent = cell,
                Components =
                {
                    new CuiImageComponent { ItemId = itemId, SkinId = skin },
                    new CuiRectTransformComponent { AnchorMin = "0.08 0.08", AnchorMax = "0.92 0.92" },
                },
            });
        }

        private static void Text(CuiElementContainer ui, string parent, string text, int size, string min, string max, string aMin, string aMax, TextAnchor align, bool bold, string color = "1 1 1 0.95")
        {
            ui.Add(new CuiLabel
            {
                Text = { Text = text ?? "", FontSize = size, Align = align, Color = color, Font = bold ? "robotocondensed-bold.ttf" : "robotocondensed-regular.ttf" },
                RectTransform = { AnchorMin = aMin, AnchorMax = aMax, OffsetMin = min, OffsetMax = max },
            }, parent);
        }

        private static void Button(CuiElementContainer ui, string parent, string text, string command, string min, string max, string aMin, string aMax, string color)
        {
            ui.Add(new CuiButton
            {
                Button = { Command = command, Color = color },
                RectTransform = { AnchorMin = aMin, AnchorMax = aMax, OffsetMin = min, OffsetMax = max },
                Text = { Text = text, FontSize = 12, Align = TextAnchor.MiddleCenter, Color = "1 1 1 0.9", Font = "robotocondensed-bold.ttf" },
            }, parent);
        }

        #endregion
    }
}
