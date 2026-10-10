// Rust Sandbox — Daily Rewards
// Once a day every player can spin for a random item: a reel of item icons scrolls across the screen,
// slows down and lands on the prize, which goes straight into their inventory. Rarity tiers decide the
// odds (a pile of dung is far likelier than a rocket launcher), and claiming several days in a row
// guarantees a better tier. Days reset at midnight server time; streaks reset on a map wipe.
//
// Install: copy this file to oxide/plugins (or carbon/plugins). The loot table is in
// oxide/config/RustSandboxDaily.json (or edit it on the Rust Sandbox "Daily rewards" page).
// Players type /daily in chat.

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
    [Info("Rust Sandbox Daily Rewards", "RustSandbox", "1.1.1")]
    [Description("A daily spin for a random item with rarity tiers, streak bonuses and a spinning reel.")]
    public class RustSandboxDaily : RustPlugin
    {
        #region Config

        private PluginConfig _config;

        private class RewardItem
        {
            [JsonProperty("Item")] public string Item = "";
            [JsonProperty("Min")] public int Min = 1;
            [JsonProperty("Max")] public int Max = 1;
            [JsonProperty("Weight")] public float Weight = 1f;
            [JsonProperty("Skin")] public ulong Skin;
        }

        private class Tier
        {
            [JsonProperty("Name")] public string Name = "";
            [JsonProperty("Chance (relative)")] public float Chance = 1f;
            [JsonProperty("Color (r g b)")] public string Color = "0.7 0.7 0.7";
            [JsonProperty("Announce wins to everyone")] public bool Announce;
            [JsonProperty("Items")] public List<RewardItem> Items = new List<RewardItem>();
        }

        private class PluginConfig
        {
            [JsonProperty("Day resets at (server time, HH:mm)")] public string ResetTime = "00:00";
            [JsonProperty("Show the reward popup when a player wakes up")] public bool PopupOnJoin = true;
            [JsonProperty("Seconds after waking before the popup")] public float PopupDelay = 4f;
            [JsonProperty("Spin length (seconds)")] public float SpinSeconds = 5f;
            [JsonProperty("Show a glowing reminder icon until the reward is claimed")] public bool Reminder = true;
            [JsonProperty("Reminder icon offset from the top-right corner (x y)")] public string ReminderOffset = "-368 -56";
            [JsonProperty("Streak: every N days in a row guarantees at least the tier below (0 = off)")] public int StreakDays = 7;
            [JsonProperty("Streak: guaranteed tier")] public string StreakTier = "Rare";
            [JsonProperty("Reset streaks on map wipe")] public bool ResetStreakOnWipe = true;
            [JsonProperty("Chat prefix")] public string ChatPrefix = "<color=#ffd479>[Daily]</color>";
            [JsonProperty("Chat icon (SteamID, 0 = server)")] public ulong ChatIcon = 0;
            [JsonProperty("Tiers (rarest last)", ObjectCreationHandling = ObjectCreationHandling.Replace)] public List<Tier> Tiers = DefaultTiers();
        }

        private static RewardItem I(string item, int min, int max = 0, float weight = 1f) =>
            new RewardItem { Item = item, Min = min, Max = max <= 0 ? min : max, Weight = weight };

        private static List<Tier> DefaultTiers() => new List<Tier>
        {
            new Tier
            {
                Name = "Common", Chance = 60, Color = "0.62 0.66 0.70",
                Items = new List<RewardItem>
                {
                    I("horsedung", 1, 3), I("cloth", 20, 60), I("stones", 300, 1000), I("wood", 300, 1000), I("lowgradefuel", 20, 60),
                    I("fat.animal", 20, 50), I("bone.fragments", 30, 80), I("charcoal", 100, 300), I("leather", 10, 30), I("can.beans", 1, 3),
                    I("apple", 2, 5), I("mushroom", 3, 8), I("torch", 1), I("spear.wooden", 1), I("arrow.wooden", 10, 30),
                },
            },
            new Tier
            {
                Name = "Uncommon", Chance = 25, Color = "0.36 0.72 0.36",
                Items = new List<RewardItem>
                {
                    I("scrap", 50, 150), I("metal.fragments", 200, 600), I("sulfur", 150, 400), I("gunpowder", 50, 150), I("largemedkit", 1, 2),
                    I("syringe.medical", 2, 4), I("bandage", 3, 6), I("rope", 2, 5), I("sewingkit", 2, 5), I("gears", 1, 3),
                    I("ammo.pistol", 30, 80), I("crossbow", 1), I("bow.hunting", 1), I("salvaged.sword", 1), I("coffeecan.helmet", 1),
                },
            },
            new Tier
            {
                Name = "Rare", Chance = 12, Color = "0.30 0.55 0.95",
                Items = new List<RewardItem>
                {
                    I("scrap", 250, 500), I("metal.refined", 25, 75), I("ammo.rifle", 60, 128), I("ammo.shotgun", 24, 48), I("pistol.semiauto", 1),
                    I("pistol.revolver", 1), I("shotgun.pump", 1), I("smg.thompson", 1), I("rifle.bolt", 1, 1, 0.5f), I("roadsign.jacket", 1),
                    I("roadsign.kilt", 1), I("metal.facemask", 1), I("metal.plate.torso", 1), I("hazmatsuit", 1), I("chainsaw", 1),
                    I("jackhammer", 1), I("grenade.f1", 2, 4), I("keycard_green", 1), I("keycard_blue", 1),
                },
            },
            new Tier
            {
                Name = "Epic", Chance = 3, Color = "0.75 0.35 0.95", Announce = true,
                Items = new List<RewardItem>
                {
                    I("rifle.ak", 1), I("rifle.lr300", 1), I("rifle.l96", 1, 1, 0.6f), I("smg.mp5", 1), I("lmg.m249", 1, 1, 0.4f),
                    I("rocket.launcher", 1, 1, 0.6f), I("ammo.rocket.basic", 2, 4, 0.6f), I("explosive.timed", 1, 2, 0.5f), I("explosive.satchel", 2, 4),
                    I("heavy.plate.helmet", 1), I("keycard_red", 1), I("supply.signal", 1), I("scrap", 1000, 1500),
                },
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
                if (_config.Tiers == null || _config.Tiers.Count == 0) _config.Tiers = DefaultTiers();
                var before = _config.Tiers.Count;
                _config.Tiers = Dedupe(_config.Tiers, t => t.Name, DefaultTiers());
                if (_config.Tiers.Count < before) Puts($"Removed {before - _config.Tiers.Count} duplicate copies of the reward tiers from the config.");
                foreach (var t in _config.Tiers)
                    if (t.Items == null) t.Items = new List<RewardItem>();
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

        #region Lang

        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["Title"] = "DAILY REWARD",
                ["Streak"] = "Streak: <color=#ffd479>{0} day(s)</color>",
                ["Ready"] = "Spin to win a random item. One spin a day, resets at {0}.",
                ["StreakNext"] = "Claim {0} more day(s) in a row for a guaranteed <color=#{1}>{2}</color> or better.",
                ["StreakNow"] = "Day {0} streak bonus: this spin is at least <color=#{1}>{2}</color>!",
                ["Spin"] = "SPIN",
                ["Later"] = "LATER",
                ["Close"] = "CLOSE",
                ["Spinning"] = "Spinning...",
                ["Won"] = "You won <color=#{0}>{1}x {2}</color> ({3})!",
                ["WonChat"] = "You won <color=#{0}>{1}x {2}</color> ({3}). It's in your inventory.",
                ["Dropped"] = "Your inventory was full, so part of it was dropped at your feet.",
                ["Claimed"] = "You've already claimed today's reward. Next spin in <color=#ffd479>{0}</color>.",
                ["LastWin"] = "Today you won {0}x {1}.",
                ["Announce"] = "{0} just won <color=#{1}>{2}x {3}</color> from their daily spin!",
                ["JoinReady"] = "Your daily reward is ready! Type <color=#ffd479>/daily</color> to spin.",
                ["Odds"] = "Odds: {0}",
                ["Empty"] = "The daily reward table is empty. Ask an admin to set it up.",
                ["ReminderLabel"] = "DAILY",
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

        private const string DataFile = "RustSandboxDaily";

        private class PlayerClaim
        {
            public string Name = "";
            public string LastDay = "";
            public int Streak;
            public int Claims;
            public string LastItem = "";
            public int LastAmount;
        }

        private class Win
        {
            public DateTime At;
            public string Id = "";
            public string Name = "";
            public string Item = "";
            public string ItemName = "";
            public int Amount;
            public string Tier = "";
        }

        private class StoredData
        {
            public Dictionary<ulong, PlayerClaim> Players = new Dictionary<ulong, PlayerClaim>();
            public List<Win> History = new List<Win>();
        }

        private StoredData _data = new StoredData();

        private void LoadData()
        {
            try { _data = Interface.Oxide.DataFileSystem.ReadObject<StoredData>(DataFile) ?? new StoredData(); }
            catch (Exception ex)
            {
                PrintError($"Couldn't read the data file ({ex.Message}); starting empty.");
                _data = new StoredData();
            }
            if (_data.Players == null) _data.Players = new Dictionary<ulong, PlayerClaim>();
            if (_data.History == null) _data.History = new List<Win>();
        }

        private void SaveData() => Interface.Oxide.DataFileSystem.WriteObject(DataFile, _data);

        private PlayerClaim Claim(BasePlayer player)
        {
            PlayerClaim c;
            if (!_data.Players.TryGetValue(player.userID, out c)) _data.Players[player.userID] = c = new PlayerClaim();
            c.Name = player.displayName;
            return c;
        }

        #endregion

        #region Days

        private TimeSpan ResetOffset
        {
            get
            {
                TimeSpan t;
                return TimeSpan.TryParseExact(_config.ResetTime ?? "00:00", @"h\:mm", CultureInfo.InvariantCulture, out t) ? t : TimeSpan.Zero;
            }
        }

        /// <summary>The reward day it is now: changes at the reset time (midnight by default), in server time.</summary>
        private DateTime Today => (DateTime.Now - ResetOffset).Date;

        private static string Key(DateTime day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        private bool ClaimedToday(PlayerClaim c) => c.LastDay == Key(Today);

        private TimeSpan UntilNext => Today.AddDays(1) + ResetOffset - DateTime.Now;

        private static string FormatSpan(TimeSpan t) =>
            t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes}m" : $"{Math.Max(1, t.Minutes)}m";

        #endregion

        #region Rolling

        private class Prize
        {
            public Tier Tier;
            public RewardItem Entry;
            public ItemDefinition Def;
            public int Amount;
        }

        private List<Tier> UsableTiers() =>
            _config.Tiers.Where(t => t.Chance > 0 && t.Items.Any(i => ItemManager.FindItemDefinition(i.Item) != null)).ToList();

        private Prize Roll(int minTier)
        {
            var tiers = UsableTiers();
            if (tiers.Count == 0) return null;
            var pool = tiers.Where(t => _config.Tiers.IndexOf(t) >= minTier).ToList();
            if (pool.Count == 0) pool = tiers;
            var tier = Pick(pool, t => t.Chance);
            var items = tier.Items.Where(i => i.Weight > 0 && ItemManager.FindItemDefinition(i.Item) != null).ToList();
            var entry = Pick(items, i => i.Weight);
            var amount = UnityEngine.Random.Range(Math.Max(1, entry.Min), Math.Max(entry.Min, entry.Max) + 1);
            return new Prize { Tier = tier, Entry = entry, Def = ItemManager.FindItemDefinition(entry.Item), Amount = amount };
        }

        private static T Pick<T>(List<T> list, Func<T, float> weight)
        {
            var total = list.Sum(x => Math.Max(0f, weight(x)));
            var r = UnityEngine.Random.value * total;
            foreach (var x in list)
            {
                r -= Math.Max(0f, weight(x));
                if (r <= 0) return x;
            }
            return list[list.Count - 1];
        }

        private int StreakTierIndex() =>
            Math.Max(0, _config.Tiers.FindIndex(t => string.Equals(t.Name, _config.StreakTier, StringComparison.OrdinalIgnoreCase)));

        private bool StreakBonusDue(int streakAfterClaim) =>
            _config.StreakDays > 0 && streakAfterClaim > 0 && streakAfterClaim % _config.StreakDays == 0;

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

        private static string Hex(string rgb)
        {
            var p = (rgb ?? "").Split(' ');
            float r = 0.7f, g = 0.7f, b = 0.7f;
            if (p.Length >= 3)
            {
                float.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out r);
                float.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out g);
                float.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out b);
            }
            return $"{(int)(Mathf.Clamp01(r) * 255):x2}{(int)(Mathf.Clamp01(g) * 255):x2}{(int)(Mathf.Clamp01(b) * 255):x2}";
        }

        private static string ItemName(ItemDefinition def) =>
            def.displayName != null && !string.IsNullOrEmpty(def.displayName.english) ? def.displayName.english : def.shortname;

        #endregion

        #region Lifecycle

        private void Init()
        {
            LoadData();
        }

        private void Unload()
        {
            foreach (var kv in _spins.ToList()) FinishSpin(kv.Key, kv.Value, true);
            foreach (var p in BasePlayer.activePlayerList)
            {
                CuiHelper.DestroyUi(p, Root);
                CuiHelper.DestroyUi(p, ReminderUi);
            }
            SaveData();
        }

        private void OnNewSave(string filename)
        {
            if (!_config.ResetStreakOnWipe) return;
            foreach (var c in _data.Players.Values) c.Streak = 0;
            SaveData();
            Puts("Map wipe: daily reward streaks reset.");
        }

        private void OnPlayerSleepEnded(BasePlayer player)
        {
            if (player == null || !player.userID.IsSteamId()) return;
            UpdateReminder(player);
            if (!_config.PopupOnJoin) return;
            if (ClaimedToday(Claim(player)) || _shownToday.Contains(player.userID)) return;
            _shownToday.Add(player.userID);
            timer.Once(Math.Max(0f, _config.PopupDelay), () =>
            {
                if (player == null || !player.IsConnected || ClaimedToday(Claim(player))) return;
                Say(player, Msg("JoinReady", player.UserIDString));
                OpenMenu(player);
            });
        }

        private void OnPlayerDisconnected(BasePlayer player, string reason)
        {
            Spin spin;
            if (player != null && _spins.TryGetValue(player.userID, out spin)) FinishSpin(player.userID, spin, true);
            if (player != null) _reminded.Remove(player.userID);
        }

        // Players get the automatic popup once per reward day (not every time they wake up).
        private readonly HashSet<ulong> _shownToday = new HashSet<ulong>();
        private string _shownDay = "";

        private void OnServerInitialized()
        {
            _shownDay = Key(Today);
            timer.Every(60f, () =>
            {
                var key = Key(Today);
                if (key == _shownDay) return;
                _shownDay = key;
                _shownToday.Clear();
                // A new reward day: everyone online gets the reminder again.
                foreach (var p in BasePlayer.activePlayerList) UpdateReminder(p);
            });
            timer.Every(0.8f, PulseReminders);
            foreach (var p in BasePlayer.activePlayerList) UpdateReminder(p);
        }

        #endregion

        #region Menu and spin

        private const string Root = "SandboxDaily.Root";
        private const string Main = Root + ".Main";
        private const string Reel = Root + ".Reel";
        private const float SlotPitch = 120f;
        private const float SlotSize = 104f;
        private const float ReelCenter = 320f;
        private const float ReelHalfWidth = 250f;
        private const float ReelLeft = ReelCenter - ReelHalfWidth - 10f;
        private const float ReelRight = ReelCenter + ReelHalfWidth + 10f;
        private const float ReelBottom = 170f;
        private const float ReelTop = 314f;

        private class Spin
        {
            public Prize Prize;
            public List<ItemDefinition> Strip = new List<ItemDefinition>();
            public List<Tier> StripTiers = new List<Tier>();
            public int WinnerIndex;
            public float Start;
            public float From;
            public float To;
            public int LastSlot = -1;
            public float LastTick;
            public Timer Timer;
            public bool Done;
        }

        private readonly Dictionary<ulong, Spin> _spins = new Dictionary<ulong, Spin>();

        [ChatCommand("daily")]
        private void CmdDaily(BasePlayer player, string command, string[] args) => OpenMenu(player);

        [ConsoleCommand("daily.spin")]
        private void CcSpin(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player != null) StartSpin(player);
        }

        [ConsoleCommand("daily.close")]
        private void CcClose(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null) return;
            Spin spin;
            if (_spins.TryGetValue(player.userID, out spin) && !spin.Done) return; // let the reel finish
            _spins.Remove(player.userID);
            CuiHelper.DestroyUi(player, Root);
            UpdateReminder(player);
        }

        [ConsoleCommand("daily.open")]
        private void CcOpen(ConsoleSystem.Arg arg) => OpenMenu(arg.Player());

        // ---- reminder: a glowing present by the top of the screen until the day's spin is claimed

        private const string ReminderUi = "SandboxDaily.Reminder";
        private const string ReminderGlow = ReminderUi + ".Glow";
        private readonly HashSet<ulong> _reminded = new HashSet<ulong>();
        private bool _glowOn;

        private void UpdateReminder(BasePlayer player)
        {
            if (player == null || !player.IsConnected) return;
            var want = _config.Reminder && !player.IsSleeping() && !ClaimedToday(Claim(player)) && UsableTiers().Count > 0;
            if (!want)
            {
                if (_reminded.Remove(player.userID)) CuiHelper.DestroyUi(player, ReminderUi);
                return;
            }
            if (_reminded.Contains(player.userID)) return;
            _reminded.Add(player.userID);

            float x, y;
            var off = (_config.ReminderOffset ?? "").Split(' ');
            if (off.Length < 2 || !float.TryParse(off[0], NumberStyles.Float, CultureInfo.InvariantCulture, out x)
                || !float.TryParse(off[1], NumberStyles.Float, CultureInfo.InvariantCulture, out y)) { x = -368; y = -56; }
            var present = ItemManager.FindItemDefinition("xmas.present.medium") ?? ItemManager.FindItemDefinition("wrappedgift");
            var ui = new CuiElementContainer();
            ui.Add(new CuiButton
            {
                Button = { Command = "daily.open", Color = "0.1 0.1 0.1 0.75" },
                Text = { Text = "" },
                RectTransform = { AnchorMin = "1 1", AnchorMax = "1 1", OffsetMin = $"{x} {y}", OffsetMax = $"{x + 42} {y + 42}" },
            }, "Hud", ReminderUi);
            if (present != null)
                ui.Add(new CuiElement
                {
                    Parent = ReminderUi,
                    Components =
                    {
                        new CuiImageComponent { ItemId = present.itemid },
                        new CuiRectTransformComponent { AnchorMin = "0 0", AnchorMax = "1 1", OffsetMin = "5 5", OffsetMax = "-5 -5" },
                    },
                });
            ui.Add(new CuiLabel
            {
                Text = { Text = Msg("ReminderLabel", player.UserIDString), FontSize = 8, Align = TextAnchor.MiddleCenter, Color = "1 0.85 0.4 1", Font = "robotocondensed-bold.ttf" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 0", OffsetMin = "-6 -13", OffsetMax = "6 0" },
            }, ReminderUi);
            CuiHelper.DestroyUi(player, ReminderUi);
            CuiHelper.AddUi(player, ui);
        }

        /// <summary>A gold glow fades in and out behind every reminder icon.</summary>
        private void PulseReminders()
        {
            if (_reminded.Count == 0) return;
            _glowOn = !_glowOn;
            foreach (var id in _reminded.ToList())
            {
                var player = BasePlayer.FindByID(id);
                if (player == null || !player.IsConnected) { _reminded.Remove(id); continue; }
                CuiHelper.DestroyUi(player, ReminderGlow);
                if (!_glowOn) continue;
                var ui = new CuiElementContainer();
                ui.Add(new CuiElement
                {
                    Name = ReminderGlow,
                    Parent = ReminderUi,
                    FadeOut = 0.35f,
                    Components =
                    {
                        new CuiImageComponent { Color = "1 0.75 0.2 0.45", FadeIn = 0.35f },
                        new CuiRectTransformComponent { AnchorMin = "0 0", AnchorMax = "1 1" },
                    },
                });
                CuiHelper.AddUi(player, ui);
            }
        }

        private void OpenMenu(BasePlayer player)
        {
            if (player == null || !player.IsConnected) return;
            if (_spins.ContainsKey(player.userID)) return;
            var c = Claim(player);
            var id = player.UserIDString;
            var ui = Frame(player, c);

            string info;
            if (UsableTiers().Count == 0) info = Msg("Empty", id);
            else if (ClaimedToday(c))
            {
                info = Msg("Claimed", id, FormatSpan(UntilNext));
                if (!string.IsNullOrEmpty(c.LastItem)) info += "\n" + Msg("LastWin", id, c.LastAmount, c.LastItem);
            }
            else
            {
                info = Msg("Ready", id, _config.ResetTime) + "\n" + StreakLine(c, id);
            }
            Label(ui, Main, info, 13, 20, 96, 620, 156, TextAnchor.MiddleCenter);
            DrawPreviewReel(ui);

            if (!ClaimedToday(c) && UsableTiers().Count > 0)
            {
                Button(ui, Main, Msg("Spin", id), 16, "daily.spin", "0.42 0.63 0.24 0.95", 200, 28, 400, 76);
                Button(ui, Main, Msg("Later", id), 13, "daily.close", "0.3 0.3 0.3 0.95", 420, 34, 520, 70);
            }
            else Button(ui, Main, Msg("Close", id), 14, "daily.close", "0.3 0.3 0.3 0.95", 260, 30, 380, 74);

            CuiHelper.DestroyUi(player, Root);
            CuiHelper.AddUi(player, ui);
        }

        private string StreakLine(PlayerClaim c, string id)
        {
            if (_config.StreakDays <= 0) return OddsLine(id);
            var next = ContinuesStreak(c) ? c.Streak + 1 : 1;
            var tier = _config.Tiers.ElementAtOrDefault(StreakTierIndex());
            if (tier == null) return OddsLine(id);
            if (StreakBonusDue(next)) return Msg("StreakNow", id, next, Hex(tier.Color), tier.Name);
            var left = _config.StreakDays - next % _config.StreakDays;
            return Msg("StreakNext", id, left, Hex(tier.Color), tier.Name);
        }

        private string OddsLine(string id)
        {
            var tiers = UsableTiers();
            var total = tiers.Sum(t => t.Chance);
            var parts = tiers.Select(t => $"<color=#{Hex(t.Color)}>{t.Name} {Math.Round(t.Chance / total * 100, 1)}%</color>");
            return Msg("Odds", id, string.Join("  ", parts));
        }

        private bool ContinuesStreak(PlayerClaim c) => c.LastDay == Key(Today.AddDays(-1));

        private CuiElementContainer Frame(BasePlayer player, PlayerClaim c)
        {
            var id = player.UserIDString;
            var ui = new CuiElementContainer();
            ui.Add(new CuiPanel
            {
                Image = { Color = "0 0 0 0.55" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" },
                CursorEnabled = true,
            }, "Overlay", Root);
            ui.Add(new CuiPanel
            {
                Image = { Color = "0.10 0.10 0.10 0.98" },
                RectTransform = { AnchorMin = "0.5 0.5", AnchorMax = "0.5 0.5", OffsetMin = "-320 -190", OffsetMax = "320 190" },
            }, Root, Main);
            Box(ui, Main, "0.07 0.07 0.07 1", 0, 334, 640, 380);
            Label(ui, Main, Msg("Title", id), 20, 20, 334, 300, 380);
            Label(ui, Main, Msg("Streak", id, ContinuesStreak(c) || ClaimedToday(c) ? c.Streak : 0), 13, 300, 334, 580, 380, TextAnchor.MiddleRight);
            Button(ui, Main, "X", 16, "daily.close", "0.70 0.22 0.17 0.95", 594, 342, 628, 372);
            // reel window and the centre marker
            Box(ui, Main, "0.05 0.05 0.05 1", ReelLeft, ReelBottom, ReelRight, ReelTop);
            Box(ui, Main, "1 0.83 0.3 0.9", ReelCenter - 1.5f, 166, ReelCenter + 1.5f, 318);
            return ui;
        }

        /// <summary>Before spinning: a few items from the table sit in the reel as a teaser.</summary>
        private void DrawPreviewReel(CuiElementContainer ui)
        {
            var tiers = UsableTiers();
            if (tiers.Count == 0) return;
            for (var i = -2; i <= 2; i++)
            {
                var tier = tiers[Math.Min(tiers.Count - 1, Math.Abs(i) == 2 ? 0 : Math.Abs(i) == 1 ? 1 : tiers.Count - 1)];
                var entry = tier.Items.FirstOrDefault(x => ItemManager.FindItemDefinition(x.Item) != null);
                if (entry == null) continue;
                Slot(ui, Main, ItemManager.FindItemDefinition(entry.Item), tier, ReelCenter + i * SlotPitch, Math.Abs(i) == 2 ? 0.45f : 1f, null);
            }
        }

        private void StartSpin(BasePlayer player)
        {
            if (player == null || _spins.ContainsKey(player.userID)) return;
            var c = Claim(player);
            if (ClaimedToday(c)) { OpenMenu(player); return; }
            var tiers = UsableTiers();
            if (tiers.Count == 0) { OpenMenu(player); return; }

            // The day is used up the moment the reel starts, so reconnecting can't re-roll it.
            var streak = ContinuesStreak(c) ? c.Streak + 1 : 1;
            var prize = Roll(StreakBonusDue(streak) ? StreakTierIndex() : 0);
            if (prize == null) { OpenMenu(player); return; }
            c.LastDay = Key(Today);
            c.Streak = streak;
            c.Claims++;
            c.LastItem = ItemName(prize.Def);
            c.LastAmount = prize.Amount;
            SaveData();
            UpdateReminder(player);

            var spin = new Spin { Prize = prize, WinnerIndex = 34 };
            for (var i = 0; i < 40; i++)
            {
                if (i == spin.WinnerIndex) { spin.Strip.Add(prize.Def); spin.StripTiers.Add(prize.Tier); continue; }
                var filler = Roll(0);
                spin.Strip.Add(filler.Def);
                spin.StripTiers.Add(filler.Tier);
            }
            spin.Start = Time.realtimeSinceStartup;
            spin.From = 2 * SlotPitch;
            spin.To = spin.WinnerIndex * SlotPitch + UnityEngine.Random.Range(-38f, 38f);
            _spins[player.userID] = spin;

            var ui = Frame(player, c);
            Label(ui, Main, Msg("Spinning", player.UserIDString), 14, 20, 96, 620, 156, TextAnchor.MiddleCenter);
            CuiHelper.DestroyUi(player, Root);
            CuiHelper.AddUi(player, ui);
            var id = player.userID;
            spin.Timer = timer.Every(0.05f, () => SpinFrame(player, id, spin));
        }

        private void SpinFrame(BasePlayer player, ulong id, Spin spin)
        {
            if (player == null || !player.IsConnected) { FinishSpin(id, spin, true); return; }
            var t = Mathf.Clamp01((Time.realtimeSinceStartup - spin.Start) / Math.Max(1f, _config.SpinSeconds));
            var eased = 1f - Mathf.Pow(1f - t, 3f);
            var offset = Mathf.Lerp(spin.From, spin.To, eased);
            DrawReel(player, spin, offset, false);

            // A soft tick each time an item passes the marker (rate-limited while it's fast).
            var slot = Mathf.FloorToInt((offset + SlotPitch / 2f) / SlotPitch);
            var now = Time.realtimeSinceStartup;
            if (slot != spin.LastSlot && now - spin.LastTick > 0.09f)
            {
                spin.LastSlot = slot;
                spin.LastTick = now;
                PlaySound(player, "assets/bundled/prefabs/fx/notice/item.select.fx.prefab");
            }
            if (t >= 1f) FinishSpin(id, spin, false);
        }

        private void DrawReel(BasePlayer player, Spin spin, float offset, bool final)
        {
            var ui = new CuiElementContainer();
            // Only as big as the reel window: a full-size layer would sit on top of the buttons and eat their clicks.
            ui.Add(new CuiPanel
            {
                Image = { Color = "0 0 0 0" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "0 0", OffsetMin = $"{ReelLeft} {ReelBottom}", OffsetMax = $"{ReelRight} {ReelTop}" },
            }, Main, Reel);
            for (var i = 0; i < spin.Strip.Count; i++)
            {
                var x = ReelCenter + i * SlotPitch - offset;
                var dx = Math.Abs(x - ReelCenter);
                if (dx > ReelHalfWidth - SlotSize / 2f) continue;
                var alpha = 1f - Mathf.Pow(dx / ReelHalfWidth, 2f) * 0.75f;
                var highlight = final && i == spin.WinnerIndex;
                Slot(ui, Reel, spin.Strip[i], spin.StripTiers[i], x, alpha, highlight ? spin.Prize.Amount.ToString() + "x" : null, highlight, ReelLeft, ReelBottom);
            }
            CuiHelper.DestroyUi(player, Reel);
            CuiHelper.AddUi(player, ui);
        }

        private void FinishSpin(ulong id, Spin spin, bool silent)
        {
            if (spin.Done) return;
            spin.Done = true;
            spin.Timer?.Destroy();
            var player = BasePlayer.FindByID(id) ?? BasePlayer.FindSleeping(id);
            var prize = spin.Prize;
            if (player != null) Give(player, prize.Def, prize.Amount, prize.Entry.Skin);

            var name = ItemName(prize.Def);
            var hex = Hex(prize.Tier.Color);
            _data.History.Insert(0, new Win
            {
                At = DateTime.UtcNow, Id = id.ToString(), Name = player != null ? player.displayName : id.ToString(),
                Item = prize.Def.shortname, ItemName = name, Amount = prize.Amount, Tier = prize.Tier.Name,
            });
            if (_data.History.Count > 300) _data.History.RemoveRange(300, _data.History.Count - 300);
            SaveData();
            if (prize.Tier.Announce && player != null)
                Server.Broadcast(Msg("Announce", null, player.displayName, hex, prize.Amount, name), _config.ChatPrefix, _config.ChatIcon);

            if (silent || player == null || !player.IsConnected)
            {
                _spins.Remove(id);
                return;
            }
            Say(player, Msg("WonChat", player.UserIDString, hex, prize.Amount, name, prize.Tier.Name));
            PlaySound(player, "assets/prefabs/misc/xmas/presents/effects/unwrap.prefab");

            var c = Claim(player);
            var ui = Frame(player, c);
            Label(ui, Main, Msg("Won", player.UserIDString, hex, prize.Amount, name, prize.Tier.Name), 16, 20, 96, 620, 156, TextAnchor.MiddleCenter);
            Button(ui, Main, Msg("Close", player.UserIDString), 14, "daily.close", "0.42 0.63 0.24 0.95", 260, 30, 380, 74);
            CuiHelper.DestroyUi(player, Root);
            CuiHelper.AddUi(player, ui);
            DrawReel(player, spin, spin.To, true);
        }

        private static void PlaySound(BasePlayer player, string prefab)
        {
            if (player?.net?.connection == null) return;
            EffectNetwork.Send(new Effect(prefab, player, 0, Vector3.zero, Vector3.forward), player.net.connection);
        }

        // ---- drawing helpers (pixels from the parent's bottom-left)

        private static void Box(CuiElementContainer c, string parent, string color, float x0, float y0, float x1, float y1, string name = null)
        {
            c.Add(new CuiPanel
            {
                Image = { Color = color },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "0 0", OffsetMin = $"{x0} {y0}", OffsetMax = $"{x1} {y1}" },
            }, parent, name);
        }

        private static void Label(CuiElementContainer c, string parent, string text, int size, float x0, float y0, float x1, float y1, TextAnchor align = TextAnchor.MiddleLeft)
        {
            c.Add(new CuiLabel
            {
                Text = { Text = text, FontSize = size, Align = align, Color = "1 1 1 0.95", Font = "robotocondensed-bold.ttf" },
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

        /// <summary>One reel slot centred on x: dark box, the tier's colour along the bottom, the item icon.</summary>
        /// <summary>ox/oy: where the parent sits inside the window, so slots can be given in window coordinates.</summary>
        private static void Slot(CuiElementContainer c, string parent, ItemDefinition def, Tier tier, float x, float alpha, string amount, bool highlight = false, float ox = 0f, float oy = 0f)
        {
            if (def == null) return;
            var a = alpha.ToString("0.00", CultureInfo.InvariantCulture);
            var x0 = x - SlotSize / 2f - ox;
            var x1 = x + SlotSize / 2f - ox;
            var y0 = 190f - oy;
            Box(c, parent, highlight ? $"0.25 0.22 0.12 {a}" : $"0.16 0.16 0.16 {a}", x0, y0, x1, y0 + 104);
            Box(c, parent, $"{tier.Color} {a}", x0, y0, x1, y0 + 6);
            c.Add(new CuiElement
            {
                Parent = parent,
                Components =
                {
                    new CuiImageComponent { ItemId = def.itemid, Color = $"1 1 1 {a}" },
                    new CuiRectTransformComponent { AnchorMin = "0 0", AnchorMax = "0 0", OffsetMin = $"{x0 + 14} {y0 + 14}", OffsetMax = $"{x1 - 14} {y0 + 94}" },
                },
            });
            if (amount != null)
                Label(c, parent, amount, 12, x0, y0 + 6, x1 - 6, y0 + 26, TextAnchor.MiddleRight);
        }

        #endregion

        #region Manager commands (RCON)

        private static bool IsAdminArg(ConsoleSystem.Arg arg) => arg.Connection == null || arg.Connection.authLevel >= 2;

        /// <summary>sandboxdaily.summary [count] - today's claims, streak leaders and recent wins for the manager.</summary>
        [ConsoleCommand("sandboxdaily.summary")]
        private void CcSummary(ConsoleSystem.Arg arg)
        {
            if (!IsAdminArg(arg)) return;
            var count = Math.Max(1, Math.Min(300, arg.GetInt(0, 50)));
            var today = Key(Today);
            arg.ReplyWith(JsonConvert.SerializeObject(new
            {
                version = Version.ToString(),
                today,
                nextResetMinutes = Math.Round(UntilNext.TotalMinutes),
                claimsToday = _data.Players.Values.Count(p => p.LastDay == today),
                players = _data.Players.Count,
                streaks = _data.Players.Where(kv => kv.Value.Streak > 0)
                    .OrderByDescending(kv => kv.Value.Streak).Take(10)
                    .Select(kv => new { id = kv.Key.ToString(), name = kv.Value.Name, streak = kv.Value.Streak, claims = kv.Value.Claims }),
                history = _data.History.Take(count).Select(w => new
                {
                    at = w.At, id = w.Id, name = w.Name, item = w.Item, itemName = w.ItemName, amount = w.Amount, tier = w.Tier,
                }),
            }));
        }

        /// <summary>sandboxdaily.reset &lt;steamid&gt; - let a player claim again today.</summary>
        [ConsoleCommand("sandboxdaily.reset")]
        private void CcReset(ConsoleSystem.Arg arg)
        {
            if (!IsAdminArg(arg)) return;
            ulong id;
            PlayerClaim c;
            if (!ulong.TryParse(arg.GetString(0), out id) || !_data.Players.TryGetValue(id, out c)) { arg.ReplyWith("No daily reward data for that Steam ID."); return; }
            if (c.LastDay == Key(Today))
            {
                c.LastDay = Key(Today.AddDays(-1));
                c.Streak = Math.Max(0, c.Streak - 1);
            }
            _shownToday.Remove(id);
            SaveData();
            var online = BasePlayer.FindByID(id);
            if (online != null) UpdateReminder(online);
            arg.ReplyWith($"{c.Name} can claim their daily reward again.");
        }

        #endregion
    }
}
