// Rust Sandbox — Skills
// Players earn XP in ten skills (Mining, Woodcutting, Combat, Gardening, Electrician, Survival,
// Mechanic, Fishing, Scrapper, Cooking), level up, and spend points on perks: more ore, wood, crops and fish,
// faster crafting, growing and recycling, cheaper research, less damage taken, stronger solar panels...
//
// Solo friendly: players without a team earn bonus XP and extra points, so one person can
// keep up with a group. Teams get a small XP share when they play close together.
//
// Install: copy this file to oxide/plugins (or carbon/plugins). Settings are in
// oxide/config/RustSandboxSkills.json. Players type /skills in chat to open the menu.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Game.Rust.Cui;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("Rust Sandbox Skills", "RustSandbox", "1.6.0")]
    [Description("Ten skills with levels and perks (Mining, Woodcutting, Combat, Gardening, Electrician, Survival, Mechanic, Fishing, Scrapper, Cooking) and a solo-friendly XP bonus.")]
    public class RustSandboxSkills : RustPlugin
    {
        #region Definitions

        private const string Mining = "mining";
        private const string Woodcutting = "woodcutting";
        private const string Combat = "combat";
        private const string Gardening = "gardening";
        private const string Electrician = "electrician";
        private const string Survival = "survival";
        private const string Mechanic = "mechanic";
        private const string Fishing = "fishing";
        private const string Scrapper = "scrapper";
        private const string Cooking = "cooking";
        private static readonly string[] Skills = { Mining, Woodcutting, Combat, Gardening, Electrician, Survival, Mechanic, Fishing, Scrapper, Cooking };

        private class PerkDef
        {
            public string Id;
            public string Skill;
            public int DefaultMaxRank;
            public double DefaultPerRank;
            /// <summary>The bonus is a plain number (e.g. health per minute) rather than a percentage.</summary>
            public bool Raw;
            /// <summary>The plugin can measure what this perk did, so it gets a "So far" total.</summary>
            public bool Tracked = true;
        }

        // The numbers here are only defaults; the config file has the live values.
        private static readonly PerkDef[] Perks =
        {
            new PerkDef { Id = "prospector", Skill = Mining, DefaultMaxRank = 5, DefaultPerRank = 0.10 },
            new PerkDef { Id = "luckystrike", Skill = Mining, DefaultMaxRank = 3, DefaultPerRank = 0.04 },
            new PerkDef { Id = "toolcare", Skill = Mining, DefaultMaxRank = 3, DefaultPerRank = 0.10 },
            new PerkDef { Id = "lumberjack", Skill = Woodcutting, DefaultMaxRank = 5, DefaultPerRank = 0.10 },
            new PerkDef { Id = "craftsman", Skill = Woodcutting, DefaultMaxRank = 5, DefaultPerRank = 0.08 },
            new PerkDef { Id = "thickskin", Skill = Combat, DefaultMaxRank = 5, DefaultPerRank = 0.04 },
            new PerkDef { Id = "hunter", Skill = Combat, DefaultMaxRank = 3, DefaultPerRank = 0.08 },
            new PerkDef { Id = "fieldmedic", Skill = Combat, DefaultMaxRank = 5, DefaultPerRank = 0.10 },
            new PerkDef { Id = "greenthumb", Skill = Gardening, DefaultMaxRank = 5, DefaultPerRank = 0.10 },
            new PerkDef { Id = "bountiful", Skill = Gardening, DefaultMaxRank = 5, DefaultPerRank = 0.10 },
            new PerkDef { Id = "forager", Skill = Gardening, DefaultMaxRank = 3, DefaultPerRank = 0.15 },
            new PerkDef { Id = "solarboost", Skill = Electrician, DefaultMaxRank = 5, DefaultPerRank = 0.10, Tracked = false },
            new PerkDef { Id = "bigbatteries", Skill = Electrician, DefaultMaxRank = 5, DefaultPerRank = 0.10, Tracked = false },
            new PerkDef { Id = "generator", Skill = Electrician, DefaultMaxRank = 3, DefaultPerRank = 0.15, Tracked = false },
            new PerkDef { Id = "ironstomach", Skill = Survival, DefaultMaxRank = 5, DefaultPerRank = 0.08 },
            new PerkDef { Id = "deepbreath", Skill = Survival, DefaultMaxRank = 3, DefaultPerRank = 0.15, Tracked = false },
            new PerkDef { Id = "regeneration", Skill = Survival, DefaultMaxRank = 5, DefaultPerRank = 4, Raw = true },
            new PerkDef { Id = "fuelsaver", Skill = Mechanic, DefaultMaxRank = 5, DefaultPerRank = 0.08 },
            new PerkDef { Id = "toughframe", Skill = Mechanic, DefaultMaxRank = 5, DefaultPerRank = 0.06 },
            new PerkDef { Id = "quickfix", Skill = Mechanic, DefaultMaxRank = 3, DefaultPerRank = 0.15 },
            new PerkDef { Id = "bigcatch", Skill = Fishing, DefaultMaxRank = 3, DefaultPerRank = 0.10 },
            new PerkDef { Id = "steadyline", Skill = Fishing, DefaultMaxRank = 5, DefaultPerRank = 0.08, Tracked = false },
            new PerkDef { Id = "rodcare", Skill = Fishing, DefaultMaxRank = 3, DefaultPerRank = 0.15, Tracked = false },
            new PerkDef { Id = "quickrecycler", Skill = Scrapper, DefaultMaxRank = 5, DefaultPerRank = 0.08, Tracked = false },
            new PerkDef { Id = "bargain", Skill = Scrapper, DefaultMaxRank = 5, DefaultPerRank = 0.08 },
            new PerkDef { Id = "techdiscount", Skill = Scrapper, DefaultMaxRank = 3, DefaultPerRank = 0.10 },
            new PerkDef { Id = "bigportions", Skill = Cooking, DefaultMaxRank = 5, DefaultPerRank = 0.06 },
            new PerkDef { Id = "masterbrewer", Skill = Cooking, DefaultMaxRank = 3, DefaultPerRank = 0.10 },
            new PerkDef { Id = "steeped", Skill = Cooking, DefaultMaxRank = 5, DefaultPerRank = 0.10 },
        };

        private static PerkDef FindPerk(string id)
        {
            foreach (var p in Perks) if (p.Id == id) return p;
            return null;
        }

        #endregion

        #region Config

        private PluginConfig _config;

        private class PerkSettings
        {
            [JsonProperty("Enabled")] public bool Enabled = true;
            [JsonProperty("Max rank")] public int MaxRank;
            [JsonProperty("Bonus per rank (0.10 = 10%)")] public double PerRank;
        }

        private class PluginConfig
        {
            [JsonProperty("Max level")] public int MaxLevel = 30;
            [JsonProperty("XP for level 2")] public double XpBase = 100;
            [JsonProperty("XP curve (higher = later levels take longer)")] public double XpCurve = 1.2;
            [JsonProperty("Points per level")] public int PointsPerLevel = 1;
            [JsonProperty("Perk ranks cost their rank in points (rank 3 costs 3)")] public bool RankCostsRank = true;

            [JsonProperty("Solo XP bonus (0.25 = +25%)")] public double SoloXpBonus = 0.25;
            [JsonProperty("Solo extra point every N levels (0 = off)")] public int SoloExtraPointEvery = 5;
            [JsonProperty("Team XP share to nearby teammates (0.10 = 10%)")] public double TeamXpShare = 0.10;
            [JsonProperty("Team share range (meters)")] public float TeamShareRange = 50f;
            [JsonProperty("Biggest team that gets the share")] public int TeamShareMaxSize = 4;

            [JsonProperty("XP per hit on ore and stone")] public double XpOreHit = 5;
            [JsonProperty("XP for finishing a node")] public double XpOreFinish = 25;
            [JsonProperty("XP per hit on trees")] public double XpTreeHit = 4;
            [JsonProperty("XP for felling a tree")] public double XpTreeFinish = 20;
            [JsonProperty("XP per player kill")] public double XpPlayerKill = 40;
            [JsonProperty("No XP for killing the same player again within (minutes)")] public double SameVictimCooldown = 15;
            [JsonProperty("XP for an NPC not in the list")] public double XpNpcDefault = 20;
            [JsonProperty("XP for an animal not in the list")] public double XpAnimalDefault = 10;
            [JsonProperty("XP for planting a seed")] public double XpPlant = 2;
            [JsonProperty("XP for harvesting a crop")] public double XpHarvest = 8;
            [JsonProperty("XP for picking wild hemp, berries, mushrooms and the like")] public double XpForage = 3;
            [JsonProperty("XP for placing an electrical item")] public double XpPlaceElectric = 10;
            [JsonProperty("XP for connecting a wire")] public double XpWire = 4;
            [JsonProperty("Most XP per hour from planting, placing and wiring (0 = no limit)")] public double BuildXpCapPerHour = 400;
            [JsonProperty("XP for healing yourself with a bandage, syringe or medkit")] public double XpMedSelf = 5;
            [JsonProperty("XP for healing someone else")] public double XpMedOther = 15;
            [JsonProperty("XP per minute alive while moving around")] public double XpSurvivalMinute = 2;
            [JsonProperty("XP per 10 seconds underwater")] public double XpUnderwater = 2;
            [JsonProperty("XP per vehicle repair hit")] public double XpRepair = 6;
            [JsonProperty("XP per 100 meters driven")] public double XpDrive = 3;
            [JsonProperty("XP per fish caught")] public double XpFish = 15;
            [JsonProperty("XP per junk item fished up")] public double XpFishJunk = 3;
            [JsonProperty("XP per recycler cycle you started")] public double XpRecycle = 2;
            [JsonProperty("XP per blueprint researched")] public double XpResearch = 20;
            [JsonProperty("XP per tech tree unlock")] public double XpTechTree = 15;
            [JsonProperty("XP per piece of meat or fish cooked")] public double XpCookMeat = 2;
            [JsonProperty("XP per tea or other item made at a mixing table")] public double XpBrew = 8;
            [JsonProperty("Combat XP by prefab")] public Dictionary<string, double> CombatXp = new Dictionary<string, double>
            {
                ["chicken"] = 5,
                ["boar"] = 10,
                ["stag"] = 10,
                ["wolf"] = 15,
                ["bear"] = 25,
                ["polarbear"] = 30,
                ["scientistnpc_heavy"] = 50,
                ["patrolhelicopter"] = 300,
                ["bradleyapc"] = 250,
            };

            [JsonProperty("Perks")] public Dictionary<string, PerkSettings> Perks = new Dictionary<string, PerkSettings>();

            [JsonProperty("Reset everyone on map wipe")] public bool ResetOnWipe = true;
            [JsonProperty("Hours between free perk resets (0 = any time)")] public double RespecCooldownHours = 24;
            [JsonProperty("Show XP pop-ups")] public bool ShowXpPopups = true;
            [JsonProperty("Show perk bonuses in the pop-up")] public bool ShowPerkPopups = true;
            [JsonProperty("Pop-up screen anchor (0 0 = bottom left, 1 1 = top right)")] public string PopupAnchor = "0 0";
            [JsonProperty("Pop-up offset from the anchor (x y)")] public string PopupOffset = "16 62";
            [JsonProperty("Pop-up width")] public float PopupWidth = 270f;
            [JsonProperty("Pop-up seconds on screen")] public float PopupSeconds = 3f;
            [JsonProperty("Players need the permission rustsandboxskills.use")] public bool RequirePermission = false;
            [JsonProperty("Chat prefix")] public string ChatPrefix = "<color=#7bd88f>[Skills]</color>";
            [JsonProperty("Chat icon (SteamID, 0 = server)")] public ulong ChatIcon = 0;
        }

        protected override void LoadDefaultConfig()
        {
            _config = new PluginConfig();
            FillPerkDefaults();
        }

        // This plugin used to be called RustServerManagerSkills (the project was renamed to Rust Sandbox).
        // The first time it loads, copy the old config and data files over so nothing is lost.
        private const string LegacyName = "RustServerManagerSkills";

        // Runs before Oxide loads the config, so it works on the files directly. A marker file makes it
        // a one-time copy: the old files win once, even if this plugin already created empty new ones.
        private void MigrateLegacyFiles(params string[] dataSuffixes)
        {
            const string newName = "RustSandboxSkills";
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
            MigrateLegacyFiles("", "_Event");
            base.LoadConfig();
            try
            {
                _config = Config.ReadObject<PluginConfig>();
                if (_config == null) throw new Exception("empty");
                if (_config.CombatXp == null) _config.CombatXp = new Dictionary<string, double>();
                if (_config.Perks == null) _config.Perks = new Dictionary<string, PerkSettings>();
                FillPerkDefaults();
                SaveConfig();
            }
            catch (Exception ex)
            {
                PrintError($"Couldn't read the config file ({ex.Message}); using defaults and leaving the file untouched.");
                _config = new PluginConfig();
                FillPerkDefaults();
            }
        }

        protected override void SaveConfig() => Config.WriteObject(_config, true);

        private void FillPerkDefaults()
        {
            foreach (var p in Perks)
            {
                PerkSettings s;
                if (!_config.Perks.TryGetValue(p.Id, out s) || s == null)
                    _config.Perks[p.Id] = s = new PerkSettings { MaxRank = p.DefaultMaxRank, PerRank = p.DefaultPerRank };
                if (s.MaxRank < 0) s.MaxRank = 0;
            }
        }

        private PerkSettings PerkCfg(string id)
        {
            PerkSettings s;
            return _config.Perks.TryGetValue(id, out s) ? s : null;
        }

        #endregion

        #region Lang

        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["Skill.mining"] = "Mining",
                ["Skill.woodcutting"] = "Woodcutting",
                ["Skill.combat"] = "Combat",
                ["Skill.gardening"] = "Gardening",
                ["Skill.electrician"] = "Electrician",
                ["Skill.survival"] = "Survival",
                ["Skill.mechanic"] = "Mechanic",
                ["Skill.fishing"] = "Fishing",
                ["Skill.scrapper"] = "Scrapper",
                ["Skill.cooking"] = "Cooking",
                ["Perk.bigportions"] = "Big Portions",
                ["Perk.bigportions.Desc"] = "{0}% chance of an extra piece when meat or fish cooks",
                ["Perk.masterbrewer"] = "Master Brewer",
                ["Perk.masterbrewer.Desc"] = "{0}% chance of extra teas from a mixing table",
                ["Perk.steeped"] = "Steeped",
                ["Perk.steeped.Desc"] = "Tea and other food buffs last {0}% longer",
                ["Pop.bigportions"] = "<color=#ffd479>Big Portions</color> +{0} cooked",
                ["Pop.masterbrewer"] = "<color=#ffd479>Master Brewer</color> +{0} extra",
                ["Pop.steeped"] = "<color=#ffd479>Steeped</color> buff +{0}s",
                ["Total.bigportions"] = "{0} extra cooked food",
                ["Total.masterbrewer"] = "{0} extra teas",
                ["Total.steeped"] = "{0} seconds of extra buffs",
                ["Perk.prospector"] = "Prospector",
                ["Perk.prospector.Desc"] = "+{0}% ore and stone from nodes",
                ["Perk.luckystrike"] = "Lucky Strike",
                ["Perk.luckystrike.Desc"] = "{0}% chance to double a hit on ore, stone or trees",
                ["Perk.toolcare"] = "Tool Care",
                ["Perk.toolcare.Desc"] = "Tools wear out {0}% slower",
                ["Perk.lumberjack"] = "Lumberjack",
                ["Perk.lumberjack.Desc"] = "+{0}% wood from trees",
                ["Perk.craftsman"] = "Craftsman",
                ["Perk.craftsman.Desc"] = "Craft {0}% faster",
                ["Perk.thickskin"] = "Thick Skin",
                ["Perk.thickskin.Desc"] = "Take {0}% less damage",
                ["Perk.hunter"] = "Hunter",
                ["Perk.hunter.Desc"] = "+{0}% damage to animals and NPCs",
                ["Perk.fieldmedic"] = "Field Medic",
                ["Perk.fieldmedic.Desc"] = "Bandages, syringes and medkits heal {0}% more",
                ["Perk.greenthumb"] = "Green Thumb",
                ["Perk.greenthumb.Desc"] = "Crops you plant grow {0}% faster",
                ["Perk.bountiful"] = "Bountiful Harvest",
                ["Perk.bountiful.Desc"] = "+{0}% from harvesting crops",
                ["Perk.forager"] = "Forager",
                ["Perk.forager.Desc"] = "+{0}% from wild hemp, berries, mushrooms and crops",
                ["Perk.solarboost"] = "Solar Boost",
                ["Perk.solarboost.Desc"] = "Your solar panels make {0}% more power",
                ["Perk.bigbatteries"] = "Big Batteries",
                ["Perk.bigbatteries.Desc"] = "Your batteries hold {0}% more charge",
                ["Perk.generator"] = "Efficient Generator",
                ["Perk.generator.Desc"] = "Your fuel generators burn {0}% less fuel",
                ["Perk.ironstomach"] = "Iron Stomach",
                ["Perk.ironstomach.Desc"] = "Get hungry and thirsty {0}% slower",
                ["Perk.deepbreath"] = "Deep Breath",
                ["Perk.deepbreath.Desc"] = "Lose air {0}% slower underwater",
                ["Perk.regeneration"] = "Regeneration",
                ["Perk.regeneration.Desc"] = "Heal {0} health per minute when out of combat",
                ["Perk.fuelsaver"] = "Fuel Saver",
                ["Perk.fuelsaver.Desc"] = "Vehicles you drive burn {0}% less fuel",
                ["Perk.toughframe"] = "Tough Frame",
                ["Perk.toughframe.Desc"] = "Vehicles you drive take {0}% less damage",
                ["Perk.quickfix"] = "Quick Fix",
                ["Perk.quickfix.Desc"] = "Vehicle repairs restore {0}% more health",
                ["Perk.bigcatch"] = "Big Catch",
                ["Perk.bigcatch.Desc"] = "{0}% chance to catch two fish",
                ["Perk.steadyline"] = "Steady Line",
                ["Perk.steadyline.Desc"] = "Fishing line strains {0}% slower",
                ["Perk.rodcare"] = "Rod Care",
                ["Perk.rodcare.Desc"] = "Fishing rods wear out {0}% slower",
                ["Perk.quickrecycler"] = "Quick Recycler",
                ["Perk.quickrecycler.Desc"] = "Recyclers you start run {0}% faster",
                ["Perk.bargain"] = "Bargain Research",
                ["Perk.bargain.Desc"] = "Research costs {0}% less scrap",
                ["Perk.techdiscount"] = "Tech Tree Discount",
                ["Perk.techdiscount.Desc"] = "{0}% scrap back on tech tree unlocks",

                ["Overview"] = "<size=16>Your skills</size>{0}\nType <color=#ffd479>/skills perks</color> to spend points, <color=#ffd479>/skills bonuses</color> to see what your perks have done.",
                ["OverviewLine"] = "\n<color=#ffd479>{0}</color> level {1}  ({2})  <color=#7bd88f>{3} point(s)</color>",
                ["OverviewMax"] = "max level",
                ["OverviewXp"] = "{0}/{1} XP",
                ["SoloOn"] = "\n<color=#7bd88f>Solo bonus active:</color> +{0}% XP and an extra point every {1} levels.",
                ["SoloOnNoPoints"] = "\n<color=#7bd88f>Solo bonus active:</color> +{0}% XP.",
                ["PerksHeader"] = "<size=16>{0} perks</size>  <color=#7bd88f>{1} point(s) to spend</color>",
                ["PerkLine"] = "\n<color=#ffd479>{0}</color> ({1}) rank {2}/{3}: {4}{5}",
                ["PerkNext"] = "  <color=#9aa0a6>next rank: {0} pt</color>",
                ["PerkMaxed"] = "  <color=#7bd88f>maxed</color>",
                ["PerkOff"] = "  <color=#9aa0a6>turned off on this server</color>",
                ["PerksFooter"] = "\nBuy with <color=#ffd479>/skills buy <name></color>, e.g. /skills buy {0}",
                ["PerksAll"] = "Pick a skill: /skills perks mining, woodcutting, combat, gardening, electrician, survival, mechanic, fishing or scrapper",
                ["UnknownPerk"] = "There's no perk called \"{0}\". Type /skills perks to see them.",
                ["UnknownSkill"] = "There's no skill called \"{0}\". Skills: mining, woodcutting, combat, gardening, electrician, survival, mechanic, fishing, scrapper.",
                ["PerkDisabled"] = "{0} is turned off on this server.",
                ["PerkAlreadyMax"] = "{0} is already at max rank.",
                ["NotEnoughPoints"] = "{0} rank {1} costs {2} {3} point(s); you have {4}.",
                ["Bought"] = "<color=#7bd88f>{0} rank {1}</color> unlocked: {2}.",
                ["ResetDone"] = "Your {0} perks were reset and {1} point(s) refunded.",
                ["ResetNothing"] = "You haven't spent any {0} points yet.",
                ["ResetCooldown"] = "You can reset perks again in {0}.",
                ["LevelUp"] = "<color=#7bd88f>{0} level {1}!</color> You got {2} point(s). Type /skills perks {3} to spend them.",
                ["XpPopup"] = "<color=#7bd88f>+{0} {1} XP</color>",
                ["Pop.prospector"] = "<color=#ffd479>Prospector</color> +{0} ore",
                ["Pop.lumberjack"] = "<color=#ffd479>Lumberjack</color> +{0} wood",
                ["Pop.luckystrike"] = "<color=#ffd479>Lucky Strike!</color> Double hit",
                ["Pop.luckystrike.Many"] = "<color=#ffd479>Lucky!</color> {0} doubles",
                ["Pop.craftsman"] = "<color=#ffd479>Craftsman</color> {0}s faster",
                ["Pop.thickskin"] = "<color=#ffd479>Thick Skin</color> blocked {0} damage",
                ["Pop.hunter"] = "<color=#ffd479>Hunter</color> +{0} damage",
                ["Pop.fieldmedic"] = "<color=#ffd479>Field Medic</color> +{0} health",
                ["Pop.bountiful"] = "<color=#ffd479>Bountiful Harvest</color> +{0}",
                ["Pop.forager"] = "<color=#ffd479>Forager</color> +{0}",
                ["Pop.toughframe"] = "<color=#ffd479>Tough Frame</color> blocked {0} damage",
                ["Pop.quickfix"] = "<color=#ffd479>Quick Fix</color> +{0} health",
                ["Pop.bigcatch"] = "<color=#ffd479>Big Catch!</color> Two fish",
                ["Pop.bargain"] = "<color=#ffd479>Bargain Research</color> saved {0} scrap",
                ["Pop.techdiscount"] = "<color=#ffd479>Tech Tree Discount</color> refunded {0} scrap",
                ["Pop.quickrecycler"] = "<color=#ffd479>Quick Recycler</color> running {0}% faster",
                ["Total.prospector"] = "{0} extra ore and stone",
                ["Total.lumberjack"] = "{0} extra wood",
                ["Total.luckystrike"] = "{0} double hits",
                ["Total.toolcare"] = "{0} tool durability saved",
                ["Total.craftsman"] = "{0} of crafting time saved",
                ["Total.thickskin"] = "{0} damage blocked",
                ["Total.hunter"] = "{0} extra damage dealt",
                ["Total.fieldmedic"] = "{0} extra health healed",
                ["Total.greenthumb"] = "{0} of growing time saved",
                ["Total.bountiful"] = "{0} extra crops",
                ["Total.forager"] = "{0} extra wild pickups",
                ["Total.ironstomach"] = "{0} food and water saved",
                ["Total.regeneration"] = "{0} health regenerated",
                ["Total.fuelsaver"] = "{0} fuel saved",
                ["Total.toughframe"] = "{0} vehicle damage blocked",
                ["Total.quickfix"] = "{0} extra repair health",
                ["Total.bigcatch"] = "{0} extra fish",
                ["Total.bargain"] = "{0} scrap saved on research",
                ["Total.techdiscount"] = "{0} scrap refunded",
                ["BonusesHeader"] = "<size=16>What your perks have done</size>{0}",
                ["BonusesLine"] = "\n<color=#ffd479>{0}</color> (rank {1}): {2}",
                ["UiTitle"] = "SKILLS & PERKS",
                ["UiSolo"] = "<color=#7bd88f>Solo bonus:</color> +{0}% XP{1}",
                ["UiSoloPoints"] = ", +1 point every {0} levels",
                ["UiTeam"] = "<color=#7bd88f>Team:</color> teammates within {0}m share {1}% of each other's XP",
                ["UiLevel"] = "Level {0}",
                ["UiTabLevel"] = "Lv {0}",
                ["UiTabPoints"] = "<color=#7bd88f>{0} pt</color>   Lv {1}",
                ["UiXp"] = "{0} / {1} XP",
                ["UiMaxLevel"] = "MAX LEVEL",
                ["UiPoints"] = "{0} point(s) to spend",
                ["UiNoPoints"] = "No points to spend",
                ["UiPerksTitle"] = "{0} perks",
                ["UiRank"] = "Rank {0}/{1}",
                ["UiNow"] = "Now: {0}",
                ["UiNotYet"] = "Not unlocked yet",
                ["UiNext"] = "Next rank: {0}",
                ["UiAllRanks"] = "All ranks unlocked",
                ["UiBuy"] = "UNLOCK RANK {0}  ({1} pt)",
                ["UiNeed"] = "Need {0} pt",
                ["UiMaxedBtn"] = "MAXED",
                ["UiOff"] = "Turned off",
                ["UiSoFar"] = "So far: {0}",
                ["UiReset"] = "Reset {0} perks",
                ["UiResetConfirm"] = "Click again to reset",
                ["UiResetAsk"] = "Resetting refunds all {0} points spent on {1} perks. You get one free reset every {2}h.",
                ["UiResetWait"] = "Next reset in {0}",
                ["UiPopupsOn"] = "Pop-ups: ON",
                ["UiPopupsOff"] = "Pop-ups: OFF",
                ["UiClose"] = "CLOSE",
                ["UiTip"] = "Tip: to open this with a key, press F1 and type  bind k skills.open",
                ["EventStart"] = "<color=#7bd88f>{0}x XP is on{1}!</color> Every skill levels faster. Type /skills to see your progress.",
                ["EventFor"] = " for {0}",
                ["EventEnd"] = "The {0}x XP event is over. Thanks for playing!",
                ["EventBadge"] = "<color=#ffd479>{0}x XP event{1}</color>",
                ["EventLeft"] = ", {0} left",
                ["PopupHelp"] = "Move the pop-up: /skills popup up, down, left or right (add a number for a bigger step, e.g. /skills popup up 40), or /skills popup reset.",
                ["PopupMoved"] = "Pop-up preview shown at {0}, {1}. Use /skills popup up/down/left/right to move it, or /skills popup reset.",
                ["BonusesNone"] = "You don't have any perks yet. Type /skills perks to see them.",
                ["XpPopupsOn"] = "XP pop-ups turned on.",
                ["XpPopupsOff"] = "XP pop-ups turned off.",
                ["NoPermission"] = "You don't have access to skills on this server.",
                ["Help"] = "/skills - open the skills menu\n/skills info - your levels and points in chat\n/skills perks <skill> - perks you can buy\n/skills buy <perk> - unlock the next rank\n/skills bonuses - what your perks have done\n/skills reset <skill> - refund that skill's points\n/skills popup up/down/left/right - move the pop-up\n/skills xp - turn pop-ups on or off",
            }, this);
        }

        private string Msg(string key, string userId, params object[] args)
        {
            var text = lang.GetMessage(key, this, userId);
            return args.Length == 0 ? text : string.Format(text, args);
        }

        private void Reply(BasePlayer player, string key, params object[] args)
        {
            Player.Message(player, Msg(key, player.UserIDString, args), _config.ChatPrefix, _config.ChatIcon);
        }

        private string SkillName(string skill, string userId) => Msg("Skill." + skill, userId);
        private string PerkName(string id, string userId) => Msg("Perk." + id, userId);

        private string PerkDesc(string id, int rank, string userId)
        {
            var s = PerkCfg(id);
            var def = FindPerk(id);
            var value = Math.Max(1, rank) * (s == null ? 0 : s.PerRank);
            return Msg("Perk." + id + ".Desc", userId, Math.Round(def != null && def.Raw ? value : value * 100, 1));
        }

        #endregion

        #region Data

        private const string DataFile = "RustSandboxSkills";
        private const string PermUse = "rustsandboxskills.use";

        private class SkillData
        {
            public int Level = 1;
            public double Xp;
            public int PointsEarned;
        }

        private class PlayerData
        {
            public string Name = "";
            public Dictionary<string, SkillData> Skills = new Dictionary<string, SkillData>();
            public Dictionary<string, int> Perks = new Dictionary<string, int>();
            public DateTime LastRespec = DateTime.MinValue;
            public bool HideXp;
            public Dictionary<string, double> Totals = new Dictionary<string, double>();
            public float? PopupX;
            public float? PopupY;
        }

        private Dictionary<ulong, PlayerData> _data = new Dictionary<ulong, PlayerData>();
        private bool _dirty;

        private void LoadData()
        {
            try { _data = Interface.Oxide.DataFileSystem.ReadObject<Dictionary<ulong, PlayerData>>(DataFile) ?? new Dictionary<ulong, PlayerData>(); }
            catch (Exception ex)
            {
                PrintError($"Couldn't read the data file ({ex.Message}); starting empty.");
                _data = new Dictionary<ulong, PlayerData>();
            }
        }

        private void SaveData()
        {
            if (!_dirty) return;
            Interface.Oxide.DataFileSystem.WriteObject(DataFile, _data);
            _dirty = false;
        }

        private PlayerData Get(ulong id)
        {
            PlayerData d;
            if (!_data.TryGetValue(id, out d)) _data[id] = d = new PlayerData();
            foreach (var s in Skills)
                if (!d.Skills.ContainsKey(s)) d.Skills[s] = new SkillData();
            if (d.Totals == null) d.Totals = new Dictionary<string, double>();
            return d;
        }

        private int Rank(ulong id, string perk)
        {
            PlayerData d;
            int r;
            if (!_data.TryGetValue(id, out d) || !d.Perks.TryGetValue(perk, out r)) return 0;
            var s = PerkCfg(perk);
            if (s == null || !s.Enabled) return 0;
            return Math.Min(r, s.MaxRank);
        }

        /// <summary>XP from planting, placing and wiring is capped per hour so it can't be farmed by placing and picking up.</summary>
        private readonly Dictionary<ulong, KeyValuePair<DateTime, double>> _buildXp = new Dictionary<ulong, KeyValuePair<DateTime, double>>();

        private void EarnCapped(BasePlayer player, string skill, double amount)
        {
            if (player == null || amount <= 0) return;
            if (_config.BuildXpCapPerHour > 0)
            {
                KeyValuePair<DateTime, double> window;
                var now = DateTime.UtcNow;
                if (!_buildXp.TryGetValue(player.userID, out window) || now - window.Key > TimeSpan.FromHours(1))
                    window = new KeyValuePair<DateTime, double>(now, 0);
                if (window.Value >= _config.BuildXpCapPerHour) return;
                amount = Math.Min(amount, _config.BuildXpCapPerHour - window.Value);
                _buildXp[player.userID] = new KeyValuePair<DateTime, double>(window.Key, window.Value + amount);
            }
            Earn(player, skill, amount);
        }

        private double Bonus(ulong id, string perk)
        {
            var r = Rank(id, perk);
            if (r <= 0) return 0;
            return r * PerkCfg(perk).PerRank;
        }

        private int RankCost(int rank) => _config.RankCostsRank ? rank : 1;

        private int PointsSpent(PlayerData d, string skill)
        {
            var spent = 0;
            foreach (var kv in d.Perks)
            {
                var p = FindPerk(kv.Key);
                if (p == null || p.Skill != skill) continue;
                for (var r = 1; r <= kv.Value; r++) spent += RankCost(r);
            }
            return spent;
        }

        private int PointsFree(PlayerData d, string skill) => Math.Max(0, d.Skills[skill].PointsEarned - PointsSpent(d, skill));

        #endregion

        #region Lifecycle

        private Timer _craftTimer;
        private Timer _slowTimer;

        private void Init()
        {
            permission.RegisterPermission(PermUse, this);
            LoadData();
            LoadEvent();
        }

        private void OnServerInitialized()
        {
            _craftTimer = timer.Every(0.2f, CraftTick);
            _slowTimer = timer.Every(10f, SlowTick);
            foreach (var net in BaseNetworkable.serverEntities)
                Track(net as BaseEntity);
            foreach (var e in _power) ApplyPower(e, false);
            foreach (var p in BasePlayer.activePlayerList) OnPlayerConnected(p);
        }

        private void Unload()
        {
            _craftTimer?.Destroy();
            _slowTimer?.Destroy();
            foreach (var e in _power) ApplyPower(e, true);
            foreach (var p in BasePlayer.activePlayerList)
            {
                HidePopup(p);
                CuiHelper.DestroyUi(p, MenuUi);
            }
            SaveData();
        }

        private void OnServerSave() => SaveData();

        private void OnNewSave(string filename)
        {
            if (!_config.ResetOnWipe) return;
            Interface.Oxide.DataFileSystem.WriteObject(DataFile + "_lastwipe", _data);
            _data = new Dictionary<ulong, PlayerData>();
            _dirty = true;
            SaveData();
            foreach (var e in _power) ApplyPower(e, false);
            Puts("Map wipe: everyone's skills were reset (the old data is in " + DataFile + "_lastwipe.json).");
        }

        private void OnPlayerConnected(BasePlayer player)
        {
            if (player == null || !player.userID.IsSteamId()) return;
            var d = Get(player.userID);
            if (d.Name != player.displayName) { d.Name = player.displayName; _dirty = true; }
        }

        private void OnPlayerDisconnected(BasePlayer player, string reason)
        {
            if (player == null) return;
            _popups.Remove(player.userID);
            _craftSeen.Remove(player.userID);
            _menus.Remove(player.userID);
            _lastPos.Remove(player.userID);
            _meta.Remove(player.userID);
        }

        private bool Allowed(BasePlayer player)
        {
            return player != null && player.userID.IsSteamId() && !player.IsNpc
                && (!_config.RequirePermission || permission.UserHasPermission(player.UserIDString, PermUse));
        }

        #endregion

        #region XP and levels

        private double XpToNext(int level) => Math.Round(_config.XpBase * Math.Pow(Math.Max(1, level), _config.XpCurve));

        private bool IsSolo(BasePlayer player)
        {
            var team = player.Team;
            return team == null || team.members == null || team.members.Count <= 1;
        }

        /// <summary>XP earned by doing something. Adds the solo bonus and shares a slice with nearby teammates.</summary>
        private void Earn(BasePlayer player, string skill, double amount)
        {
            if (amount <= 0 || !Allowed(player)) return;
            amount *= XpEventRate;
            if (IsSolo(player))
            {
                AddXp(player, skill, amount * (1 + _config.SoloXpBonus));
                return;
            }
            AddXp(player, skill, amount);
            var team = player.Team;
            if (_config.TeamXpShare <= 0 || team.members.Count > _config.TeamShareMaxSize) return;
            var share = amount * _config.TeamXpShare;
            var rangeSq = _config.TeamShareRange * _config.TeamShareRange;
            foreach (var id in team.members)
            {
                if (id == player.userID) continue;
                var mate = BasePlayer.FindByID(id);
                if (mate == null || !mate.IsConnected || mate.IsDead() || !Allowed(mate)) continue;
                if ((mate.transform.position - player.transform.position).sqrMagnitude > rangeSq) continue;
                AddXp(mate, skill, share);
            }
        }

        private void AddXp(BasePlayer player, string skill, double amount) => AddXp(player.userID, player, skill, amount);

        /// <summary>Add XP and handle level-ups. player is null when the manager grants XP to someone offline.</summary>
        private void AddXp(ulong id, BasePlayer player, string skill, double amount)
        {
            var d = Get(id);
            var s = d.Skills[skill];
            _dirty = true;
            if (s.Level >= _config.MaxLevel) return;
            s.Xp += amount;
            if (player != null) QueuePopup(player, "xp:" + skill, amount);
            var gained = 0;
            while (s.Level < _config.MaxLevel && s.Xp >= XpToNext(s.Level))
            {
                s.Xp -= XpToNext(s.Level);
                s.Level++;
                var points = _config.PointsPerLevel;
                if (_config.SoloExtraPointEvery > 0 && s.Level % _config.SoloExtraPointEvery == 0 && (player == null || IsSolo(player))) points++;
                s.PointsEarned += points;
                gained += points;
                if (player == null) continue;
                Reply(player, "LevelUp", SkillName(skill, player.UserIDString), s.Level, points, skill);
                NextTick(() => RefreshMenu(player));
            }
            if (s.Level >= _config.MaxLevel) s.Xp = 0;
            if (gained > 0 && player != null)
                Effect.server.Run("assets/prefabs/misc/xmas/presents/effects/unwrap.prefab", player.transform.position);
        }

        // ---- XP events: the manager (or a schedule) runs "sandboxskills.xprate 2 2880" for a 48-hour double XP weekend.

        private const string EventFile = "RustSandboxSkills_Event";

        private class XpEvent
        {
            public double Rate = 1;
            public DateTime? Until;
        }

        private XpEvent _event = new XpEvent();

        private double XpEventRate => _event.Rate > 1 && (_event.Until == null || _event.Until > DateTime.UtcNow) ? _event.Rate : 1;

        private void LoadEvent()
        {
            try { _event = Interface.Oxide.DataFileSystem.ReadObject<XpEvent>(EventFile) ?? new XpEvent(); }
            catch { _event = new XpEvent(); }
        }

        private void SetEvent(double rate, double minutes)
        {
            var was = XpEventRate;
            _event = rate > 1 ? new XpEvent { Rate = rate, Until = minutes > 0 ? DateTime.UtcNow.AddMinutes(minutes) : (DateTime?)null } : new XpEvent();
            Interface.Oxide.DataFileSystem.WriteObject(EventFile, _event);
            if (rate > 1)
            {
                var length = minutes > 0 ? Msg("EventFor", null, FormatSeconds(minutes * 60)) : "";
                Server.Broadcast(Msg("EventStart", null, Math.Round(rate, 2), length), _config.ChatPrefix, _config.ChatIcon);
            }
            else if (was > 1)
                Server.Broadcast(Msg("EventEnd", null, Math.Round(was, 2)), _config.ChatPrefix, _config.ChatIcon);
            foreach (var p in BasePlayer.activePlayerList) RefreshMenu(p);
        }

        private void CheckEventEnd()
        {
            if (_event.Rate > 1 && _event.Until != null && _event.Until <= DateTime.UtcNow) SetEvent(1, 0);
        }

        private string EventText(string userId)
        {
            if (XpEventRate <= 1) return "";
            var left = _event.Until == null ? "" : Msg("EventLeft", userId, FormatSeconds((_event.Until.Value - DateTime.UtcNow).TotalSeconds));
            return Msg("EventBadge", userId, Math.Round(XpEventRate, 2), left);
        }

        // Pop-ups are batched so a burst of hits shows one "+45 Mining XP" instead of nine. Lines are
        // keyed "xp:<skill>" or "perk:<perk>" and shown in the order they first happened.
        private const string PopupUi = "SandboxSkills.Popup";

        private class Popup
        {
            public readonly List<string> Order = new List<string>();
            public readonly Dictionary<string, double> Pending = new Dictionary<string, double>();
            public Timer Show;
            public Timer Hide;
        }

        private readonly Dictionary<ulong, Popup> _popups = new Dictionary<ulong, Popup>();

        private bool WantsPopup(BasePlayer player, bool perk)
        {
            if (perk ? !_config.ShowPerkPopups : !_config.ShowXpPopups) return false;
            return !Get(player.userID).HideXp;
        }

        /// <summary>Count what a perk did (for /skills bonuses) and show it in the pop-up.</summary>
        private void Note(BasePlayer player, string perk, double amount, bool popup = true)
        {
            if (amount <= 0) return;
            NoteTotal(player.userID, perk, amount);
            if (popup) QueuePopup(player, "perk:" + perk, amount);
        }

        private void NoteTotal(ulong id, string perk, double amount)
        {
            if (amount <= 0) return;
            var d = Get(id);
            double total;
            d.Totals.TryGetValue(perk, out total);
            d.Totals[perk] = total + amount;
            _dirty = true;
        }

        private void QueuePopup(BasePlayer player, string key, double amount)
        {
            if (!WantsPopup(player, key.StartsWith("perk:"))) return;
            Popup p;
            if (!_popups.TryGetValue(player.userID, out p)) _popups[player.userID] = p = new Popup();
            double cur;
            if (!p.Pending.TryGetValue(key, out cur)) p.Order.Add(key);
            p.Pending[key] = cur + amount;
            if (p.Show != null && !p.Show.Destroyed) return;
            var id = player.userID;
            p.Show = timer.Once(1f, () =>
            {
                if (player == null || !player.IsConnected) { _popups.Remove(id); return; }
                var lines = new List<string>();
                foreach (var k in p.Order)
                {
                    var line = PopupLine(k, p.Pending[k], player.UserIDString);
                    if (line != null) lines.Add(line);
                }
                p.Order.Clear();
                p.Pending.Clear();
                if (lines.Count == 0) return;
                DrawPopup(player, lines);
                p.Hide?.Destroy();
                p.Hide = timer.Once(_config.PopupSeconds, () => HidePopup(player));
            });
        }

        private string PopupLine(string key, double value, string userId)
        {
            var name = key.Substring(key.IndexOf(':') + 1);
            if (key.StartsWith("xp:"))
                return value < 0.5 ? null : Msg("XpPopup", userId, Math.Round(value), SkillName(name, userId));
            if (name == "quickrecycler")
                return Msg("Pop.quickrecycler", userId, Math.Round(Bonus(ulong.Parse(userId), name) * 100));
            if (name == "luckystrike" || name == "bigcatch")
                return value <= 1 ? Msg("Pop." + name, userId) : Msg("Pop.luckystrike.Many", userId, Math.Round(value));
            var shown = value < 10 ? Math.Round(value, 1) : Math.Round(value);
            return shown <= 0 ? null : Msg("Pop." + name, userId, shown);
        }

        private void DrawPopup(BasePlayer player, List<string> lines)
        {
            const float lineHeight = 19f;
            float x, y;
            PopupSpot(player, out x, out y);
            var height = lines.Count * lineHeight + 10;
            var ui = new CuiElementContainer();
            ui.Add(new CuiPanel
            {
                Image = { Color = "0.08 0.08 0.08 0.72" },
                RectTransform =
                {
                    AnchorMin = _config.PopupAnchor, AnchorMax = _config.PopupAnchor,
                    OffsetMin = $"{x} {y}", OffsetMax = $"{x + _config.PopupWidth} {y + height}",
                },
            }, "Hud", PopupUi);
            ui.Add(new CuiLabel
            {
                Text = { Text = string.Join("\n", lines), FontSize = 14, Font = "robotocondensed-bold.ttf", Align = TextAnchor.MiddleLeft, Color = "1 1 1 0.95" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1", OffsetMin = "9 4", OffsetMax = "-9 -4" },
            }, PopupUi);
            CuiHelper.DestroyUi(player, PopupUi);
            CuiHelper.AddUi(player, ui);
        }

        private void PopupSpot(BasePlayer player, out float x, out float y)
        {
            var offset = (_config.PopupOffset ?? "").Split(' ');
            if (offset.Length < 2 || !float.TryParse(offset[0], out x) || !float.TryParse(offset[1], out y)) { x = 16; y = 62; }
            var d = Get(player.userID);
            if (d.PopupX.HasValue) x = d.PopupX.Value;
            if (d.PopupY.HasValue) y = d.PopupY.Value;
        }

        private static void HidePopup(BasePlayer player)
        {
            if (player != null && player.IsConnected) CuiHelper.DestroyUi(player, PopupUi);
        }

        #endregion

        #region Gathering (Mining, Woodcutting, Tool Care)

        private static bool IsOre(Item item)
        {
            var s = item.info.shortname;
            return s == "stones" || s == "metal.ore" || s == "sulfur.ore" || s == "hq.metal.ore";
        }

        private void OnDispenserGather(ResourceDispenser dispenser, BaseEntity entity, Item item) => Gather(dispenser, entity as BasePlayer, item, false);

        private void OnDispenserBonus(ResourceDispenser dispenser, BasePlayer player, Item item) => Gather(dispenser, player, item, true);

        // A finished node can hand out several bonus items; the finishing XP is paid once per node.
        private readonly HashSet<ResourceDispenser> _finishPaid = new HashSet<ResourceDispenser>();

        private double GatherXp(ResourceDispenser dispenser, bool finished, double hit, double finish)
        {
            if (!finished) return hit;
            if (!_finishPaid.Add(dispenser)) return 0;
            if (_finishPaid.Count == 1) NextTick(() => _finishPaid.Clear());
            return finish;
        }

        private void Gather(ResourceDispenser dispenser, BasePlayer player, Item item, bool finished)
        {
            if (dispenser == null || item == null || item.info == null || !Allowed(player)) return;
            double mult;
            if (dispenser.gatherType == ResourceDispenser.GatherType.Ore)
            {
                Earn(player, Mining, GatherXp(dispenser, finished, _config.XpOreHit, _config.XpOreFinish));
                if (!IsOre(item)) return;
                mult = 1 + Bonus(player.userID, "prospector");
            }
            else if (dispenser.gatherType == ResourceDispenser.GatherType.Tree)
            {
                Earn(player, Woodcutting, GatherXp(dispenser, finished, _config.XpTreeHit, _config.XpTreeFinish));
                if (item.info.shortname != "wood") return;
                mult = 1 + Bonus(player.userID, "lumberjack");
            }
            else return;

            var perk = dispenser.gatherType == ResourceDispenser.GatherType.Ore ? "prospector" : "lumberjack";
            var lucky = !finished && Bonus(player.userID, "luckystrike") > UnityEngine.Random.value;
            var before = item.amount;
            var withPerk = (int)Math.Max(1, Math.Round(before * mult));
            item.amount = lucky ? withPerk * 2 : withPerk;
            Note(player, perk, withPerk - before);
            if (lucky) Note(player, "luckystrike", 1);
        }

        private void OnLoseCondition(Item item, ref float amount)
        {
            if (item == null || item.info == null || item.info.category != ItemCategory.Tool) return;
            var player = item.GetOwnerPlayer();
            if (player == null || !Allowed(player)) return;
            var care = Bonus(player.userID, "toolcare");
            if (care <= 0) return;
            var saved = amount * (float)Math.Min(1, care);
            amount -= saved;
            Note(player, "toolcare", saved, false);
        }

        #endregion

        #region Crafting (Craftsman)

        // The game sets a craft's end time when it starts (no hook there), so this checks the front
        // of each crafting queue a few times a second and shortens any newly started craft once.
        private readonly Dictionary<ulong, Dictionary<int, float>> _craftSeen = new Dictionary<ulong, Dictionary<int, float>>();

        private void CraftTick()
        {
            var now = UnityEngine.Time.realtimeSinceStartup;
            foreach (var player in BasePlayer.activePlayerList)
            {
                if (player == null || player.inventory == null || player.inventory.crafting == null) continue;
                var queue = player.inventory.crafting.queue;
                if (queue.Count == 0)
                {
                    _craftSeen.Remove(player.userID);
                    continue;
                }
                var speed = Bonus(player.userID, "craftsman");
                if (speed <= 0 || !Allowed(player)) continue;
                var task = queue.First.Value;
                if (task == null || task.cancelled || task.endTime <= now) continue;

                Dictionary<int, float> seen;
                if (!_craftSeen.TryGetValue(player.userID, out seen)) _craftSeen[player.userID] = seen = new Dictionary<int, float>();
                float last;
                if (seen.TryGetValue(task.taskUID, out last) && Mathf.Approximately(last, task.endTime)) continue;

                var full = task.endTime - now;
                var remaining = full * (float)Math.Max(0.05, 1 - speed);
                task.endTime = now + remaining;
                Note(player, "craftsman", full - remaining);
                seen[task.taskUID] = task.endTime;
                player.Command("note.craft_start", task.taskUID, remaining, task.amount);
            }
        }

        #endregion

        #region Combat (XP, Thick Skin, Hunter, Field Medic)

        private readonly Dictionary<string, float> _lastKill = new Dictionary<string, float>();

        private static bool IsAnimalOrNpc(BaseCombatEntity entity)
        {
            if (entity is BaseNpc) return true;
            var bp = entity as BasePlayer;
            return bp != null && (bp.IsNpc || !bp.userID.IsSteamId());
        }

        private void OnEntityTakeDamage(BaseCombatEntity entity, HitInfo info)
        {
            if (entity == null || info == null || info.damageTypes == null) return;

            var victim = entity as BasePlayer;
            if (victim != null && Allowed(victim))
            {
                var skin = Bonus(victim.userID, "thickskin");
                if (skin > 0)
                {
                    var before = info.damageTypes.Total();
                    info.damageTypes.ScaleAll((float)Math.Max(0, 1 - skin));
                    Note(victim, "thickskin", before - info.damageTypes.Total());
                }
            }

            var vehicle = entity is BasePlayer ? null : VehicleOf(entity);
            if (vehicle != null)
            {
                var driver = vehicle.GetDriver();
                if (driver != null && Allowed(driver))
                {
                    var frame = Bonus(driver.userID, "toughframe");
                    if (frame > 0)
                    {
                        var before = info.damageTypes.Total();
                        info.damageTypes.ScaleAll((float)Math.Max(0, 1 - frame));
                        Note(driver, "toughframe", before - info.damageTypes.Total());
                    }
                }
            }

            var attacker = info.InitiatorPlayer;
            if (attacker != null && attacker != entity && Allowed(attacker) && IsAnimalOrNpc(entity))
            {
                var hunter = Bonus(attacker.userID, "hunter");
                if (hunter > 0)
                {
                    var before = info.damageTypes.Total();
                    info.damageTypes.ScaleAll((float)(1 + hunter));
                    Note(attacker, "hunter", info.damageTypes.Total() - before);
                }
            }
        }

        private void OnEntityDeath(BaseCombatEntity entity, HitInfo info)
        {
            if (entity == null || info == null) return;
            var killer = info.InitiatorPlayer;
            if (killer == null || killer == entity || !Allowed(killer)) return;

            double xp;
            var victim = entity as BasePlayer;
            if (victim != null && victim.userID.IsSteamId() && !victim.IsNpc)
            {
                if (killer.Team != null && killer.Team.members.Contains(victim.userID)) return;
                var key = killer.userID + ":" + victim.userID;
                float last;
                var now = UnityEngine.Time.realtimeSinceStartup;
                if (_lastKill.TryGetValue(key, out last) && now - last < _config.SameVictimCooldown * 60) return;
                _lastKill[key] = now;
                xp = _config.XpPlayerKill;
            }
            else if (!_config.CombatXp.TryGetValue(entity.ShortPrefabName, out xp))
            {
                if (victim != null) xp = _config.XpNpcDefault;
                else if (entity is BaseNpc) xp = _config.XpAnimalDefault;
                else return;
            }
            Earn(killer, Combat, xp);
        }

        private void OnHealingItemUse(MedicalTool tool, BasePlayer healer, IMedicalToolTarget toTarget)
        {
            if (tool == null || toTarget == null || !Allowed(healer)) return;
            var patient = toTarget.GetEntity() as BasePlayer;
            Earn(healer, Survival, patient != null && patient != healer ? _config.XpMedOther : _config.XpMedSelf);
            var medic = Bonus(healer.userID, "fieldmedic");
            if (medic <= 0) return;
            var def = tool.GetOwnerItemDefinition();
            var consumable = def == null ? null : def.GetComponent<ItemModConsumable>();
            var target = toTarget.GetEntity() as BasePlayer;
            if (consumable == null || target == null) return;

            // Top up right after the game applies the normal healing.
            NextTick(() =>
            {
                if (target == null || target.IsDead()) return;
                double extra = 0;
                foreach (var effect in consumable.effects)
                {
                    if (effect.amount <= 0) continue;
                    if (effect.type == MetabolismAttribute.Type.Health)
                        target.health += (float)(effect.amount * medic);
                    else if (effect.type == MetabolismAttribute.Type.HealthOverTime)
                        target.metabolism.ApplyChange(MetabolismAttribute.Type.HealthOverTime, (float)(effect.amount * medic), effect.time);
                    else continue;
                    extra += effect.amount * medic;
                }
                target.SendNetworkUpdate();
                if (healer != null && healer.IsConnected) Note(healer, "fieldmedic", extra);
            });
        }

        #endregion

        #region Gardening (Green Thumb, Bountiful Harvest, Forager)

        private void OnGrowableGathered(GrowableEntity plant, Item item, BasePlayer player)
        {
            if (item == null || !Allowed(player)) return;
            Earn(player, Gardening, _config.XpHarvest);
            var bonus = Bonus(player.userID, "bountiful");
            if (bonus <= 0) return;
            var before = item.amount;
            item.amount = (int)Math.Max(1, Math.Round(before * (1 + bonus)));
            Note(player, "bountiful", item.amount - before);
        }

        private void OnCollectiblePickup(CollectibleEntity collectible, BasePlayer player)
        {
            if (collectible == null || collectible.itemList == null || !Allowed(player)) return;
            // Only plants count: ground stones, wood and ore pickups are left to the other skills.
            if (!collectible.itemList.Any(IsForaged)) return;
            Earn(player, Gardening, _config.XpForage);
            var bonus = Bonus(player.userID, "forager");
            if (bonus <= 0) return;
            double extra = 0;
            foreach (var ia in collectible.itemList)
            {
                if (!IsForaged(ia) || ia.amount <= 0) continue;
                var more = (float)Math.Round(ia.amount * bonus);
                ia.amount += more;
                extra += more;
            }
            Note(player, "forager", extra);
        }

        private static bool IsForaged(ItemAmount ia)
        {
            if (ia == null || ia.itemDef == null) return false;
            var name = ia.itemDef.shortname;
            return ia.itemDef.category == ItemCategory.Food || name == "cloth" || name.StartsWith("seed.");
        }

        // Plants have no per-plant growth speed, so a slow tick tops up the growth each owned plant made
        // since the last check (stageAge only moves forward within one growth stage).
        private class PlantSeen
        {
            public PlantProperties.State State;
            public float StageAge;
        }

        private readonly Dictionary<GrowableEntity, PlantSeen> _plants = new Dictionary<GrowableEntity, PlantSeen>();

        private void GrowTick()
        {
            List<GrowableEntity> dead = null;
            foreach (var kv in _plants)
            {
                var plant = kv.Key;
                if (plant == null || plant.IsDestroyed) { (dead ?? (dead = new List<GrowableEntity>())).Add(plant); continue; }
                var seen = kv.Value;
                var bonus = plant.OwnerID == 0 ? 0 : Bonus(plant.OwnerID, "greenthumb");
                if (bonus > 0 && plant.State == seen.State && plant.stageAge > seen.StageAge)
                {
                    var extra = (plant.stageAge - seen.StageAge) * (float)bonus;
                    plant.stageAge += extra;
                    NoteTotal(plant.OwnerID, "greenthumb", extra);
                }
                seen.State = plant.State;
                seen.StageAge = plant.stageAge;
            }
            if (dead != null) foreach (var p in dead) _plants.Remove(p);
        }

        #endregion

        #region Electrician (Solar Boost, Big Batteries, Efficient Generator)

        // Power items keep the bonus of whoever placed them (their OwnerID). The prefab holds the vanilla value.
        private readonly HashSet<BaseEntity> _power = new HashSet<BaseEntity>();
        private readonly Dictionary<uint, Component> _prefabDefaults = new Dictionary<uint, Component>();

        private T PrefabDefault<T>(BaseEntity e) where T : Component
        {
            Component c;
            if (!_prefabDefaults.TryGetValue(e.prefabID, out c))
            {
                var go = GameManager.server.FindPrefab(e.PrefabName);
                _prefabDefaults[e.prefabID] = c = go == null ? null : go.GetComponent<T>();
            }
            return c as T;
        }

        private void Track(BaseEntity e)
        {
            if (e == null || e.IsDestroyed) return;
            if (e is SolarPanel || e is ElectricBattery || e is FuelGenerator) _power.Add(e);
            else
            {
                var plant = e as GrowableEntity;
                if (plant != null && !_plants.ContainsKey(plant))
                    _plants[plant] = new PlantSeen { State = plant.State, StageAge = plant.stageAge };
            }
        }

        private void OnEntitySpawned(BaseNetworkable net)
        {
            var e = net as BaseEntity;
            if (!(e is SolarPanel || e is ElectricBattery || e is FuelGenerator || e is GrowableEntity)) return;
            // The owner is set as it's placed; wait a tick so it's there.
            NextTick(() =>
            {
                Track(e);
                if (_power.Contains(e)) ApplyPower(e, false);
            });
        }

        private void OnEntityKill(BaseNetworkable net)
        {
            var oven = net as BaseOven;
            if (oven != null) _rawBefore.Remove(oven);
            var e = net as BaseEntity;
            if (e == null) return;
            _power.Remove(e);
            var plant = e as GrowableEntity;
            if (plant != null) _plants.Remove(plant);
            var recycler = e as Recycler;
            if (recycler != null) _recyclerUser.Remove(recycler);
            var table = e as ResearchTable;
            if (table != null) _researcher.Remove(table);
        }

        private void ApplyPower(BaseEntity e, bool vanilla)
        {
            if (e == null || e.IsDestroyed) return;
            var owner = vanilla ? 0 : e.OwnerID;
            var solar = e as SolarPanel;
            if (solar != null)
            {
                var def = PrefabDefault<SolarPanel>(e);
                if (def == null) return;
                var b = owner == 0 ? 0 : Bonus(owner, "solarboost");
                solar.maximalPowerOutput = (int)Math.Round(def.maximalPowerOutput * (1 + b));
                return;
            }
            var battery = e as ElectricBattery;
            if (battery != null)
            {
                var def = PrefabDefault<ElectricBattery>(e);
                if (def == null) return;
                var b = owner == 0 ? 0 : Bonus(owner, "bigbatteries");
                battery.maxCapactiySeconds = def.maxCapactiySeconds * (float)(1 + b);
                return;
            }
            var gen = e as FuelGenerator;
            if (gen != null)
            {
                var def = PrefabDefault<FuelGenerator>(e);
                if (def == null) return;
                var b = owner == 0 ? 0 : Bonus(owner, "generator");
                gen.fuelPerSec = def.fuelPerSec * (float)Math.Max(0.05, 1 - b);
            }
        }

        /// <summary>Re-apply a player's power perks to everything they placed (after buying or resetting).</summary>
        private void RefreshOwned(ulong id)
        {
            foreach (var e in _power)
                if (e != null && e.OwnerID == id) ApplyPower(e, false);
        }

        private void OnEntityBuilt(Planner planner, GameObject go)
        {
            var player = planner == null ? null : planner.GetOwnerPlayer();
            var e = go == null ? null : go.ToBaseEntity();
            if (e == null || !Allowed(player)) return;
            if (e is GrowableEntity) EarnCapped(player, Gardening, _config.XpPlant);
            else if (e is IOEntity) EarnCapped(player, Electrician, _config.XpPlaceElectric);
        }

        private void OnWireConnect(BasePlayer player)
        {
            if (Allowed(player)) EarnCapped(player, Electrician, _config.XpWire);
        }

        #endregion

        #region Survival (Iron Stomach, Deep Breath, Regeneration)

        // Snapshot food, water and air before the game's metabolism tick and give part of the loss back after it.
        private class MetaSnap
        {
            public float Calories;
            public float Hydration;
            public float Oxygen;
            public float Delta;
        }

        private readonly Dictionary<ulong, MetaSnap> _meta = new Dictionary<ulong, MetaSnap>();

        private void OnRunPlayerMetabolism(PlayerMetabolism m, BaseCombatEntity entity, float delta)
        {
            var player = entity as BasePlayer;
            if (m == null || player == null || !Allowed(player)) return;
            if (Bonus(player.userID, "ironstomach") <= 0 && Bonus(player.userID, "deepbreath") <= 0 && Bonus(player.userID, "regeneration") <= 0) return;
            _meta[player.userID] = new MetaSnap { Calories = m.calories.value, Hydration = m.hydration.value, Oxygen = m.oxygen.value, Delta = delta };
        }

        private void OnPlayerMetabolize(PlayerMetabolism m, BaseCombatEntity entity, float delta)
        {
            var player = entity as BasePlayer;
            MetaSnap snap;
            if (m == null || player == null || !_meta.TryGetValue(player.userID, out snap)) return;
            _meta.Remove(player.userID);
            if (player.IsDead()) return;

            var stomach = Bonus(player.userID, "ironstomach");
            if (stomach > 0)
            {
                double saved = 0;
                if (m.calories.value < snap.Calories)
                {
                    var back = (snap.Calories - m.calories.value) * (float)stomach;
                    m.calories.Add(back);
                    saved += back;
                }
                if (m.hydration.value < snap.Hydration)
                {
                    var back = (snap.Hydration - m.hydration.value) * (float)stomach;
                    m.hydration.Add(back);
                    saved += back;
                }
                Note(player, "ironstomach", saved, false);
            }

            var breath = Bonus(player.userID, "deepbreath");
            if (breath > 0 && m.oxygen.value < snap.Oxygen)
                m.oxygen.Add((snap.Oxygen - m.oxygen.value) * (float)Math.Min(0.95, breath));

            var regen = Bonus(player.userID, "regeneration");
            if (regen > 0 && player.SecondsSinceAttacked > 10f && !player.IsWounded() && player.health < player.MaxHealth())
            {
                var heal = (float)Math.Min(regen / 60.0 * snap.Delta, player.MaxHealth() - player.health);
                player.Heal(heal);
                Note(player, "regeneration", heal, false);
            }
        }

        #endregion

        #region Mechanic (Fuel Saver, Tough Frame, Quick Fix)

        /// <summary>The machine an entity belongs to. Horses are vehicles to the game but not to the Mechanic skill.</summary>
        private static BaseVehicle VehicleOf(BaseEntity e)
        {
            for (var i = 0; e != null && i < 4; i++, e = e.GetParentEntity())
            {
                var v = e as BaseVehicle;
                if (v != null) return v is RidableHorse ? null : v;
            }
            return null;
        }

        // Same as EntityFuelSystem.TryUseFuel, with the driver's Fuel Saver taken off the burn rate.
        private object CanUseFuel(EntityFuelSystem fuel, StorageContainer container, float seconds, float fuelUsedPerSecond)
        {
            if (fuel == null || container == null || container.inventory == null) return null;
            var vehicle = VehicleOf(container);
            var driver = vehicle == null ? null : vehicle.GetDriver();
            if (driver == null || !Allowed(driver)) return null;
            var save = Bonus(driver.userID, "fuelsaver");
            if (save <= 0) return null;
            var slot = container.inventory.GetSlot(0);
            if (slot == null || slot.amount < 1) return 0;
            var burn = seconds * fuelUsedPerSecond;
            fuel.pendingFuel += burn * (float)Math.Max(0, 1 - save);
            Note(driver, "fuelsaver", burn * save, false);
            if (fuel.pendingFuel < 1f) return 0;
            var used = Mathf.FloorToInt(fuel.pendingFuel);
            slot.UseItem(used);
            fuel.pendingFuel -= used;
            return used;
        }

        private void OnStructureRepair(BaseCombatEntity entity, BasePlayer player)
        {
            if (entity == null || VehicleOf(entity) == null || !Allowed(player)) return;
            var before = entity.health;
            NextTick(() =>
            {
                if (entity == null || entity.IsDestroyed || player == null) return;
                var gained = entity.health - before;
                if (gained <= 0) return;
                Earn(player, Mechanic, _config.XpRepair);
                var fix = Bonus(player.userID, "quickfix");
                if (fix <= 0) return;
                var extra = Math.Min(gained * (float)fix, entity.MaxHealth() - entity.health);
                if (extra <= 0) return;
                entity.health += extra;
                entity.SendNetworkUpdate();
                Note(player, "quickfix", extra);
            });
        }

        #endregion

        #region Fishing (Big Catch, Steady Line, Rod Care)

        private void OnFishingRodCast(BaseFishingRod rod, BasePlayer player)
        {
            if (rod == null || !Allowed(player)) return;
            var def = PrefabDefault<BaseFishingRod>(rod);
            if (def == null) return;
            // Set on every cast so the rod always matches whoever is holding it now.
            rod.GlobalStrainSpeedMultiplier = def.GlobalStrainSpeedMultiplier * (float)Math.Max(0.05, 1 - Bonus(player.userID, "steadyline"));
            var care = (float)Math.Max(0, 1 - Bonus(player.userID, "rodcare"));
            rod.ConditionLossOnSuccess = def.ConditionLossOnSuccess * care;
            rod.ConditionLossOnFail = def.ConditionLossOnFail * care;
        }

        private void OnFishCatch(Item item, BaseFishingRod rod, BasePlayer player)
        {
            if (item == null || item.info == null || !Allowed(player)) return;
            ItemModFishable fishable;
            var junk = !item.info.TryGetComponent(out fishable) || fishable.IsJunk;
            Earn(player, Fishing, junk ? _config.XpFishJunk : _config.XpFish);
            if (junk) return;
            var chance = Bonus(player.userID, "bigcatch");
            if (chance <= 0 || UnityEngine.Random.value >= chance) return;
            var info = item.info;
            NextTick(() =>
            {
                if (player == null || !player.IsConnected) return;
                var extra = ItemManager.Create(info, 1);
                if (extra != null) player.GiveItem(extra, BaseEntity.GiveItemReason.Crafted);
                Note(player, "bigcatch", 1);
            });
        }

        #endregion

        #region Cooking (Big Portions, Master Brewer, Steeped)

        // Ovens don't say what they cooked, so count the raw food before and after each cook tick.
        // Raw food is anything that cooks into a ".cooked" item (meat and fish; burning cooked food doesn't count).
        private readonly Dictionary<BaseOven, Dictionary<ItemDefinition, int>> _rawBefore = new Dictionary<BaseOven, Dictionary<ItemDefinition, int>>();

        private static bool IsRawFood(Item item)
        {
            var info = item?.info?.ItemModCookable;
            return info != null && info.becomeOnCooked != null && item.info.category == ItemCategory.Food
                   && info.becomeOnCooked.shortname.EndsWith(".cooked");
        }

        private void OnOvenCook(BaseOven oven, Item fuel)
        {
            if (oven?.inventory == null) return;
            Dictionary<ItemDefinition, int> counts = null;
            foreach (var item in oven.inventory.itemList)
            {
                if (!IsRawFood(item)) continue;
                if (counts == null) counts = new Dictionary<ItemDefinition, int>();
                int n;
                counts.TryGetValue(item.info, out n);
                counts[item.info] = n + item.amount;
            }
            if (counts == null) _rawBefore.Remove(oven);
            else _rawBefore[oven] = counts;
        }

        private void OnOvenCooked(BaseOven oven, Item fuel, BaseEntity slot)
        {
            Dictionary<ItemDefinition, int> before;
            if (oven == null || !_rawBefore.TryGetValue(oven, out before)) return;
            _rawBefore.Remove(oven);
            var player = oven.LastLootedByPlayer;
            if (player == null || !player.IsConnected || !Allowed(player)) return;
            var cooked = 0;
            var extra = 0;
            var chance = Bonus(player.userID, "bigportions");
            foreach (var kv in before)
            {
                var now = 0;
                foreach (var item in oven.inventory.itemList)
                    if (item.info == kv.Key) now += item.amount;
                var done = kv.Value - now;
                if (done <= 0) continue;
                cooked += done;
                if (chance <= 0) continue;
                var bonus = 0;
                for (var i = 0; i < done; i++) if (UnityEngine.Random.value < chance) bonus++;
                if (bonus <= 0) continue;
                var output = ItemManager.Create(kv.Key.ItemModCookable.becomeOnCooked, bonus);
                if (output == null) continue;
                if (!output.MoveToContainer(oven.inventory)) output.Drop(oven.transform.position + Vector3.up, Vector3.up);
                extra += bonus;
            }
            if (cooked <= 0) return;
            Earn(player, Cooking, cooked * _config.XpCookMeat);
            if (extra > 0) Note(player, "bigportions", extra);
        }

        private void OnMixingTableFinished(MixingTable table, BasePlayer player, Recipe recipe, int quantity)
        {
            if (table == null || recipe == null || recipe.ProducedItem == null || !Allowed(player)) return;
            var made = Math.Max(1, quantity) * Math.Max(1, recipe.ProducedItemCount);
            Earn(player, Cooking, made * _config.XpBrew);
            var chance = Bonus(player.userID, "masterbrewer");
            if (chance <= 0) return;
            var bonus = 0;
            for (var i = 0; i < made; i++) if (UnityEngine.Random.value < chance) bonus++;
            if (bonus <= 0) return;
            var extra = ItemManager.Create(recipe.ProducedItem, bonus);
            if (extra == null) return;
            if (!extra.MoveToContainer(table.inventory)) player.GiveItem(extra, BaseEntity.GiveItemReason.Crafted);
            Note(player, "masterbrewer", bonus);
        }

        // Teas (and any food with a timed buff) last longer: add the buffs ourselves with a longer duration.
        private object OnPlayerAddModifiers(BasePlayer player, Item item, ItemModConsumable consumable)
        {
            if (player == null || consumable?.modifiers == null || consumable.modifiers.Count == 0 || !Allowed(player)) return null;
            var longer = Bonus(player.userID, "steeped");
            if (longer <= 0) return null;
            PlayerModifiers.AddToPlayer(player, consumable.modifiers, 1f, 1f + (float)longer);
            var added = consumable.modifiers.Max(m => m.duration) * longer;
            if (added >= 1) Note(player, "steeped", Math.Round(added));
            return false;
        }

        #endregion

        #region Scrapper (Quick Recycler, Bargain Research, Tech Tree Discount)

        // The recycler's think method is private; a delegate to it compares equal to the game's own,
        // so CancelInvoke/StopRecycling still find the restarted timer.
        private static readonly MethodInfo RecycleThinkMethod = typeof(Recycler).GetMethod("RecycleThink", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        private readonly Dictionary<Recycler, ulong> _recyclerUser = new Dictionary<Recycler, ulong>();
        private readonly Dictionary<ResearchTable, ulong> _researcher = new Dictionary<ResearchTable, ulong>();
        private readonly Dictionary<ulong, int> _scrapBeforeUnlock = new Dictionary<ulong, int>();

        private void OnRecyclerToggle(Recycler recycler, BasePlayer player)
        {
            if (recycler == null || !Allowed(player)) return;
            NextTick(() =>
            {
                if (recycler == null || recycler.IsDestroyed || !recycler.IsOn() || player == null) return;
                _recyclerUser[recycler] = player.userID;
                var speed = Bonus(player.userID, "quickrecycler");
                if (speed <= 0 || RecycleThinkMethod == null) return;
                float efficiency, duration;
                recycler.GetRecyclerStats(out efficiency, out duration);
                var think = (Action)Delegate.CreateDelegate(typeof(Action), recycler, RecycleThinkMethod);
                var faster = duration * (float)Math.Max(0.1, 1 - speed);
                recycler.CancelInvoke(think);
                recycler.InvokeRepeating(think, faster, faster);
                Note(player, "quickrecycler", 1);
            });
        }

        private void OnItemRecycle(Item item, Recycler recycler)
        {
            ulong id;
            if (recycler == null || !_recyclerUser.TryGetValue(recycler, out id)) return;
            var player = BasePlayer.FindByID(id);
            if (player == null || !player.IsConnected) { _recyclerUser.Remove(recycler); return; }
            Earn(player, Scrapper, _config.XpRecycle);
        }

        private void OnItemResearch(ResearchTable table, Item item, BasePlayer player)
        {
            if (table != null && Allowed(player)) _researcher[table] = player.userID;
        }

        private object OnItemResearched(ResearchTable table, int cost)
        {
            ulong id;
            if (table == null || !_researcher.TryGetValue(table, out id)) return null;
            _researcher.Remove(table);
            var player = BasePlayer.FindByID(id);
            if (player == null) return null;
            Earn(player, Scrapper, _config.XpResearch);
            var discount = Bonus(id, "bargain");
            if (discount <= 0 || cost <= 1) return null;
            var newCost = Math.Max(1, (int)Math.Round(cost * (1 - discount)));
            Note(player, "bargain", cost - newCost);
            return newCost;
        }

        private static int ScrapAmount(BasePlayer player)
        {
            var scrap = ItemManager.FindItemDefinition("scrap");
            return scrap == null ? 0 : player.inventory.GetAmount(scrap.itemid);
        }

        private void OnTechTreeNodeUnlock(Workbench bench, object node, BasePlayer player)
        {
            if (Allowed(player)) _scrapBeforeUnlock[player.userID] = ScrapAmount(player);
        }

        private void OnTechTreeNodeUnlocked(Workbench bench, object node, BasePlayer player)
        {
            int before;
            if (!Allowed(player) || !_scrapBeforeUnlock.TryGetValue(player.userID, out before)) return;
            _scrapBeforeUnlock.Remove(player.userID);
            Earn(player, Scrapper, _config.XpTechTree);
            var spent = before - ScrapAmount(player);
            var refund = (int)Math.Floor(spent * Bonus(player.userID, "techdiscount"));
            if (refund <= 0) return;
            var scrap = ItemManager.CreateByName("scrap", refund);
            if (scrap == null) return;
            player.GiveItem(scrap);
            Note(player, "techdiscount", refund);
        }

        #endregion

        #region Slow tick (survival time, underwater, driving, plant growth)

        private class PosSeen
        {
            public Vector3 Survive;
            public Vector3 Drive;
            public bool Driving;
            public float Seconds;
            public float Meters;
            public float Underwater;
        }

        private readonly Dictionary<ulong, PosSeen> _lastPos = new Dictionary<ulong, PosSeen>();

        private void SlowTick()
        {
            GrowTick();
            CheckEventEnd();
            foreach (var player in BasePlayer.activePlayerList)
            {
                if (player == null || player.IsDead() || player.IsSleeping() || !Allowed(player)) continue;
                var pos = player.transform.position;
                PosSeen seen;
                if (!_lastPos.TryGetValue(player.userID, out seen))
                {
                    _lastPos[player.userID] = new PosSeen { Survive = pos, Drive = pos };
                    continue;
                }

                // Survival: a little XP per minute alive, as long as you've moved (no AFK farming).
                seen.Seconds += 10f;
                if (seen.Seconds >= 60f)
                {
                    seen.Seconds = 0;
                    if ((pos - seen.Survive).sqrMagnitude > 30f * 30f) Earn(player, Survival, _config.XpSurvivalMinute);
                    seen.Survive = pos;
                }
                if (player.IsHeadUnderwater())
                {
                    seen.Underwater += 10f;
                    Earn(player, Survival, _config.XpUnderwater);
                }

                // Mechanic: XP for distance driven (teleports and respawns are ignored).
                var vehicle = player.isMounted ? VehicleOf(player.GetMounted()) : null;
                if (vehicle != null && vehicle.GetDriver() == player)
                {
                    if (seen.Driving)
                    {
                        var d = Vector3.Distance(pos, seen.Drive);
                        if (d < 600f) seen.Meters += d;
                        while (seen.Meters >= 100f)
                        {
                            seen.Meters -= 100f;
                            Earn(player, Mechanic, _config.XpDrive);
                        }
                    }
                    seen.Driving = true;
                }
                else seen.Driving = false;
                seen.Drive = pos;
            }
        }

        #endregion

        #region Chat command

        [ChatCommand("skills")]
        private void CmdSkills(BasePlayer player, string command, string[] args)
        {
            if (!Allowed(player)) { Reply(player, "NoPermission"); return; }
            var d = Get(player.userID);
            var action = args.Length > 0 ? args[0].ToLowerInvariant() : "";
            switch (action)
            {
                case "":
                case "menu":
                    OpenMenu(player);
                    return;
                case "info":
                    ShowOverview(player, d);
                    return;
                case "perks":
                    if (args.Length < 2) { Reply(player, "PerksAll"); return; }
                    var skill = ParseSkill(args[1]);
                    if (skill == null) { Reply(player, "UnknownSkill", args[1]); return; }
                    ShowPerks(player, d, skill);
                    return;
                case "buy":
                    if (args.Length < 2) { Reply(player, "Help"); return; }
                    bool bought;
                    Say(player, Buy(player, d, string.Join(" ", args.Skip(1)), out bought));
                    RefreshMenu(player);
                    return;
                case "reset":
                    if (args.Length < 2) { Reply(player, "Help"); return; }
                    var rs = ParseSkill(args[1]);
                    if (rs == null) { Reply(player, "UnknownSkill", args[1]); return; }
                    bool reset;
                    Say(player, Respec(player, d, rs, out reset));
                    RefreshMenu(player);
                    return;
                case "bonuses":
                case "bonus":
                    ShowBonuses(player, d);
                    return;
                case "popup":
                    MovePopup(player, d, args.Length > 1 ? args[1].ToLowerInvariant() : "", args.Length > 2 ? args[2] : null);
                    return;
                case "xp":
                    d.HideXp = !d.HideXp;
                    _dirty = true;
                    Reply(player, d.HideXp ? "XpPopupsOff" : "XpPopupsOn");
                    return;
                default:
                    Reply(player, "Help");
                    return;
            }
        }

        private static string ParseSkill(string s)
        {
            s = s.ToLowerInvariant();
            if (s.Length == 0) return null;
            foreach (var k in Skills)
                if (k == s) return k;
            foreach (var k in Skills)
                if (k.StartsWith(s)) return k;
            return null;
        }

        private void ShowOverview(BasePlayer player, PlayerData d)
        {
            var id = player.UserIDString;
            var sb = new StringBuilder();
            foreach (var skill in Skills)
            {
                var s = d.Skills[skill];
                var progress = s.Level >= _config.MaxLevel ? Msg("OverviewMax", id) : Msg("OverviewXp", id, Math.Floor(s.Xp), XpToNext(s.Level));
                sb.Append(Msg("OverviewLine", id, SkillName(skill, id), s.Level, progress, PointsFree(d, skill)));
            }
            if (IsSolo(player) && (_config.SoloXpBonus > 0 || _config.SoloExtraPointEvery > 0))
            {
                var pct = Math.Round(_config.SoloXpBonus * 100);
                sb.Append(_config.SoloExtraPointEvery > 0 ? Msg("SoloOn", id, pct, _config.SoloExtraPointEvery) : Msg("SoloOnNoPoints", id, pct));
            }
            Reply(player, "Overview", sb.ToString());
        }

        private void ShowPerks(BasePlayer player, PlayerData d, string skill)
        {
            var id = player.UserIDString;
            var sb = new StringBuilder(Msg("PerksHeader", id, SkillName(skill, id), PointsFree(d, skill)));
            string example = null;
            foreach (var p in Perks)
            {
                if (p.Skill != skill) continue;
                var cfg = PerkCfg(p.Id);
                int rank;
                d.Perks.TryGetValue(p.Id, out rank);
                string tail;
                if (cfg == null || !cfg.Enabled || cfg.MaxRank == 0) tail = Msg("PerkOff", id);
                else if (rank >= cfg.MaxRank) tail = Msg("PerkMaxed", id);
                else tail = Msg("PerkNext", id, RankCost(rank + 1));
                var desc = PerkDesc(p.Id, rank >= (cfg == null ? 0 : cfg.MaxRank) ? rank : rank + 1, id);
                sb.Append(Msg("PerkLine", id, PerkName(p.Id, id), p.Id, rank, cfg == null ? 0 : cfg.MaxRank, desc, tail));
                if (example == null) example = p.Id;
            }
            sb.Append(Msg("PerksFooter", id, example));
            Player.Message(player, sb.ToString(), _config.ChatPrefix, _config.ChatIcon);
        }

        private void MovePopup(BasePlayer player, PlayerData d, string dir, string stepText)
        {
            float x, y, step;
            if (stepText == null || !float.TryParse(stepText, out step) || step <= 0) step = 10;
            PopupSpot(player, out x, out y);
            switch (dir)
            {
                case "up": y += step; break;
                case "down": y -= step; break;
                case "left": x -= step; break;
                case "right": x += step; break;
                case "reset": d.PopupX = d.PopupY = null; break;
                case "": break;
                default: Reply(player, "PopupHelp"); return;
            }
            if (dir != "reset" && dir != "") { d.PopupX = x; d.PopupY = y; }
            _dirty = true;
            PopupSpot(player, out x, out y);
            var id = player.UserIDString;
            DrawPopup(player, new List<string> { Msg("XpPopup", id, 25, SkillName(Mining, id)), Msg("Pop.prospector", id, 12) });
            Popup p;
            if (!_popups.TryGetValue(player.userID, out p)) _popups[player.userID] = p = new Popup();
            p.Hide?.Destroy();
            p.Hide = timer.Once(Math.Max(_config.PopupSeconds, 6f), () => HidePopup(player));
            Reply(player, "PopupMoved", x, y);
        }

        private string TotalText(string perk, PlayerData d, string userId)
        {
            double total;
            d.Totals.TryGetValue(perk, out total);
            string amount;
            if (perk == "craftsman" || perk == "greenthumb") amount = FormatSeconds(total);
            else if (perk == "toolcare" || perk == "fuelsaver") amount = Math.Round(total, 1).ToString("0.#");
            else amount = Math.Round(total).ToString("N0");
            return Msg("Total." + perk, userId, amount);
        }

        private static string FormatSeconds(double total)
        {
            if (total >= 3600) return $"{Math.Floor(total / 3600)}h {Math.Floor(total % 3600 / 60)}m";
            if (total >= 60) return $"{Math.Floor(total / 60)}m {Math.Round(total % 60)}s";
            return $"{Math.Round(total)}s";
        }

        private void ShowBonuses(BasePlayer player, PlayerData d)
        {
            var id = player.UserIDString;
            var sb = new StringBuilder();
            foreach (var p in Perks)
            {
                int rank;
                if (!d.Perks.TryGetValue(p.Id, out rank) || rank <= 0 || !p.Tracked) continue;
                sb.Append(Msg("BonusesLine", id, PerkName(p.Id, id), rank, TotalText(p.Id, d, id)));
            }
            if (sb.Length == 0) { Reply(player, "BonusesNone"); return; }
            Reply(player, "BonusesHeader", sb.ToString());
        }

        private PerkDef ParsePerk(string text, string userId)
        {
            var t = text.Replace(" ", "").ToLowerInvariant();
            foreach (var p in Perks)
                if (p.Id == t || PerkName(p.Id, userId).Replace(" ", "").ToLowerInvariant() == t) return p;
            foreach (var p in Perks)
                if (p.Id.StartsWith(t)) return p;
            return null;
        }

        /// <summary>Unlock the next rank of a perk. Returns the message to show; ok says whether it worked.</summary>
        private string Buy(BasePlayer player, PlayerData d, string text, out bool ok)
        {
            ok = false;
            var id = player.UserIDString;
            var p = ParsePerk(text, id);
            if (p == null) return Msg("UnknownPerk", id, text);
            var cfg = PerkCfg(p.Id);
            var name = PerkName(p.Id, id);
            if (cfg == null || !cfg.Enabled || cfg.MaxRank == 0) return Msg("PerkDisabled", id, name);
            int rank;
            d.Perks.TryGetValue(p.Id, out rank);
            if (rank >= cfg.MaxRank) return Msg("PerkAlreadyMax", id, name);
            var cost = RankCost(rank + 1);
            var free = PointsFree(d, p.Skill);
            if (free < cost) return Msg("NotEnoughPoints", id, name, rank + 1, cost, SkillName(p.Skill, id), free);
            d.Perks[p.Id] = rank + 1;
            _dirty = true;
            ok = true;
            RefreshOwned(player.userID);
            return Msg("Bought", id, name, rank + 1, PerkDesc(p.Id, rank + 1, id));
        }

        /// <summary>Refund every point spent on one skill's perks. Returns the message to show.</summary>
        private string Respec(BasePlayer player, PlayerData d, string skill, out bool ok)
        {
            ok = false;
            var id = player.UserIDString;
            var spent = PointsSpent(d, skill);
            if (spent == 0) return Msg("ResetNothing", id, SkillName(skill, id));
            var wait = RespecWait(d);
            if (wait > TimeSpan.Zero) return Msg("ResetCooldown", id, FormatWait(wait));
            foreach (var p in Perks)
                if (p.Skill == skill) d.Perks.Remove(p.Id);
            d.LastRespec = DateTime.UtcNow;
            _dirty = true;
            ok = true;
            RefreshOwned(player.userID);
            return Msg("ResetDone", id, SkillName(skill, id), spent);
        }

        private TimeSpan RespecWait(PlayerData d)
        {
            if (_config.RespecCooldownHours <= 0) return TimeSpan.Zero;
            var wait = d.LastRespec.AddHours(_config.RespecCooldownHours) - DateTime.UtcNow;
            return wait > TimeSpan.Zero ? wait : TimeSpan.Zero;
        }

        private static string FormatWait(TimeSpan wait) =>
            wait.TotalHours >= 1 ? $"{(int)wait.TotalHours}h {wait.Minutes}m" : $"{Math.Max(1, wait.Minutes)}m";

        private void Say(BasePlayer player, string text) => Player.Message(player, text, _config.ChatPrefix, _config.ChatIcon);

        #endregion

        #region Menu

        private const string MenuUi = "SandboxSkills.Menu";
        private const string MenuMain = MenuUi + ".Main";
        private const string ColBg = "0.10 0.10 0.10 0.98";
        private const string ColHeader = "0.07 0.07 0.07 1";
        private const string ColCard = "0.17 0.17 0.17 0.95";
        private const string ColTabOn = "0.26 0.36 0.17 0.95";
        private const string ColGreen = "0.42 0.63 0.24 1";
        private const string ColGray = "0.30 0.30 0.30 0.95";
        private const string ColRed = "0.70 0.22 0.17 0.95";
        private const string ColBarBg = "0.06 0.06 0.06 1";
        private const string ColMuted = "0.75 0.75 0.75 0.9";

        private static readonly Dictionary<string, string> Icons = new Dictionary<string, string>
        {
            [Mining] = "pickaxe",
            [Woodcutting] = "hatchet",
            [Combat] = "longsword",
            ["prospector"] = "metal.ore",
            ["luckystrike"] = "sulfur.ore",
            ["toolcare"] = "hammer",
            ["lumberjack"] = "wood",
            ["craftsman"] = "workbench1",
            ["thickskin"] = "metal.facemask",
            ["hunter"] = "bow.hunting",
            ["fieldmedic"] = "syringe.medical",
            [Gardening] = "corn",
            [Electrician] = "wiretool",
            [Survival] = "bandage",
            [Mechanic] = "gears",
            ["greenthumb"] = "fertilizer",
            ["bountiful"] = "pumpkin",
            ["forager"] = "mushroom",
            ["solarboost"] = "electric.solarpanel.large",
            ["bigbatteries"] = "electric.battery.rechargable.large",
            ["generator"] = "electric.fuelgenerator.small",
            ["ironstomach"] = "black.raspberries",
            ["deepbreath"] = "diving.tank",
            ["regeneration"] = "largemedkit",
            ["fuelsaver"] = "lowgradefuel",
            ["toughframe"] = "sheetmetal",
            ["quickfix"] = "vehicle.1mod.engine",
            [Fishing] = "fishingrod.handmade",
            [Scrapper] = "scrap",
            ["bigcatch"] = "fish.salmon",
            ["steadyline"] = "fish.smallshark",
            ["rodcare"] = "fishing.tackle",
            ["quickrecycler"] = "scraptea",
            ["bargain"] = "research.table",
            ["techdiscount"] = "workbench2",
            [Cooking] = "bbq",
            ["bigportions"] = "meat.pork.cooked",
            ["masterbrewer"] = "mixingtable",
            ["steeped"] = "maxhealthtea.advanced",
        };

        private class MenuState
        {
            public string Skill = Mining;
            public bool ConfirmReset;
            public string Status;
            public bool StatusGood;
        }

        private readonly Dictionary<ulong, MenuState> _menus = new Dictionary<ulong, MenuState>();

        private void OpenMenu(BasePlayer player)
        {
            MenuState m;
            if (!_menus.TryGetValue(player.userID, out m)) _menus[player.userID] = m = new MenuState();
            m.ConfirmReset = false;
            m.Status = null;
            DrawMenu(player, m);
        }

        private void CloseMenu(BasePlayer player)
        {
            _menus.Remove(player.userID);
            CuiHelper.DestroyUi(player, MenuUi);
        }

        private void RefreshMenu(BasePlayer player)
        {
            MenuState m;
            if (player != null && player.IsConnected && _menus.TryGetValue(player.userID, out m)) DrawMenu(player, m);
        }

        [ConsoleCommand("skills.open")]
        private void CcOpenMenu(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null) return;
            if (!Allowed(player)) { Reply(player, "NoPermission"); return; }
            if (_menus.ContainsKey(player.userID)) CloseMenu(player);
            else OpenMenu(player);
        }

        [ConsoleCommand("sandboxskills.ui")]
        private void CcMenu(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            MenuState m;
            if (player == null || !Allowed(player) || !_menus.TryGetValue(player.userID, out m)) return;
            var d = Get(player.userID);
            bool ok;
            switch (arg.GetString(0))
            {
                case "close":
                    CloseMenu(player);
                    return;
                case "tab":
                    var skill = ParseSkill(arg.GetString(1, "-"));
                    if (skill == null) return;
                    m.Skill = skill;
                    m.ConfirmReset = false;
                    m.Status = null;
                    break;
                case "buy":
                    m.Status = Buy(player, d, arg.GetString(1, ""), out ok);
                    m.StatusGood = ok;
                    m.ConfirmReset = false;
                    if (ok) PlaySound(player, "assets/prefabs/deployable/research table/effects/research-success.prefab");
                    break;
                case "reset":
                    if (!m.ConfirmReset)
                    {
                        m.ConfirmReset = true;
                        m.Status = Msg("UiResetAsk", player.UserIDString, PointsSpent(d, m.Skill), SkillName(m.Skill, player.UserIDString), _config.RespecCooldownHours);
                        m.StatusGood = false;
                        break;
                    }
                    m.Status = Respec(player, d, m.Skill, out ok);
                    m.StatusGood = ok;
                    m.ConfirmReset = false;
                    break;
                case "popups":
                    d.HideXp = !d.HideXp;
                    _dirty = true;
                    break;
                default:
                    return;
            }
            DrawMenu(player, m);
        }

        private static void PlaySound(BasePlayer player, string prefab)
        {
            if (player?.net?.connection == null) return;
            var effect = new Effect(prefab, player, 0, Vector3.zero, Vector3.forward);
            EffectNetwork.Send(effect, player.net.connection);
        }

        // ---- drawing helpers: every box is placed in pixels from its parent's bottom-left corner

        private static void Box(CuiElementContainer c, string parent, string color, float x0, float y0, float x1, float y1, string name = null)
        {
            c.Add(new CuiPanel
            {
                Image = { Color = color },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "0 0", OffsetMin = $"{x0} {y0}", OffsetMax = $"{x1} {y1}" },
            }, parent, name);
        }

        private static void Label(CuiElementContainer c, string parent, string text, int size, float x0, float y0, float x1, float y1,
            TextAnchor align = TextAnchor.MiddleLeft, string color = "1 1 1 0.95", bool bold = true)
        {
            c.Add(new CuiLabel
            {
                Text = { Text = text, FontSize = size, Align = align, Color = color, Font = bold ? "robotocondensed-bold.ttf" : "robotocondensed-regular.ttf" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "0 0", OffsetMin = $"{x0} {y0}", OffsetMax = $"{x1} {y1}" },
            }, parent);
        }

        private static void Button(CuiElementContainer c, string parent, string text, int size, string command, string color,
            float x0, float y0, float x1, float y1, string name = null, string textColor = "1 1 1 0.95")
        {
            c.Add(new CuiButton
            {
                Button = { Command = command ?? "", Color = color },
                Text = { Text = text, FontSize = size, Align = TextAnchor.MiddleCenter, Color = textColor, Font = "robotocondensed-bold.ttf" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "0 0", OffsetMin = $"{x0} {y0}", OffsetMax = $"{x1} {y1}" },
            }, parent, name);
        }

        private static void Icon(CuiElementContainer c, string parent, string key, float x0, float y0, float x1, float y1)
        {
            string shortname;
            if (!Icons.TryGetValue(key, out shortname)) return;
            var def = ItemManager.FindItemDefinition(shortname);
            if (def == null) return;
            c.Add(new CuiElement
            {
                Parent = parent,
                Components =
                {
                    new CuiImageComponent { ItemId = def.itemid },
                    new CuiRectTransformComponent { AnchorMin = "0 0", AnchorMax = "0 0", OffsetMin = $"{x0} {y0}", OffsetMax = $"{x1} {y1}" },
                },
            });
        }

        // ---- the menu: 860x500, centered. Skills on the left, the selected skill's perks on the right.

        private void DrawMenu(BasePlayer player, MenuState m)
        {
            var id = player.UserIDString;
            var d = Get(player.userID);
            var c = new CuiElementContainer();

            c.Add(new CuiPanel
            {
                Image = { Color = "0 0 0 0.6" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" },
                CursorEnabled = true,
            }, "Overlay", MenuUi);
            // Clicking the dark area around the window closes it.
            c.Add(new CuiButton
            {
                Button = { Command = "sandboxskills.ui close", Color = "0 0 0 0" },
                Text = { Text = "" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1" },
            }, MenuUi);
            c.Add(new CuiPanel
            {
                Image = { Color = ColBg },
                RectTransform = { AnchorMin = "0.5 0.5", AnchorMax = "0.5 0.5", OffsetMin = "-430 -250", OffsetMax = "430 250" },
            }, MenuUi, MenuMain);

            // Header
            Box(c, MenuMain, ColHeader, 0, 452, 860, 500);
            Label(c, MenuMain, Msg("UiTitle", id), 20, 20, 452, 260, 500);
            string bonus;
            if (IsSolo(player))
            {
                var pts = _config.SoloExtraPointEvery > 0 ? Msg("UiSoloPoints", id, _config.SoloExtraPointEvery) : "";
                bonus = _config.SoloXpBonus > 0 || pts.Length > 0 ? Msg("UiSolo", id, Math.Round(_config.SoloXpBonus * 100), pts) : "";
            }
            else bonus = _config.TeamXpShare > 0 ? Msg("UiTeam", id, Math.Round(_config.TeamShareRange), Math.Round(_config.TeamXpShare * 100)) : "";
            var ev = EventText(id);
            if (ev.Length > 0) bonus = bonus.Length > 0 ? ev + "    " + bonus : ev;
            Label(c, MenuMain, bonus, 13, 260, 452, 800, 500, TextAnchor.MiddleRight, ColMuted);
            Button(c, MenuMain, "X", 16, "sandboxskills.ui close", ColRed, 812, 462, 848, 490);

            // Skill tabs: one compact row each, squeezed to fit above the footer
            var rowH = Skills.Length > 9 ? 34f : 37f;
            for (var i = 0; i < Skills.Length; i++)
            {
                var skill = Skills[i];
                var sd = d.Skills[skill];
                var top = 436f - i * (rowH + (Skills.Length > 9 ? 3f : 4f));
                var tab = MenuMain + ".Tab" + i;
                Button(c, MenuMain, "", 1, "sandboxskills.ui tab " + skill, skill == m.Skill ? ColTabOn : ColCard, 16, top - rowH, 276, top, tab);
                Icon(c, tab, skill, 5, 3, 5 + rowH - 6, rowH - 3);
                Label(c, tab, SkillName(skill, id), 13, 42, 16, 170, rowH - 1);
                var free = PointsFree(d, skill);
                Label(c, tab, free > 0 ? Msg("UiTabPoints", id, free, sd.Level) : Msg("UiTabLevel", id, sd.Level), 11, 140, 16, 254, rowH - 1,
                    TextAnchor.MiddleRight, "1 0.83 0.47 1");
                var maxed = sd.Level >= _config.MaxLevel;
                var need = XpToNext(sd.Level);
                var frac = maxed ? 1f : (float)Math.Max(0, Math.Min(1, sd.Xp / need));
                Box(c, tab, ColBarBg, 42, 4, 254, 15);
                if (frac > 0) Box(c, tab, ColGreen, 42, 4, 42 + 212 * frac, 15);
                Label(c, tab, maxed ? Msg("UiMaxLevel", id) : Msg("UiXp", id, Math.Floor(sd.Xp).ToString("N0"), need.ToString("N0")), 8, 42, 4, 254, 15, TextAnchor.MiddleCenter);
            }

            // Left footer
            Button(c, MenuMain, Msg(d.HideXp ? "UiPopupsOff" : "UiPopupsOn", id), 12, "sandboxskills.ui popups", ColGray, 16, 20, 142, 56);
            Button(c, MenuMain, Msg("UiClose", id), 12, "sandboxskills.ui close", ColGray, 150, 20, 276, 56);

            // Perks of the selected skill
            var freePts = PointsFree(d, m.Skill);
            Label(c, MenuMain, Msg("UiPerksTitle", id, SkillName(m.Skill, id)), 18, 292, 404, 600, 436);
            Label(c, MenuMain, freePts > 0 ? Msg("UiPoints", id, freePts) : Msg("UiNoPoints", id), 14, 600, 404, 844, 436,
                TextAnchor.MiddleRight, freePts > 0 ? "0.48 0.85 0.56 1" : ColMuted);
            var j = 0;
            foreach (var p in Perks)
            {
                if (p.Skill != m.Skill) continue;
                var top = 396f - j * 114;
                DrawPerkCard(c, MenuMain + ".Perk" + j, p, d, freePts, id, top);
                j++;
            }

            // Right footer: what just happened, the reset button and a key-bind tip
            if (!string.IsNullOrEmpty(m.Status))
                Label(c, MenuMain, m.Status, 12, 292, 34, 670, 58, TextAnchor.MiddleLeft, m.StatusGood ? "0.48 0.85 0.56 1" : "1 0.62 0.45 1");
            Label(c, MenuMain, Msg("UiTip", id), 10, 292, 12, 670, 32, TextAnchor.MiddleLeft, "0.6 0.6 0.6 0.9", false);
            if (PointsSpent(d, m.Skill) > 0)
            {
                var wait = RespecWait(d);
                if (wait > TimeSpan.Zero)
                    Button(c, MenuMain, Msg("UiResetWait", id, FormatWait(wait)), 11, null, ColGray, 680, 20, 844, 56, null, ColMuted);
                else
                    Button(c, MenuMain, m.ConfirmReset ? Msg("UiResetConfirm", id) : Msg("UiReset", id, SkillName(m.Skill, id)), 12,
                        "sandboxskills.ui reset", m.ConfirmReset ? ColRed : ColGray, 680, 20, 844, 56);
            }

            CuiHelper.DestroyUi(player, MenuUi);
            CuiHelper.AddUi(player, c);
        }

        private void DrawPerkCard(CuiElementContainer c, string card, PerkDef p, PlayerData d, int freePts, string id, float top)
        {
            var cfg = PerkCfg(p.Id);
            var max = cfg == null ? 0 : cfg.MaxRank;
            var on = cfg != null && cfg.Enabled && max > 0;
            int rank;
            d.Perks.TryGetValue(p.Id, out rank);
            rank = Math.Min(rank, max);

            Box(c, MenuMain, ColCard, 292, top - 104, 844, top, card);
            Icon(c, card, p.Id, 12, 20, 76, 84);
            Label(c, card, PerkName(p.Id, id), 16, 88, 74, 380, 98);
            for (var r = 0; r < max; r++)
                Box(c, card, r < rank ? ColGreen : "0.32 0.32 0.32 1", 88 + r * 22, 62, 106 + r * 22, 70);
            Label(c, card, Msg("UiRank", id, rank, max), 11, 94 + max * 22, 56, 260, 76, TextAnchor.MiddleLeft, ColMuted);
            Label(c, card, rank > 0 ? Msg("UiNow", id, PerkDesc(p.Id, rank, id)) : Msg("UiNotYet", id), 12, 88, 32, 390, 54,
                TextAnchor.MiddleLeft, rank > 0 ? "1 1 1 0.95" : ColMuted);
            Label(c, card, rank < max ? Msg("UiNext", id, PerkDesc(p.Id, rank + 1, id)) : Msg("UiAllRanks", id), 12, 88, 8, 390, 30,
                TextAnchor.MiddleLeft, ColMuted, false);

            if (!on)
                Button(c, card, Msg("UiOff", id), 12, null, ColGray, 400, 46, 540, 86, null, ColMuted);
            else if (rank >= max)
                Button(c, card, Msg("UiMaxedBtn", id), 13, null, ColTabOn, 400, 46, 540, 86);
            else
            {
                var cost = RankCost(rank + 1);
                if (freePts >= cost)
                    Button(c, card, Msg("UiBuy", id, rank + 1, cost), 12, "sandboxskills.ui buy " + p.Id, ColGreen, 400, 46, 540, 86);
                else
                    Button(c, card, Msg("UiNeed", id, cost), 12, null, ColGray, 400, 46, 540, 86, null, ColMuted);
            }
            if (rank > 0 && p.Tracked)
                Label(c, card, Msg("UiSoFar", id, TotalText(p.Id, d, id)), 11, 330, 10, 540, 36, TextAnchor.MiddleRight, "1 0.83 0.47 1", false);
        }

        #endregion

        #region Manager commands (RCON)

        private static bool IsAdminArg(ConsoleSystem.Arg arg) => arg.Connection == null || arg.Connection.authLevel >= 2;

        private object PlayerJson(ulong id, PlayerData d)
        {
            return new
            {
                id = id.ToString(),
                name = d.Name,
                total = d.Skills.Values.Sum(x => x.Level),
                online = BasePlayer.FindByID(id) != null,
                skills = Skills.ToDictionary(s => s, s => new
                {
                    level = d.Skills[s].Level,
                    xp = Math.Floor(d.Skills[s].Xp),
                    next = d.Skills[s].Level >= _config.MaxLevel ? 0 : XpToNext(d.Skills[s].Level),
                    points = PointsFree(d, s),
                }),
                perks = d.Perks.Where(kv => kv.Value > 0).ToDictionary(kv => kv.Key, kv => kv.Value),
                totals = d.Totals,
            };
        }

        [ConsoleCommand("sandboxskills.player")]
        private void CcPlayer(ConsoleSystem.Arg arg)
        {
            if (!IsAdminArg(arg)) return;
            ulong id;
            PlayerData d;
            if (!ulong.TryParse(arg.GetString(0), out id) || !_data.TryGetValue(id, out d)) { arg.ReplyWith("{}"); return; }
            Get(id);
            arg.ReplyWith(JsonConvert.SerializeObject(PlayerJson(id, d)));
        }

        /// <summary>sandboxskills.top [count] [skill] - best players overall (sum of levels) or in one skill.</summary>
        [ConsoleCommand("sandboxskills.top")]
        private void CcTop(ConsoleSystem.Arg arg)
        {
            if (!IsAdminArg(arg)) return;
            var count = Math.Max(1, Math.Min(500, arg.GetInt(0, 10)));
            var skill = ParseSkill(arg.GetString(1, ""));
            foreach (var id in _data.Keys.ToList()) Get(id);
            var ordered = skill == null
                ? _data.OrderByDescending(kv => kv.Value.Skills.Values.Sum(x => x.Level)).ThenByDescending(kv => kv.Value.Skills.Values.Sum(x => x.Xp))
                : _data.OrderByDescending(kv => kv.Value.Skills[skill].Level).ThenByDescending(kv => kv.Value.Skills[skill].Xp);
            arg.ReplyWith(JsonConvert.SerializeObject(ordered.Take(count).Select(kv => PlayerJson(kv.Key, kv.Value))));
        }

        /// <summary>sandboxskills.summary - counts for the manager's dashboard.</summary>
        [ConsoleCommand("sandboxskills.summary")]
        private void CcSummary(ConsoleSystem.Arg arg)
        {
            if (!IsAdminArg(arg)) return;
            foreach (var id in _data.Keys.ToList()) Get(id);
            var players = _data.Values.ToList();
            arg.ReplyWith(JsonConvert.SerializeObject(new
            {
                version = Version.ToString(),
                players = players.Count,
                maxLevel = _config.MaxLevel,
                xpRate = XpEventRate,
                xpMinutesLeft = XpEventRate > 1 && _event.Until != null ? (double?)Math.Round((_event.Until.Value - DateTime.UtcNow).TotalMinutes) : null,
                skills = Skills,
                avgLevel = Skills.ToDictionary(s => s, s => players.Count == 0 ? 0 : Math.Round(players.Average(p => p.Skills[s].Level), 1)),
                perkOwners = Perks.ToDictionary(p => p.Id, p => players.Count(x => { int r; return x.Perks.TryGetValue(p.Id, out r) && r > 0; })),
            }));
        }

        /// <summary>sandboxskills.xprate - show the XP event; sandboxskills.xprate 2 2880 - double XP for 48 hours; sandboxskills.xprate 1 - stop.</summary>
        [ConsoleCommand("sandboxskills.xprate")]
        private void CcXpRate(ConsoleSystem.Arg arg)
        {
            if (!IsAdminArg(arg)) return;
            if (arg.HasArgs(1))
            {
                var rate = Math.Max(1, Math.Min(10, arg.GetFloat(0, 1)));
                SetEvent(rate, Math.Max(0, arg.GetFloat(1, 0)));
            }
            var left = XpEventRate > 1 && _event.Until != null ? (double?)Math.Round((_event.Until.Value - DateTime.UtcNow).TotalMinutes) : null;
            arg.ReplyWith(JsonConvert.SerializeObject(new { xpRate = XpEventRate, xpMinutesLeft = left }));
        }

        [ConsoleCommand("sandboxskills.givexp")]
        private void CcGiveXp(ConsoleSystem.Arg arg)
        {
            if (!IsAdminArg(arg)) return;
            var who = arg.GetString(0);
            var skill = ParseSkill(arg.GetString(1, ""));
            var amount = arg.GetFloat(2, 0);
            var player = BasePlayer.Find(who);
            ulong id;
            if (player != null) id = player.userID;
            else if (!ulong.TryParse(who, out id) || !_data.ContainsKey(id)) id = 0;
            if (id == 0 || skill == null || amount <= 0)
            {
                arg.ReplyWith("Usage: sandboxskills.givexp <steamid or name> <skill> <amount>. Skills: " + string.Join(", ", Skills) + ". Offline players need their Steam ID.");
                return;
            }
            AddXp(id, player, skill, amount);
            var s = Get(id).Skills[skill];
            arg.ReplyWith($"{Get(id).Name}: {skill} level {s.Level}, {Math.Floor(s.Xp)} XP");
        }

        [ConsoleCommand("sandboxskills.reset")]
        private void CcReset(ConsoleSystem.Arg arg)
        {
            if (!IsAdminArg(arg)) return;
            ulong id;
            if (!ulong.TryParse(arg.GetString(0), out id) || !_data.Remove(id)) { arg.ReplyWith("No skills saved for that Steam ID."); return; }
            RefreshOwned(id);
            _dirty = true;
            arg.ReplyWith("Skills reset for " + id + ".");
        }

        #endregion
    }
}
