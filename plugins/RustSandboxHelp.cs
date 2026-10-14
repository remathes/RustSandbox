// Rust Sandbox — Help
// A server guide for players: /help opens a panel with a page per system (quests, skills, daily rewards,
// events, the event HUD, inventory and building tools) listing its commands, with a RUN button for the ones
// that open a menu. Pages only show when their plugin is loaded, and the admin page only shows to admins.
// New players get the guide the first time they wake up; everyone gets a one-line chat tip after that.
//
// Install: copy this file to oxide/plugins (or carbon/plugins). The pages are in oxide/config/RustSandboxHelp.json,
// so you can reword them or add your own (rules, Discord link...).

using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Game.Rust.Cui;
using UnityEngine;
using UnityEngine.UI;

namespace Oxide.Plugins
{
    [Info("Rust Sandbox Help", "RustSandbox", "1.2.6")]
    [Description("A /help server guide listing the commands of every Rust Sandbox system, shown to new players.")]
    public class RustSandboxHelp : RustPlugin
    {
        #region Config

        private PluginConfig _config;

        private class Cmd
        {
            [JsonProperty("Command")] public string Command = "";
            [JsonProperty("What it does")] public string Text = "";
        }

        private class Section
        {
            [JsonProperty("Title")] public string Title = "";
            [JsonProperty("Only when this plugin is loaded (blank = always)")] public string Plugin = "";
            [JsonProperty("Admins only")] public bool AdminOnly;
            [JsonProperty("Intro")] public string Intro = "";
            [JsonProperty("Commands", ObjectCreationHandling = ObjectCreationHandling.Replace)] public List<Cmd> Commands = new List<Cmd>();
        }

        private class PluginConfig
        {
            [JsonProperty("Open the guide the first time a player wakes up")] public bool ShowFirstJoin = true;
            [JsonProperty("Chat tip every time a player wakes up after joining")] public bool JoinTip = true;
            [JsonProperty("Chat tip")] public string Tip = "Welcome back! Type <color=#f0b04a>/help</color> for the server guide and commands.";
            [JsonProperty("Title (blank = the server's name)")] public string Title = "";
            [JsonProperty("Chat prefix")] public string ChatPrefix = "<color=#f0b04a>[Help]</color>";
            [JsonProperty("Pages", ObjectCreationHandling = ObjectCreationHandling.Replace)] public List<Section> Sections = DefaultSections();
        }

        private static Cmd C(string command, string text) => new Cmd { Command = command, Text = text };

