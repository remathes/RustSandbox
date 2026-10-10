// Rust Sandbox — Event HUD
// A small HUD for every player showing which server events are running (airdrop, Patrol Helicopter,
// Bradley, Cargo Ship, Chinook, Small/Large Oil Rig), where the locked crates and airdrops are,
// how long hacks have left, and a radiation detector that pulses faster the closer you get. When radiation
// gets through your clothing the screen edges glow green, and red once radiation poisoning starts to hurt.
// Events are also announced in chat to everyone.
//
// Install: copy this file to oxide/plugins (or carbon/plugins). Settings are in
// oxide/config/RustSandboxEventHud.json. Players move or hide their HUD with /eventhud.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Game.Rust.Cui;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("Rust Sandbox Event HUD", "RustSandbox", "1.3.1")]
    [Description("Shows active server events, locked crate timers and a radiation detector on every player's HUD, and announces events in chat.")]
    public class RustSandboxEventHud : RustPlugin
    {
        #region Config

        private PluginConfig _config;

        private class PluginConfig
        {
            [JsonProperty("Show the HUD")] public bool ShowHud = true;
            [JsonProperty("Default position (TopLeft, TopRight, MiddleLeft, MiddleRight, BottomLeft, BottomRight)")] public string DefaultPosition = "TopRight";
            [JsonProperty("Default nudge X (pixels, + moves right)")] public float DefaultX = 0f;
            [JsonProperty("Default nudge Y (pixels, + moves up)")] public float DefaultY = 0f;
            [JsonProperty("Let players move their HUD (/eventhud)")] public bool AllowMove = true;
            [JsonProperty("Icon size (pixels)")] public float IconSize = 34f;
            [JsonProperty("Max location lines under the icons")] public int MaxLines = 6;
            [JsonProperty("Events to show")] public Dictionary<string, bool> Events = new Dictionary<string, bool>
            {
                ["Airdrop"] = true,
                ["PatrolHelicopter"] = true,
                ["Bradley"] = true,
                ["CargoShip"] = true,
                ["Chinook"] = true,
                ["SmallOilRig"] = true,
                ["LargeOilRig"] = true,
            };
            [JsonProperty("Show untouched oil rig crates in the list")] public bool ShowIdleRigCrates = false;
            [JsonProperty("Keep looted entries in the list for (seconds)")] public float LootedLinger = 60f;
            [JsonProperty("Active color")] public string ActiveColor = "0.18 0.62 0.25 0.9";
            [JsonProperty("Inactive color")] public string InactiveColor = "0.62 0.13 0.13 0.75";
            [JsonProperty("Loot waiting color")] public string LootColor = "0.85 0.55 0.10 0.9";

            [JsonProperty("Announce events in chat")] public bool Announce = true;
            [JsonProperty("Announce untouched oil rig crates")] public bool AnnounceIdleRigCrates = false;
            [JsonProperty("Hack reminders (minutes left)", ObjectCreationHandling = ObjectCreationHandling.Replace)] public List<int> HackReminders = new List<int> { 10, 5, 1 };
            [JsonProperty("Chat prefix")] public string ChatPrefix = "<color=#e8a33d>[Events]</color>";
            [JsonProperty("Chat icon (SteamID, 0 = server)")] public ulong ChatIcon = 0;

            [JsonProperty("Radiation detector")] public bool RadiationDetector = true;
            [JsonProperty("Radiation detection range (meters)")] public float RadiationRange = 60f;
            [JsonProperty("Compass (direction you're facing)")] public bool Compass = true;
            [JsonProperty("Coordinates (grid square and x, y, z)")] public bool Coordinates = true;
            [JsonProperty("Player HUD default position (AboveHotbar, TopLeft, TopRight, MiddleLeft, MiddleRight, BottomLeft, BottomRight)")] public string InfoPosition = "AboveHotbar";
            [JsonProperty("Player HUD default nudge X (pixels, + moves right)")] public float InfoX = 0f;
            [JsonProperty("Player HUD default nudge Y (pixels, + moves up)")] public float InfoY = 0f;
            [JsonProperty("Tint the screen edges in radiation")] public bool RadiationTint = false;
            [JsonProperty("Screen tint strength (0-1)")] public float TintStrength = 0.4f;
            [JsonProperty("Red screen once radiation poisoning reaches")] public float RedPoison = 10f;
        }

        protected override void LoadDefaultConfig() => _config = new PluginConfig();

        // This plugin used to be called RustServerManagerEventHud (the project was renamed to Rust Sandbox).
        // The first time it loads, copy the old config and data files over so nothing is lost.
        private const string LegacyName = "RustServerManagerEventHud";

        // Runs before Oxide loads the config, so it works on the files directly. A marker file makes it
        // a one-time copy: the old files win once, even if this plugin already created empty new ones.
        private void MigrateLegacyFiles(params string[] dataSuffixes)
        {
            const string newName = "RustSandboxEventHud";
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
            MigrateLegacyFiles("_players");
            base.LoadConfig();
            try
            {
                _config = Config.ReadObject<PluginConfig>();
                if (_config == null) throw new Exception("empty");
                var defaults = new PluginConfig();
                if (_config.Events == null) _config.Events = defaults.Events;
                foreach (var kv in defaults.Events)
                    if (!_config.Events.ContainsKey(kv.Key)) _config.Events[kv.Key] = kv.Value;
                if (_config.HackReminders == null) _config.HackReminders = new List<int>();
                // Older versions added 10, 5, 1 again on every load.
                _config.HackReminders = _config.HackReminders.Distinct().OrderByDescending(m => m).ToList();
                SaveConfig();
            }
            catch (Exception ex)
            {
                PrintError($"Couldn't read the config file ({ex.Message}); using defaults and leaving the file untouched.");
                _config = new PluginConfig();
            }
        }

        protected override void SaveConfig() => Config.WriteObject(_config, true);

        #endregion

        #region Lang

        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["PlaneInbound"] = "A cargo plane is inbound with an airdrop.",
                ["DropFalling"] = "An airdrop is falling at <color=#ffd479>{0}</color>.",
                ["DropLanded"] = "The airdrop landed at <color=#ffd479>{0}</color>.",
                ["DropLooted"] = "The airdrop at <color=#ffd479>{0}</color> was looted by {1}.",
                ["HeliSpawned"] = "The Patrol Helicopter is in the air.",
                ["HeliDown"] = "{1} shot down the Patrol Helicopter at <color=#ffd479>{0}</color>! Its crates are burning.",
                ["HeliDownNoKiller"] = "The Patrol Helicopter went down at <color=#ffd479>{0}</color>! Its crates are burning.",
                ["HeliLeft"] = "The Patrol Helicopter has left.",
                ["HeliLoot"] = "The Patrol Helicopter crates at <color=#ffd479>{0}</color> are being looted by {1}.",
                ["BradleySpawned"] = "Bradley APC is patrolling at <color=#ffd479>{0}</color>.",
                ["BradleyDown"] = "{1} destroyed Bradley APC at <color=#ffd479>{0}</color>! Its crates are burning.",
                ["BradleyDownNoKiller"] = "Bradley APC was destroyed at <color=#ffd479>{0}</color>! Its crates are burning.",
                ["BradleyLoot"] = "The Bradley crates at <color=#ffd479>{0}</color> are being looted by {1}.",
                ["CargoArrived"] = "The Cargo Ship has entered the map.",
                ["CargoLeft"] = "The Cargo Ship has left the map.",
                ["ChinookInbound"] = "A Chinook is in the air.",
                ["CrateWaiting"] = "A locked crate ({1}) is waiting at <color=#ffd479>{0}</color>.",
                ["CrateDropped"] = "The Chinook dropped a locked crate at <color=#ffd479>{0}</color>.",
                ["CrateHacking"] = "Someone is hacking the locked crate ({1}) at <color=#ffd479>{0}</color>. It unlocks in <color=#ffd479>{2}</color>.",
                ["RigEvent"] = "<color=#ff6b5b>{1} event started!</color> The crate at <color=#ffd479>{0}</color> unlocks in <color=#ffd479>{2}</color>.",
                ["CrateReminder"] = "<color=#ffd479>{2}</color> left on the locked crate ({1}) at <color=#ffd479>{0}</color>.",
                ["CrateUnlocked"] = "The locked crate ({1}) at <color=#ffd479>{0}</color> is unlocked!",
                ["CrateLooted"] = "The locked crate ({1}) at <color=#ffd479>{0}</color> was looted by {2}.",
                ["HudOn"] = "Event HUD shown. Type /eventhud to move or hide it.",
                ["HudOff"] = "Event HUD hidden. Type /eventhud show to bring it back.",
                ["HudReset"] = "Event HUD moved back to the default spot.",
                ["HudHelp"] = "/eventhud opens the move panel. Also: /eventhud show, hide, reset, tint (radiation screen tint on/off), or a spot: topleft, topright, middleleft, middleright, bottomleft, bottomright.",
                ["EdTitle"] = "Move the event HUD",
                ["EdEvents"] = "Event icons",
                ["EdInfo"] = "Radiation, compass, grid",
                ["EdAboveHotbar"] = "Above hotbar",
                ["LabelGrid"] = "X, Y, Z",
                ["EdHint"] = "Pick a corner, then nudge it. The yellow box shows where it goes.",
                ["EdTopLeft"] = "Top left",
                ["EdTopRight"] = "Top right",
                ["EdMiddleLeft"] = "Middle left",
                ["EdMiddleRight"] = "Middle right",
                ["EdBottomLeft"] = "Bottom left",
                ["EdBottomRight"] = "Bottom right",
                ["EdUp"] = "Up",
                ["EdDown"] = "Down",
                ["EdLeft"] = "Left",
                ["EdRight"] = "Right",
                ["EdStep"] = "Step {0}",
                ["EdReset"] = "Reset",
                ["EdHide"] = "Hide HUD",
                ["EdShow"] = "Show HUD",
                ["EdDone"] = "Done",
                ["SomeoneUnknown"] = "someone",
                ["SrcCargo"] = "Cargo Ship",
                ["SrcLargeRig"] = "Large Oil Rig",
                ["SrcSmallRig"] = "Small Oil Rig",
                ["SrcChinook"] = "Chinook drop",
                ["SrcOther"] = "monument",
                ["LabelAirdrop"] = "AIR",
                ["LabelHeli"] = "HELI",
                ["LabelBradley"] = "APC",
                ["LabelCargo"] = "CARGO",
                ["LabelChinook"] = "CH47",
                ["LabelSmallRig"] = "OIL S",
                ["LabelLargeRig"] = "OIL L",
                ["LabelRad"] = "RAD",
                ["LabelSafe"] = "SAFE",
                ["TintOn"] = "Radiation screen tint turned on: green edges while radiation gets through your clothing, red once it's hurting you.",
                ["TintOff"] = "Radiation screen tint turned off. Type /eventhud tint to turn it back on.",
                ["LineWaiting"] = "waiting",
                ["LineHacking"] = "hacking {0}",
                ["LineUnlocked"] = "unlocked",
                ["LineLooted"] = "looted",
                ["LineFalling"] = "falling",
                ["LineLanded"] = "landed",
                ["LineBurning"] = "burning",
                ["LineReady"] = "{0} to loot",
                ["LineAirdrop"] = "Airdrop",
                ["LineHeliLoot"] = "Heli crates",
                ["LineBradleyLoot"] = "Bradley crates",
            }, this);
        }

        private string Msg(string key, params object[] args)
        {
            var text = lang.GetMessage(key, this);
            return args.Length == 0 ? text : string.Format(text, args);
        }

        private void Announce(string key, params object[] args)
        {
            if (!_config.Announce) return;
            var text = Msg(key, args);
            Server.Broadcast(text, _config.ChatPrefix, _config.ChatIcon);
            Puts(StripTags(text));
        }

        private static string StripTags(string s)
        {
            var sb = new StringBuilder(s.Length);
            var inTag = false;
            foreach (var c in s)
            {
                if (c == '<') inTag = true;
                else if (c == '>') inTag = false;
                else if (!inTag) sb.Append(c);
            }
            return sb.ToString();
        }

        #endregion

        #region Icons

        // White 64x64 PNG silhouettes, tinted in game. Sent to players through the server's file storage,
        // so they work without any web host or image plugin.
        private static readonly Dictionary<string, string> IconData = new Dictionary<string, string>
        {
            ["chinook"] = "iVBORw0KGgoAAAANSUhEUgAAAEAAAABACAYAAACqaXHeAAAE0UlEQVR42u2Zy4scVRTGf6e6RhNxzEw07gRFF24imgcovjaKIuhKXAgREYJL9Q8IwSzcZCO6kQH/AKMgCkp0b4wElYxgCAnGlYvEpDOZBJNMV38u5ly4llXdVf2oaZ36oKnqvtX3nnPq3O88LrRo0aJFixYtWrTYnLBZE0hSYzKZmTat8r5eYn6TbpTOZpblhFoENEXvDHNnZnbFZuTNJ0AHeB94BegDyYS2eH6efvR51yR1gDeBHVO2fP4tJMBJM/vcjbAAdBu2/RmTdAA4tIEO8JiZHXcveA3YB2TuEeMgAa4CTwJ3RF51ErgI9IBDSNonqaeNwQVJD0oyN8Ckt9arkq65fn2/v/tfYVDSLmDBXbOOhVeAmz6PgFuA+Rpb4JyZnZNkZibfjuOGpjkzuyHpDeBj/y141GngYWAtyJH64j+NaOFngIfcnVLgrJl9WTf0hXicjwgjyJO68nuBD93tY/xqZtcldf6xlqREUqfi51b/z3MFLv1BeKbiXMkE3T31615Jl1yezK9rfn1rrLDvLoqknZL+9L11wz83JT0VP9dgGO0MUD7cZ5IeH1k+95JE0nZJyz5xIBdJ6kra3nRGV0H5IN9lSTvy8qVV92lEXl8BOyNiCddjwGV3r6whI3TMrCfpPuAbYLEgiQq5zW9AN+acygZY5yfrS1oCHo1Ij4i1vzazfgHxTBM9T52PuPJF+UMwyLIbK7y0agYIoUnSQWC//zn+X5jwlAuTNGQEA7YBnwB7KiRPPxZVwGmFV5858x8F/gDeA+6MJgufzxou4wVsAba6wTsD8pU+8HPOY6t7gMfWe4ADnjDlLZm4C24EBr35kHBd8SSIvHemFZTPJO0Hlipkd2VNFg1pwowz3qlQ+p4pIsCBBoiUf9aVz0rKy7xbrriVw/c54PZIoGmOU0KAx5wAUyfwynG/I+mEx9JBBVOcC9wraVHSXX59MXpumuNZgVxB5tfLMsC0tFW0HvZ2A7vc0lWyJwEXzWw1mmulwfEiAux5CUxRdEqHMOfT0SRV8/Ytkv7yuXteITY5nt//XU+CKKo0h0WBuswuoGtmvbDXJK02OJ7f/x3gFLAavLquAXo1DTAHvOBChQTpkYbGy1Lvnm/nQg8eZoCqbh8Wnwe+GPDcNMdtFB2GGWBthA5Nf4gQ0x4v0qG2AYLS3w+J/aN4zbTHYx0E/JAj9kqT9b2cPQ78PsDys4ywJY4Mkj8pKYAEJGZ2DTjsz2X/IeXXnCQ/NbNfPKvt1yGOfDq85KVwL9oSs3awGlw+82hxAngeuMz6EZxGMYBFzZCPWD9BKiOjUBY31QvIrxV783fAS2Z2qSz+12pbhxgq6WVJ30q6qtlDz3uV70iaCyl9VaKoVBwFS3pv4IEcux4GdgNvA8tRojJJlK2VeLPmdCSjTfz836tDKxk76m9iTwOd4NK1pPXDnqpz1TogCKcp7lrmBBksHarFrTn3mwhZVlgrnC7VSt/TEYXpOzdsAzqS+tFc837U3QFW6go0gIcaWauSMH5dkHTWT4guSLruRNSVdN5/fyLqLM/kWumYhHR/we8L0f1tEyS/qaw1jgHWvDKbj5oPoU8XenHnyxoRM7zW5oKNyQfDEg1NKhY3uVaLFi1atGjRokWLFi1a/O/xN2I6zTiGXhagAAAAAElFTkSuQmCC",
            ["crate"] = "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",
            ["heli"] = "iVBORw0KGgoAAAANSUhEUgAAAEAAAABACAYAAACqaXHeAAAFbUlEQVR42u2ZT4hVVRzHP7/33jilOOkUElLaHzFKMSOnaKHg7PoDURG2kwZm06pcRS1atAmCFlGbiGgR1aJNRCIUiBZtKrQgQrEQTInMRnGcnJl377dFv6s/L/e9ue/PPGW8Xzjc884995zz+53f/wcVKlSoUKFChQrXKSw/IKleNL5kCDZrtnwpya5bCZBkZiZJTwNbgBSoLRE65c8zZvZOpLeRib2ZJZJ2AZ8u5RuXtMHMXvTLTRo5SVg34PNk+jhIu7Mx/mhkhsH1/y1gDfDgIqqAuUjeAdzpY1PA4UGoAPCC99OrLYqv6DK+ulrnaLRwg+rTTVvBTdRd9IfjOSTVXOIW82YMSM1MLRlgZkmXN2pOQCbiV2wU5jXCvEvDZpZKwszSPklYkU1R/kyNPmxUA2oeYCS5d6ty08+b2ay/m1nkgEddqUCnN+4Sk0paDmwFxoEHgNuBDbnP/pR0EvjG5yiTmH6rgKQbgBvjHv483zYa7ODWs/4mSW9I+k2do+nPgzkb1MvZstjmVUn/SDrtzzP+3N7TPtmHkkYkvSYpbUHcnKTE32ctcaLnvS9/zkn6RNLGXpkQGLCnxbnG4x6NThf3mGEM+MiDir+AA8AvLmLrgUeAe4Llt1bJl4t9DXgOeEzSS2b2QbZXDz7/+8wYB9Wqde3hMrGXtM3FSS4BawrmDkt6PsxLS6jDfOhP+jrLJNUl1comakFCx4OExefOjqUsEL/FdUqSJuKmkhre6mH8IUl/B1VYCEmwC7uLDK8z49JeeQYFBuwIatc7AyQNSdrvi7wfbshaHHTY+7tzBq8ME1JJFyQ96ze5TtLqMp4pMOdWSVNBAgsZYGVEyjPFJ4AvgAvANuCo+9t0gcCoAfzkNqFsfqHc2WaAWeAYMO3rnQN+9jziGDBtZlO5M5wBRn09+d7jZrY/o6sTIzjhi0wDf3jkZu0CEf2fdM9KOu4MKGuALBgwA5Z7G/P3O3PzZ4BZSRmDDgP/Aity63UWCHnRIJG00jc3Dy5WhkiuHVHmTBpt4wXaMaGe2yPN/baSDGqJsunuELDS+yPApIt+ow3zhjxK3N6H9NoCQ+q+b8P7tSDiibdmqDX0hQEpMOf9BNgjaczM5oIHqEdvYGbzkkaBtwdQWssYVAvNyoTVtYUSCneB54BfA6dvAvZJetTMEjNr+jPrNyVtAvYB9/exuNLqppNgLzJG1EMAFFvH2WDNCfoY2BEMyiiw14sZe90im5fVdgK7gGU9EJ+GG4yEEWwDRcYQ+B04C6wF7s1ddqOwKlwirx4BDnkpa6HwNhLRLfG1NgSWcoeS1gbvk53zsJlNZVXh0uGle4NngM9c5DI9S4KPjWLabaEz8W9/9LR5QX/fKnjrV3ElH2NPhuiqqf4iC5ffKxHxtQ2JQwRbzzXrhQmNwIT5kMT0wogkrHFR0rvh8G0JvFrV3IwJD0v6us1Npm3G8zWBDB9KWu2EXrv/SuUyvglJ37VIe5NcK8IRL5F/6d9PREZfy0zI69uTkmYkTUs66f0icT8t6YCkNz3TW+7fv+xzXh8kA7reJEuGvPg4B5zwPOEg8DhwC3BXvigKnDKzs7lawhBwxIe2Xn51pb6XrfQOhAHhQBf9tPf58Ckzm3Y/fbzEGgmQSDrh7nNNL/9PDIwBfjsjnig1gc3+6qgXL4YKYnGFlk99pzyF3Sxpvfv/WM6ed8b2PYnomHDPEVYBPwCr/JDDnpJedEKswwKkeY6BE69cVPitmT3V7wCnFxWoAXcXjK8IhYhucXPB2G3XlAoA88DnXidQSFjUB4lUQWh8qEQBpsKi24CicvnADtvP5KZChQoVKlSoUKFChQoVKlSocN3iP+thji/3AMpEAAAAAElFTkSuQmCC",
            ["oilrig_large"] = "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",
            ["oilrig_small"] = "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",
            ["plane"] = "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",
            ["radiation"] = "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",
            ["ship"] = "iVBORw0KGgoAAAANSUhEUgAAAEAAAABACAYAAACqaXHeAAAD00lEQVR42u2av4sdVRTHP2feW3ZXxBiriCBrY2UhWOS/sLDyjxAVISI2WlooNnYimkKbQBIiFmks/FkYTFKIaJAVmxV2dTfZYNzdN+9rseeuNzfz5s2GmQczuV8Y5td3Zt4995zvOefyICMjIyMjIyMjIyMjI+PBgx33AUnj+NTMymzGoXuAJDMzSVoFXgZW/da6mZ0N9wdrJUnm+8d0N6749aKvYzvuDxewCUx82+775I7v85nw3Oi4XlQbj4dh1iqvCwPcn9g01Ii2ea0ZwNNfyje/Xkg6qPtRklZcPJWIbzifmNlui7wDM7vdhSD+FYng5aZ1g6Q3JP0tadP3Ydvy/RfOe7Ml3qUmAj0+Zvo7GaVAgKclveVi+omZrUsqzGxa8bqH/PlZeLRl3ok2Q2AVeKfi+hrwth9/C6zX1Balu+ck+W7pYjrpiNdKGozTn5Lr+359v0HRNW/rgtdaFggiqGRQYzek0UN0kgaTHG3RuXybJl6kw8c64R19vypLdWKA5EMHkVEMWJoRhksuuEWbPNeEhXtAnKML318ErvtxCew6Z9kHsiPpJHAe+LEF3rbzjiaiqi4YdzD7paQzwOuRIuMi+Y+ff29mL0p6CTjjIrrsqW0PuNMib+qT8A3wfJqmuyqF5+XoDUkj4AngyQXxTrTRDTZFyNEHsShFaXTPV5L2F8CbRPuFNUOz8vG8XN4Fr7Yu6O1CRut1QLLYGedwNfSUUfSO0QwDFwl3EbyUU0iaAqWZ/T9oM6urnTfdEHXYDu+QtAP8W1On75rZZEG8wLlVNcaw1rcMvAqszFgwXQFeSe6n/fdZ4PeoSXqqplffAa46Z61jXjjeBq5FtclHZvaHefoQ8B1w+gEJ/ZtuqJvjQ++3UtIHwHMVbtY0Y5RRo2RzBDbU74vkhc51CXjXzHYkjS1a9Hgc+BV4uMLVhoAwOXvAM2b2m6Si8MGPzGwDuBTN5tBQ+qRe8MGPzGyausunA64PQlP2YVUWCGHwCPALcGpgYRAaop+AZ0MNcDTTURjcAi4PMAxC9/ex1wKjulL43MDCQD7g28Bn6eQWFVb6CvgzipmhiN/nZrbhnq57DBCFwa6HgQYSBpXiN68bPNeg+OhL7BvwM/C1i31ZZ4CpL15+Cdzw+9Oex78B76fiV2kAj42Rmd3xRUx6bIDQ9GwBF31iyyb1fRCI88BrPdaCidf9F8xsy/WtrGyH77HAobWWvCha67kOnAZ+AIoqA8zq8Aoz25f0HvACdy9v96nyuwFc8fDOf+erQm2t3+d/f0XCPs3TnJGRkZGRkVGF/wBTc7Q1Mc7WFwAAAABJRU5ErkJggg==",
            ["supplydrop"] = "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",
            ["tank"] = "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",
        };

        // White edge glow (transparent middle), tinted green or red for the radiation screen tint.
        private const string VignettePng = "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";

        private readonly Dictionary<string, string> _icons = new Dictionary<string, string>();

        private void StoreIcons()
        {
            foreach (var kv in IconData)
            {
                var crc = FileStorage.server.Store(Convert.FromBase64String(kv.Value), FileStorage.Type.png, CommunityEntity.ServerInstance.net.ID, 0);
                _icons[kv.Key] = crc.ToString();
            }
            _icons["vignette"] = FileStorage.server.Store(Convert.FromBase64String(VignettePng), FileStorage.Type.png, CommunityEntity.ServerInstance.net.ID, 0).ToString();
        }

        #endregion

        #region State

        private enum Kind { Airdrop, PatrolHelicopter, Bradley, CargoShip, Chinook, SmallOilRig, LargeOilRig }
        private enum Tile { Inactive, Active, Loot }
        private enum CrateState { Waiting, Hacking, Unlocked, Looted }

        private static readonly Kind[] AllKinds = { Kind.Airdrop, Kind.PatrolHelicopter, Kind.Bradley, Kind.CargoShip, Kind.Chinook, Kind.SmallOilRig, Kind.LargeOilRig };

        private class CrateInfo
        {
            public string SourceKey = "SrcOther";
            public bool Classified;
            public bool Announced;
            public CrateState State;
            public float LootedAt;
            public string Looter;
            public readonly HashSet<int> Reminded = new HashSet<int>();
        }

        private class DropInfo
        {
            public bool Announced, Landed, Looted;
            public float LootedAt;
            public string Looter;
        }

        private class LootCrateInfo
        {
            public bool IsHeli;
            public bool Looted;
            public float LootedAt;
        }

        private class RigInfo
        {
            public Vector3 Position;
            public bool Large;
        }

        private readonly HashSet<CargoPlane> _planes = new HashSet<CargoPlane>();
        private readonly Dictionary<SupplyDrop, DropInfo> _drops = new Dictionary<SupplyDrop, DropInfo>();
        private readonly HashSet<PatrolHelicopter> _helis = new HashSet<PatrolHelicopter>();
        private readonly HashSet<BradleyAPC> _bradleys = new HashSet<BradleyAPC>();
        private readonly HashSet<CargoShip> _ships = new HashSet<CargoShip>();
        private readonly HashSet<CH47HelicopterAIController> _chinooks = new HashSet<CH47HelicopterAIController>();
        private readonly Dictionary<HackableLockedCrate, CrateInfo> _crates = new Dictionary<HackableLockedCrate, CrateInfo>();
        private readonly Dictionary<LockedByEntCrate, LootCrateInfo> _lootCrates = new Dictionary<LockedByEntCrate, LootCrateInfo>();
        private readonly HashSet<BaseEntity> _destroyed = new HashSet<BaseEntity>();
        private readonly List<RigInfo> _rigs = new List<RigInfo>();
        private readonly Dictionary<string, float> _lastLootAnnounce = new Dictionary<string, float>();

        // Looted entries that outlived their entity, so they can linger in the list.
        private readonly List<KeyValuePair<float, HudLine>> _ghostLines = new List<KeyValuePair<float, HudLine>>();

        private bool _ready;
        private Timer _tick, _radTick;
        private string _tilesSig = "", _linesSig = "";
        private int _tileCount;
        private List<Kind> _kinds = new List<Kind>();
        private List<Tile> _states = new List<Tile>();
        private bool _dropWaiting;
        private List<HudLine> _lines = new List<HudLine>();
        private bool _built;
        // Players can each put the HUD somewhere else, so containers are cached per layout.
        private readonly Dictionary<string, CuiElementContainer> _tilesCache = new Dictionary<string, CuiElementContainer>();
        private readonly Dictionary<string, CuiElementContainer> _linesCache = new Dictionary<string, CuiElementContainer>();

        private class PlayerPrefs
        {
            public bool Hidden;
            public bool NoTint;
            public string Position; // null = server default
            public string InfoPosition; // the radiation/compass/grid HUD; null = server default
            public float InfoX, InfoY;
            public float X, Y;
        }

        private Dictionary<ulong, PlayerPrefs> _prefs = new Dictionary<ulong, PlayerPrefs>();
        private const string DataFile = "RustSandboxEventHud_players";
        private const string OldHiddenFile = "RustSandboxEventHud_hidden";

        #endregion

        #region Hooks

        private void Init()
        {
            try { _prefs = Interface.Oxide.DataFileSystem.ReadObject<Dictionary<ulong, PlayerPrefs>>(DataFile) ?? new Dictionary<ulong, PlayerPrefs>(); }
            catch { _prefs = new Dictionary<ulong, PlayerPrefs>(); }
            try
            {
                // 1.0.0 only stored who had hidden the HUD.
                if (Interface.Oxide.DataFileSystem.ExistsDatafile(OldHiddenFile))
                    foreach (var id in Interface.Oxide.DataFileSystem.ReadObject<HashSet<ulong>>(OldHiddenFile) ?? new HashSet<ulong>())
                        Prefs(id).Hidden = true;
                Interface.Oxide.DataFileSystem.WriteObject(OldHiddenFile, new HashSet<ulong>());
            }
            catch { }
        }

        private void OnServerInitialized()
        {
            StoreIcons();
            FindRigs();
            foreach (var net in BaseNetworkable.serverEntities.ToList())
                Track(net, silent: true);
            RefreshRadiationTriggers();
            _ready = true;

            Tick();
            _tick = timer.Every(1f, Tick);
            if (_config.RadiationDetector) _radTick = timer.Every(0.1f, RadiationTick);
            if (_config.Compass || _config.Coordinates) timer.Every(0.2f, InfoTick);
            timer.Every(60f, RefreshRadiationTriggers);
        }

        private void Unload()
        {
            foreach (var player in BasePlayer.activePlayerList)
            {
                DestroyHud(player);
                CloseEditor(player);
                HideTint(player);
            }
        }

        private void OnEntitySpawned(BaseNetworkable entity)
        {
            if (_ready) Track(entity, silent: false);
        }

        private void OnEntityDeath(BaseCombatEntity entity, HitInfo info)
        {
            if (entity == null) return;
            var heli = entity as PatrolHelicopter;
            var apc = entity as BradleyAPC;
            if (heli == null && apc == null) return;
            if (!_destroyed.Add(entity)) return;

            var grid = Grid(entity.transform.position);
            var killer = info?.InitiatorPlayer;
            var name = killer != null && !killer.IsNpc ? killer.displayName : null;
            if (heli != null) Announce(name != null ? "HeliDown" : "HeliDownNoKiller", grid, name);
            else Announce(name != null ? "BradleyDown" : "BradleyDownNoKiller", grid, name);
        }

        private void OnEntityKill(BaseNetworkable entity)
        {
            if (entity == null) return;
            var be = entity as BaseEntity;

            var heli = entity as PatrolHelicopter;
            if (heli != null && _helis.Remove(heli))
            {
                if (!_destroyed.Remove(heli)) { if (heli.health > 0f) Announce("HeliLeft"); else Announce("HeliDownNoKiller", Grid(heli.transform.position)); }
                return;
            }
            var apc = entity as BradleyAPC;
            if (apc != null && _bradleys.Remove(apc))
            {
                if (!_destroyed.Remove(apc) && apc.health <= 0f) Announce("BradleyDownNoKiller", Grid(apc.transform.position));
                return;
            }
            var ship = entity as CargoShip;
            if (ship != null && _ships.Remove(ship))
            {
                Announce("CargoLeft");
                return;
            }

            var crate = entity as HackableLockedCrate;
            CrateInfo ci;
            if (crate != null && _crates.TryGetValue(crate, out ci))
            {
                if (ci.State == CrateState.Looted) Linger(ci.LootedAt, CrateLine(crate, ci));
                _crates.Remove(crate);
                return;
            }
            var drop = entity as SupplyDrop;
            DropInfo di;
            if (drop != null && _drops.TryGetValue(drop, out di))
            {
                if (di.Looted) Linger(di.LootedAt, DropLine(drop, di));
                _drops.Remove(drop);
                return;
            }

            var plane = entity as CargoPlane;
            if (plane != null) { _planes.Remove(plane); return; }
            var ch47 = entity as CH47HelicopterAIController;
            if (ch47 != null) { _chinooks.Remove(ch47); return; }
            var lc = entity as LockedByEntCrate;
            if (lc != null) { _lootCrates.Remove(lc); return; }
            if (be != null) _destroyed.Remove(be);
        }

        private void OnLootEntity(BasePlayer player, BaseEntity entity)
        {
            if (player == null || entity == null) return;
            DropInfo di;
            var drop = entity as SupplyDrop;
            if (drop != null && _drops.TryGetValue(drop, out di) && di.Looter == null) di.Looter = player.displayName;
            CrateInfo ci;
            var crate = entity as HackableLockedCrate;
            if (crate != null && _crates.TryGetValue(crate, out ci) && ci.Looter == null) ci.Looter = player.displayName;

            LootCrateInfo li;
            var lc = entity as LockedByEntCrate;
            if (lc != null && _lootCrates.TryGetValue(lc, out li) && !li.Looted)
            {
                var key = li.IsHeli ? "HeliLoot" : "BradleyLoot";
                float last;
                if (!_lastLootAnnounce.TryGetValue(key, out last) || Time.realtimeSinceStartup - last > 300f)
                {
                    _lastLootAnnounce[key] = Time.realtimeSinceStartup;
                    Announce(key, Grid(lc.transform.position), player.displayName);
                }
            }
        }

        private void OnPlayerConnected(BasePlayer player)
        {
            timer.Once(3f, () => { if (player != null && player.IsConnected) DrawHud(player); });
        }

        private void OnPlayerSleepEnded(BasePlayer player)
        {
            if (player != null && player.IsConnected && _ready) DrawHud(player);
        }

        private void OnPlayerDisconnected(BasePlayer player, string reason)
        {
            if (player != null) { _rad.Remove(player.userID); _info.Remove(player.userID); }
        }

        #endregion

        #region Tracking

        private void Track(BaseNetworkable entity, bool silent)
        {
            if (entity == null || entity.IsDestroyed) return;

            var plane = entity as CargoPlane;
            if (plane != null)
            {
                if (_planes.Add(plane) && !silent) Announce("PlaneInbound");
                return;
            }
            var drop = entity as SupplyDrop;
            if (drop != null)
            {
                if (!_drops.ContainsKey(drop))
                {
                    var di = new DropInfo { Announced = silent };
                    if (silent) { di.Landed = drop.isLootable; di.Looted = drop.HasBeenLooted; }
                    _drops[drop] = di;
                }
                return;
            }
            var heli = entity as PatrolHelicopter;
            if (heli != null)
            {
                if (_helis.Add(heli) && !silent) Announce("HeliSpawned");
                return;
            }
            var apc = entity as BradleyAPC;
            if (apc != null)
            {
                if (_bradleys.Add(apc) && !silent)
                {
                    var a = apc;
                    // Position is final a moment after spawn.
                    timer.Once(1f, () => { if (a != null && !a.IsDestroyed) Announce("BradleySpawned", Grid(a.transform.position)); });
                }
                return;
            }
            var ship = entity as CargoShip;
            if (ship != null)
            {
                if (_ships.Add(ship) && !silent) Announce("CargoArrived");
                return;
            }
            var ch47 = entity as CH47HelicopterAIController;
            if (ch47 != null)
            {
                if (_chinooks.Add(ch47) && !silent) Announce("ChinookInbound");
                return;
            }
            var crate = entity as HackableLockedCrate;
            if (crate != null)
            {
                if (!_crates.ContainsKey(crate))
                {
                    var ci = new CrateInfo { Announced = silent, State = ReadState(crate) };
                    if (silent && ci.State == CrateState.Looted) ci.LootedAt = -9999f;
                    _crates[crate] = ci;
                }
                return;
            }
            var lc = entity as LockedByEntCrate;
            if (lc != null && !_lootCrates.ContainsKey(lc))
            {
                var name = lc.ShortPrefabName ?? "";
                if (name.Contains("heli_crate")) _lootCrates[lc] = new LootCrateInfo { IsHeli = true, Looted = silent && lc.HasBeenLooted };
                else if (name.Contains("bradley_crate")) _lootCrates[lc] = new LootCrateInfo { IsHeli = false, Looted = silent && lc.HasBeenLooted };
            }
        }

        private void FindRigs()
        {
            _rigs.Clear();
            if (TerrainMeta.Path == null || TerrainMeta.Path.Monuments == null) return;
            foreach (var m in TerrainMeta.Path.Monuments)
            {
                if (m == null) continue;
                var name = (m.name ?? "").ToLowerInvariant();
                if (!name.Contains("oilrig")) continue;
                var english = m.displayPhrase != null ? m.displayPhrase.english ?? "" : "";
                var large = english.Length > 0 ? english.IndexOf("large", StringComparison.OrdinalIgnoreCase) >= 0 : name.Contains("oilrig_1");
                _rigs.Add(new RigInfo { Position = m.transform.position, Large = large });
            }
        }

        private static CrateState ReadState(HackableLockedCrate crate)
        {
            if (crate.HasBeenLooted()) return CrateState.Looted;
            if (crate.IsFullyHacked()) return CrateState.Unlocked;
            if (crate.IsBeingHacked()) return CrateState.Hacking;
            return CrateState.Waiting;
        }

        private void Classify(HackableLockedCrate crate, CrateInfo ci)
        {
            if (crate.GetParentEntity() is CargoShip) ci.SourceKey = "SrcCargo";
            else if ((crate.ShortPrefabName ?? "").Contains("oilrig"))
            {
                var pos = crate.transform.position;
                RigInfo best = null;
                var bestDist = float.MaxValue;
                foreach (var rig in _rigs)
                {
                    var d = Vector3.Distance(rig.Position, pos);
                    if (d < bestDist) { bestDist = d; best = rig; }
                }
                ci.SourceKey = best == null ? "SrcOther" : best.Large ? "SrcLargeRig" : "SrcSmallRig";
            }
            else if (crate.wasDropped) ci.SourceKey = "SrcChinook";
            else ci.SourceKey = "SrcOther";
            ci.Classified = true;
        }

        private bool IsRig(CrateInfo ci) => ci.SourceKey == "SrcLargeRig" || ci.SourceKey == "SrcSmallRig";

        private static float HackRemaining(HackableLockedCrate crate) => Mathf.Max(0f, HackableLockedCrate.requiredHackSeconds - crate.hackSeconds);

        private static string Clock(float seconds)
        {
            var s = Mathf.CeilToInt(seconds);
            return $"{s / 60}:{s % 60:00}";
        }

        private static string Grid(Vector3 pos)
        {
            try { return MapHelper.PositionToString(pos); }
            catch { return $"{pos.x:0}, {pos.z:0}"; }
        }

        private string PlayerName(ulong id, string fallback)
        {
            if (!string.IsNullOrEmpty(fallback)) return fallback;
            if (id != 0)
            {
                var p = covalence.Players.FindPlayerById(id.ToString());
                if (p != null) return p.Name;
            }
            return Msg("SomeoneUnknown");
        }

        #endregion

        #region Tick

        private class HudLine
        {
            public int Order;
            public string Title, Grid, Status, Color;
        }

        private void Tick()
        {
            var now = Time.realtimeSinceStartup;

            foreach (var kv in _crates.ToList())
            {
                var crate = kv.Key;
                var ci = kv.Value;
                if (crate == null || crate.IsDestroyed) { _crates.Remove(crate); continue; }
                if (!ci.Classified || ci.SourceKey == "SrcOther") Classify(crate, ci);

                var state = ReadState(crate);
                var grid = Grid(crate.transform.position);
                var src = Msg(ci.SourceKey);

                if (!ci.Announced)
                {
                    ci.Announced = true;
                    if (!IsRig(ci) || _config.AnnounceIdleRigCrates)
                        Announce(ci.SourceKey == "SrcChinook" ? "CrateDropped" : "CrateWaiting", grid, src);
                }

                if (state != ci.State)
                {
                    if (state == CrateState.Hacking)
                    {
                        var left = HackRemaining(crate);
                        foreach (var m in _config.HackReminders)
                            if (m * 60f >= left - 1f) ci.Reminded.Add(m);
                        Announce(IsRig(ci) ? "RigEvent" : "CrateHacking", grid, src, Clock(left));
                    }
                    else if (state == CrateState.Unlocked) Announce("CrateUnlocked", grid, src);
                    else if (state == CrateState.Looted)
                    {
                        ci.LootedAt = now;
                        Announce("CrateLooted", grid, src, PlayerName(crate.FirstLooterId, ci.Looter));
                    }
                    ci.State = state;
                }
                else if (state == CrateState.Hacking)
                {
                    var left = HackRemaining(crate);
                    foreach (var m in _config.HackReminders)
                    {
                        if (m <= 0 || left > m * 60f || !ci.Reminded.Add(m)) continue;
                        Announce("CrateReminder", grid, src, Clock(left));
                    }
                }
            }

            foreach (var kv in _drops.ToList())
            {
                var drop = kv.Key;
                var di = kv.Value;
                if (drop == null || drop.IsDestroyed) { _drops.Remove(drop); continue; }
                var grid = Grid(drop.transform.position);
                if (!di.Announced) { di.Announced = true; Announce("DropFalling", grid); }
                if (!di.Landed && drop.isLootable) { di.Landed = true; Announce("DropLanded", grid); }
                if (!di.Looted && drop.HasBeenLooted)
                {
                    di.Looted = true;
                    di.LootedAt = now;
                    Announce("DropLooted", grid, PlayerName(drop.FirstLooterId, di.Looter));
                }
            }

            foreach (var kv in _lootCrates.ToList())
            {
                if (kv.Key == null || kv.Key.IsDestroyed) { _lootCrates.Remove(kv.Key); continue; }
                if (!kv.Value.Looted && kv.Key.HasBeenLooted) { kv.Value.Looted = true; kv.Value.LootedAt = now; }
            }

            _ghostLines.RemoveAll(g => now - g.Key > _config.LootedLinger);

            UpdateHud(now);
        }

        private Tile TileState(Kind kind)
        {
            switch (kind)
            {
                case Kind.Airdrop:
                    return _planes.Count > 0 || _drops.Values.Any(d => !d.Looted) ? Tile.Active : Tile.Inactive;
                case Kind.PatrolHelicopter:
                    if (_helis.Any(h => h != null && !h.IsDestroyed && h.health > 0f)) return Tile.Active;
                    return _lootCrates.Values.Any(c => c.IsHeli && !c.Looted) ? Tile.Loot : Tile.Inactive;
                case Kind.Bradley:
                    if (_bradleys.Any(b => b != null && !b.IsDestroyed && b.health > 0f)) return Tile.Active;
                    return _lootCrates.Values.Any(c => !c.IsHeli && !c.Looted) ? Tile.Loot : Tile.Inactive;
                case Kind.CargoShip:
                    return _ships.Count > 0 ? Tile.Active : Tile.Inactive;
                case Kind.Chinook:
                    return _chinooks.Count > 0 ? Tile.Active : Tile.Inactive;
                case Kind.SmallOilRig:
                case Kind.LargeOilRig:
                    var key = kind == Kind.LargeOilRig ? "SrcLargeRig" : "SrcSmallRig";
                    return _crates.Values.Any(c => c.SourceKey == key && (c.State == CrateState.Hacking || c.State == CrateState.Unlocked)) ? Tile.Active : Tile.Inactive;
            }
            return Tile.Inactive;
        }

        private HudLine CrateLine(HackableLockedCrate crate, CrateInfo ci)
        {
            var line = new HudLine { Title = Msg(ci.SourceKey == "SrcOther" ? "SrcOther" : ci.SourceKey), Grid = Grid(crate.transform.position) };
            switch (ci.State)
            {
                case CrateState.Hacking:
                    line.Status = Msg("LineHacking", Clock(HackRemaining(crate)));
                    line.Color = "#ffb347";
                    line.Order = 0;
                    break;
                case CrateState.Unlocked:
                    line.Status = Msg("LineUnlocked");
                    line.Color = "#7ee081";
                    line.Order = 1;
                    break;
                case CrateState.Looted:
                    line.Status = Msg("LineLooted");
                    line.Color = "#9a9a9a";
                    line.Order = 9;
                    break;
                default:
                    line.Status = Msg("LineWaiting");
                    line.Color = "#ff8a80";
                    line.Order = 2;
                    break;
            }
            if (line.Title.Length > 0) line.Title = char.ToUpper(line.Title[0]) + line.Title.Substring(1);
            return line;
        }

        private HudLine DropLine(SupplyDrop drop, DropInfo di)
        {
            return new HudLine
            {
                Title = Msg("LineAirdrop"),
                Grid = Grid(drop.transform.position),
                Status = di.Looted ? Msg("LineLooted") : di.Landed ? Msg("LineLanded") : Msg("LineFalling"),
                Color = di.Looted ? "#9a9a9a" : di.Landed ? "#7ee081" : "#ffd479",
                Order = di.Looted ? 9 : 3,
            };
        }

        private void Linger(float lootedAt, HudLine line)
        {
            if (Time.realtimeSinceStartup - lootedAt < _config.LootedLinger)
                _ghostLines.Add(new KeyValuePair<float, HudLine>(lootedAt, line));
        }

        private List<HudLine> BuildLines(float now)
        {
            var lines = new List<HudLine>();
            foreach (var kv in _crates)
            {
                var ci = kv.Value;
                if (kv.Key == null || kv.Key.IsDestroyed) continue;
                if (ci.State == CrateState.Waiting && IsRig(ci) && !_config.ShowIdleRigCrates) continue;
                if (ci.State == CrateState.Looted && now - ci.LootedAt > _config.LootedLinger) continue;
                if (!Shown(ci.SourceKey)) continue;
                lines.Add(CrateLine(kv.Key, ci));
            }
            if (Enabled(Kind.Airdrop))
            {
                foreach (var kv in _drops)
                {
                    if (kv.Key == null || kv.Key.IsDestroyed) continue;
                    if (kv.Value.Looted && now - kv.Value.LootedAt > _config.LootedLinger) continue;
                    lines.Add(DropLine(kv.Key, kv.Value));
                }
            }

            // Heli and Bradley crates, one line per kind: where they are and how many are left.
            foreach (var heli in new[] { true, false })
            {
                if (!Enabled(heli ? Kind.PatrolHelicopter : Kind.Bradley)) continue;
                var crates = _lootCrates.Where(kv => kv.Value.IsHeli == heli && kv.Key != null && !kv.Key.IsDestroyed && !kv.Value.Looted).Select(kv => kv.Key).ToList();
                if (crates.Count == 0) continue;
                var burning = crates.Any(c => c.IsLocked());
                lines.Add(new HudLine
                {
                    Title = Msg(heli ? "LineHeliLoot" : "LineBradleyLoot"),
                    Grid = Grid(crates[0].transform.position),
                    Status = burning ? Msg("LineBurning") : Msg("LineReady", crates.Count),
                    Color = burning ? "#ffb347" : "#7ee081",
                    Order = 4,
                });
            }

            lines.AddRange(_ghostLines.Select(g => g.Value));
            return lines.OrderBy(l => l.Order).ThenBy(l => l.Grid).Take(Math.Max(0, _config.MaxLines)).ToList();
        }

        private bool Enabled(Kind kind)
        {
            bool on;
            return !_config.Events.TryGetValue(kind.ToString(), out on) || on;
        }

        private bool Shown(string sourceKey)
        {
            if (sourceKey == "SrcLargeRig") return Enabled(Kind.LargeOilRig);
            if (sourceKey == "SrcSmallRig") return Enabled(Kind.SmallOilRig);
            if (sourceKey == "SrcCargo") return Enabled(Kind.CargoShip);
            if (sourceKey == "SrcChinook") return Enabled(Kind.Chinook);
            return true;
        }

        #endregion

        #region HUD

        private const string TilesRoot = "EventHud.Tiles";
        private const string LinesRoot = "EventHud.Lines";
        private const string RadRoot = "EventHud.Rad";
        private const string RadGlow = "EventHud.RadGlow";
        private const float Gap = 4f;
        private const float LabelHeight = 12f;
        private const float LineHeight = 17f;
        private const float LineWidth = 280f;

        private bool Wants(BasePlayer player) => _config.ShowHud && player != null && player.IsConnected && !Prefs(player.userID, false).Hidden;

        private string ColorFor(Tile t) => t == Tile.Active ? _config.ActiveColor : t == Tile.Loot ? _config.LootColor : _config.InactiveColor;

        private void UpdateHud(float now)
        {
            if (!_config.ShowHud) return;

            var kinds = AllKinds.Where(Enabled).ToList();
            var states = kinds.Select(TileState).ToList();
            var dropWaiting = _drops.Values.Any(d => !d.Looted);
            var tilesSig = string.Join(",", states.Select(s => ((int)s).ToString())) + (dropWaiting ? "+" : "-") + (_planes.Count > 0 ? "p" : "");
            var lines = BuildLines(now);
            var linesSig = string.Join("|", lines.Select(l => l.Title + l.Grid + l.Status));
            var first = !_built;
            _built = true;

            if (first || tilesSig != _tilesSig)
            {
                _tilesSig = tilesSig;
                _kinds = kinds;
                _states = states;
                _dropWaiting = dropWaiting;
                _tileCount = kinds.Count;
                _tilesCache.Clear();
                foreach (var p in BasePlayer.activePlayerList)
                    if (Wants(p)) { CuiHelper.DestroyUi(p, TilesRoot); CuiHelper.AddUi(p, TilesFor(LayoutFor(p))); }
            }
            if (first || linesSig != _linesSig)
            {
                _linesSig = linesSig;
                _lines = lines;
                _linesCache.Clear();
                foreach (var p in BasePlayer.activePlayerList)
                    if (Wants(p)) { CuiHelper.DestroyUi(p, LinesRoot); CuiHelper.AddUi(p, LinesFor(LayoutFor(p))); }
            }
        }

        private CuiElementContainer TilesFor(Layout L)
        {
            CuiElementContainer c;
            if (!_tilesCache.TryGetValue(L.Key, out c)) _tilesCache[L.Key] = c = BuildTiles(L, _kinds, _states, _dropWaiting);
            return c;
        }

        private CuiElementContainer LinesFor(Layout L)
        {
            CuiElementContainer c;
            if (!_linesCache.TryGetValue(L.Key, out c)) _linesCache[L.Key] = c = BuildLinesUi(L, _lines);
            return c;
        }

        private void DrawHud(BasePlayer player)
        {
            if (!Wants(player)) return;
            if (!_built) UpdateHud(Time.realtimeSinceStartup);
            var L = LayoutFor(player);
            CuiHelper.DestroyUi(player, TilesRoot);
            CuiHelper.DestroyUi(player, LinesRoot);
            CuiHelper.AddUi(player, TilesFor(L));
            CuiHelper.AddUi(player, LinesFor(L));
            RadiationState rs;
            if (_rad.TryGetValue(player.userID, out rs)) rs.Sig = null; // redraw the detector too
            _info.Remove(player.userID); // and the compass and grid
        }

        private void DestroyHud(BasePlayer player)
        {
            CuiHelper.DestroyUi(player, TilesRoot);
            CuiHelper.DestroyUi(player, LinesRoot);
            CuiHelper.DestroyUi(player, RadGlow);
            CuiHelper.DestroyUi(player, RadRoot);
            CuiHelper.DestroyUi(player, InfoRoot);
            _info.Remove(player.userID);
        }

        #endregion

        #region Layout

        private static readonly string[] Positions = { "TopLeft", "TopRight", "MiddleLeft", "MiddleRight", "BottomLeft", "BottomRight", "AboveHotbar" };
        private const float Margin = 12f;

        // Where the HUD's top-left corner sits: a screen anchor plus a pixel offset from it.
        private class Layout
        {
            public string Anchor;
            public float Left, Top;
            public string Key;
        }

        private PlayerPrefs Prefs(ulong id, bool create = true)
        {
            PlayerPrefs p;
            if (_prefs.TryGetValue(id, out p)) return p;
            p = new PlayerPrefs();
            if (create) _prefs[id] = p;
            return p;
        }

        private void SavePrefs() => Interface.Oxide.DataFileSystem.WriteObject(DataFile, _prefs);

        private static string NormalizePosition(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            foreach (var p in Positions)
                if (string.Equals(p, name.Replace(" ", "").Replace("-", ""), StringComparison.OrdinalIgnoreCase)) return p;
            return null;
        }

        private float HudWidth => Math.Max(LineWidth, _tileCount * (_config.IconSize + Gap) - Gap);
        private float HudHeight => _config.IconSize + LabelHeight + 4f + Math.Max(0, _config.MaxLines) * LineHeight;

        private Layout LayoutFor(BasePlayer player)
        {
            var pr = player != null ? Prefs(player.userID, false) : null;
            var custom = _config.AllowMove && pr != null && pr.Position != null;
            var position = (custom ? pr.Position : NormalizePosition(_config.DefaultPosition)) ?? "TopRight";
            var dx = custom ? pr.X : _config.DefaultX;
            var dy = custom ? pr.Y : _config.DefaultY;
            return Place(position, dx, dy, HudWidth, HudHeight);
        }

        // The second HUD: radiation detector, compass and grid/coordinates in one row.
        private const float CoordWidth = 104f;
        private float RadX => 0f;
        private float CompassX => _config.RadiationDetector ? _config.IconSize + Gap : 0f;
        private float CoordX => CompassX + (_config.Compass ? _config.IconSize + Gap : 0f);
        private float InfoWidth => Math.Max(_config.IconSize, CoordX + (_config.Coordinates ? CoordWidth : 0f) - (_config.Coordinates ? 0f : Gap));
        private float InfoHeight => _config.IconSize + LabelHeight;

        private Layout InfoLayoutFor(BasePlayer player)
        {
            var pr = player != null ? Prefs(player.userID, false) : null;
            var custom = _config.AllowMove && pr != null && pr.InfoPosition != null;
            var position = (custom ? pr.InfoPosition : NormalizePosition(_config.InfoPosition)) ?? "AboveHotbar";
            var dx = custom ? pr.InfoX : _config.InfoX;
            var dy = custom ? pr.InfoY : _config.InfoY;
            return Place(position, dx, dy, InfoWidth, InfoHeight);
        }

        private static Layout Place(string position, float dx, float dy, float w, float h)
        {
            var L = new Layout();
            switch (position)
            {
                // Just above the first hotbar slot (the hotbar is centred at the bottom of the screen).
                case "AboveHotbar": L.Anchor = "0.5 0"; L.Left = -196f; L.Top = 84f + h; break;
                case "TopLeft": L.Anchor = "0 1"; L.Left = Margin; L.Top = -Margin; break;
                case "MiddleLeft": L.Anchor = "0 0.5"; L.Left = Margin; L.Top = h / 2f; break;
                case "MiddleRight": L.Anchor = "1 0.5"; L.Left = -Margin - w; L.Top = h / 2f; break;
                // Bottom spots stay above the chat (left) and the vitals and upkeep panels (right).
                case "BottomLeft": L.Anchor = "0 0"; L.Left = Margin; L.Top = h + 190f; break;
                case "BottomRight": L.Anchor = "1 0"; L.Left = -Margin - w; L.Top = h + 170f; break;
                default: L.Anchor = "1 1"; L.Left = -Margin - w; L.Top = -Margin; break;
            }
            L.Left += dx;
            L.Top += dy;
            L.Key = $"{L.Anchor}|{L.Left}|{L.Top}";
            return L;
        }

        // A rect placed relative to the HUD's top-left corner: x to the right, y downwards, in pixels.
        private static CuiRectTransformComponent Rect(Layout L, float x, float y, float w, float h) => new CuiRectTransformComponent
        {
            AnchorMin = L.Anchor,
            AnchorMax = L.Anchor,
            OffsetMin = $"{L.Left + x} {L.Top - y - h}",
            OffsetMax = $"{L.Left + x + w} {L.Top - y}",
        };

        #endregion

        #region HUD drawing

        private static CuiRectTransformComponent Inset(float l, float b, float r, float t) => new CuiRectTransformComponent
        {
            AnchorMin = "0 0",
            AnchorMax = "1 1",
            OffsetMin = $"{l} {b}",
            OffsetMax = $"{-r} {-t}",
        };

        private string IconOf(Kind kind)
        {
            switch (kind)
            {
                case Kind.Airdrop: return "plane";
                case Kind.PatrolHelicopter: return "heli";
                case Kind.Bradley: return "tank";
                case Kind.CargoShip: return "ship";
                case Kind.Chinook: return "chinook";
                case Kind.SmallOilRig: return "oilrig_small";
                default: return "oilrig_large";
            }
        }

        private string LabelOf(Kind kind)
        {
            switch (kind)
            {
                case Kind.Airdrop: return Msg("LabelAirdrop");
                case Kind.PatrolHelicopter: return Msg("LabelHeli");
                case Kind.Bradley: return Msg("LabelBradley");
                case Kind.CargoShip: return Msg("LabelCargo");
                case Kind.Chinook: return Msg("LabelChinook");
                case Kind.SmallOilRig: return Msg("LabelSmallRig");
                default: return Msg("LabelLargeRig");
            }
        }

        private void AddIcon(CuiElementContainer c, string parent, string icon, string color, CuiRectTransformComponent rect, string name = null, float fadeIn = 0f, float fadeOut = 0f)
        {
            string png;
            if (!_icons.TryGetValue(icon, out png)) return;
            c.Add(new CuiElement
            {
                Name = name ?? CuiHelper.GetGuid(),
                Parent = parent,
                FadeOut = fadeOut,
                Components = { new CuiRawImageComponent { Png = png, Color = color, FadeIn = fadeIn }, rect },
            });
        }

        private void AddLabel(CuiElementContainer c, string parent, string text, int size, CuiRectTransformComponent rect, TextAnchor align = TextAnchor.MiddleCenter, string color = "1 1 1 0.9")
        {
            c.Add(new CuiElement
            {
                Parent = parent,
                Components =
                {
                    new CuiTextComponent { Text = text, FontSize = size, Align = align, Color = color, Font = "robotocondensed-bold.ttf" },
                    new CuiOutlineComponent { Color = "0 0 0 0.8", Distance = "0.6 -0.6" },
                    rect,
                },
            });
        }

        private CuiElementContainer BuildTiles(Layout L, List<Kind> kinds, List<Tile> states, bool dropWaiting)
        {
            var size = _config.IconSize;
            var c = new CuiElementContainer();
            var width = Math.Max(1, kinds.Count) * (size + Gap);
            c.Add(new CuiElement
            {
                Name = TilesRoot,
                Parent = "Hud",
                Components = { new CuiImageComponent { Color = "0 0 0 0" }, Rect(L, 0, 0, width, size + LabelHeight) },
            });

            for (var i = 0; i < kinds.Count; i++)
            {
                var kind = kinds[i];
                var x = i * (size + Gap);
                var tile = TilesRoot + "." + kind;
                c.Add(new CuiElement
                {
                    Name = tile,
                    Parent = TilesRoot,
                    Components =
                    {
                        new CuiImageComponent { Color = ColorFor(states[i]) },
                        new CuiRectTransformComponent { AnchorMin = "0 1", AnchorMax = "0 1", OffsetMin = $"{x} {-size}", OffsetMax = $"{x + size} 0" },
                    },
                });
                if (kind == Kind.Airdrop)
                {
                    // Plane on the tile, and a small crate badge in the corner: green while a drop is on the map unlooted.
                    AddIcon(c, tile, "plane", _planes.Count > 0 ? "1 1 1 1" : "1 1 1 0.55", Inset(3, size * 0.3f, size * 0.3f, 3));
                    var badge = tile + ".badge";
                    var b = size * 0.48f;
                    c.Add(new CuiElement
                    {
                        Name = badge,
                        Parent = tile,
                        Components =
                        {
                            new CuiImageComponent { Color = dropWaiting ? _config.ActiveColor : _config.InactiveColor },
                            new CuiOutlineComponent { Color = "0 0 0 0.7", Distance = "1 -1" },
                            new CuiRectTransformComponent { AnchorMin = "1 0", AnchorMax = "1 0", OffsetMin = $"{-b} 0", OffsetMax = $"0 {b}" },
                        },
                    });
                    AddIcon(c, badge, "supplydrop", "1 1 1 1", Inset(1.5f, 1.5f, 1.5f, 1.5f));
                }
                else
                {
                    AddIcon(c, tile, IconOf(kind), states[i] == Tile.Inactive ? "1 1 1 0.6" : "1 1 1 1", Inset(3, 3, 3, 3));
                }
                c.Add(new CuiElement
                {
                    Parent = TilesRoot,
                    Components =
                    {
                        new CuiTextComponent { Text = LabelOf(kind), FontSize = 8, Align = TextAnchor.MiddleCenter, Color = "1 1 1 0.85", Font = "robotocondensed-bold.ttf" },
                        new CuiOutlineComponent { Color = "0 0 0 0.8", Distance = "0.6 -0.6" },
                        new CuiRectTransformComponent { AnchorMin = "0 1", AnchorMax = "0 1", OffsetMin = $"{x - 4} {-size - LabelHeight}", OffsetMax = $"{x + size + 4} {-size}" },
                    },
                });
            }
            return c;
        }

        private CuiElementContainer BuildLinesUi(Layout L, List<HudLine> lines)
        {
            var c = new CuiElementContainer();
            var top = _config.IconSize + LabelHeight + 4f;
            var height = Math.Max(1, lines.Count) * LineHeight;
            c.Add(new CuiElement
            {
                Name = LinesRoot,
                Parent = "Hud",
                Components = { new CuiImageComponent { Color = "0 0 0 0" }, Rect(L, 0, top, LineWidth, height) },
            });
            for (var i = 0; i < lines.Count; i++)
            {
                var l = lines[i];
                var y = i * LineHeight;
                var row = LinesRoot + "." + i;
                c.Add(new CuiElement
                {
                    Name = row,
                    Parent = LinesRoot,
                    Components =
                    {
                        new CuiImageComponent { Color = "0 0 0 0.45" },
                        new CuiRectTransformComponent { AnchorMin = "0 1", AnchorMax = "0 1", OffsetMin = $"0 {-y - LineHeight + 1}", OffsetMax = $"{LineWidth} {-y}" },
                    },
                });
                c.Add(new CuiElement
                {
                    Parent = row,
                    Components =
                    {
                        new CuiImageComponent { Color = HexToRgba(l.Color, 1f) },
                        new CuiRectTransformComponent { AnchorMin = "0 0", AnchorMax = "0 1", OffsetMin = "0 0", OffsetMax = "3 0" },
                    },
                });
                AddLabel(c, row, $"{l.Title}  <color=#ffd479>{l.Grid}</color>  <color={l.Color}>{l.Status}</color>", 11, Inset(8, 0, 4, 0), TextAnchor.MiddleLeft);
            }
            return c;
        }

        private static string HexToRgba(string hex, float alpha)
        {
            Color col;
            if (!ColorUtility.TryParseHtmlString(hex, out col)) col = Color.white;
            return $"{col.r:0.###} {col.g:0.###} {col.b:0.###} {alpha:0.###}";
        }

        #endregion

        #region Radiation detector

        private class RadiationState
        {
            public float Level;       // 0 = nothing nearby, up to 1 = deep in strong radiation
            public int Distance = -1; // meters to the nearest radiation zone, 0 inside, -1 none in range
            public bool Protected;    // inside, but clothing stops all of it
            public float Exposure;    // rads getting through clothing right now (the game's own number)
            public float Poison;      // radiation poisoning: while above 0 it hurts
            public string TintSig;
            public float NextPulse;
            public float GlowOff;
            public bool GlowOn;
            public string Sig;
            public float NextSample;
        }

        private readonly Dictionary<ulong, RadiationState> _rad = new Dictionary<ulong, RadiationState>();
        private readonly List<KeyValuePair<TriggerRadiation, Collider>> _radZones = new List<KeyValuePair<TriggerRadiation, Collider>>();

        private void RefreshRadiationTriggers()
        {
            if (!_config.RadiationDetector) return;
            _radZones.Clear();
            foreach (var t in UnityEngine.Object.FindObjectsByType<TriggerRadiation>(FindObjectsSortMode.None))
            {
                if (t == null) continue;
                var col = t.GetComponent<Collider>();
                if (col != null) _radZones.Add(new KeyValuePair<TriggerRadiation, Collider>(t, col));
            }
        }

        private void Sample(BasePlayer player, RadiationState rs)
        {
            var pos = player.transform.position;
            rs.Exposure = player.metabolism.radiation_level.value;
            rs.Poison = player.metabolism.radiation_poison.value;

            // Inside = within a zone's real shape (closest point is the player's own position), not its bounding box.
            var rads = 0f;
            var range = Mathf.Max(1f, _config.RadiationRange);
            var nearest = float.MaxValue;
            foreach (var zone in _radZones)
            {
                var col = zone.Value;
                if (zone.Key == null || col == null || !col.enabled || !col.gameObject.activeInHierarchy) continue;
                if (col.bounds.SqrDistance(pos) > range * range) continue;
                float d;
                var mesh = col as MeshCollider;
                if (col is SphereCollider || col is BoxCollider || col is CapsuleCollider || (mesh != null && mesh.convex))
                    d = Vector3.Distance(col.ClosestPoint(pos), pos);
                else
                    d = Mathf.Sqrt(col.bounds.SqrDistance(pos)); // ClosestPoint only works on simple shapes
                if (d < nearest) nearest = d;
                if (d > 0.05f) continue;
                // Raw dose ignoring clothing: the detector measures the zone, the tint shows what gets through.
                try { rads = Mathf.Max(rads, zone.Key.GetRadiationForPosition(pos, 0f, player)); }
                catch { }
            }
            if (rads > 0.01f || rs.Exposure > 0.01f)
            {
                rs.Distance = 0;
                rs.Protected = rs.Exposure <= 0.01f;
                rs.Level = 0.6f + 0.4f * Mathf.Clamp01(Mathf.Max(rads, rs.Exposure) / 50f);
                return;
            }

            rs.Protected = false;
            if (nearest <= range)
            {
                // Right at the edge but not counted inside by the game: show 1 m rather than IN.
                rs.Distance = Mathf.Max(1, Mathf.RoundToInt(nearest));
                rs.Level = 0.5f * (1f - nearest / range) + 0.05f;
            }
            else
            {
                rs.Distance = -1;
                rs.Level = 0f;
            }
        }

        // ---- screen tint: green while radiation gets through, red (pulsing) once poisoning hurts

        private const string TintRoot = "EventHud.RadTint";

        private bool WantsTint(BasePlayer player) =>
            _config.RadiationTint && player != null && player.IsConnected && !Prefs(player.userID, false).NoTint;

        private void HideTint(BasePlayer player)
        {
            RadiationState rs;
            if (player != null && _rad.TryGetValue(player.userID, out rs)) rs.TintSig = null;
            if (player != null && player.IsConnected) CuiHelper.DestroyUi(player, TintRoot);
        }

        private void UpdateTint(BasePlayer player, RadiationState rs, float now)
        {
            string color = null;
            var strength = Mathf.Clamp01(_config.TintStrength);
            if (!player.IsDead() && rs.Poison >= Mathf.Max(0.1f, _config.RedPoison))
            {
                // Throbs twice a second; stronger the more poisoning there is.
                var a = strength * (0.45f + 0.55f * Mathf.Clamp01(rs.Poison / 50f));
                if (((int)(now * 2f) & 1) == 1) a *= 0.6f;
                color = "0.85 0.08 0.05 " + (Mathf.Round(a * 20f) / 20f).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
            }
            else if (!player.IsDead() && rs.Exposure > 0.01f)
            {
                var a = strength * (0.3f + 0.5f * Mathf.Clamp01(rs.Exposure / 30f));
                color = "0.35 0.9 0.15 " + (Mathf.Round(a * 20f) / 20f).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
            }
            if (color == rs.TintSig) return;
            rs.TintSig = color;
            CuiHelper.DestroyUi(player, TintRoot);
            if (color == null) return;
            var c = new CuiElementContainer();
            c.Add(new CuiElement
            {
                Name = TintRoot,
                Parent = "Under",
                Components =
                {
                    new CuiRawImageComponent { Png = _icons.ContainsKey("vignette") ? _icons["vignette"] : null, Color = color, FadeIn = 0.25f },
                    new CuiRectTransformComponent { AnchorMin = "0 0", AnchorMax = "1 1" },
                },
            });
            CuiHelper.AddUi(player, c);
        }

        private void RadiationTick()
        {
            var now = Time.realtimeSinceStartup;
            foreach (var player in BasePlayer.activePlayerList)
            {
                var hud = Wants(player);
                var tint = WantsTint(player);
                if ((!hud && !tint) || player.IsSleeping())
                {
                    if (tint || player.IsSleeping()) HideTint(player);
                    continue;
                }
                RadiationState rs;
                if (!_rad.TryGetValue(player.userID, out rs)) _rad[player.userID] = rs = new RadiationState();

                if (now >= rs.NextSample)
                {
                    rs.NextSample = now + 0.5f;
                    Sample(player, rs);
                    if (tint) UpdateTint(player, rs, now);
                    else if (rs.TintSig != null) HideTint(player);
                    var tier = rs.Distance == 0 ? (rs.Protected ? "safe" : "in") : rs.Distance > 0 ? (rs.Distance / 5 * 5).ToString() : "none";
                    if (hud && tier != rs.Sig)
                    {
                        rs.Sig = tier;
                        DrawRadiationBase(player, rs);
                        rs.GlowOn = false;
                    }
                }
                if (!hud) continue;

                if (rs.GlowOn && now >= rs.GlowOff)
                {
                    rs.GlowOn = false;
                    CuiHelper.DestroyUi(player, RadGlow);
                }
                if (rs.Level > 0f && !rs.GlowOn && now >= rs.NextPulse)
                {
                    // Pulse period: 1.6 s at the edge of the range, down to 0.3 s deep inside.
                    var period = Mathf.Lerp(1.6f, 0.3f, rs.Level);
                    rs.NextPulse = now + period;
                    rs.GlowOff = now + period * 0.45f;
                    rs.GlowOn = true;
                    var fade = period * 0.35f;
                    var color = rs.Distance == 0 && !rs.Protected ? "1 0.25 0.2 1" : rs.Protected ? "1 0.6 0.1 1" : "1 0.85 0.2 1";
                    var c = new CuiElementContainer();
                    c.Add(new CuiElement
                    {
                        Name = RadGlow,
                        Parent = RadRoot,
                        FadeOut = fade,
                        Components = { new CuiImageComponent { Color = rs.Distance == 0 && !rs.Protected ? "1 0.2 0.15 0.55" : rs.Protected ? "1 0.55 0.1 0.45" : "1 0.8 0.1 0.4", FadeIn = fade }, Inset(0, 0, 0, 0) },
                    });
                    AddIcon(c, RadGlow, "radiation", color, Inset(2, 2, 2, 2), null, fade, fade);
                    CuiHelper.DestroyUi(player, RadGlow);
                    CuiHelper.AddUi(player, c);
                }
            }
        }

        private void DrawRadiationBase(BasePlayer player, RadiationState rs)
        {
            var size = _config.IconSize;
            var c = new CuiElementContainer();
            c.Add(new CuiElement
            {
                Name = RadRoot,
                Parent = "Hud",
                Components = { new CuiImageComponent { Color = "0.1 0.1 0.1 0.7" }, Rect(InfoLayoutFor(player), RadX, 0, size, size) },
            });
            var iconColor = rs.Distance == 0 ? (rs.Protected ? "1 0.6 0.15 1" : "1 0.3 0.25 1") : rs.Distance > 0 ? "1 0.85 0.25 0.95" : "1 1 1 0.35";
            AddIcon(c, RadRoot, "radiation", iconColor, Inset(3, 3, 3, 3));
            var label = rs.Distance == 0 ? (rs.Protected ? Msg("LabelSafe") : "IN") : rs.Distance > 0 ? rs.Distance + "m" : Msg("LabelRad");
            c.Add(new CuiElement
            {
                Parent = RadRoot,
                Components =
                {
                    new CuiTextComponent { Text = label, FontSize = 8, Align = TextAnchor.MiddleCenter, Color = "1 1 1 0.85", Font = "robotocondensed-bold.ttf" },
                    new CuiOutlineComponent { Color = "0 0 0 0.8", Distance = "0.6 -0.6" },
                    new CuiRectTransformComponent { AnchorMin = "0 0", AnchorMax = "1 0", OffsetMin = $"-4 {-LabelHeight}", OffsetMax = "4 0" },
                },
            });
            CuiHelper.DestroyUi(player, RadGlow);
            CuiHelper.DestroyUi(player, RadRoot);
            CuiHelper.AddUi(player, c);
        }

        #endregion

        #region Commands

        [ChatCommand("eventhud")]
        private void CmdEventHud(BasePlayer player, string command, string[] args)
        {
            if (player == null) return;
            var action = args.Length > 0 ? args[0].ToLowerInvariant() : "";
            var pr = Prefs(player.userID);

            if (action == "help") { player.ChatMessage(Msg("HudHelp")); return; }
            if (action == "tint")
            {
                pr.NoTint = !pr.NoTint;
                SavePrefs();
                if (pr.NoTint) HideTint(player);
                player.ChatMessage(Msg(pr.NoTint ? "TintOff" : "TintOn"));
                return;
            }
            if (action == "show" || action == "on") { SetHidden(player, false); return; }
            if (action == "hide" || action == "off") { SetHidden(player, true); return; }
            if (action == "" && !_config.AllowMove) { SetHidden(player, !pr.Hidden); return; }
            if (!_config.AllowMove) { player.ChatMessage(Msg("HudHelp")); return; }

            if (action == "reset")
            {
                pr.Position = null;
                pr.X = pr.Y = 0f;
                SavePrefs();
                DrawHud(player);
                player.ChatMessage(Msg("HudReset"));
                return;
            }
            var spot = NormalizePosition(action);
            if (spot != null)
            {
                pr.Position = spot;
                pr.X = pr.Y = 0f;
                SavePrefs();
                DrawHud(player);
                return;
            }
            if (action == "" || action == "move") { OpenEditor(player); return; }
            player.ChatMessage(Msg("HudHelp"));
        }

        private void SetHidden(BasePlayer player, bool hidden)
        {
            Prefs(player.userID).Hidden = hidden;
            SavePrefs();
            if (hidden)
            {
                DestroyHud(player);
                _rad.Remove(player.userID);
                player.ChatMessage(Msg("HudOff"));
            }
            else
            {
                DrawHud(player);
                player.ChatMessage(Msg("HudOn"));
            }
        }

        // Buttons on the move panel.
        [ConsoleCommand("eventhud.ui")]
        private void CmdUi(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null || !_config.AllowMove) return;
            var pr = Prefs(player.userID);
            var step = EditStep(player);
            var info = _editInfo.Contains(player.userID);
            switch (arg.GetString(0))
            {
                case "target":
                    if (arg.GetString(1) == "info") _editInfo.Add(player.userID);
                    else _editInfo.Remove(player.userID);
                    break;
                case "pos":
                    var spot = NormalizePosition(arg.GetString(1));
                    if (spot == null) return;
                    if (info) { pr.InfoPosition = spot; pr.InfoX = pr.InfoY = 0f; }
                    else { pr.Position = spot; pr.X = pr.Y = 0f; }
                    break;
                case "nudge":
                    if (info)
                    {
                        if (pr.InfoPosition == null) { pr.InfoPosition = NormalizePosition(_config.InfoPosition) ?? "AboveHotbar"; pr.InfoX = _config.InfoX; pr.InfoY = _config.InfoY; }
                        pr.InfoX += arg.GetFloat(1) * step;
                        pr.InfoY += arg.GetFloat(2) * step;
                        break;
                    }
                    if (pr.Position == null) { pr.Position = NormalizePosition(_config.DefaultPosition) ?? "TopRight"; pr.X = _config.DefaultX; pr.Y = _config.DefaultY; }
                    pr.X += arg.GetFloat(1) * step;
                    pr.Y += arg.GetFloat(2) * step;
                    break;
                case "step":
                    _editStep[player.userID] = step >= 50 ? 2 : step >= 10 ? 50 : 10;
                    break;
                case "reset":
                    if (info) { pr.InfoPosition = null; pr.InfoX = pr.InfoY = 0f; }
                    else { pr.Position = null; pr.X = pr.Y = 0f; }
                    break;
                case "toggle":
                    pr.Hidden = !pr.Hidden;
                    if (pr.Hidden) { DestroyHud(player); _rad.Remove(player.userID); }
                    break;
                case "close":
                    CloseEditor(player);
                    _editInfo.Remove(player.userID);
                    return;
                default:
                    return;
            }
            SavePrefs();
            DrawHud(player);
            OpenEditor(player);
        }

        #endregion

        #region Move panel

        private const string EditorRoot = "EventHud.Editor";
        private const string FrameRoot = "EventHud.Frame";
        private readonly Dictionary<ulong, int> _editStep = new Dictionary<ulong, int>();
        private readonly HashSet<ulong> _editInfo = new HashSet<ulong>(); // players moving the radiation/compass/grid HUD

        private int EditStep(BasePlayer player)
        {
            int s;
            return _editStep.TryGetValue(player.userID, out s) ? s : 10;
        }

        private void CloseEditor(BasePlayer player)
        {
            CuiHelper.DestroyUi(player, EditorRoot);
            CuiHelper.DestroyUi(player, FrameRoot);
        }

        private void AddButton(CuiElementContainer c, string parent, string text, string command, string color, float x, float y, float w, float h)
        {
            c.Add(new CuiButton
            {
                Button = { Command = command, Color = color },
                RectTransform = { AnchorMin = "0 1", AnchorMax = "0 1", OffsetMin = $"{x} {-y - h}", OffsetMax = $"{x + w} {-y}" },
                Text = { Text = text, FontSize = 11, Align = TextAnchor.MiddleCenter, Color = "1 1 1 0.95", Font = "robotocondensed-bold.ttf" },
            }, parent);
        }

        private void OpenEditor(BasePlayer player)
        {
            CloseEditor(player);
            var pr = Prefs(player.userID, false);
            var info = _editInfo.Contains(player.userID);
            var current = info
                ? pr.InfoPosition ?? NormalizePosition(_config.InfoPosition) ?? "AboveHotbar"
                : pr.Position ?? NormalizePosition(_config.DefaultPosition) ?? "TopRight";
            const string idle = "0.25 0.25 0.25 0.95", picked = "0.18 0.55 0.25 0.95", accent = "0.75 0.45 0.12 0.95";
            const float W = 360f, H = 272f, top = 80f;

            var c = new CuiElementContainer();
            // Yellow box over the whole HUD area (icons plus the list) so players see where it lands.
            c.Add(new CuiElement
            {
                Name = FrameRoot,
                Parent = "Hud",
                Components = { new CuiImageComponent { Color = "1 0.85 0.2 0.18" }, new CuiOutlineComponent { Color = "1 0.85 0.2 0.9", Distance = "1 -1" }, info ? Rect(InfoLayoutFor(player), 0, 0, InfoWidth, InfoHeight) : Rect(LayoutFor(player), 0, 0, HudWidth, HudHeight) },
            });
            c.Add(new CuiElement
            {
                Name = EditorRoot,
                Parent = "Overlay",
                Components =
                {
                    new CuiImageComponent { Color = "0.08 0.08 0.08 0.92" },
                    new CuiNeedsCursorComponent(),
                    new CuiRectTransformComponent { AnchorMin = "0.5 0.5", AnchorMax = "0.5 0.5", OffsetMin = $"{-W / 2} {-H / 2}", OffsetMax = $"{W / 2} {H / 2}" },
                },
            });
            c.Add(new CuiElement
            {
                Parent = EditorRoot,
                Components =
                {
                    new CuiTextComponent { Text = Msg("EdTitle"), FontSize = 15, Align = TextAnchor.MiddleLeft, Color = "1 1 1 1", Font = "robotocondensed-bold.ttf" },
                    new CuiRectTransformComponent { AnchorMin = "0 1", AnchorMax = "1 1", OffsetMin = "12 -30", OffsetMax = "-12 -8" },
                },
            });
            c.Add(new CuiElement
            {
                Parent = EditorRoot,
                Components =
                {
                    new CuiTextComponent { Text = Msg("EdHint"), FontSize = 10, Align = TextAnchor.MiddleLeft, Color = "1 1 1 0.6" },
                    new CuiRectTransformComponent { AnchorMin = "0 1", AnchorMax = "1 1", OffsetMin = "12 -46", OffsetMax = "-12 -30" },
                },
            });

            // Which HUD the buttons move.
            const float bw = 108f, bh = 24f;
            AddButton(c, EditorRoot, Msg("EdEvents"), "eventhud.ui target events", info ? idle : accent, 12f, 50f, bw, bh);
            AddButton(c, EditorRoot, Msg("EdInfo"), "eventhud.ui target info", info ? accent : idle, 12f + bw + 6f, 50f, W - 24f - bw - 6f, bh);

            // Corner buttons, laid out like the screen: top row, middle row, bottom row.
            var grid = new[] { new[] { "TopLeft", "TopRight" }, new[] { "MiddleLeft", "MiddleRight" }, new[] { "BottomLeft", "BottomRight" } };
            for (var row = 0; row < grid.Length; row++)
                for (var col = 0; col < 2; col++)
                {
                    var spot = grid[row][col];
                    AddButton(c, EditorRoot, Msg("Ed" + spot), "eventhud.ui pos " + spot, spot == current ? picked : idle, 12f + col * (bw + 6f), top + 4f + row * (bh + 6f), bw, bh);
                }
            if (info)
                AddButton(c, EditorRoot, Msg("EdAboveHotbar"), "eventhud.ui pos AboveHotbar", current == "AboveHotbar" ? picked : idle, 12f, top + 4f + 3f * (bh + 6f), bw * 2f + 6f, bh);

            // Nudge pad on the right.
            const float px = 250f, py = top + 4f, pw = 46f;
            AddButton(c, EditorRoot, Msg("EdUp"), "eventhud.ui nudge 0 1", idle, px + pw / 2f + 2f, py, pw, bh);
            AddButton(c, EditorRoot, Msg("EdLeft"), "eventhud.ui nudge -1 0", idle, px - 2f, py + bh + 6f, pw, bh);
            AddButton(c, EditorRoot, Msg("EdRight"), "eventhud.ui nudge 1 0", idle, px + pw + 6f, py + bh + 6f, pw, bh);
            AddButton(c, EditorRoot, Msg("EdDown"), "eventhud.ui nudge 0 -1", idle, px + pw / 2f + 2f, py + 2f * (bh + 6f), pw, bh);
            AddButton(c, EditorRoot, Msg("EdStep", EditStep(player) + "px"), "eventhud.ui step", idle, px - 2f, py + 3f * (bh + 6f) + 4f, pw * 2f + 8f, bh);

            // Bottom row.
            var by = H - bh - 12f;
            AddButton(c, EditorRoot, Msg("EdReset"), "eventhud.ui reset", idle, 12f, by, 80f, bh);
            AddButton(c, EditorRoot, Msg(pr.Hidden ? "EdShow" : "EdHide"), "eventhud.ui toggle", idle, 98f, by, 90f, bh);
            AddButton(c, EditorRoot, Msg("EdDone"), "eventhud.ui close", accent, W - 12f - 90f, by, 90f, bh);

            CuiHelper.AddUi(player, c);
        }

        #region Compass and coordinates

        private const string InfoRoot = "EventHud.Info";
        private static readonly string[] Directions = { "N", "NE", "E", "SE", "S", "SW", "W", "NW" };

        private class InfoState
        {
            public string LayoutKey;
            public string Dir, Grid, Pos;
        }

        private readonly Dictionary<ulong, InfoState> _info = new Dictionary<ulong, InfoState>();

        private void InfoTick()
        {
            foreach (var player in BasePlayer.activePlayerList)
            {
                if (!Wants(player) || player.IsSleeping() || player.IsDead()) continue;
                InfoState st;
                if (!_info.TryGetValue(player.userID, out st)) _info[player.userID] = st = new InfoState();

                var L = InfoLayoutFor(player);
                if (st.LayoutKey != L.Key)
                {
                    DrawInfoBase(player, L);
                    st.LayoutKey = L.Key;
                    st.Dir = st.Grid = st.Pos = null;
                }

                var pos = player.transform.position;
                if (_config.Compass)
                {
                    // Yaw 0 is north (+z) and 90 is east, the same as the in-game compass.
                    var yaw = player.eyes != null ? player.eyes.rotation.eulerAngles.y : player.transform.eulerAngles.y;
                    yaw = (yaw % 360f + 360f) % 360f;
                    var dir = Directions[Mathf.RoundToInt(yaw / 45f) % 8] + "|" + Mathf.RoundToInt(yaw) % 360;
                    if (dir != st.Dir)
                    {
                        st.Dir = dir;
                        var parts = dir.Split('|');
                        SetInfoText(player, InfoRoot + ".Dir", InfoRoot + ".Compass", parts[0], 15, Inset(0, 0, 0, 0));
                        SetInfoText(player, InfoRoot + ".Deg", InfoRoot, parts[1] + "°", 8,
                            new CuiRectTransformComponent { AnchorMin = "0 1", AnchorMax = "0 1", OffsetMin = $"{CompassX - 4} {-_config.IconSize - LabelHeight}", OffsetMax = $"{CompassX + _config.IconSize + 4} {-_config.IconSize}" });
                    }
                }
                if (_config.Coordinates)
                {
                    var grid = Grid(pos);
                    var xyz = $"{pos.x:0}, {pos.y:0}, {pos.z:0}";
                    if (grid != st.Grid)
                    {
                        st.Grid = grid;
                        SetInfoText(player, InfoRoot + ".Grid", InfoRoot + ".Coords", grid, 14, new CuiRectTransformComponent { AnchorMin = "0 0.45", AnchorMax = "1 1", OffsetMin = "2 0", OffsetMax = "-2 -1" }, "1 0.83 0.47 1");
                    }
                    if (xyz != st.Pos)
                    {
                        st.Pos = xyz;
                        SetInfoText(player, InfoRoot + ".Xyz", InfoRoot + ".Coords", xyz, 9, new CuiRectTransformComponent { AnchorMin = "0 0", AnchorMax = "1 0.48", OffsetMin = "2 2", OffsetMax = "-2 0" });
                    }
                }
            }
        }

        // Tile backgrounds and fixed labels; the text inside is swapped as it changes.
        private void DrawInfoBase(BasePlayer player, Layout L)
        {
            CuiHelper.DestroyUi(player, InfoRoot);
            var size = _config.IconSize;
            var c = new CuiElementContainer();
            c.Add(new CuiElement
            {
                Name = InfoRoot,
                Parent = "Hud",
                Components = { new CuiImageComponent { Color = "0 0 0 0" }, Rect(L, 0, 0, InfoWidth, InfoHeight) },
            });
            if (_config.Compass)
                c.Add(new CuiElement
                {
                    Name = InfoRoot + ".Compass",
                    Parent = InfoRoot,
                    Components =
                    {
                        new CuiImageComponent { Color = "0.1 0.1 0.1 0.7" },
                        new CuiRectTransformComponent { AnchorMin = "0 1", AnchorMax = "0 1", OffsetMin = $"{CompassX} {-size}", OffsetMax = $"{CompassX + size} 0" },
                    },
                });
            if (_config.Coordinates)
            {
                c.Add(new CuiElement
                {
                    Name = InfoRoot + ".Coords",
                    Parent = InfoRoot,
                    Components =
                    {
                        new CuiImageComponent { Color = "0.1 0.1 0.1 0.7" },
                        new CuiRectTransformComponent { AnchorMin = "0 1", AnchorMax = "0 1", OffsetMin = $"{CoordX} {-size}", OffsetMax = $"{CoordX + CoordWidth} 0" },
                    },
                });
                AddLabel(c, InfoRoot, Msg("LabelGrid"), 8,
                    new CuiRectTransformComponent { AnchorMin = "0 1", AnchorMax = "0 1", OffsetMin = $"{CoordX} {-size - LabelHeight}", OffsetMax = $"{CoordX + CoordWidth} {-size}" }, TextAnchor.MiddleCenter, "1 1 1 0.85");
            }
            CuiHelper.AddUi(player, c);
        }

        private void SetInfoText(BasePlayer player, string name, string parent, string text, int size, CuiRectTransformComponent rect, string color = "1 1 1 0.95")
        {
            CuiHelper.DestroyUi(player, name);
            var c = new CuiElementContainer();
            c.Add(new CuiElement
            {
                Name = name,
                Parent = parent,
                Components =
                {
                    new CuiTextComponent { Text = text, FontSize = size, Align = TextAnchor.MiddleCenter, Color = color, Font = "robotocondensed-bold.ttf" },
                    new CuiOutlineComponent { Color = "0 0 0 0.8", Distance = "0.6 -0.6" },
                    rect,
                },
            });
            CuiHelper.AddUi(player, c);
        }

        #endregion

        // For the manager or RCON: the current event state as JSON.
        [ConsoleCommand("eventhud.status")]
        private void CmdStatus(ConsoleSystem.Arg arg)
        {
            if (arg.Connection != null && !arg.IsAdmin) return;
            var now = Time.realtimeSinceStartup;
            var events = AllKinds.ToDictionary(k => k.ToString(), k => TileState(k).ToString().ToLowerInvariant());
            var lines = BuildLines(now).Select(l => new Dictionary<string, string> { ["title"] = l.Title, ["grid"] = l.Grid, ["status"] = l.Status }).ToList();
            arg.ReplyWith(JsonConvert.SerializeObject(new Dictionary<string, object> { ["events"] = events, ["lines"] = lines }));
        }

        #endregion
    }
}