        private static List<Section> DefaultSections() => new List<Section>
        {
            new Section
            {
                Title = "Getting started",
                Intro = "Welcome! This is a sandbox server with extra systems: quests from NPCs, skills that level up as you play, a daily reward spin, server events and handy inventory tools. Pick a page on the left to see how each one works.",
                Commands =
                {
                    C("/help", "Open this guide again any time"),
                    C("/quests", "Your quest log"),
                    C("/skills", "Your skills and perks"),
                    C("/daily", "Spin for today's free reward"),
                    C("F1: bind j sort.inventory", "Sort your inventory with the J key"),
                },
            },
            new Section
            {
                Title = "Quests",
                Plugin = "RustSandboxQuests",
                Intro = "Quest givers stand around the map; hover over the small icons on your map to see their names. A green ? pin means they have a new quest for you, and a yellow ! pin means you can hand one in there. Walk up and press E to see their quests. A tracker on the right shows your progress. When a quest is done, go back to a quest giver to claim the reward.",
                Commands =
                {
                    C("/quests", "Quest log: progress, abandon or claim"),
                    C("/quests list", "Where every quest giver and quest location is"),
                },
            },
            new Section
            {
                Title = "Skins",
                Plugin = "RustSandboxSkins",
                Intro = "Put skins on your clothes, weapons, tools and deployables: the ones you own on Steam plus Facepunch's free ones. Type /skins, pick an item on the left, then click a skin. Holding an item when you open it starts on that item.",
                Commands =
                {
                    C("/skins", "Open the skin picker"),
                },
            },
            new Section
            {
                Title = "Repair Recycler",
                Plugin = "RustSandboxRecycler",
                Intro = "A recycler and repair bench joined together in an L, as one item. Place it like a repair bench, then recycle at the recycler and repair at the bench. The owner can rotate or pick up the whole station from the small panel that shows when either part is open.",
                Commands =
                {
                    C("/recycler", "What it is and what it costs"),
                    C("/recycler craft", "Make one from the listed materials"),
                },
            },
            new Section
            {
                Title = "Pets",
                Plugin = "RustSandboxPet",
                Intro = "Call your own pet with /pet and pick the animal with /pet choose: wolf, tiger or panther (fighters: /pet roar makes you hit harder and heals you) or bear, polar bear, boar or crocodile (tanks: tougher, and /pet roar pulls enemies onto it while you take less damage). It runs at your side, fights what attacks you and what you hit, and levels up the Companion skill in /skills. If it goes down, look at it and press E with a bandage, syringe or medkit to get it back up. Press E on it to open its backpack. Middle mouse: look at loot, a crate or a body to fetch it, or at an animal or NPC to attack it.",
                Commands =
                {
                    C("/pet", "Call your pet, or bring it back to you"),
                    C("/pet choose", "Pick an animal (wolf, tiger, panther, bear, polar bear, boar, crocodile)"),
                    C("/pet bag", "Open its backpack"),
                    C("/pet fetch", "Fetch what you look at, or the loot around you"),
                    C("/pet attack", "Attack the animal or NPC you look at"),
                    C("/pet stay", "Sit and wait, or follow again"),
                    C("/pet patrol", "Roam within 30 m of you while you build, or follow again"),
                    C("/pet roar", "Fighters: you hit harder and heal. Tanks: enemies go for it, you take less damage"),
                    C("/pet heal", "Patch it up with a bandage, syringe or medkit (or press E on it)"),
                    C("/pet sniff", "What's around you (Keen Nose perk)"),
                    C("/pet dismiss", "Send it home (the backpack is kept)"),
                    C("/pet keys", "How to put these on keys (bind g pet.fetch...)"),
                },
            },
            new Section
            {
                Title = "Weather",
                Plugin = "RustSandboxWeather",
                Intro = "The weather changes on its own: clear skies, overcast, fog, rain, storms and dusty haze, fading from one to the next. You'll get a chat line when it starts to turn. Check the forecast any time.",
                Commands =
                {
                    C("/weather", "What the weather is now and what's coming next"),
                },
            },
            new Section
            {
                Title = "Skills",
                Plugin = "RustSandboxSkills",
                Intro = "You earn XP in eleven skills just by playing: mining, woodcutting, combat, gardening, electrician, survival, mechanic, fishing, scrapper, cooking and companion (fighting alongside your wolf). Each level gives points to spend on perks like more ore, faster crafting or less damage. Playing solo earns bonus XP.",
                Commands =
                {
                    C("/skills", "Open the skills menu"),
                    C("/skills info", "Your levels and points in chat"),
                    C("/skills perks <skill>", "Perks you can buy for a skill"),
                    C("/skills buy <perk>", "Unlock the next rank of a perk"),
                    C("/skills bonuses", "What your perks have done for you"),
                    C("/skills reset <skill>", "Refund that skill's points"),
                    C("/skills popup up/down/left/right", "Move the XP pop-up"),
                    C("/skills xp", "Turn XP pop-ups on or off"),
                },
            },
            new Section
            {
                Title = "Daily rewards",
                Plugin = "RustSandboxDaily",
                Intro = "Once a day you can spin a reel for a random item, from a pile of dung to a rocket launcher. Claim several days in a row for a guaranteed better prize. A glowing present at the top of the screen means today's spin is waiting.",
                Commands =
                {
                    C("/daily", "Spin today's reward"),
                },
            },
            new Section
            {
                Title = "Events",
                Plugin = "RustSandboxEvents",
                Intro = "Server events are announced in chat. Treasure Hunt: go to the Airfield to get a Treasure Compass, follow the riddles to hidden spots at monuments, and be first to open the Golden Statue. Train Heist: board the loot train, clear the scientist guards and hack the locked crate before it leaves.",
                Commands =
                {
                    C("/hunt", "Your current treasure hunt clue"),
                },
            },
            new Section
            {
                Title = "Event HUD",
                Plugin = "RustSandboxEventHud",
                Intro = "The HUD shows which events are running (airdrop, Patrol Helicopter, Bradley, Cargo Ship, Chinook, oil rigs), where locked crates are and how long their hacks have left, plus a radiation detector, compass and your grid position.",
                Commands =
                {
                    C("/eventhud", "Move the HUD or switch between HUDs"),
                    C("/eventhud hide", "Hide the HUD (/eventhud show brings it back)"),
                    C("/eventhud tint", "Turn the radiation screen tint on or off"),
                    C("/eventhud help", "All HUD commands"),
                },
            },
            new Section
            {
                Title = "Inventory & building",
                Plugin = "RustSandboxTweaks",
                Intro = "While looting a box, crate or tool cupboard there's a Sort button. Furnaces spread ore evenly over their slots. The remove tool lets you pick up your own building pieces and deployables with a Hammer. Some of these can be turned off by the admin.",
                Commands =
                {
                    C("/sort", "Sort your inventory (hotbar and clothes stay put)"),
                    C("/autosplit", "Turn furnace auto-split on or off for you"),
                    C("/remove", "Remove mode: hit your own pieces with a Hammer"),
                },
            },
            new Section
            {
                Title = "Admin",
                AdminOnly = true,
                Intro = "Only admins see this page. Most settings live in the Rust Sandbox web manager.",
                Commands =
                {
                    C("/questgiver add <name>", "Place a quest giver where you stand"),
                    C("/questgiver move <id>", "Move a quest giver to where you stand"),
                    C("/questgiver remove", "Remove the quest giver next to you"),
                    C("/questgiver list", "List quest givers"),
                    C("/questgiver look <id> <look>", "Change a quest giver's outfit"),
                    C("/questgiver outfit <id>", "Dress a quest giver in what you're wearing (skins too)"),
                    C("/hunt tp", "Teleport to your current treasure hunt clue"),
                    C("/weather storm", "Weather now: clear, overcast, fog, rain, storm or dust [minutes]"),
                    C("/weather random", "Back to random weather (/weather on|off turns it on or off)"),
                    C("/npcs add <preset> [count]", "Spawn a group of NPCs where you stand (scientists, bandits, wolfpack...)"),
                    C("/npcs remove", "Remove the nearest NPC spot (/npcs list shows them)"),
                    C("/npcs list", "Every NPC group with its grid; /npcs go <number> teleports you there"),
                    C("/npcs show", "Mark the NPCs near you on screen"),
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
                if (_config.Sections == null || _config.Sections.Count == 0) _config.Sections = DefaultSections();
                foreach (var s in _config.Sections)
                    if (s.Commands == null) s.Commands = new List<Cmd>();
                AddNewPages();
                SaveConfig();
            }
            catch (Exception ex)
            {
                PrintError($"Couldn't read the config file ({ex.Message}); using defaults and leaving the file untouched.");
                _config = new PluginConfig();
            }
        }

        protected override void SaveConfig() => Config.WriteObject(_config, true);

        // Pages for plugins added after the config file was written, plus updates to the built-in text that changed.
        private void AddNewPages()
        {
            var defaults = DefaultSections();
            var adminAt = _config.Sections.FindIndex(x => x.AdminOnly);
            foreach (var d in defaults.Where(x => !string.IsNullOrEmpty(x.Plugin)))
            {
                if (_config.Sections.Any(x => x.Plugin == d.Plugin)) continue;
                if (adminAt < 0) _config.Sections.Add(d);
                else _config.Sections.Insert(adminAt++, d);
            }
            var skins = _config.Sections.FirstOrDefault(x => x.Plugin == "RustSandboxSkins");
            if (skins != null && skins.Intro.Contains("even ones you don't own on Steam"))
                skins.Intro = defaults.First(x => x.Plugin == "RustSandboxSkins").Intro;
            var wolf = _config.Sections.FirstOrDefault(x => x.Plugin == "RustSandboxPet");
            var wolfDefault = defaults.First(x => x.Plugin == "RustSandboxPet");
            if (wolf != null && wolf.Intro.StartsWith("Call your own wolf with /pet."))
            {
                wolf.Intro = wolfDefault.Intro;
                if (wolf.Title == "Wolf companion") wolf.Title = wolfDefault.Title;
                wolf.Commands.RemoveAll(x => x.Command == "/pet howl");
                var call = wolf.Commands.FirstOrDefault(x => x.Command == "/pet");
                if (call != null) call.Text = wolfDefault.Commands.First(x => x.Command == "/pet").Text;
            }
            if (wolf != null)
                foreach (var c in wolfDefault.Commands)
                    if (!wolf.Commands.Any(x => x.Command == c.Command)) wolf.Commands.Insert(Math.Max(0, wolf.Commands.Count - 2), c);
            var skillsPage = _config.Sections.FirstOrDefault(x => x.Plugin == "RustSandboxSkills");
            if (skillsPage != null && skillsPage.Intro.StartsWith("You earn XP in ten skills"))
                skillsPage.Intro = defaults.First(x => x.Plugin == "RustSandboxSkills").Intro;
            var admin = _config.Sections.FirstOrDefault(x => x.AdminOnly);
            var adminDefault = defaults.FirstOrDefault(x => x.AdminOnly);
            if (admin != null && adminDefault != null)
                foreach (var c in adminDefault.Commands)
                    if (!admin.Commands.Any(x => x.Command == c.Command)) admin.Commands.Add(c);
        }

        #endregion

        #region Data

        private const string DataFile = "RustSandboxHelp";
        private HashSet<ulong> _seen = new HashSet<ulong>();
        private readonly HashSet<ulong> _tipped = new HashSet<ulong>();

        private void Init()
        {
            try { _seen = Interface.Oxide.DataFileSystem.ReadObject<HashSet<ulong>>(DataFile) ?? new HashSet<ulong>(); }
            catch { _seen = new HashSet<ulong>(); }
        }

        private void SaveData() => Interface.Oxide.DataFileSystem.WriteObject(DataFile, _seen);

        private void Unload()
        {
            foreach (var p in BasePlayer.activePlayerList) Close(p);
        }

        #endregion

        #region Hooks and commands

        private void OnPlayerSleepEnded(BasePlayer player)
        {
            if (player == null || !player.userID.IsSteamId()) return;
            if (_seen.Add(player.userID))
            {
                SaveData();
                _tipped.Add(player.userID);
                if (_config.ShowFirstJoin)
                    timer.Once(6f, () => { if (player != null && player.IsConnected) Open(player, 0); });
                return;
            }
            // Once per connection, not every time they respawn.
            if (!_config.JoinTip || !_tipped.Add(player.userID)) return;
            timer.Once(3f, () => { if (player != null && player.IsConnected) Player.Message(player, _config.Tip, _config.ChatPrefix, 0); });
        }

        private void OnPlayerDisconnected(BasePlayer player, string reason)
        {
            if (player == null) return;
            _tipped.Remove(player.userID);
            _shown.Remove(player.userID);
        }

        [ChatCommand("help")]
        private void CmdHelp(BasePlayer player, string command, string[] args) => Open(player, 0);

        [ChatCommand("commands")]
        private void CmdCommands(BasePlayer player, string command, string[] args) => Open(player, 0);

        [ConsoleCommand("sandboxhelp.ui")]
        private void CcUi(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null) return;
            var action = arg.GetString(0);
            var visible = Visible(player);
            switch (action)
            {
                case "page":
                    Open(player, arg.GetInt(1));
                    return;
                case "run":
                {
                    var page = arg.GetInt(1);
                    var row = arg.GetInt(2);
                    Close(player);
                    if (page < 0 || page >= visible.Count || row < 0 || row >= visible[page].Commands.Count) return;
                    var cmd = visible[page].Commands[row].Command;
                    if (Runnable(cmd)) player.SendConsoleCommand("chat.say", cmd);
                    return;
                }
                default:
                    Close(player);
                    return;
            }
        }

        private List<Section> Visible(BasePlayer player) =>
            _config.Sections.Where(s => (!s.AdminOnly || player.IsAdmin) && (string.IsNullOrEmpty(s.Plugin) || plugins.Find(s.Plugin) != null)).ToList();

        /// <summary>Commands that open something on their own: a single chat command with no placeholders.</summary>
        private static bool Runnable(string cmd) =>
            !string.IsNullOrEmpty(cmd) && cmd.StartsWith("/") && !cmd.Contains("<") && cmd.IndexOf('/', 1) < 0;

        #endregion

        #region Panel

        private const string Root = "SandboxHelp.Root";
        private const string Main = Root + ".Main";

        // Players whose backdrop (the part that frees the mouse) is on screen. Changing page only replaces the window
        // inside it: destroying and re-adding the backdrop made the game grab the mouse and re-centre it on every click.
        private readonly HashSet<ulong> _shown = new HashSet<ulong>();


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

        private void Close(BasePlayer player)
        {
            _shown.Remove(player.userID);
            CuiHelper.DestroyUi(player, Root);
        }

        private void Open(BasePlayer player, int page)
        {
            if (player == null || !player.IsConnected) return;
            var sections = Visible(player);
            if (sections.Count == 0) return;
            page = Mathf.Clamp(page, 0, sections.Count - 1);
            var s = sections[page];

            var ui = new CuiElementContainer();
            var fresh = _shown.Add(player.userID);
            if (fresh)
                ui.Add(new CuiPanel
                {
                    Image = { Color = "0 0 0 0.55" },
                    RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" },
                    CursorEnabled = true,
                }, "Overlay", Root);
            ui.Add(new CuiPanel
            {
                Image = { Color = "0.10 0.10 0.10 0.98" },
                RectTransform = { AnchorMin = "0.5 0.5", AnchorMax = "0.5 0.5", OffsetMin = "-410 -250", OffsetMax = "410 250" },
            }, Root, Main);

            // header
            var title = string.IsNullOrEmpty(_config.Title) ? ConVar.Server.hostname : _config.Title;
            Box(ui, Main, "0.07 0.07 0.07 1", 0, 454, 820, 500);
            Box(ui, Main, "0.94 0.69 0.29 1", 0, 452, 820, 454);
            Label(ui, Main, "SERVER GUIDE  <color=#9a9a9a>· " + Escape(title) + "</color>", 18, 18, 454, 760, 500);
            Button(ui, Main, "X", 16, "sandboxhelp.ui close", "0.70 0.22 0.17 0.95", 780, 462, 812, 492);

            // page list
            Box(ui, Main, "0.07 0.07 0.07 1", 12, 12, 202, 442);
            // Rows shrink to fit when there are more pages than the list has room for at full size.
            var step = Mathf.Min(38f, 426f / Mathf.Max(1, sections.Count));
            var height = step - 4f;
            var font = step >= 34f ? 13 : 12;
            var y = 442f;
            for (var i = 0; i < sections.Count; i++)
            {
                y -= step;
                var on = i == page;
                Button(ui, Main, sections[i].Title, font, $"sandboxhelp.ui page {i}", on ? "0.24 0.20 0.12 1" : "0.13 0.13 0.13 1", 16, y, 198, y + height, TextAnchor.MiddleLeft);
                if (on) Box(ui, Main, "0.94 0.69 0.29 1", 16, y, 19, y + height);
            }

            // page content
            Label(ui, Main, s.Title, 20, 220, 408, 808, 442);
            Label(ui, Main, s.Intro, 13, 220, 330, 808, 406, TextAnchor.UpperLeft, "0.82 0.82 0.82 1");
            // Commands scroll when there are more than fit (the admin page has lots), above the footer line.
            const float listBottom = 40f, listTop = 322f, rowStep = 34f, rowH = 30f;
            var viewH = listTop - listBottom;
            var contentH = Mathf.Max(viewH, s.Commands.Count * rowStep);
            var list = Main + ".List";
            ui.Add(new CuiElement
            {
                Name = list,
                Parent = Main,
                Components =
                {
                    // The wheel only scrolls while the pointer is over something that catches it, so the whole
                    // view gets an (almost) invisible background.
                    new CuiImageComponent { Color = "0 0 0 0.01" },
                    new CuiScrollViewComponent
                    {
                        Vertical = true,
                        Horizontal = false,
                        MovementType = ScrollRect.MovementType.Clamped,
                        ScrollSensitivity = 40f,
                        Inertia = false,
                        ContentTransform = new CuiRectTransform { AnchorMin = "0 1", AnchorMax = "1 1", OffsetMin = $"0 {-contentH}", OffsetMax = "0 0" },
                        VerticalScrollbar = new CuiScrollbar { Size = 6f, AutoHide = true, HandleColor = "0.55 0.55 0.55 0.9", HighlightColor = "0.7 0.7 0.7 1", PressedColor = "0.8 0.8 0.8 1", TrackColor = "0.1 0.1 0.1 0.6" },
                    },
                    new CuiRectTransformComponent { AnchorMin = "0 0", AnchorMax = "0 0", OffsetMin = $"220 {listBottom}", OffsetMax = $"808 {listTop}" },
                },
            });
            for (var r = 0; r < s.Commands.Count; r++)
            {
                var c = s.Commands[r];
                var top = contentH - r * rowStep;
                var bottom = top - rowH;
                var run = Runnable(c.Command) && c.Command != "/help";
                Box(ui, list, r % 2 == 0 ? "0.13 0.13 0.13 1" : "0.11 0.11 0.11 1", 0, bottom, 578, top);
                Label(ui, list, Escape(c.Command), 13, 10, bottom, 250, top, TextAnchor.MiddleLeft, "0.94 0.69 0.29 1");
                Label(ui, list, c.Text, 12, 255, bottom, run ? 504 : 572, top, TextAnchor.MiddleLeft, "0.85 0.85 0.85 1");
                if (run)
                    Button(ui, list, "RUN", 11, $"sandboxhelp.ui run {page} {r}", "0.42 0.63 0.24 0.95", 510, bottom + 4, 572, top - 4);
            }
            var more = s.Commands.Count * rowStep > viewH ? "Scroll for more commands.  " : "";
            Label(ui, Main, more + "Type /help any time to open this guide.", 11, 220, 12, 808, 34, TextAnchor.MiddleRight, "0.55 0.55 0.55 1");

            CuiHelper.DestroyUi(player, fresh ? Root : Main);
            CuiHelper.AddUi(player, ui);
            Drawn(player, ui, fresh, Root, Main);
        }

        /// <summary>Rust's UI text only treats real tags (b, i, size, color) as markup, so "&lt;name&gt;" already shows as typed.
        /// It doesn't know &lt;noparse&gt;, which printed literally. Only those real tags need breaking up.</summary>
        private static string Escape(string s)
        {
            s = s ?? "";
            foreach (var tag in new[] { "b>", "i>", "size", "color", "material", "quad", "/b>", "/i>", "/size", "/color" })
                s = s.Replace("<" + tag, "<\u200B" + tag);
            return s;
        }

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

        private static void Button(CuiElementContainer c, string parent, string text, int size, string command, string color, float x0, float y0, float x1, float y1,
            TextAnchor align = TextAnchor.MiddleCenter)
        {
            c.Add(new CuiButton
            {
                Button = { Command = command, Color = color },
                Text = { Text = align == TextAnchor.MiddleLeft ? "   " + text : text, FontSize = size, Align = align, Color = "1 1 1 0.95", Font = "robotocondensed-bold.ttf" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "0 0", OffsetMin = $"{x0} {y0}", OffsetMax = $"{x1} {y1}" },
            }, parent);
        }

        #endregion
    }
}
