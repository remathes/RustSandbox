// Rust Sandbox — Store
// Rust Credits (RC): a server currency players earn for kills, gathering (wood, stone, ore), farming, picking
// things up, fishing and keycard puzzles. They spend it at shopkeeper NPCs an admin places in the world:
// Guns & Ammo, the Clothes store, the Hardware store and the Farmers Market out of the box. Pressing E on a
// shopkeeper opens their store: pages of items with a cart on the right and a checkout button.
//
// Install: copy this file to oxide/plugins (or carbon/plugins). Shops, prices and earning rates live in
// oxide/config/RustSandboxStore.json and are easiest to edit on the Rust Sandbox "Store" page.
// Admin chat commands:
//   /store build [shops]           build a wooden strip mall in front of you: one shop per store with a counter, the
//                                  shopkeeper, guns on wall racks, clothes on mannequins, displays and a neon sign
//   /store unbuild | rebuild       remove the nearest strip mall | rebuild them all with the current displays
//   /store add <shop|all> [name]   place a shopkeeper where you stand, facing you ("all" places one of each in a row)
//   /store remove                  remove the shopkeeper next to you
//   /store list                    list shopkeepers
//   /store move <id>               move a shopkeeper to where you stand
//   /store outfit <id> [clear]     dress a shopkeeper in what you're wearing
//   /store open <shop>             open a store anywhere (for testing)
//   /credits give|take|set <player> <amount>
// Players: /credits (balance and where the stores are), /credits pay <player> <amount>.
// A shopkeeper placed inside a monument (like Outpost) is saved relative to it, so it comes back at the same
// spot after a wipe, even on a new map. Anywhere else it is saved for this map only.

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
    [Info("Rust Sandbox Store", "RustSandbox", "1.4.0")]
    [Description("Rust Credits earned for kills, gathering, farming, fishing and keycards, spent at NPC shopkeepers with a paged store, cart and checkout.")]
    public class RustSandboxStore : RustPlugin
    {
        #region Config

        private PluginConfig _config;

        private class StoreItem
        {
            [JsonProperty("Item")] public string Item = "";
            [JsonProperty("Amount")] public int Amount = 1;
            [JsonProperty("Price")] public int Price = 10;
            [JsonProperty("Category")] public string Category = "";
            [JsonProperty("Skin")] public ulong Skin;
            [JsonProperty("Blueprint")] public bool Blueprint;
            [JsonProperty("Name")] public string Name = "";
        }

        private class Shop
        {
            [JsonProperty("Id")] public string Id = "";
            [JsonProperty("Name")] public string Name = "";
            [JsonProperty("Keeper")] public string Keeper = "";
            [JsonProperty("Look")] public string Look = "outpost_a";
            [JsonProperty("Color")] public string Color = "0.85 0.62 0.20";
            [JsonProperty("Enabled")] public bool Enabled = true;
            [JsonProperty("Items", ObjectCreationHandling = ObjectCreationHandling.Replace)] public List<StoreItem> Items = new List<StoreItem>();
            // What the strip mall shows in this shop. Each entry is one display: a gun or tool goes on a wall rack,
            // a comma list of clothes dresses a mannequin, anything else that can be placed (a solar panel, a planter...) stands on the floor.
            [JsonProperty("Displays", ObjectCreationHandling = ObjectCreationHandling.Replace)] public List<string> Displays = new List<string>();
            // An open-air stall instead of a closed shop: no front, side or back walls and no roof. A seed in Displays
            // becomes a planter of ripe crops and a comma list of food is laid out on a market table.
            [JsonProperty("Outdoor")] public bool? Outdoor;
            // The neon sign's picture as a base64 PNG or JPG, made on the manager's Store page. Blank = the built-in one.
            [JsonProperty("Sign")] public string Sign = "";
        }

        private class EarnSettings
        {
            [JsonProperty("PlayerKill")] public double PlayerKill = 25;
            [JsonProperty("PlayerKillCooldownMinutes")] public double PlayerKillCooldownMinutes = 10;
            [JsonProperty("NpcKill")] public double NpcKill = 8;
            [JsonProperty("AnimalKill")] public double AnimalKill = 3;
            [JsonProperty("Kills", ObjectCreationHandling = ObjectCreationHandling.Replace)] public Dictionary<string, double> Kills = new Dictionary<string, double>
            {
                ["chicken"] = 1, ["boar"] = 3, ["stag"] = 3, ["wolf"] = 4, ["bear"] = 6, ["polarbear"] = 8,
                ["scientistnpc_heavy"] = 15, ["bradleyapc"] = 150, ["patrolhelicopter"] = 250,
            };
            [JsonProperty("GatherPer100", ObjectCreationHandling = ObjectCreationHandling.Replace)] public Dictionary<string, double> GatherPer100 = new Dictionary<string, double>
            {
                ["wood"] = 1.5, ["stones"] = 2, ["metal.ore"] = 3, ["sulfur.ore"] = 4, ["hq.metal.ore"] = 8,
            };
            [JsonProperty("FarmHarvest")] public double FarmHarvest = 1;
            [JsonProperty("Pickup")] public double Pickup = 0.5;
            [JsonProperty("Fish")] public double Fish = 4;
            [JsonProperty("FishJunk")] public double FishJunk = 1;
            [JsonProperty("Keycards", ObjectCreationHandling = ObjectCreationHandling.Replace)] public Dictionary<string, double> Keycards = new Dictionary<string, double>
            {
                ["green"] = 15, ["blue"] = 35, ["red"] = 75,
            };
            [JsonProperty("KeycardCooldownMinutes")] public double KeycardCooldownMinutes = 30;
        }

        private class PluginConfig
        {
            [JsonProperty("Enabled")] public bool Enabled = true;
            // Bumped when an update adds to existing configs (2: melee weapons in Guns N Ammo).
            [JsonProperty("ConfigVersion")] public int ConfigVersion;
            [JsonProperty("CurrencyName")] public string CurrencyName = "Rust Credits";
            [JsonProperty("Short")] public string Short = "RC";
            [JsonProperty("StartingBalance")] public double StartingBalance = 50;
            [JsonProperty("ShowEarnings")] public bool ShowEarnings = true;
            [JsonProperty("EarningsAnchor")] public string EarningsAnchor = "0.5 1";
            [JsonProperty("EarningsOffset")] public string EarningsOffset = "-90 -78";
            [JsonProperty("AllowPay")] public bool AllowPay = true;
            [JsonProperty("ResetOnWipe")] public bool ResetOnWipe = false;
            [JsonProperty("MapMarkers")] public bool MapMarkers = true;
            [JsonProperty("MaxPerLine")] public int MaxPerLine = 20;
            [JsonProperty("BuildGrade")] public string BuildGrade = "Wood";
            [JsonProperty("FlipWallRacks")] public bool FlipWallRacks;
            [JsonProperty("NeonSigns")] public bool NeonSigns = true;
            [JsonProperty("FlipSigns")] public bool FlipSigns;
            [JsonProperty("ChatPrefix")] public string ChatPrefix = "<color=#f0c040>[Store]</color>";
            [JsonProperty("Earn")] public EarnSettings Earn = new EarnSettings();
            [JsonProperty("Shops", ObjectCreationHandling = ObjectCreationHandling.Replace)] public List<Shop> Shops = DefaultShops();
        }

        private static StoreItem I(string category, string item, int amount, int price) =>
            new StoreItem { Category = category, Item = item, Amount = amount, Price = price };

        private static List<Shop> DefaultShops() => new List<Shop>
        {
            new Shop
            {
                Id = "guns", Name = "Guns N Ammo", Keeper = "Gunny", Look = "bandit_b", Color = "0.78 0.27 0.22",
                Items = new List<StoreItem>
                {
                    I("Weapons", "bow.hunting", 1, 40), I("Weapons", "crossbow", 1, 90), I("Weapons", "bow.compound", 1, 160),
                    I("Weapons", "pistol.revolver", 1, 150), I("Weapons", "pistol.semiauto", 1, 250), I("Weapons", "pistol.python", 1, 400),
                    I("Weapons", "shotgun.waterpipe", 1, 120), I("Weapons", "shotgun.double", 1, 220), I("Weapons", "shotgun.pump", 1, 550),
                    I("Weapons", "smg.2", 1, 600), I("Weapons", "smg.thompson", 1, 650), I("Weapons", "smg.mp5", 1, 900),
                    I("Weapons", "rifle.semiauto", 1, 700), I("Weapons", "rifle.bolt", 1, 1600), I("Weapons", "rifle.ak", 1, 1800),
                    I("Weapons", "rifle.lr300", 1, 1900),
                    I("Ammo", "arrow.wooden", 20, 15), I("Ammo", "arrow.hv", 20, 30), I("Ammo", "ammo.handmade.shell", 20, 20),
                    I("Ammo", "ammo.shotgun", 20, 45), I("Ammo", "ammo.shotgun.slug", 10, 45), I("Ammo", "ammo.pistol", 50, 40),
                    I("Ammo", "ammo.pistol.hv", 50, 60), I("Ammo", "ammo.rifle", 50, 90), I("Ammo", "ammo.rifle.hv", 50, 120),
                    I("Attachments", "weapon.mod.flashlight", 1, 40), I("Attachments", "weapon.mod.simplesight", 1, 60),
                    I("Attachments", "weapon.mod.lasersight", 1, 120), I("Attachments", "weapon.mod.muzzleboost", 1, 120),
                    I("Attachments", "weapon.mod.silencer", 1, 150), I("Attachments", "weapon.mod.holosight", 1, 200),
                    I("Attachments", "weapon.mod.small.scope", 1, 250),
                    I("Explosives", "grenade.smoke", 2, 40), I("Explosives", "grenade.beancan", 3, 90), I("Explosives", "grenade.f1", 2, 120),
                }.Concat(MeleeItems()).ToList(),
                Displays = new List<string>
                {
                    "rifle.ak", "rifle.lr300", "rifle.bolt", "smg.mp5", "smg.thompson", "smg.2", "rifle.semiauto", "shotgun.pump",
                    "shotgun.double", "shotgun.waterpipe", "pistol.python", "pistol.revolver", "pistol.semiauto", "crossbow", "bow.compound",
                    "box.wooden.large", "box.wooden.large",
                }.Concat(MeleeDisplays).ToList(),
            },
            new Shop
            {
                Id = "clothes", Name = "Clothes Shop", Keeper = "Stitch", Look = "outpost_b", Color = "0.30 0.55 0.78",
                Items = new List<StoreItem>
                {
                    I("Basics", "burlap.shirt", 1, 6), I("Basics", "burlap.trousers", 1, 6), I("Basics", "tshirt", 1, 10),
                    I("Basics", "tshirt.long", 1, 12), I("Basics", "hoodie", 1, 25), I("Basics", "pants", 1, 25),
                    I("Basics", "shoes.boots", 1, 25), I("Basics", "hat.cap", 1, 10), I("Basics", "hat.beenie", 1, 10),
                    I("Basics", "mask.bandana", 1, 8), I("Basics", "sunglasses", 1, 15), I("Basics", "burlap.gloves", 1, 6),
                    I("Armor", "bucket.helmet", 1, 25), I("Armor", "wood.armor.jacket", 1, 50), I("Armor", "wood.armor.pants", 1, 40),
                    I("Armor", "coffeecan.helmet", 1, 90), I("Armor", "roadsign.jacket", 1, 140), I("Armor", "roadsign.kilt", 1, 110),
                    I("Armor", "roadsign.gloves", 1, 60), I("Armor", "tactical.gloves", 1, 120), I("Armor", "riot.helmet", 1, 120),
                    I("Armor", "metal.plate.torso", 1, 400), I("Armor", "metal.facemask", 1, 450),
                    I("Outdoor", "attire.hide.vest", 1, 10), I("Outdoor", "attire.hide.pants", 1, 10), I("Outdoor", "jacket", 1, 35),
                    I("Outdoor", "jacket.snow", 1, 60), I("Outdoor", "hat.miner", 1, 40), I("Outdoor", "hazmatsuit", 1, 250),
                    I("Outdoor", "nightvisiongoggles", 1, 300),
                },
                Displays = new List<string>
                {
                    "metal.facemask,metal.plate.torso,hoodie,pants,shoes.boots,tactical.gloves",
                    "coffeecan.helmet,roadsign.jacket,roadsign.kilt,tshirt.long,pants,shoes.boots",
                    "hat.cap,sunglasses,hoodie,pants,shoes.boots",
                    "hat.beenie,jacket.snow,pants,shoes.boots",
                    "hazmatsuit",
                    "hat.miner,jacket,pants,shoes.boots",
                },
            },
            new Shop
            {
                Id = "hardware", Name = "Hardware Store", Keeper = "Bolt", Look = "outpost_a", Color = "0.85 0.62 0.20",
                Items = new List<StoreItem>
                {
                    I("Tools", "building.planner", 1, 10), I("Tools", "hammer", 1, 10), I("Tools", "torch", 1, 5),
                    I("Tools", "flashlight.held", 1, 20), I("Tools", "hatchet", 1, 60), I("Tools", "pickaxe", 1, 70),
                    I("Tools", "axe.salvaged", 1, 150), I("Tools", "icepick.salvaged", 1, 160), I("Tools", "chainsaw", 1, 450),
                    I("Tools", "jackhammer", 1, 500),
                    I("Building", "wood", 1000, 15), I("Building", "stones", 1000, 25), I("Building", "metal.fragments", 500, 60),
                    I("Building", "metal.refined", 20, 100), I("Building", "lowgradefuel", 50, 30), I("Building", "cupboard.tool", 1, 30),
                    I("Building", "lock.key", 1, 10), I("Building", "lock.code", 1, 40), I("Building", "door.hinged.metal", 1, 70),
                    I("Building", "door.double.hinged.metal", 1, 100), I("Building", "wall.frame.garagedoor", 1, 150),
                    I("Building", "box.wooden.large", 1, 30), I("Building", "furnace", 1, 40), I("Building", "sleepingbag", 1, 10),
                    I("Building", "workbench1", 1, 80),
                    I("Parts", "rope", 5, 30), I("Parts", "tarp", 3, 30), I("Parts", "sewingkit", 5, 40), I("Parts", "propanetank", 3, 30),
                    I("Parts", "sheetmetal", 3, 60), I("Parts", "gears", 5, 75), I("Parts", "metalpipe", 5, 80),
                    I("Parts", "metalspring", 3, 90), I("Parts", "techparts", 2, 120), I("Parts", "semibody", 1, 150),
                    I("Parts", "smgbody", 1, 180), I("Parts", "riflebody", 1, 250),
                    I("Electrical", "wiretool", 1, 15), I("Electrical", "electric.switch", 1, 15), I("Electrical", "electric.splitter", 1, 15),
                    I("Electrical", "electric.timer", 1, 20), I("Electrical", "electric.counter", 1, 25), I("Electrical", "ceilinglight", 1, 20),
                    I("Electrical", "electric.flasherlight", 1, 25), I("Electrical", "electric.sirenlight", 1, 30), I("Electrical", "electric.heater", 1, 40),
                    I("Electrical", "electric.pressurepad", 1, 30), I("Electrical", "electric.hbhfsensor", 1, 30), I("Electrical", "electric.doorcontroller", 1, 30),
                    I("Electrical", "electric.igniter", 1, 40), I("Electrical", "smart.switch", 1, 50), I("Electrical", "smart.alarm", 1, 50),
                    I("Electrical", "electric.battery.rechargable.small", 1, 60), I("Electrical", "electric.battery.rechargable.medium", 1, 150),
                    I("Electrical", "electric.battery.rechargable.large", 1, 350), I("Electrical", "electric.solarpanel.large", 1, 120),
                    I("Electrical", "generator.wind.scrap", 1, 150), I("Electrical", "electric.generator.small", 1, 300), I("Electrical", "autoturret", 1, 600),
                    I("Garden", "planter.small", 1, 30), I("Garden", "planter.large", 1, 60), I("Garden", "composter", 1, 30),
                    I("Garden", "hosetool", 1, 15), I("Garden", "fluid.switch", 1, 30), I("Garden", "fluid.splitter", 1, 25),
                    I("Garden", "electric.sprinkler", 1, 40), I("Garden", "water.barrel", 1, 40), I("Garden", "waterpump", 1, 120),
                    I("Medical", "bandage", 5, 15), I("Medical", "syringe.medical", 3, 45), I("Medical", "largemedkit", 1, 70),
                },
                Displays = new List<string>
                {
                    "chainsaw", "jackhammer", "axe.salvaged", "icepick.salvaged", "hatchet", "pickaxe", "hammer",
                    "electric.solarpanel.large", "electric.battery.rechargable.large", "electric.generator.small", "electric.battery.rechargable.medium",
                    "water.barrel", "composter", "furnace", "box.wooden.large",
                },
            },
            new Shop
            {
                Id = "farmers", Name = "Farmers Market", Keeper = "Daisy", Look = "stables_a", Color = "0.40 0.68 0.30",
                Items = new List<StoreItem>
                {
                    I("Seeds", "seed.corn", 10, 20), I("Seeds", "seed.pumpkin", 10, 20), I("Seeds", "seed.potato", 10, 20),
                    I("Seeds", "seed.hemp", 10, 15), I("Seeds", "seed.blue.berry", 10, 15), I("Seeds", "seed.yellow.berry", 10, 15),
                    I("Farming", "planter.small", 1, 30), I("Farming", "planter.large", 1, 60), I("Farming", "fertilizer", 50, 30),
                    I("Farming", "waterjug", 1, 15), I("Farming", "bucket.water", 1, 15), I("Farming", "water.catcher.small", 1, 40),
                    I("Farming", "water.purifier", 1, 25),
                    I("Food", "apple", 5, 8), I("Food", "corn", 5, 10), I("Food", "potato", 5, 10), I("Food", "pumpkin", 3, 12),
                    I("Food", "blueberries", 5, 10), I("Food", "mushroom", 5, 8), I("Food", "chocolate", 3, 8),
                    I("Food", "granolabar", 3, 10), I("Food", "can.tuna", 3, 12), I("Food", "can.beans", 3, 15),
                    I("Food", "chicken.cooked", 5, 15), I("Food", "fish.cooked", 5, 15), I("Food", "bearmeat.cooked", 3, 20),
                    I("Food", "smallwaterbottle", 1, 10),
                },
                Outdoor = true,
                Displays = FarmersDisplays(),
            },
        };

        private static IEnumerable<StoreItem> MeleeItems() => new[]
        {
            I("Melee", "knife.butcher", 1, 25), I("Melee", "knife.combat", 1, 60), I("Melee", "spear.wooden", 1, 15),
            I("Melee", "spear.stone", 1, 35), I("Melee", "bone.club", 1, 15), I("Melee", "machete", 1, 80),
            I("Melee", "salvaged.cleaver", 1, 90), I("Melee", "mace", 1, 100), I("Melee", "salvaged.sword", 1, 100),
            I("Melee", "longsword", 1, 150),
        };

        // Blades that go on the racks too (any that Rust can't rack are skipped).
        private static readonly string[] MeleeDisplays = { "longsword", "salvaged.sword", "mace", "machete", "salvaged.cleaver", "spear.stone" };

        private static List<string> FarmersDisplays() => new List<string>
        {
            "seed.corn", "seed.pumpkin", "seed.potato", "seed.blue.berry",
            "corn,pumpkin,potato,apple,mushroom", "chicken.cooked,fish.cooked,bearmeat.cooked,can.beans,blueberries",
            "composter", "water.barrel",
        };

        // What 1.1.x shipped for the market (a closed shop). Configs still holding it get the open-air market.
        private static readonly string[] OldFarmersDisplays =
            { "planter.small", "planter.small", "composter", "water.barrel", "bbq", "fridge", "box.wooden.large", "box.wooden.large" };

        protected override void LoadDefaultConfig() => _config = new PluginConfig { ConfigVersion = 2 };

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                _config = Config.ReadObject<PluginConfig>();
                if (_config == null) throw new Exception("empty");
                if (_config.Earn == null) _config.Earn = new EarnSettings();
                if (_config.Earn.Kills == null) _config.Earn.Kills = new Dictionary<string, double>();
                if (_config.Earn.GatherPer100 == null) _config.Earn.GatherPer100 = new Dictionary<string, double>();
                if (_config.Earn.Keycards == null) _config.Earn.Keycards = new Dictionary<string, double>();
                if (_config.Shops == null) _config.Shops = new List<Shop>();
                foreach (var s in _config.Shops)
                {
                    if (s.Items == null) s.Items = new List<StoreItem>();
                    if (s.Displays == null) s.Displays = new List<string>();
                    if (string.IsNullOrEmpty(s.Id)) s.Id = Slug(s.Name);
                    if (s.Outdoor == null)
                    {
                        s.Outdoor = s.Id == "farmers";
                        if (s.Outdoor == true && s.Displays.SequenceEqual(OldFarmersDisplays)) s.Displays = FarmersDisplays();
                    }
                    foreach (var i in s.Items)
                    {
                        if (i.Item == "electric.windmill.small") i.Item = "generator.wind.scrap";
                        if (i.Amount < 1) i.Amount = 1;
                        if (i.Price < 0) i.Price = 0;
                    }
                }
                if (_config.MaxPerLine < 1) _config.MaxPerLine = 1;
                if (_config.ConfigVersion < 2)
                {
                    var guns = _config.Shops.FirstOrDefault(x => x.Id == "guns");
                    if (guns != null && guns.Items.All(i => i.Category != "Melee"))
                    {
                        guns.Items.AddRange(MeleeItems());
                        guns.Displays.AddRange(MeleeDisplays.Where(d => !guns.Displays.Contains(d)));
                    }
                    _config.ConfigVersion = 2;
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

        private static string Slug(string s)
        {
            var chars = (s ?? "").ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray();
            return chars.Length == 0 ? "shop" : new string(chars);
        }

        private Shop FindShop(string id) =>
            _config.Shops.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase))
            ?? _config.Shops.FirstOrDefault(s => s.Name.IndexOf(id ?? "", StringComparison.OrdinalIgnoreCase) >= 0 && !string.IsNullOrEmpty(id));

        #endregion

        #region Data

        private class Account
        {
            public string Name = "";
            public double Balance;
            public double Earned;
            public double Spent;
            public Dictionary<string, double> EarnedBy = new Dictionary<string, double>();
        }

        private class LogEntry
        {
            public DateTime Time;
            public ulong Id;
            public string Name = "";
            public string Kind = "";
            public double Amount;
            public string Text = "";
        }

        private class StoredData
        {
            public Dictionary<ulong, Account> Players = new Dictionary<ulong, Account>();
            public List<LogEntry> Log = new List<LogEntry>();
        }

        private class Vendor
        {
            public string Id = "";
            public string Shop = "";
            public string Name = "";
            public string Monument = "";   // monument key when placed inside one, else blank
            public float X, Y, Z, Yaw;     // monument-local when Monument is set, else world
            public string Map = "";        // size_seed for world-placed vendors
            public string Mall = "";       // the strip mall this shopkeeper works in (removed with it)
            public List<OutfitItem> Outfit = new List<OutfitItem>();
        }

        private class OutfitItem
        {
            public string Item = "";
            public ulong Skin;
        }

        private class Mall
        {
            public string Id = "";
            public string Map = "";
            public float X, Y, Z, Yaw;     // front-left corner at floor height; Yaw = the way into the shops
            public List<string> Shops = new List<string>();
        }

        private class VendorData
        {
            public int NextId = 1;
            public List<Vendor> Vendors = new List<Vendor>();
            public List<Mall> Malls = new List<Mall>();
        }

        private const string DataFile = "RustSandboxStore";
        private const string VendorFile = "RustSandboxStore_vendors";
        private StoredData _data;
        private VendorData _vendors;
        private bool _dirty;

        private void LoadData()
        {
            try { _data = Interface.Oxide.DataFileSystem.ReadObject<StoredData>(DataFile); } catch { _data = null; }
            if (_data == null) _data = new StoredData();
            if (_data.Players == null) _data.Players = new Dictionary<ulong, Account>();
            if (_data.Log == null) _data.Log = new List<LogEntry>();
            try { _vendors = Interface.Oxide.DataFileSystem.ReadObject<VendorData>(VendorFile); } catch { _vendors = null; }
            if (_vendors == null) _vendors = new VendorData();
            if (_vendors.Vendors == null) _vendors.Vendors = new List<Vendor>();
            if (_vendors.Malls == null) _vendors.Malls = new List<Mall>();
        }

        private void SaveData()
        {
            Interface.Oxide.DataFileSystem.WriteObject(DataFile, _data);
            _dirty = false;
        }

        private void SaveVendors() => Interface.Oxide.DataFileSystem.WriteObject(VendorFile, _vendors);

        private Account AccountOf(ulong id, string name = null)
        {
            Account a;
            if (!_data.Players.TryGetValue(id, out a))
            {
                _data.Players[id] = a = new Account { Balance = _config.StartingBalance };
                _dirty = true;
            }
            if (!string.IsNullOrEmpty(name) && a.Name != name) { a.Name = name; _dirty = true; }
            if (a.EarnedBy == null) a.EarnedBy = new Dictionary<string, double>();
            return a;
        }

        private Account AccountOf(BasePlayer p) => AccountOf(p.userID.Get(), p.displayName);

        private void Log(ulong id, string name, string kind, double amount, string text)
        {
            _data.Log.Add(new LogEntry { Time = DateTime.UtcNow, Id = id, Name = name ?? "", Kind = kind, Amount = Math.Round(amount, 2), Text = text ?? "" });
            if (_data.Log.Count > 300) _data.Log.RemoveRange(0, _data.Log.Count - 300);
            _dirty = true;
        }

        private static long Whole(double balance) => (long)Math.Floor(balance + 1e-9);

        private string Money(double amount) => Whole(amount).ToString("N0", CultureInfo.InvariantCulture) + " " + _config.Short;

        #endregion

        #region Lifecycle

        private void Init()
        {
            permission.RegisterPermission(AdminPerm, this);
            LoadData();
        }

        private void OnServerInitialized()
        {
            CheckItems();
            FindDisplayPrefabs();
            StoreCardImage();
            foreach (var m in _vendors.Malls) SpawnMall(m);
            SpawnAllVendors();
            timer.Every(1f, FlushEarnings);
        }

        private void Unload()
        {
            foreach (var p in BasePlayer.activePlayerList)
            {
                CuiHelper.DestroyUi(p, Root);
                CuiHelper.DestroyUi(p, Toast);
            }
            DespawnAllVendors();
            foreach (var m in _vendors.Malls) DespawnMall(m);
            SaveData();
        }

        private void OnServerSave() { if (_dirty) SaveData(); }

        private void OnNewSave(string filename)
        {
            if (!_config.ResetOnWipe) return;
            foreach (var a in _data.Players.Values)
            {
                a.Balance = _config.StartingBalance;
                a.Earned = a.Spent = 0;
                a.EarnedBy.Clear();
            }
            SaveData();
            Puts("Map wipe: everyone's Rust Credits were reset.");
        }

        private void OnPlayerDisconnected(BasePlayer player, string reason)
        {
            if (player == null) return;
            _shown.Remove(player.userID.Get());
            _views.Remove(player.userID.Get());
        }

        // Shortnames that don't exist in this Rust build are skipped in the store and named in the log once.
        private void CheckItems()
        {
            var bad = new List<string>();
            foreach (var s in _config.Shops)
                foreach (var i in s.Items)
                    if (ItemManager.FindItemDefinition(i.Item) == null) bad.Add($"{s.Id}:{i.Item}");
            if (bad.Count > 0) PrintWarning("These store items don't exist in this Rust version and are hidden: " + string.Join(", ", bad));
        }

        #endregion

        #region Earning

        private readonly Dictionary<ulong, double> _pending = new Dictionary<ulong, double>();
        private readonly Dictionary<ulong, string> _pendingWhy = new Dictionary<ulong, string>();

        /// <summary>Adds credits. The pop-up shows whole credits gained, so small earnings add up quietly.</summary>
        private void Earn(BasePlayer player, double amount, string why)
        {
            if (!_config.Enabled || amount <= 0 || player == null || !player.userID.IsSteamId() || player.IsNpc) return;
            var a = AccountOf(player);
            var before = Whole(a.Balance);
            a.Balance += amount;
            a.Earned += amount;
            double by;
            a.EarnedBy.TryGetValue(why, out by);
            a.EarnedBy[why] = by + amount;
            _dirty = true;
            var gained = Whole(a.Balance) - before;
            if (gained <= 0 || !_config.ShowEarnings) return;
            double p;
            _pending.TryGetValue(player.userID.Get(), out p);
            _pending[player.userID.Get()] = p + gained;
            _pendingWhy[player.userID.Get()] = why;
        }

        private void FlushEarnings()
        {
            if (_pending.Count == 0) return;
            foreach (var kv in _pending.ToList())
            {
                var player = BasePlayer.FindByID(kv.Key);
                if (player == null || !player.IsConnected) continue;
                string why;
                _pendingWhy.TryGetValue(kv.Key, out why);
                ShowToast(player, kv.Value, why);
            }
            _pending.Clear();
            _pendingWhy.Clear();
        }

        private readonly Dictionary<ulong, Timer> _toastTimers = new Dictionary<ulong, Timer>();

        private void ShowToast(BasePlayer player, double amount, string why)
        {
            var id = player.userID.Get();
            var a = AccountOf(player);
            var anchor = string.IsNullOrEmpty(_config.EarningsAnchor) ? "0.5 1" : _config.EarningsAnchor;
            var off = (_config.EarningsOffset ?? "-90 -78").Split(' ');
            float ox = 0, oy = 0;
            if (off.Length >= 2) { float.TryParse(off[0], NumberStyles.Float, CultureInfo.InvariantCulture, out ox); float.TryParse(off[1], NumberStyles.Float, CultureInfo.InvariantCulture, out oy); }
            var ui = new CuiElementContainer();
            ui.Add(new CuiPanel
            {
                Image = { Color = "0.08 0.10 0.16 0.85" },
                RectTransform = { AnchorMin = anchor, AnchorMax = anchor, OffsetMin = $"{ox} {oy}", OffsetMax = $"{ox + 180} {oy + 34}" },
                FadeOut = 0.4f,
            }, "Hud", Toast);
            ui.Add(new CuiPanel { Image = { Color = "0.94 0.75 0.25 1" }, RectTransform = { AnchorMin = "0 0", AnchorMax = "0 1", OffsetMin = "0 0", OffsetMax = "3 0" } }, Toast);
            ui.Add(new CuiLabel
            {
                Text = { Text = $"<color=#f0c040>+{amount:N0} {_config.Short}</color>  <size=10><color=#9aa4b8>{WhyLabel(why)}</color></size>", FontSize = 14, Align = TextAnchor.UpperLeft, Font = "robotocondensed-bold.ttf" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1", OffsetMin = "10 0", OffsetMax = "-4 -3" },
            }, Toast);
            ui.Add(new CuiLabel
            {
                Text = { Text = $"Balance {Money(a.Balance)}", FontSize = 10, Align = TextAnchor.LowerLeft, Color = "1 1 1 0.6" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "1 1", OffsetMin = "10 3", OffsetMax = "-4 0" },
            }, Toast);
            CuiHelper.DestroyUi(player, Toast);
            CuiHelper.AddUi(player, ui);
            Timer t;
            if (_toastTimers.TryGetValue(id, out t)) t?.Destroy();
            _toastTimers[id] = timer.Once(3f, () =>
            {
                _toastTimers.Remove(id);
                if (player != null && player.IsConnected) CuiHelper.DestroyUi(player, Toast);
            });
        }

        private static string WhyLabel(string why)
        {
            switch (why)
            {
                case "kill": return "Kill";
                case "gather": return "Gathering";
                case "farm": return "Farming";
                case "pickup": return "Foraging";
                case "fish": return "Fishing";
                case "keycard": return "Keycard puzzle";
                case "pay": return "Payment";
                case "admin": return "Gift";
                default: return why ?? "";
            }
        }

        private readonly Dictionary<string, float> _lastKill = new Dictionary<string, float>();
        private readonly Dictionary<string, float> _lastSwipe = new Dictionary<string, float>();

        private void OnEntityDeath(BaseCombatEntity entity, HitInfo info)
        {
            if (!_config.Enabled || entity == null) return;
            var killer = info?.InitiatorPlayer ?? entity.lastAttacker as BasePlayer;
            if (killer == null || killer == entity || !killer.userID.IsSteamId() || killer.IsNpc) return;
            var e = _config.Earn;
            var victim = entity as BasePlayer;
            if (victim != null && victim.userID.IsSteamId() && !victim.IsNpc)
            {
                if (killer.Team != null && killer.Team.members.Contains(victim.userID.Get())) return;
                var key = killer.userID.Get() + ":" + victim.userID.Get();
                float last;
                var now = Time.realtimeSinceStartup;
                if (_lastKill.TryGetValue(key, out last) && now - last < e.PlayerKillCooldownMinutes * 60) return;
                _lastKill[key] = now;
                Earn(killer, e.PlayerKill, "kill");
                return;
            }
            Earn(killer, KillValue(entity), "kill");
        }

        private double KillValue(BaseCombatEntity entity)
        {
            var e = _config.Earn;
            double v;
            var name = entity.ShortPrefabName ?? "";
            if (e.Kills.TryGetValue(name, out v)) return v;
            if (name.EndsWith("2") && e.Kills.TryGetValue(name.Substring(0, name.Length - 1), out v)) return v;
            if (entity is BasePlayer) return e.NpcKill;
            if (entity is BaseNpc || entity is Rust.Ai.Gen2.BaseNPC2) return e.AnimalKill;
            return 0;
        }

        private void OnDispenserGather(ResourceDispenser dispenser, BaseEntity entity, Item item) => Gathered(entity as BasePlayer, item);

        private void OnDispenserBonus(ResourceDispenser dispenser, BasePlayer player, Item item) => Gathered(player, item);

        private void Gathered(BasePlayer player, Item item)
        {
            if (player == null || item == null || item.info == null) return;
            double per100;
            if (!_config.Earn.GatherPer100.TryGetValue(item.info.shortname, out per100) || per100 <= 0) return;
            Earn(player, item.amount * per100 / 100.0, "gather");
        }

        private void OnGrowableGathered(GrowableEntity plant, Item item, BasePlayer player) => Earn(player, _config.Earn.FarmHarvest, "farm");

        private void OnCollectiblePickup(CollectibleEntity collectible, BasePlayer player)
        {
            if (collectible == null) return;
            Earn(player, _config.Earn.Pickup, "pickup");
        }

        private void OnFishCatch(Item item, BaseFishingRod rod, BasePlayer player)
        {
            if (item == null || item.info == null) return;
            ItemModFishable fishable;
            var junk = !item.info.TryGetComponent(out fishable) || fishable.IsJunk;
            Earn(player, junk ? _config.Earn.FishJunk : _config.Earn.Fish, "fish");
        }

        private static readonly string[] CardColours = { "", "green", "blue", "red" };

        private void OnCardSwipe(CardReader reader, Keycard card, BasePlayer player)
        {
            if (reader == null || card == null || player == null || reader.net == null) return;
            var item = card.GetItem();
            // The same test the game makes right after this hook: the right colour, in the swiper's hands, not broken.
            if (item == null || item.parent == null || item.parent.playerOwner != player || card.accessLevel != reader.accessLevel || item.conditionNormalized <= 0f) return;
            var level = reader.accessLevel;
            if (level < 1 || level >= CardColours.Length) return;
            double v;
            if (!_config.Earn.Keycards.TryGetValue(CardColours[level], out v) || v <= 0) return;
            var key = player.userID.Get() + ":" + reader.net.ID.Value;
            float last;
            var now = Time.realtimeSinceStartup;
            if (_lastSwipe.TryGetValue(key, out last) && now - last < _config.Earn.KeycardCooldownMinutes * 60) return;
            _lastSwipe[key] = now;
            Earn(player, v, "keycard");
        }

        #endregion

        #region API for other plugins (pass ulong ids as player.userID.Get())

        private double API_GetBalance(ulong id) => _data.Players.ContainsKey(id) ? AccountOf(id).Balance : _config.StartingBalance;

        private void API_AddCredits(ulong id, double amount, string reason)
        {
            if (amount <= 0) return;
            var player = BasePlayer.FindByID(id);
            if (player != null) { Earn(player, amount, string.IsNullOrEmpty(reason) ? "admin" : reason); return; }
            var a = AccountOf(id);
            a.Balance += amount;
            a.Earned += amount;
            _dirty = true;
        }

        private bool API_TakeCredits(ulong id, double amount)
        {
            var a = AccountOf(id);
            if (amount < 0 || Whole(a.Balance) < amount) return false;
            a.Balance -= amount;
            a.Spent += amount;
            _dirty = true;
            return true;
        }

        #endregion

        #region Shopkeepers

        private const string AdminPerm = "rustsandboxstore.admin";
        private const string MarkerPrefab = "assets/prefabs/deployable/vendingmachine/vending_mapmarker.prefab";

        private static string PrefabFor(string look) =>
            "assets/prefabs/npc/bandit/missionproviders/missionprovider_" + (look ?? "outpost_a").ToLowerInvariant() + ".prefab";

        private readonly Dictionary<string, NPCTalking> _spawned = new Dictionary<string, NPCTalking>();
        private readonly Dictionary<ulong, string> _npcToVendor = new Dictionary<ulong, string>();
        private readonly Dictionary<string, VendingMachineMapMarker> _markers = new Dictionary<string, VendingMachineMapMarker>();

        private static string MapKey() => World.Size + "_" + World.Seed;

        private static IEnumerable<MonumentInfo> AllMonuments() =>
            TerrainMeta.Path == null ? Enumerable.Empty<MonumentInfo>() : TerrainMeta.Path.Monuments.Where(m => m != null);

        private static string MonumentKey(MonumentInfo m)
        {
            var path = m.name ?? "";
            var slash = path.LastIndexOf('/');
            if (slash >= 0) path = path.Substring(slash + 1);
            if (path.EndsWith(".prefab")) path = path.Substring(0, path.Length - 7);
            return path.ToLowerInvariant();
        }

        private static string MonumentName(MonumentInfo m)
        {
            var phrase = m.displayPhrase?.english;
            return !string.IsNullOrEmpty(phrase) ? phrase.Trim() : MonumentKey(m).Replace('_', ' ');
        }

        private static MonumentInfo MonumentAt(Vector3 pos) => AllMonuments().FirstOrDefault(m => m.IsInBounds(pos));

        /// <summary>World position and yaw for a vendor on this map, or false if it doesn't belong here.</summary>
        private static bool Place(Vendor v, out Vector3 pos, out float yaw)
        {
            pos = new Vector3(v.X, v.Y, v.Z);
            yaw = v.Yaw;
            if (string.IsNullOrEmpty(v.Monument)) return string.IsNullOrEmpty(v.Map) || v.Map == MapKey();
            var m = AllMonuments().FirstOrDefault(x => MonumentKey(x) == v.Monument);
            if (m == null) return false;
            pos = m.transform.TransformPoint(pos);
            yaw = Mathf.Repeat(m.transform.eulerAngles.y + v.Yaw, 360f);
            return true;
        }

        private static void SetPlace(Vendor v, Vector3 pos, float yaw)
        {
            var m = MonumentAt(pos);
            if (m != null)
            {
                var local = m.transform.InverseTransformPoint(pos);
                v.Monument = MonumentKey(m);
                v.X = local.x; v.Y = local.y; v.Z = local.z;
                v.Yaw = Mathf.Repeat(yaw - m.transform.eulerAngles.y, 360f);
                v.Map = "";
            }
            else
            {
                v.Monument = "";
                v.X = pos.x; v.Y = pos.y; v.Z = pos.z;
                v.Yaw = Mathf.Repeat(yaw, 360f);
                v.Map = MapKey();
            }
        }

        private void SpawnAllVendors()
        {
            foreach (var v in _vendors.Vendors) SpawnVendor(v);
        }

        private void DespawnAllVendors()
        {
            foreach (var v in _vendors.Vendors.ToList()) DespawnVendor(v);
            _spawned.Clear();
            _npcToVendor.Clear();
            _markers.Clear();
        }

        private void DespawnVendor(Vendor v)
        {
            VendingMachineMapMarker m;
            if (_markers.TryGetValue(v.Id, out m))
            {
                _markers.Remove(v.Id);
                if (m != null && !m.IsDestroyed) m.Kill();
            }
            NPCTalking npc;
            if (!_spawned.TryGetValue(v.Id, out npc)) return;
            _spawned.Remove(v.Id);
            if (npc == null) return;
            if (npc.net != null) _npcToVendor.Remove(npc.net.ID.Value);
            if (!npc.IsDestroyed) npc.Kill();
        }

        private void SpawnVendor(Vendor v)
        {
            DespawnVendor(v);
            var shop = FindShop(v.Shop);
            if (shop == null || !shop.Enabled) return;
            Vector3 pos;
            float yaw;
            if (!Place(v, out pos, out yaw)) return;
            var rot = Quaternion.Euler(0f, yaw, 0f);
            var entity = GameManager.server.CreateEntity(PrefabFor(shop.Look), pos, rot);
            if (!(entity is NPCTalking))
            {
                if (entity != null) entity.Kill();
                entity = GameManager.server.CreateEntity(PrefabFor("outpost_a"), pos, rot);
            }
            var npc = entity as NPCTalking;
            if (npc == null)
            {
                if (entity != null) entity.Kill();
                PrintError($"Couldn't spawn shopkeeper {v.Id}: the NPC prefab didn't load.");
                return;
            }
            npc.enableSaving = false;
            npc.Spawn();
            npc.displayName = KeeperName(v, shop);
            Dress(npc, v);
            _spawned[v.Id] = npc;
            if (npc.net != null) _npcToVendor[npc.net.ID.Value] = v.Id;

            if (_config.MapMarkers)
            {
                var m = GameManager.server.CreateEntity(MarkerPrefab, pos) as VendingMachineMapMarker;
                if (m != null)
                {
                    m.markerShopName = $"{shop.Name} ({_config.CurrencyName})";
                    m.enableSaving = false;
                    m.flags |= BaseEntity.Flags.Busy; // green "open" icon
                    m.Spawn();
                    _markers[v.Id] = m;
                }
            }

            // Mission NPCs drop a map marker for the game's own missions; shopkeepers don't offer those.
            NextTick(() =>
            {
                var found = new List<MapMarkerMissionProvider>();
                Vis.Entities(pos, 2f, found);
                foreach (var f in found)
                    if (f != null && !f.IsDestroyed) f.Kill();
            });
        }

        private static string KeeperName(Vendor v, Shop shop) =>
            !string.IsNullOrEmpty(v.Name) ? v.Name : !string.IsNullOrEmpty(shop.Keeper) ? shop.Keeper : shop.Name;

        private static void Dress(NPCTalking npc, Vendor v)
        {
            if (v.Outfit == null || v.Outfit.Count == 0) return;
            var wear = npc.inventory.containerWear;
            foreach (var old in wear.itemList.ToList()) { old.RemoveFromContainer(); old.Remove(); }
            foreach (var o in v.Outfit)
            {
                var item = ItemManager.CreateByName(o.Item, 1, o.Skin);
                if (item != null && !item.MoveToContainer(wear)) item.Remove();
            }
            npc.SendNetworkUpdate();
        }

        private Vendor VendorOf(BaseEntity npc)
        {
            if (npc == null || npc.net == null) return null;
            string id;
            return _npcToVendor.TryGetValue(npc.net.ID.Value, out id) ? FindVendor(id) : null;
        }

        private Vendor FindVendor(string id) =>
            _vendors.Vendors.FirstOrDefault(v => string.Equals(v.Id, id, StringComparison.OrdinalIgnoreCase));

        private Vendor NearestVendor(BasePlayer player, float within)
        {
            Vendor best = null;
            var bestD = within;
            foreach (var kv in _spawned)
            {
                if (kv.Value == null || kv.Value.IsDestroyed) continue;
                var d = Vector3.Distance(kv.Value.transform.position, player.transform.position);
                if (d < bestD) { bestD = d; best = FindVendor(kv.Key); }
            }
            return best;
        }

        private static float FacingYaw(BasePlayer p) =>
            Mathf.Repeat((p.eyes != null ? p.eyes.rotation.eulerAngles.y : p.transform.eulerAngles.y) + 180f, 360f);

        private Vendor AddVendor(Shop shop, Vector3 pos, float yaw, string name)
        {
            var v = new Vendor { Id = "v" + _vendors.NextId++, Shop = shop.Id, Name = name ?? "" };
            SetPlace(v, pos, yaw);
            _vendors.Vendors.Add(v);
            SaveVendors();
            SpawnVendor(v);
            return v;
        }

        // Pressing E on a shopkeeper: open the store instead of the game's conversation.
        private object OnNpcConversationStart(NPCTalking npc, BasePlayer player, ConversationData conversation)
        {
            var v = VendorOf(npc);
            if (v == null) return null;
            var shop = FindShop(v.Shop);
            if (shop != null) Open(player, shop, KeeperName(v, shop));
            return false;
        }

        // Fallback if a game update changes the talk flow: E while looking at a shopkeeper up close.
        private readonly Dictionary<ulong, float> _lastUse = new Dictionary<ulong, float>();

        private void OnPlayerInput(BasePlayer player, InputState input)
        {
            if (input == null || !input.WasJustPressed(BUTTON.USE) || _npcToVendor.Count == 0) return;
            if (_shown.Contains(player.userID.Get())) return;
            RaycastHit hit;
            if (!Physics.Raycast(player.eyes.HeadRay(), out hit, 3f, Rust.Layers.Mask.Player_Server, QueryTriggerInteraction.Ignore)) return;
            var v = VendorOf(hit.GetEntity() as NPCTalking);
            if (v == null) return;
            float last;
            var now = Time.realtimeSinceStartup;
            if (_lastUse.TryGetValue(player.userID.Get(), out last) && now - last < 0.6f) return;
            _lastUse[player.userID.Get()] = now;
            timer.Once(0.3f, () =>
            {
                if (player == null || !player.IsConnected || _shown.Contains(player.userID.Get())) return;
                var shop = FindShop(v.Shop);
                if (shop != null) Open(player, shop, KeeperName(v, shop));
            });
        }

        #endregion

        #region Strip mall

        // /store build puts up a row of wooden shops (3 x 3 foundations each) in front of the admin: open doorways at
        // the front, windows either side, a flat roof, a counter with the shopkeeper behind it, guns and tools on wall
        // racks, clothes on mannequins and deployables (solar panels, planters, boxes...) on the floor. Nothing in it
        // can be damaged, looted, picked up or built onto. It isn't saved with the map: the plugin puts it back
        // every time it loads, on the map it was built on.

        private const string FoundationPrefab = "assets/prefabs/building core/foundation/foundation.prefab";
        private const string WallPrefab = "assets/prefabs/building core/wall/wall.prefab";
        private const string DoorwayPrefab = "assets/prefabs/building core/wall.doorway/wall.doorway.prefab";
        private const string WindowPrefab = "assets/prefabs/building core/wall.window/wall.window.prefab";
        private const string FloorPrefab = "assets/prefabs/building core/floor/floor.prefab";
        private const string FramePrefab = "assets/prefabs/building core/wall.frame/wall.frame.prefab";
        private const string LowWallPrefab = "assets/prefabs/building core/wall.low/wall.low.prefab";
        private const float Cell = 3f, ShopCells = 3, ShopWidth = Cell * ShopCells, Depth = Cell * 3, WallHalf = 0.12f;

        private readonly Dictionary<string, List<BaseEntity>> _mallEntities = new Dictionary<string, List<BaseEntity>>();
        private readonly HashSet<ulong> _protected = new HashSet<ulong>();
        private string _wallLightPrefab;
        private Bounds _wallLightBounds;
        private string _rackPrefab, _mannequinPrefab, _neonPrefab, _lightPrefab, _lanternPrefab, _tablePrefab, _planterPrefab;
        private Bounds _neonBounds, _lightBounds, _lanternBounds, _tableBounds, _planterBounds;
        private float _ceiling = Cell;
        private Vector2i _neonSize;
        private Vector3 _rackCenter, _rackSize;

        // The wall rack and mannequin come from the items that place them, so no prefab paths need to be known.
        private void FindDisplayPrefabs()
        {
            var bestCells = 0;
            foreach (var def in ItemManager.itemList)
            {
                var dep = def.GetComponent<ItemModDeployable>();
                var path = dep?.entityPrefab?.resourcePath;
                if (string.IsNullOrEmpty(path)) continue;
                var go = GameManager.server.FindPrefab(path);
                if (go == null) continue;
                var rack = go.GetComponent<WeaponRack>();
                if (rack != null && rack.Type == WeaponRack.RackType.Board && rack.CustomRackType == WeaponRack.SpecialRackType.None && rack.Collision != null)
                {
                    var cells = rack.GridCellCountX * rack.GridCellCountY;
                    if (cells > bestCells)
                    {
                        bestCells = cells;
                        _rackPrefab = path;
                        var t = rack.Collision.transform;
                        _rackCenter = go.transform.InverseTransformPoint(t.TransformPoint(rack.Collision.center));
                        _rackSize = Vector3.Scale(rack.Collision.size, t.lossyScale);
                    }
                }
                if (_mannequinPrefab == null && go.GetComponent<Mannequin>() != null) _mannequinPrefab = path;
                // The biggest still (not animated) neon sign.
                var sign = go.GetComponent<NeonSign>();
                if (sign != null && !def.shortname.Contains("animated") && (_neonPrefab == null || sign.bounds.size.x > _neonBounds.size.x))
                {
                    _neonPrefab = path;
                    _neonBounds = sign.bounds;
                    _neonSize = NeonTextureSize(sign);
                }
            }
            // Lights: an electric ceiling light indoors (powered without wires, like the neon signs), oil lanterns at the market.
            _lightPrefab = DeployPrefab("ceilinglight");
            if (_lightPrefab == null || PrefabEntity(_lightPrefab) as CeilingLight == null)
                _lightPrefab = ItemManager.itemList.Select(d => d.GetComponent<ItemModDeployable>()?.entityPrefab?.resourcePath)
                    .FirstOrDefault(path => !string.IsNullOrEmpty(path) && PrefabEntity(path) is CeilingLight);
            _wallLightPrefab = DeployPrefab("industrial.wall.light") ?? DeployPrefab("electric.simplelight");
            _wallLightBounds = PrefabBounds(_wallLightPrefab);
            _lanternPrefab = DeployPrefab("lantern");
            _tablePrefab = DeployPrefab("table");
            _planterPrefab = DeployPrefab("planter.small");
            _lightBounds = PrefabBounds(_lightPrefab);
            _lanternBounds = PrefabBounds(_lanternPrefab);
            _tableBounds = PrefabBounds(_tablePrefab);
            _planterBounds = PrefabBounds(_planterPrefab);
            var floorBounds = PrefabBounds(FloorPrefab);
            if (floorBounds.size.y > 0.01f && floorBounds.size.y < 1f) _ceiling = Cell + floorBounds.min.y;
            if (_rackPrefab == null) PrintWarning("Couldn't find Rust's wall weapon rack; guns won't be shown in the strip mall.");
            if (_mannequinPrefab == null) PrintWarning("Couldn't find Rust's mannequin; clothes won't be shown in the strip mall.");
        }

        private static BaseEntity PrefabEntity(string path) =>
            string.IsNullOrEmpty(path) ? null : GameManager.server.FindPrefab(path)?.GetComponent<BaseEntity>();

        private static Bounds PrefabBounds(string path)
        {
            var e = PrefabEntity(path);
            return e != null ? e.bounds : new Bounds(Vector3.zero, Vector3.zero);
        }

        private static string DeployPrefab(string shortname)
        {
            var def = ItemManager.FindItemDefinition(shortname);
            var path = def?.GetComponent<ItemModDeployable>()?.entityPrefab?.resourcePath;
            return string.IsNullOrEmpty(path) ? null : path;
        }

        private static Quaternion MallRot(Mall m) => Quaternion.Euler(0f, m.Yaw, 0f);

        private static Vector3 MallPoint(Mall m, float x, float y, float z) => new Vector3(m.X, m.Y, m.Z) + MallRot(m) * new Vector3(x, y, z);

        /// <summary>Yaw for something facing a direction in the mall's frame: 0 = into the shops, 180 = out of the front.</summary>
        private static float MallYaw(Mall m, float local) => Mathf.Repeat(m.Yaw + local, 360f);

        private static float MallWidth(Mall m) => ShopWidth * m.Shops.Count;

        private bool InMall(Vector3 pos, float margin)
        {
            foreach (var m in _vendors.Malls)
            {
                if (m.Map != MapKey()) continue;
                var local = Quaternion.Inverse(MallRot(m)) * (pos - new Vector3(m.X, m.Y, m.Z));
                if (local.x > -margin && local.x < MallWidth(m) + margin && local.z > -margin && local.z < Depth + margin && local.y > -10f && local.y < 12f)
                    return true;
            }
            return false;
        }

        private BuildingGrade.Enum Grade()
        {
            switch ((_config.BuildGrade ?? "").ToLowerInvariant())
            {
                case "twig": return BuildingGrade.Enum.Twigs;
                case "stone": return BuildingGrade.Enum.Stone;
                case "metal": return BuildingGrade.Enum.Metal;
                case "toptier":
                case "armored": return BuildingGrade.Enum.TopTier;
                default: return BuildingGrade.Enum.Wood;
            }
        }

        private void Track(Mall m, BaseEntity e)
        {
            if (e == null || e.net == null) return;
            List<BaseEntity> list;
            if (!_mallEntities.TryGetValue(m.Id, out list)) _mallEntities[m.Id] = list = new List<BaseEntity>();
            list.Add(e);
            _protected.Add(e.net.ID.Value);
        }

        private BuildingBlock Block(Mall m, string prefab, Vector3 pos, Quaternion rot, uint buildingId, BuildingGrade.Enum grade)
        {
            var block = GameManager.server.CreateEntity(prefab, pos, rot) as BuildingBlock;
            if (block == null) return null;
            block.enableSaving = false;
            block.grounded = true;
            block.grade = grade;
            block.buildingID = buildingId;
            block.Spawn();
            block.SetGrade(grade);
            block.SetHealthToMax();
            block.SendNetworkUpdate();
            Track(m, block);
            return block;
        }

        private BaseEntity Place(Mall m, string prefab, Vector3 pos, float yaw)
        {
            if (string.IsNullOrEmpty(prefab)) return null;
            var e = GameManager.server.CreateEntity(prefab, pos, Quaternion.Euler(0f, yaw, 0f));
            if (e == null) return null;
            e.enableSaving = false;
            e.OwnerID = 0;
            var stab = e as StabilityEntity;
            if (stab != null) stab.grounded = true;
            e.Spawn();
            Track(m, e);
            return e;
        }

        // Things near the mall that the build would clip: trees, rocks, ore, bushes and pickups.
        private static void ClearNature(Mall m)
        {
            var center = MallPoint(m, MallWidth(m) / 2f, 0f, Depth / 2f);
            var list = new List<BaseEntity>();
            Vis.Entities(center, MallWidth(m) / 2f + 6f, list);
            foreach (var e in list)
            {
                if (e == null || e.IsDestroyed) continue;
                if (!(e is TreeEntity || e is ResourceEntity || e is BushEntity || e is CollectibleEntity)) continue;
                var local = Quaternion.Inverse(MallRot(m)) * (e.transform.position - new Vector3(m.X, m.Y, m.Z));
                if (local.x > -2f && local.x < MallWidth(m) + 2f && local.z > -3f && local.z < Depth + 2f) e.Kill();
            }
        }

        private void DespawnMall(Mall m)
        {
            List<BaseEntity> list;
            if (!_mallEntities.TryGetValue(m.Id, out list)) return;
            _mallEntities.Remove(m.Id);
            // Racks and mannequins hold real items: empty them first so nothing drops on the ground.
            foreach (var e in list)
            {
                if (e == null || e.IsDestroyed) continue;
                if (e.net != null) _protected.Remove(e.net.ID.Value);
                var sc = e as StorageContainer;
                if (sc != null && sc.inventory != null) sc.inventory.Clear();
                var wi = e as WorldItem;
                if (wi != null && wi.item != null) wi.DestroyItem();
            }
            ItemManager.DoRemoves();
            foreach (var e in list)
                if (e != null && !e.IsDestroyed) e.Kill();
        }

        private void SpawnMall(Mall m)
        {
            DespawnMall(m);
            if (m.Map != MapKey()) return;
            ClearNature(m);
            var rot = MallRot(m);
            var side = rot * Quaternion.Euler(0f, 90f, 0f);
            var grade = Grade();
            var buildingId = BuildingManager.server.NewBuildingID();
            int cellsX = (int)(ShopCells * m.Shops.Count);

            // Open-air shops (the market) get no roof, open frames across the front and low walls round the rest.
            Func<int, bool> open = s => s >= 0 && s < m.Shops.Count && FindShop(m.Shops[s])?.Outdoor == true;
            for (int i = 0; i < cellsX; i++)
                for (int j = 0; j < 3; j++)
                {
                    var c = MallPoint(m, Cell * i + Cell / 2f, 0f, Cell * j + Cell / 2f);
                    Block(m, FoundationPrefab, c, rot, buildingId, grade);
                    if (!open(i / (int)ShopCells)) Block(m, FloorPrefab, c + Vector3.up * Cell, rot, buildingId, grade);
                }
            for (int i = 0; i < cellsX; i++)
            {
                var outdoor = open(i / (int)ShopCells);
                var front = outdoor ? FramePrefab : (i % (int)ShopCells) == 1 ? DoorwayPrefab : WindowPrefab;
                // A wall's length runs along its own Z axis, so walls across the front and back are turned 90 degrees.
                Block(m, front, MallPoint(m, Cell * i + Cell / 2f, 0f, 0f), side, buildingId, grade);
                Block(m, outdoor ? LowWallPrefab : WallPrefab, MallPoint(m, Cell * i + Cell / 2f, 0f, Depth), side, buildingId, grade);
            }
            for (int s = 0; s <= m.Shops.Count; s++)
            {
                // A wall between two shops stays full height unless both sides are open-air (or it's an open-air end).
                var full = (s > 0 && !open(s - 1)) || (s < m.Shops.Count && !open(s));
                for (int j = 0; j < 3; j++)
                    Block(m, full ? WallPrefab : LowWallPrefab, MallPoint(m, ShopWidth * s, 0f, Cell * j + Cell / 2f), rot, buildingId, grade);
            }

            for (int s = 0; s < m.Shops.Count; s++)
            {
                var shop = FindShop(m.Shops[s]);
                if (shop == null) continue;
                FitOut(m, s * ShopWidth, shop);
                if (_config.NeonSigns) Neon(m, s * ShopWidth, shop);
            }
        }

        // A lit neon sign on the roof edge above each shop's door, with the shop's name. The four built-in shops have
        // pictures made for them (in NeonImages); other shops get a sign only once a picture is added for their id.
        private void Neon(Mall m, float x0, Shop shop)
        {
            string b64 = shop.Sign;
            if (string.IsNullOrEmpty(b64) && !NeonImages.TryGetValue(shop.Id, out b64)) return;
            if (_neonPrefab == null) return;
            // The sign faces out of the front with its picture towards the street (seen in game). Before the
            // pictures showed at all both sides looked like bare wire, which misled 1.1.2 into turning it round.
            // FlipSigns turns it the other way.
            var flip = _config.FlipSigns;
            var facingOut = 180f + (flip ? 180f : 0f);
            var outward = MallRot(m) * Vector3.back;
            var back = flip ? -(_neonBounds.center.z + _neonBounds.extents.z) : _neonBounds.center.z - _neonBounds.extents.z;
            var bottom = _neonBounds.center.y - _neonBounds.extents.y;
            var pos = MallPoint(m, x0 + ShopWidth / 2f, Cell + 0.05f - bottom, -WallHalf) + outward * -back;
            var sign = Place(m, _neonPrefab, pos, MallYaw(m, facingOut)) as NeonSign;
            if (sign == null) return;
            try
            {
                sign.EnsureInitialized();
                ((Signage)sign).EnsureInitialized();
                // Players' games only show a sign picture that fits the sign's own texture size, so it's scaled to fit.
                var size = _neonSize;
                var png = FitPicture(Convert.FromBase64String(b64), size.x, size.y);
                if (png == null) { PrintWarning($"{shop.Name}'s sign picture isn't a PNG or JPG Rust can read."); return; }
                sign.textureIDs[0] = FileStorage.server.Store(png, FileStorage.Type.png, sign.net.ID, 0);
            }
            catch (Exception ex) { PrintWarning($"Couldn't put the picture on {shop.Name}'s neon sign: {ex.Message}"); }
            // The sign's glow lights take the shop's colour (they start clear, which is what painting in game sets).
            var glow = ShopColor(shop.Color);
            if (sign.frameLighting != null)
                foreach (var l in sign.frameLighting)
                {
                    l.topLeft = l.topRight = l.bottomLeft = l.bottomRight = glow;
                }
            PowerUp(sign);
        }

        // The sign's paint texture size. The server's copy of the prefab may not carry it (TextureSize throws there), and
        // a picture smaller than the texture is always accepted, so fall back to 256 wide in the sign's own shape.
        private static Vector2i NeonTextureSize(NeonSign sign)
        {
            try
            {
                var size = sign.TextureSize;
                if (size.x > 0 && size.y > 0) return size;
            }
            catch { }
            var b = sign.bounds.size;
            var aspect = b.y > 0.01f ? Mathf.Clamp(b.x / b.y, 0.25f, 4f) : 2f;
            return aspect >= 1f ? new Vector2i(256, Mathf.RoundToInt(256 / aspect)) : new Vector2i(Mathf.RoundToInt(256 * aspect), 256);
        }

        private readonly Dictionary<string, byte[]> _fitted = new Dictionary<string, byte[]>();

        // The picture scaled to fit w x h (keeping its shape), with its dark background made transparent, as PNG.
        private byte[] FitPicture(byte[] data, int w, int h)
        {
            if (w <= 0 || h <= 0) return data;
            var key = $"{w}x{h}:{data.Length}:{Convert.ToBase64String(data, 0, Math.Min(64, data.Length))}:{data[data.Length / 2]}";
            byte[] cached;
            if (_fitted.TryGetValue(key, out cached)) return cached;
            var src = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            Texture2D dst = null;
            try
            {
                if (!src.LoadImage(data)) return ImageProcessing.IsValidPNG(data, 4096) ? data : null;
                var scale = Mathf.Min(w / (float)src.width, h / (float)src.height);
                int fw = Mathf.Max(1, Mathf.RoundToInt(src.width * scale)), fh = Mathf.Max(1, Mathf.RoundToInt(src.height * scale));
                int ox = (w - fw) / 2, oy = (h - fh) / 2;
                var pixels = new Color32[w * h];
                // A neon sign only lights what's painted: like a picture painted in game, the background has to be
                // see-through, so dark pixels become transparent and bright ones keep their colour.
                for (int y = 0; y < fh; y++)
                    for (int x = 0; x < fw; x++)
                    {
                        var c = src.GetPixelBilinear((x + 0.5f) / fw, (y + 0.5f) / fh);
                        var bright = Mathf.Max(c.r, Mathf.Max(c.g, c.b)) * c.a;
                        if (bright < 0.06f) continue;
                        var max = Mathf.Max(c.r, Mathf.Max(c.g, c.b));
                        pixels[(y + oy) * w + x + ox] = new Color(c.r / max, c.g / max, c.b / max, Mathf.Clamp01(bright * 1.2f));
                    }
                dst = new Texture2D(w, h, TextureFormat.RGBA32, false);
                dst.SetPixels32(pixels);
                dst.Apply();
                return _fitted[key] = dst.EncodeToPNG();
            }
            catch (Exception ex)
            {
                PrintWarning($"Couldn't resize a sign picture ({ex.Message}); using it as it is.");
                return data;
            }
            finally
            {
                UnityEngine.Object.Destroy(src);
                if (dst != null) UnityEngine.Object.Destroy(dst);
            }
        }

        // Spots inside one shop (x across 0..9 from the left wall, z 0..9 from the front), with the way they face.
        private static readonly float[][] FloorSpots =
        {
            new[] { 1.3f, 7.8f, 180f }, new[] { 7.7f, 7.8f, 180f },
            new[] { 1.2f, 3.6f, 90f }, new[] { 7.8f, 3.6f, -90f },
            new[] { 1.2f, 1.6f, 90f }, new[] { 7.8f, 1.6f, -90f },
            new[] { 2.8f, 7.9f, 180f }, new[] { 6.2f, 7.9f, 180f },
        };

        // Wall racks: back wall either side of the shopkeeper, then the side walls.
        private static readonly float[][] RackSpots =
        {
            new[] { 2.2f, Depth - WallHalf, 180f }, new[] { 6.8f, Depth - WallHalf, 180f },
            new[] { WallHalf, 6.3f, 90f }, new[] { ShopWidth - WallHalf, 6.3f, -90f },
            new[] { WallHalf, 2.8f, 90f }, new[] { ShopWidth - WallHalf, 2.8f, -90f },
        };

        // Open-air market: planters of ripe crops down both sides, two food stalls either side of the shopkeeper.
        private static readonly float[][] PlanterSpots = { new[] { 1.1f, 2.5f }, new[] { 7.9f, 2.5f }, new[] { 1.1f, 5.7f }, new[] { 7.9f, 5.7f } };
        private static readonly float[][] StallSpots = { new[] { 2.5f, 7.4f }, new[] { 6.5f, 7.4f } };
        private static readonly float[][] YardSpots = { new[] { 1.1f, 8.4f, 180f }, new[] { 7.9f, 8.4f, 180f } };

        private void FitOut(Mall m, float x0, Shop shop)
        {
            // The shopkeeper stands near the back facing the door, with nothing in between.
            var keeper = _vendors.Vendors.FirstOrDefault(v => v.Mall == m.Id && v.Shop == shop.Id);
            if (keeper == null)
            {
                keeper = new Vendor { Id = "v" + _vendors.NextId++, Shop = shop.Id, Mall = m.Id };
                _vendors.Vendors.Add(keeper);
                SaveVendors();
            }
            var kp = MallPoint(m, x0 + 4.5f, 0f, 7.0f);
            keeper.Monument = "";
            keeper.X = kp.x; keeper.Y = kp.y; keeper.Z = kp.z;
            keeper.Yaw = MallYaw(m, 180f);
            keeper.Map = m.Map;

            var outdoor = shop.Outdoor == true;
            if (!outdoor)
            {
                CeilingLight(m, x0 + 4.5f, 2.4f);
                CeilingLight(m, x0 + 4.5f, 6.2f);
                // Wall lights light up the racks and displays round the sides.
                WallLight(m, x0 + WallHalf, 4.55f, 90f);
                WallLight(m, x0 + ShopWidth - WallHalf, 4.55f, -90f);
                WallLight(m, x0 + 2.2f, Depth - WallHalf, 180f);
                WallLight(m, x0 + 6.8f, Depth - WallHalf, 180f);
            }

            var guns = new List<ItemDefinition>();
            int floor = 0, planters = 0, stalls = 0;
            foreach (var entry in shop.Displays)
            {
                var names = (entry ?? "").Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
                var defs = names.Select(ItemManager.FindItemDefinition).Where(d => d != null).ToList();
                if (defs.Count == 0) continue;
                if (defs.Count == 1 && WorldModelRackMountConfig.GetForItemDef(defs[0]) != null) { guns.Add(defs[0]); continue; }
                if (outdoor && defs.Count == 1 && defs[0].shortname.StartsWith("seed.") && planters < PlanterSpots.Length)
                {
                    var ps = PlanterSpots[planters++];
                    Planter(m, x0 + ps[0], ps[1], defs[0]);
                    continue;
                }
                if (outdoor && defs.All(d => d.category == ItemCategory.Food) && stalls < StallSpots.Length)
                {
                    var ss = StallSpots[stalls++];
                    Stall(m, x0 + ss[0], ss[1], defs, ss[0] < 4.5f);
                    continue;
                }
                var spots = outdoor ? YardSpots : FloorSpots;
                if (floor >= spots.Length) continue;
                var spot = spots[floor];
                var pos = MallPoint(m, x0 + spot[0], 0f, spot[1]);
                if (defs.All(d => d.ItemModWearable != null))
                {
                    var mq = Place(m, _mannequinPrefab, pos, MallYaw(m, spot[2])) as StorageContainer;
                    if (mq == null) continue;
                    foreach (var d in defs)
                    {
                        var item = ItemManager.Create(d, 1);
                        if (item != null && !item.MoveToContainer(mq.inventory)) item.Remove();
                    }
                    mq.SendNetworkUpdate();
                    floor++;
                    continue;
                }
                var prefab = DeployPrefab(defs[0].shortname);
                if (prefab == null) continue;
                if (Place(m, prefab, pos, MallYaw(m, spot[2])) != null) floor++;
            }
            if (guns.Count > 0) FillRacks(m, x0, guns);
        }

        // An electric ceiling light hung under the roof, switched on without wires.
        private void CeilingLight(Mall m, float x, float z)
        {
            if (_lightPrefab == null) return;
            var light = Place(m, _lightPrefab, MallPoint(m, x, _ceiling - _lightBounds.max.y, z), MallYaw(m, 0f)) as IOEntity;
            if (light == null) return;
            PowerUp(light);
        }

        // A light on the inside of a wall (x, z on the wall's face), shining into the shop, above head height.
        private void WallLight(Mall m, float x, float z, float facing)
        {
            if (_wallLightPrefab == null) return;
            var forward = Quaternion.Euler(0f, m.Yaw + facing, 0f) * Vector3.forward;
            var pos = MallPoint(m, x, 2.45f, z) + forward * Mathf.Max(0f, -_wallLightBounds.min.z);
            var light = Place(m, _wallLightPrefab, pos, MallYaw(m, facing)) as IOEntity;
            if (light != null) PowerUp(light);
        }

        // Power without wires, as if a battery were plugged into the main input, and again a moment later in case the
        // electric system's first tick after spawning reset it.
        private void PowerUp(IOEntity e)
        {
            Action power = () =>
            {
                if (e == null || e.IsDestroyed) return;
                e.UpdateFromInput(e.ConsumptionAmount() + 10, 0);
                using (var flags = e.StartSetFlags(BaseEntity.FlagsUpdateMode.SendNetworkUpdate))
                    flags.Set(BaseEntity.Flags.Reserved8, true);
                e.SendNetworkUpdate();
            };
            power();
            timer.Once(2f, power);
        }

        private static Color ShopColor(string rgb)
        {
            var p = (rgb ?? "").Split(' ');
            float r, g, b;
            if (p.Length >= 3 && float.TryParse(p[0], NumberStyles.Float, CultureInfo.InvariantCulture, out r)
                && float.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out g)
                && float.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out b))
                return new Color(r, g, b, 1f);
            return Color.white;
        }

        // Yaw (in the mall's frame) that lays a prefab's longer side along the mall's x axis, or along z.
        private static float AlongYaw(Bounds b, bool alongX) => (b.size.x >= b.size.z) == alongX ? 0f : 90f;

        private void Planter(Mall m, float x, float z, ItemDefinition seed)
        {
            var planter = Place(m, _planterPrefab, MallPoint(m, x, 0f, z), MallYaw(m, AlongYaw(_planterBounds, false)));
            var crop = DeployPrefab(seed.shortname);
            if (planter == null || crop == null) return;
            // Three plants along the planter, standing in the soil.
            var length = Mathf.Max(_planterBounds.size.x, _planterBounds.size.z);
            var soil = Mathf.Max(0.1f, _planterBounds.max.y - 0.1f);
            for (int k = -1; k <= 1; k++)
            {
                var pos = MallPoint(m, x, soil, z + k * length / 3.2f);
                var g = GameManager.server.CreateEntity(crop, pos, Quaternion.Euler(0f, UnityEngine.Random.Range(0f, 360f), 0f)) as GrowableEntity;
                if (g == null) continue;
                g.enableSaving = false;
                g.SetParent(planter, true);
                g.Spawn();
                g.ChangeState(PlantProperties.State.Ripe, true);
                g.SetGrowing(false);
                g.SendNetworkUpdate();
                Track(m, g);
            }
        }

        // A market table with food laid out along it and a lit lantern on the end away from the shopkeeper.
        private void Stall(Mall m, float x, float z, List<ItemDefinition> foods, bool outerIsLeft)
        {
            var table = Place(m, _tablePrefab, MallPoint(m, x, 0f, z), MallYaw(m, AlongYaw(_tableBounds, true)));
            if (table == null) return;
            var length = Mathf.Max(_tableBounds.size.x, _tableBounds.size.z);
            var top = _tableBounds.max.y;
            var outer = outerIsLeft ? -1f : 1f;
            if (_lanternPrefab != null)
            {
                var lantern = Place(m, _lanternPrefab, MallPoint(m, x + outer * length * 0.38f, top - _lanternBounds.min.y, z), MallYaw(m, 180f)) as BaseOven;
                if (lantern != null)
                {
                    var fuel = ItemManager.CreateByName("lowgradefuel", 1);
                    if (fuel != null && !fuel.MoveToContainer(lantern.inventory)) fuel.Remove();
                    lantern.StartCooking();
                }
            }
            // Food fills the rest of the table, from the lantern towards the shopkeeper.
            var from = outer * length * 0.22f;
            var to = -outer * length * 0.42f;
            for (int k = 0; k < foods.Count; k++)
            {
                var t = foods.Count == 1 ? 0.5f : k / (float)(foods.Count - 1);
                var pos = MallPoint(m, x + Mathf.Lerp(from, to, t), top + 0.08f, z + (k % 2 == 0 ? -0.1f : 0.12f));
                var item = ItemManager.Create(foods[k], 1);
                if (item == null) continue;
                var e = item.CreateWorldObject(pos, Quaternion.Euler(0f, UnityEngine.Random.Range(0f, 360f), 0f));
                var wi = e as WorldItem;
                if (wi == null) { item.Remove(); continue; }
                wi.EnableSaving(false);
                var dropped = wi as DroppedItem;
                if (dropped != null)
                {
                    dropped.NeverCombine = true;
                    dropped.CancelInvoke(dropped.IdleDestroy);
                    dropped.StickIn(); // frozen in place, like an arrow stuck in a wall
                }
                Track(m, wi);
            }
        }

        private void FillRacks(Mall m, float x0, List<ItemDefinition> guns)
        {
            if (_rackPrefab == null) return;
            // Every rack is filled, going round the display list again when it runs out, so the side walls get guns too.
            var next = 0;
            foreach (var spot in RackSpots)
            {
                var yaw = spot[2] + (_config.FlipWallRacks ? 180f : 0f);
                var facing = Quaternion.Euler(0f, m.Yaw + spot[2], 0f);
                // Back of the rack against the wall, bottom about a metre off the floor.
                var back = _rackCenter.z - _rackSize.z / 2f;
                var bottom = _rackCenter.y - _rackSize.y / 2f;
                var pos = MallPoint(m, x0 + spot[0], 0f, spot[1]) + facing * Vector3.forward * (_config.FlipWallRacks ? back : -back) + Vector3.up * (1.0f - bottom);
                var rack = Place(m, _rackPrefab, pos, Mathf.Repeat(m.Yaw + yaw, 360f)) as WeaponRack;
                if (rack == null) continue;
                var misses = 0;
                for (int tries = 0; tries < 40 && misses < guns.Count; tries++)
                {
                    var gun = guns[next++ % guns.Count];
                    if (Mount(rack, gun) == MountResult.Mounted) misses = 0;
                    else misses++;
                }
                rack.SendNetworkUpdateImmediate();
            }
        }

        private enum MountResult { Mounted, Full, Unsuitable }

        private static MountResult Mount(WeaponRack rack, ItemDefinition def)
        {
            var cfg = WorldModelRackMountConfig.GetForItemDef(def);
            if (cfg == null || !rack.CanAcceptWeaponType(cfg)) return MountResult.Unsuitable;
            var cell = -1;
            for (int y = rack.GridCellCountY - 1; y >= 0 && cell < 0; y -= 2)
                for (int x = 0; x < rack.GridCellCountX && cell < 0; x += 2)
                {
                    var c = rack.GetBestPlacementCellIndex(new Vector2Int(x, y), cfg, 0, null);
                    if (c >= 0) cell = c;
                }
            if (cell < 0) return MountResult.Full;
            var item = ItemManager.Create(def, 1);
            if (item == null) return MountResult.Unsuitable;
            if (!item.MoveToContainer(rack.inventory, -1, false) || item.position < 0 || item.position >= rack.gridSlots.Length)
            {
                item.Remove();
                return MountResult.Full;
            }
            var slot = rack.gridSlots[item.position];
            slot.SetItem(item, item.info, cell, 0);
            rack.SetGridCellContents(slot, false);
            return MountResult.Mounted;
        }

        private string BuildMall(BasePlayer player, List<Shop> shops)
        {
            var eyes = player.eyes != null ? player.eyes.rotation.eulerAngles.y : player.transform.eulerAngles.y;
            var yaw = Mathf.Round(eyes);
            var fwd = Quaternion.Euler(0f, yaw, 0f) * Vector3.forward;
            var right = Quaternion.Euler(0f, yaw, 0f) * Vector3.right;
            var width = ShopWidth * shops.Count;
            var corner = player.transform.position + fwd * 6f - right * (width / 2f);

            // Floor height: the highest ground under the footprint. Refuse water and steep slopes.
            float lo = float.MaxValue, hi = float.MinValue;
            for (float x = 0; x <= width; x += 1.5f)
                for (float z = 0; z <= Depth; z += 1.5f)
                {
                    var p = corner + right * x + fwd * z;
                    var h = TerrainMeta.HeightMap.GetHeight(p);
                    if (TerrainMeta.WaterMap != null && TerrainMeta.WaterMap.GetHeight(p) > h + 0.1f) return "Part of that spot is under water. Move to dry, flat ground.";
                    lo = Math.Min(lo, h);
                    hi = Math.Max(hi, h);
                }
            if (hi - lo > 2.5f) return $"The ground there drops {hi - lo:0.0} m across the mall, so the shops would stand on stilts. Find flatter ground (under 2.5 m) and try again.";

            var probe = new Mall { X = corner.x, Y = hi + 0.1f, Z = corner.z, Yaw = yaw, Shops = shops.Select(s => s.Id).ToList(), Map = MapKey() };
            if (InMall(MallPoint(probe, width / 2f, 0f, Depth / 2f), width / 2f + Depth)) return "That overlaps another strip mall. /store unbuild removes the nearest one.";
            var blockers = new List<BaseEntity>();
            Vis.Entities(MallPoint(probe, width / 2f, 0f, Depth / 2f), width / 2f + 2f, blockers);
            if (blockers.Any(e => e != null && e.OwnerID != 0 && (e is BuildingBlock || e is DecayEntity) && !_protected.Contains(e.net?.ID.Value ?? 0)))
                return "Someone has built there. Pick an empty spot.";

            probe.Id = "m" + _vendors.NextId++;
            _vendors.Malls.Add(probe);
            SpawnMall(probe);
            SaveVendors();
            SpawnAllVendors();
            var step = hi + 0.1f - TerrainMeta.HeightMap.GetHeight(corner + right * (ShopWidth / 2f) - fwd * 0.5f);
            var note = step > 0.9f ? $" The floor is {step:0.0} m above the ground at the front doors, so players may need to jump up; flatter ground fixes that." : "";
            return $"Built strip mall {probe.Id} with {string.Join(", ", shops.Select(s => s.Name))} at {Grid(corner)}.{note} /store unbuild removes it.";
        }

        private Mall NearestMall(BasePlayer player, float within)
        {
            Mall best = null;
            var bestD = within;
            foreach (var m in _vendors.Malls.Where(x => x.Map == MapKey()))
            {
                var d = Vector3.Distance(player.transform.position, MallPoint(m, MallWidth(m) / 2f, 0f, Depth / 2f));
                if (d < bestD) { bestD = d; best = m; }
            }
            return best;
        }

        private void RemoveMall(Mall m)
        {
            DespawnMall(m);
            foreach (var v in _vendors.Vendors.Where(v => v.Mall == m.Id).ToList())
            {
                DespawnVendor(v);
                _vendors.Vendors.Remove(v);
            }
            _vendors.Malls.Remove(m);
            SaveVendors();
        }

        private bool IsMallEntity(BaseEntity e) => e != null && e.net != null && _protected.Contains(e.net.ID.Value);

        // Nobody can hurt, loot, pick up, upgrade, rotate, demolish or build onto the mall.
        private object OnEntityTakeDamage(BaseCombatEntity entity, HitInfo info) => IsMallEntity(entity) ? (object)true : null;
        private object CanLootEntity(BasePlayer player, StorageContainer container) => IsMallEntity(container) ? (object)false : null;
        private object CanPickupEntity(BasePlayer player, BaseEntity entity) => IsMallEntity(entity) ? (object)false : null;
        private object OnStructureUpgrade(BaseCombatEntity entity, BasePlayer player, BuildingGrade.Enum grade) => IsMallEntity(entity) ? (object)true : null;
        private object OnStructureRotate(BaseCombatEntity entity, BasePlayer player) => IsMallEntity(entity) ? (object)true : null;
        private object OnStructureDemolish(BaseCombatEntity entity, BasePlayer player, bool immediate) => IsMallEntity(entity) ? (object)true : null;
        private object OnStructureRepair(BaseCombatEntity entity, BasePlayer player) => IsMallEntity(entity) ? (object)true : null;
        // Rust passes the racked gun's Item (not the rack slot) to these hooks. Taking a gun off a display rack puts it in
        // the shop's cart instead; the rack is re-sent so the gun shows on the wall again.
        private object OnRackedWeaponTake(Item item, BasePlayer player, WeaponRack rack)
        {
            if (!IsMallEntity(rack)) return null;
            Resend(rack);
            if (player != null && item != null) Offer(player, rack.transform.position, new[] { item.info.shortname });
            return true;
        }

        private object OnRackedWeaponSwap(Item item, WeaponRackSlot slot, BasePlayer player, WeaponRack rack)
        {
            if (!IsMallEntity(rack)) return null;
            Resend(rack);
            if (player != null && slot?.ItemDef != null) Offer(player, rack.transform.position, new[] { slot.ItemDef.shortname });
            return true;
        }

        private object OnRackedWeaponUnload(Item item, BasePlayer player, WeaponRack rack) => IsMallEntity(rack) ? (object)true : null;
        private object OnRackedWeaponLoad(Item item, ItemDefinition ammo, BasePlayer player, WeaponRack rack) => IsMallEntity(rack) ? (object)true : null;
        private object OnRackedWeaponMount(Item item, BasePlayer player, WeaponRack rack) => IsMallEntity(rack) ? (object)false : null;
        private object CanUpdateSign(BasePlayer player, Signage sign) => IsMallEntity(sign) ? (object)false : null;
        private object CanMannequinChangePose(Mannequin mannequin, BasePlayer player) => IsMallEntity(mannequin) ? (object)false : null;

        // Trying to take a mannequin's outfit, a crop or food off a stall puts it in the cart the same way.
        private object CanMannequinSwap(Mannequin mannequin, BasePlayer player)
        {
            if (!IsMallEntity(mannequin)) return null;
            if (player != null) Offer(player, mannequin.transform.position, mannequin.inventory.itemList.Select(i => i.info.shortname).ToList());
            return false;
        }

        private object OnGrowableGather(GrowableEntity plant, BasePlayer player, bool eat)
        {
            if (!IsMallEntity(plant)) return null;
            var produce = plant.Properties?.pickupItem;
            if (player != null && produce != null) Offer(player, plant.transform.position, new[] { produce.shortname });
            return true;
        }

        private object CanTakeCutting(BasePlayer player, GrowableEntity plant) => IsMallEntity(plant) ? (object)false : null;
        private object OnGrowableStateChange(GrowableEntity plant, PlantProperties.State state) => IsMallEntity(plant) ? (object)true : null;

        private object OnItemPickup(Item item, BasePlayer player)
        {
            var world = item?.GetWorldEntity();
            if (!IsMallEntity(world)) return null;
            if (player != null) Offer(player, world.transform.position, new[] { item.info.shortname });
            return true;
        }

        // Market lanterns burn forever and can't be switched off.
        private object OnFuelConsume(BaseOven oven, Item fuel, ItemModBurnable burnable) => IsMallEntity(oven) ? (object)true : null;
        private object OnOvenToggle(BaseOven oven, BasePlayer player) => IsMallEntity(oven) ? (object)true : null;

        // The player's game shows the gun leaving the rack before the server answers, so the rack is re-sent straight
        // away and again shortly after to put it back on the wall.
        private void Resend(BaseEntity e)
        {
            Action send = () => { if (e != null && !e.IsDestroyed) e.SendNetworkUpdateImmediate(); };
            send();
            NextTick(() => send());
            timer.Once(0.3f, send);
            timer.Once(1f, send);
        }

        private Shop ShopAt(Vector3 pos, out Mall mall)
        {
            mall = null;
            foreach (var m in _vendors.Malls)
            {
                if (m.Map != MapKey()) continue;
                var local = Quaternion.Inverse(MallRot(m)) * (pos - new Vector3(m.X, m.Y, m.Z));
                if (local.x < -1f || local.x > MallWidth(m) + 1f || local.z < -1f || local.z > Depth + 1f) continue;
                mall = m;
                var s = Mathf.Clamp(Mathf.FloorToInt(local.x / ShopWidth), 0, m.Shops.Count - 1);
                return FindShop(m.Shops[s]);
            }
            return null;
        }

        // Opens the shop a display belongs to with those items already in the cart.
        private void Offer(BasePlayer player, Vector3 pos, IEnumerable<string> shortnames)
        {
            Mall mall;
            var shop = ShopAt(pos, out mall);
            if (shop == null || !shop.Enabled) { Say(player, "That's on display. Buy it from the shopkeeper."); return; }
            var vendor = _vendors.Vendors.FirstOrDefault(x => x.Mall == mall.Id && x.Shop == shop.Id);
            View v;
            if (!_views.TryGetValue(player.userID.Get(), out v)) _views[player.userID.Get()] = v = new View();
            if (v.Shop != shop.Id) { v.Category = ""; v.Page = 0; }
            v.Shop = shop.Id;
            var cart = CartOf(v);
            var added = new List<string>();
            foreach (var name in shortnames.Distinct())
            {
                var idx = shop.Items.FindIndex(i => i.Item == name && !i.Blueprint);
                if (idx < 0) continue;
                var def = ItemManager.FindItemDefinition(name);
                if (def == null) continue;
                if (cart.All(l => l.Index != idx))
                {
                    if (cart.Count >= MaxLines) break;
                    cart.Add(new CartLine { Index = idx, Qty = 1 });
                }
                added.Add(NameOf(shop.Items[idx], def));
            }
            var notice = added.Count > 2 ? $"That's on display. Its {added.Count} items are in your cart."
                : added.Count > 0 ? $"That's on display. {string.Join(" and ", added)} {(added.Count == 1 ? "is" : "are")} in your cart."
                : "That's on display and isn't for sale here.";
            Open(player, shop, vendor != null ? KeeperName(vendor, shop) : shop.Keeper, notice, added.Count > 0);
        }

        private object CanBuild(Planner planner, Construction prefab, Construction.Target target)
        {
            var player = planner?.GetOwnerPlayer();
            if (player == null || _vendors.Malls.Count == 0) return null;
            var pos = target.entity != null ? target.entity.transform.position : target.position;
            if (!IsMallEntity(target.entity) && !InMall(pos, 4f)) return null;
            Say(player, "You can't build on or right next to the shops.");
            return false;
        }

        #endregion

        // The credit card picture in the store window (made by store/card/make_card.py), sent to players once from file storage.
        private uint _cardImage;

        private void StoreCardImage()
        {
            try { _cardImage = FileStorage.server.Store(Convert.FromBase64String(CardImage), FileStorage.Type.png, CommunityEntity.ServerInstance.net.ID, 0); }
            catch (Exception ex) { PrintWarning($"Couldn't load the credit card picture: {ex.Message}"); }
        }

        private const string CardImage = "iVBORw0KGgoAAAANSUhEUgAAAPAAAACYCAMAAADtLJSXAAAWfmNhQlgAABZ+anVtYgAAAB5qdW1kYzJwYQARABCAAACqADibcQNjMnBhAAAAFlhqdW1iAAAAR2p1bWRjMm1hABEAEIAAAKoAOJtxA3VybjpjMnBhOjIyNTYwNmE2LTk3NjAtNDNmNy1hOGVhLTJlMjk1MWQ2NzgzZgAAAAOTanVtYgAAAClqdW1kYzJhcwARABCAAACqADibcQNjMnBhLmFzc2VydGlvbnMAAAAAuGp1bWIAAABEanVtZGNib3IAEQAQgAAAqgA4m3ETYzJwYS5pbmdyZWRpZW50LnYzAAAAABhjMnNoKriTYlqamrepP22z1DdcswAAAGxjYm9yo2lkYzpmb3JtYXRpaW1hZ2UvcG5namluc3RhbmNlSUR4LHhtcDppaWQ6YzVlZDUxOTQtOWU4MC00Yjg5LWExN2EtYmVmNjNiNGQyZDRjbHJlbGF0aW9uc2hpcGhwYXJlbnRPZgAAAeJqdW1iAAAAQWp1bWRjYm9yABEAEIAAAKoAOJtxE2MycGEuYWN0aW9ucy52MgAAAAAYYzJzaBOqo47cGKYhRjk8wYBSUt8AAAGZY2JvcqJnYWN0aW9uc4KiZmFjdGlvbmtjMnBhLm9wZW5lZGpwYXJhbWV0ZXJzoWtpbmdyZWRpZW50c4GiY3VybHgtc2VsZiNqdW1iZj1jMnBhLmFzc2VydGlvbnMvYzJwYS5pbmdyZWRpZW50LnYzZGhhc2hYIK1eMv54lwiv+0oE1X1BbcmjGgNyBpTlJtq7bRfMyRwIpGZhY3Rpb254HWNvbS5hbnRocm9waWMuY2xhdWRlLnByb3ZpZGVkanBhcmFtZXRlcnOheB9jb20uYW50aHJvcGljLm9yaWdpbi1jb25maWRlbmNlZ3Vua25vd25rZGVzY3JpcHRpb254ZkNsYXVkZSBwcm92aWRlZCB0aGlzIGZpbGUgYXQgdGhlIHJlcXVlc3Qgb2YgYSB1c2VyIGFuZCBtYXkgaGF2ZSBjcmVhdGVkIG9yIG1vZGlmaWVkIHRoZSBmaWxlIGNvbnRlbnRzLm1zb2Z0d2FyZUFnZW50oWRuYW1lZkNsYXVkZXJhbGxBY3Rpb25zSW5jbHVkZWT1AAAAyGp1bWIAAABAanVtZGNib3IAEQAQgAAAqgA4m3ETYzJwYS5oYXNoLmRhdGEAAAAAGGMyc2iu9oCLDIeagAe/AdEc2wgpAAAAgGNib3KlY2FsZ2ZzaGEyNTZjcGFkTQAAAAAAAAAAAAAAAABkaGFzaFggEnK/AE9KlrMNRfVNS9SMOK0vwVf1TUnp+BggONTrXWRkbmFtZW5qdW1iZiBtYW5pZmVzdGpleGNsdXNpb25zgaJlc3RhcnQYIWZsZW5ndGgZFooAAAI+anVtYgAAACdqdW1kYzJjbAARABCAAACqADibcQNjMnBhLmNsYWltLnYyAAAAAg9jYm9ypWNhbGdmc2hhMjU2aXNpZ25hdHVyZXhNc2VsZiNqdW1iZj0vYzJwYS91cm46YzJwYToyMjU2MDZhNi05NzYwLTQzZjctYThlYS0yZTI5NTFkNjc4M2YvYzJwYS5zaWduYXR1cmVqaW5zdGFuY2VJRHgseG1wOmlpZDpiYTc4ZWQ1Ny02N2I4LTRiNTktYWJmYS1jOTE3OTQwZTVmODByY3JlYXRlZF9hc3NlcnRpb25zg6JjdXJseC1zZWxmI2p1bWJmPWMycGEuYXNzZXJ0aW9ucy9jMnBhLmluZ3JlZGllbnQudjNkaGFzaFggrV4y/niXCK/7SgTVfUFtyaMaA3IGlOUm2rttF8zJHAiiY3VybHgqc2VsZiNqdW1iZj1jMnBhLmFzc2VydGlvbnMvYzJwYS5hY3Rpb25zLnYyZGhhc2hYIO9HtY1kaJQqnab29B3G8JISjejDFmigCBi3YtZ8tZhBomN1cmx4KXNlbGYjanVtYmY9YzJwYS5hc3NlcnRpb25zL2MycGEuaGFzaC5kYXRhZGhhc2hYIB5QC+xpnZeRTSJ8PeUg4MXaTQ3WHVlQt0LBuLBvhfqPdGNsYWltX2dlbmVyYXRvcl9pbmZvo2RuYW1lb0FudGhyb3BpYyBGaWxlc2d2ZXJzaW9uZTEuMC4wa3NwZWNWZXJzaW9uZTIuNC4wAAAQOGp1bWIAAAAoanVtZGMyY3MAEQAQgAAAqgA4m3EDYzJwYS5zaWduYXR1cmUAAAAQCGNib3LShFkCEqIBJhghWQIKMIICBjCCAY2gAwIBAgIUQOWgCu7COdC+uIP6BkIFPWdVEwAwCgYIKoZIzj0EAwMwSTEXMBUGA1UEChMOQW50aHJvcGljLCBQQkMxLjAsBgNVBAMTJUFudGhyb3BpYyBDb250ZW50IENyZWRlbnRpYWxzIFJvb3QgQ0EwHhcNMjYwODA3MTg0MzU2WhcNMjgwODA2MTk0MzU2WjBEMRcwFQYDVQQKEw5BbnRocm9waWMsIFBCQzEpMCcGA1UEAxMgQW50aHJvcGljIENsYXVkZSBDb250ZW50IFNpZ25pbmcwWTATBgcqhkjOPQIBBggqhkjOPQMBBwNCAASYegpry1AYBRTVNL1CpTlbROnY3dey+UrsF9C3phYrATN3ZHf93Mo8RQN0KOUuOn19P4oWNFWe5n2/She9N7eTo1gwVjAOBgNVHQ8BAf8EBAMCB4AwFQYDVR0lBA4wDAYKKwYBBAGD6F4CATAMBgNVHRMBAf8EAjAAMB8GA1UdIwQYMBaAFM5R4gSBTmRbI/jjxM+aPpzB11zCMAoGCCqGSM49BAMDA2cAMGQCMDFzHRSeAXrSy1WOzkbhPZ6Km2wGTmZ/2gK18k8BQGXyqz88Rdrz6CTX9flAnYNVxgIwcF9c3fVhqmJKpi+UhasNUMko69cyX6STPfta3Q8EjyzDjzoyrol46FP6VFHhvUcJoWNwYWRZDZ4AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA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";

        private static readonly Dictionary<string, string> NeonImages = new Dictionary<string, string>
        {
            ["guns"] = "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",
            ["clothes"] = "iVBORw0KGgoAAAANSUhEUgAAAgAAAAEACAMAAADyTj5VAAAAwFBMVEXI///H/v/H/v7G/v7F/f3C+vu++Pmv+vuu+fq+9ve28fWo5eyT2eCX1N2Q1d6IyNRnssZWn7VJmbRAjqk7hZ4qhagpf6A1eI44dIUleJgib40hZX8eXXUbVm0ZUGUXS18WRlkVQlQUQFATPU0ROkkQNUQPMT4OLjoNKzcMKTUMJzILJC4KICkIHCQIGSEHGB4HFhwGFBkFEhYEDxQEDhIEDBADCw0DCAsCBwkCBggBBAUBAwQBAgMAAQIAAQEAAACYuYeOAABbZ0lEQVR42u2dB38qOQ7AIW2XQEINkFBCSCO9kN7e9/9WZzVb9niGISGv3frud7f7HjAe629ZlmWp8CNP+0i09y+0jy+099+n/ZZdSwzYLNEWPiH96JPfXPsmEGY81Wu5P5ivfUkmX3/gnH2bC4JZAGS/zmve9uXBffvccxfdckvwF/ctxOFzAJhJl/q0l5T2/DK7feUtkw+MtpTOfa7lfYusd/7S4+b4cGTICIK5AUDp07Ofgva4oPY0Z3v8pvYQa49/bnMjRoggBB9zAQAL7tvL85MZm/v7+zvTpol2+5k2/Xq7/fXtt+2e7ghI7e7+3qD89EwM5AbAiP/tFaR/P729ub6+urqUdvE97TK7XfzXPjumV9c3t9N7ZiCKQAQAFL+RvhH+xcX52dnp6YlukwW2k/xt8l+be1hPT8/Ozs8vDASGAUQgDwBm7X95fLi7vTbCPzk+PNinNv4pbd9rP+eZ471YG/8FzQzhweHxyZlh4PYOEXifCcDHh1n7H+5uri7OTo4Oxru97n/tj2693fHB0cnZxdXN3cOTIeAjGwCj/l+e7s3sP5sc7hvpN6ubplX+a39mM7KrNg0D+4eTM6MF7h+fX18/sgAw6v/58f7m8nxyOELpV/7995//2h/c/v23ggyMDibnlzf3D2YZ+EgH4MNYfw/T64vTo71e6z/p/00MtHp7R6cX19MEAQVv/pvl//bq/ORwt1Ml6Zsv/9f+6CZirHZ2D0/Or27BEPiIA0Dyvzw73h+2qmX4WgXUR+c/Q+rPbR1YxivAQLnaGu4fn13e3vsEFLz1H+R/NDKL/78gfrN2dLvDkdoTRTdMo6y2N7t9cp82+plt77Ptl3VvTE8fGhE2q4DAv0aco6MEAQVl/5P8Uf2T+Iej8cHh0bH4F469dvSpdhxvGV6N8KNHv7Yd52i/R+dg5I4OD8YGgiaLtLMLBDzovYAD4O3lcXqF8i8TLsO9w+PJKbiSpJ377WzOdp7Wsp2aiY+f/bJ2nr/98s7RyJ2dTo4O9oak1MtIwJWxBJMAGAPg8e76/HjUoU+2Bkb8p+cXl9fXNze3N9KuqV1xizvqow79K92uXbvJ065zPTfXwUL+o4asN7iON+8lry4X3bI8/37P7LhdXV6cnx4f7g1aNK87o+Pz67tHtwgIAGAA3Fyc7HfN54yuaA0PjPgvr2+nd/eu2WNBPnyKySgq1ayTqzv9BGoP/r+Gj71Ne2wupK7naalvMfskMOeTb/K3Gd93HaNzQBw5OM4DBMCwN8tAudrdP7m4ATPABwAWgNuL08Nhs/LPP5V6d/dwcm52jXf6XBxOyUUi/hun9ThtsO7ubBdR3lmn7w/useFzb35Ki8jc673G1D82z4Jh9uPyf0v3jjsmEQ0wdnfo2D3Y7dZBts3h4enFrVsEGID3lyezABzttqpIyejw1GwZ8Rjx+enZBmNQqIQ3Iz04Z8x6bwSV9FUARjT8gx+b+tyfI3/V/wDgQE9FAwM+9cCs7wUawM19HlIaTvMfM3x36NwZoXavtnaPaBHQAHzADuBisgcGAHzkEJxG949PEnjkAOBXlTeMa6ZUCOBLSdXvNMBTKgBqhNOfm4nD9adabKbdSUvMf9fBmPhyrjRzr15B39SIykjeT6/PTw9xehszYG+iVAAD8Pp0d3V22CNEhij/hyeMOEP5B3KQORg1fZKmiAzfDQ+Jmk53M1eBB0dd2nN9Cy2PeZa7xeSkFlqvaTgjwsr3iIh0Iz/hG6a2c9gzDwGICgMEHgwBJwdDUvC9w7MrowLeFQDvL6AARi1cJAYHk3OU/0sofxY/vqR0KWKhBj2jl4AxuQmUFnX43lu1ItF6HnX+c9MM9xRz/OLyE1FKgZy48zELMCn6SCfy7FpioKTuBOgL3DcYUkGAlABJEAiYHAzQxGuNlAoo8Apwfw0KADREd+/4/ErJn5SxFj+/o9uNRTf16l3gw/hf+lqguabTOAEP/to/67nKnTBvONn5xSzfhHsZNduSq7WV+UKi2eJ7wHhPMfxLGJh6SoBk+DC9Ojve66KIjQq4vn+i6BAC4NVsAU7G3c1/yUoEd2FE/lr85h293iT9EeGcTFIuyEYQsMG5ofiD58b8IDMdSZ9qTigEQcJiF+F7Yv/6A2MvEnMAQd+kZ4yAT8C97PL+3eyOTy5ueQ1AAN6fH27Oj3fhL1EBXN8/hvLnTSW9qJIC9eFUN69T2YBbBmglSMRmB+Kf/dyzb3LMBbPtKmHhiGpKE/siXI5nkZfm1z6zDDACU7UM8CoAfj5UAZXm7vH5jVkDBICPt6f7q7ODHv7dwCgAXgDU8i9OBXpRfknpjQ7Z5GDE07MQgzTNdcXITu+8XaFa+p34E8+dJJ4rA+K308wWDUI1f3x6Eg6zjPOFU7g3bmGyr5n+bPfr2Y2fRk/0f+pETkjsm7tZhz1jAlgJMAGwCIAKGOAs7x2cXfEagAC8PE5lBWBXoZY/z0SWA74n9Yp7445A1NHOiQ9nchpYvYUI0BbGEXB/r8R/i+JPeS6egRypB8dGNzusOMcBleba9ttaM1c8988VnfDUSDx1rgMwh4j+NfqFxGET/RR1jrp2pZSAJgA8PaOOrAFTWgMKsgegFcD81eTiBhWA6H8rf5qGIgYUgu3LITe/U04XxCaijOXlpVUC3t5aNlZ29mc8V54sA+tHk3/9kO5Y3kjG+Vw74LX09Rz9xBmhj0cIT/DO7s3hCycWAVarAQGP9zcXEzb0do/NPgAPBApgAoIJwHuA3j6sAM+R+S/TH4+YQP1if7ALB7qpTnkwa5VNQuKwdddhjwC7tWL16p57lP7cw+SJ7VGClc80EQ9326oBsc8FzhONp2pzPyr+Hvqt9/fVq1PnoG+CgEfAE/hzH6aXp/uyD0Aj4F0AMJvAA9kDgJPgWc1/tfw7MVjpY0ckhp/+SaThJqSaEpr1CXdYKwFcCO6c8g+ww+fyIOibA96D1RjGWflU4x+mblsErIGjdVNEqvM/yn8L+xv80hTwYaP/GYJjN6IhAUaesBOUfcCB2Qg+Q5A4A3B1agD4B0yAI9gDPL34+l9Nf/OiIgbujQ1+cSE+VhiK48RMYGbNUF5wh29uA1c6PZaei+I/Sj7XRcQwCGoIzWftsO2nXECJhyDBEIefd8M8UQuBKH9Hp8Vz/zP3XCzL6i3sRMO3ToYD0YgLAjiiZFoRAbQIPD2IEfCPAeD0ygLwDn5goxsAANgE3phNoFsAcP7LyTK/p0gBuoN92HXN9smO2GFyIrqxPJ4wslfiFEA9wJvrax+7xHNHwXNHDoN9KwUXVJYZQzUz5gp++ICH+VgWXLFujfKXWXGwP3cgG8wer1tjBXMge3nhoffqOOAOARpR1gHiFX68x40gAmBW+rsnBcClBuABPchO/rdK/oj54SFJgbsz1E13yWkCNRMt6ApZawgoAG5k9XfTi567F32ufrCOM7Sjvuu3QAS7M9rIvhJ2W2yuM7H8pH8H0Uma9ym6Y1bD82uMvLceSONXhy8wAkCnJsCaAcYKZAAq3f0TC8CHAWB6ebJPf7E3gXCBJ2cAyPwnNQzTEMQg0seu9HXzurRn7YJ9C/GefUEcTE3stXKu3firznHyuf3Ic4dJ2fJ8STRv1IezG/0yzzQE9+RUNjdkmlo8k5M05zO8nlnV496DJW9etydN3h2+gEOKfcMRvfQJeIRtgAJgGgUAd4GwYKTIH17TyAHfEmXf63Za1JrNpvnfLvSJeyQQyFQc7Tp48UVHo/GBRwAjcHOjjH8zwDS8B2N+7hCfKw+V5+JIDELZyoh9sunvUrdNr8c40djZQbsb5HNMndNPpA7N3YOBPMvnBwccRrypWqsD704929sjJTAhQwAIcIsA7gMZgHEWAPePev7fsvwN6TL9QQwghZ7pSb1erW7YVq3W6k2UBovYcgwMD7rdlmt04WD34OhocmLtFutgZceqrP4WO/vcmv/cun0u6wJWlX39yHxNDW0Pn4WEqV4Px7IM8B6d9NMYYrA7/m91etCh3vydAKb7Wi8MaLqZ3phX31ANhryJr44IoBJQOkCpAHIEeAB8FD5iADwyAEr+5yR/Mw1JDGZkmij78npJtfUySsN0qJ+YjcOu+ULQ6t0xdveU9i6CgHX9ofpH7Zr3ucSAtH7HH625WrXW2mnVPM6ozyNebMWtgdbf/kjeT/9CByb0pzphnt4fOJWJ0m/U4YeSrw4UwJAPQalGCQCBzgGAKACS/6Wd/ygHEEO3BV2BjqyuLBWLxSXT4P9WVtdMl0yH6iwKXtXwRVpVr+emQfgZdPdEdq/Ou+qW/6PYc9fC55bUc60W7feSz8zf1qvNTjPx/fVqd3fMFjdH35Ni3O1GPts0vek1P9OJ9Y2mhRlW/Q68ehlffVm9enEZh7yMr44I7I0dAXoRyAZgHADgNgBkiGv5kxjWjeyXi9iWlldX7b8Ul4001h0CA1Fh/Va1tOS3khnM/cNDawYwAVey+dfLjnluL3zuSvS5HbKNoPWaG+Ez87biUmmj0WmY79Nguz53hntMwNExe36gh8OO/37mW6WN5k6vt/OpTpQ26l1rFJD413m68duaV1+yAwGvXmv1fALQDBAV8AC7gHkAoAXAkz+o4UGvg30xXSnYOb9eRjZpXhbMn5IoyCLkhsIoLMHnSXMUEADpLW8G7Zm6zH9YXUH86rn0/gi+ee46PrcIz10mBDo7YiTD2BeLK/zM/A1/0IigVTffD/vcGowsAUdW/qMBAF4gZWQ+u7pUML/QAAAa+kfyNPOgpTUDAIIMr9GyQ17AV6d3p1dfXaYhX1svm+WpDwTsi1rFRYBUwMPDXV4AHvQCgJaYlf+useSatY2y6YvpShHAK5PxV61ubmxUygCp6Y8Zh1J5o9bs9JxpS7NxrUxLXHl9BQEYut6euwASudYiZoeRv3ouAl+u4HNr/FwQWYGeWzXPZQRw7JdL5XkX4Eq5tFxcQwDWiktr62Xd59Zgd8QOAZY/dhEBWFq1n10FhBrbOzvYCfMj+R9vHgRP7/BLdJq1TW/IK5WNTbA28NXh3ZeBjJW1MioBJsDY1rQIsB2IANhdwEUKAMcWAF4AUP4nIn8jh3q1XFqFvpjFB5aeWr1ebxhLuWH+34hjQ/oDQ1HFd2D5IwAr6+YLjUajXiuvwr/YC4hyCY2PV1D+ev7r55b0c+HB5rnAACGwispnB9s2qPBV0w/XGunNfahWNd1jAEw3N2v0p9RnAIC8GNZNOwbV2IL3K23wZzcIIQPAdh1fnH/E9CCrE/jX8CCjf4himP4GJzvkFUM9vwd1dROmHQ/5BlzqIwJoEbi8EivgPh8A5zd3HIzDCgD9f578V2kWltYrIMxmq21by0jDjN06SQqI1OqYhWGm585OhwYFAPBUgD1fsfbfWD23mHjutnvuJiOwhM9tbSMAIMDVcq3Z3g7ajm7+X5kfbNYEgAp8v9pomT+XPrdooRUCDsREYQDq8FlDzroCgH4k0YmUBgsPAoAN5I8vVjTajV69ueUaDnmFJwfov2ZvuEuz6uTUqQAA4DoVgAsBYM8CAAoAdoCkiXGV0/I3K46ZhCgFf+i2YJ9aYVGsrsNcDAHA12rQYHYGbLOcnKoDNm3/+9yZ51aizwXXgEFAZkIVxbDdZgBaUbnHGn2MAKjUmwxAc9vN5GqrTwBYVz3InwFYLm2C1t9uNzYJgLbtBPxInh4wMQQPKDF+9eVVJF8LfwsGHBhQQ25etj+kQfVVQF4ArpGWe2sB8gKAq5zIAdW7EQNJoe037A/pLCbAvhYBsO0BsCtGq1MB587/48vfPTfSthABXCrN5zbrTTf2tWbK1I+Lf7sdAgCTF1+gSABoAtD/azrZb6K9YQBA8hCAShKA7Z2ZXdEAAItrS2TcbDrxi7PKqgEz5Pjqy7B7QDMAZpVYAQjANATgDQF4iwIQLgBa/sCiWelYDFtt6I1ZkLg7LApD5LKdiztKHRMA2w4Adl2cSEDL+bk9dvDlHzx3q6VHgRCAmcCkbILeb5MAa3qdSgy/p0loSWlEANh2AKDHRU46UP6wS2EAQOYBABVvCUj0JGjtlgCAy8GmMT3hhUDzsc43Ro80MAZgAJp1fnXTwbpZBJQKQF8AxNjkBGB6bxUA7QBYEFYOZGjzkLPtB426Y3pjhNMwk3Ftheeitx77ALTEZj1Gh7A7X52IATjUz92k5241E6MAj0Xl4xGwxWNfq4fNrCLMZbvZSPwtGYFZABABeFjHLmoGYMMHAHrl/QgsVrMaWyAAT6NqthMof5r+sOJvVoztT61SAZsQ3l5evWie1Oo7FUDuQAgM0ABMAgAmAQBkAegFYHfYazr54zRk+8P1pgxZhYyRgpyCTkIdYN/caUK1ng6G4rg4sUfsdgFA4yr23FptsxKMAjKA5BlDgHSP6YgZe9gGmk8kPK2mVyj/RtJRW6FtYABAWwDoydHLHh/+ggLoawDa6QAYm24z5zawDcaI+a4RqpF/A+c5GDpm92+2/9jW1syWaLOKY44EGFhMF1gF0EbgikNDkgC8pwAQVQD9jpIDaNStBm6+VG9Md9bXZaVqgvEKklhjIzwBQFEMKgTg+EQdsKsFwHsuyL9pn7uqR0E0pCUAngsCREdQ2EpmMSFV1Kpt4Dv4DbwZ6QDg4ZtEcMjJqAfAVhSANpkXkedFng4/BArA6NGVEsmfjKuVZdjtYjP/sLyyCmNOf23sAFwEcGVVa0AMgEcC4P3t9TEBAJwCewpgBEc54MgrgBwaoIZh0SmrowByTeNOtcq0EgErZJC1ZSlsBgaVGAETOWC38h+j48l8cpmeC9zRYuc9lxyDljwkABdDMyzwTN+VS1+BrWITDbIm9DLyieJSBIA1AYAWATmqHyoAwAZopwFgGuj0peIsPzQ4outow5RIq6OW53UVTMJVAgUQKKyIgoANA6oA3AjYNeCavIG3uQG4cyagKACzyUEWl2GPjfJHGNkLucrconfY7NQIyCZIwvRvlZDZiq+nQ2cEcBD5qacAzHNXcT6z/MHSJ8/XsnssogeOpyarQhgmMEBBK66uRiYYrBBocoFAChElYUjejAPQ7HHEg0Rq8Em9BaCdAgCaL8jzUkQpBW0dfgh2I0VEGV6MZ1QB3b6QBRAdrwWxeGDOgcVYXDUbAbMG7B/wPoAAmN5e5QLg6lZOAWA7BgoAZiJOxCIsRqT/QbjsdmF38Ab6ANFXY4EkSSyXIkthEgDcCWr5WwWwZJ+LUK0tk7unVC7TY9FJb41T+1xYBAymYCQkllgYVOoIKtmV9dBbDN+qNlopAPR1vAYddYnbWQFQAAC2QgBgUVpFd26WL7qyARoDOwfvIRp+mcWP3kBygJLNCz+PH1krJtYAMgLyA3CnVgD0ATkFgCyy/MUxw27ZOjqAZBteYUkgsrAP28oCgGNs5ALBxLmA7XM3vOfiIGzSc9H9DDqBXI9NvRhuyi4hNLKLywwAjjHsFMPPwNda7SwAhlr+6QCgJbq0ogEwU6I2cyvQaLVpBYCNHb5VlTeEsNjR3s9Yw7z5K9GIowqA84p+CMA0DgDUhgkBmNIKcHFBCsBaAEUzl8soB6OM1p1jpt4kR1urobfh9SaZAau0NeVxiAGwK2EsFGRFEVaoAIaD1OdW5LnsAyQvyBpNBH5uAaYOxvZ4e2484fEBWCMHrnUObFNkjzVcfQB2ejYIcWhDvhwAW1EA1hUAYBa1sl0BLegz8FeE+dOgnwEFgBvChnME85RgFYBrRpGMgPG+tgKzALgNAJjKCoAmIG4BcCIW2ZIBY2NNNtvgJpdha0l3lq3WqphxAEE00wAYWAAw0PLkJKkA4Ln19Ofi1spNBBqHCikOHPXA67bNANQ9APgT7LaWX84AYOCCtTBWzwCwlh8A8QpluiQBAFhBcXLDjhBeid7QtgZZWubNccrVYIUpbTTFCpwXgCMGQK0A4Izp4UQ0ihgmSpuHV7v5LAI8R8VuaYgKaKQCINuAIwqyoxA7PmBjBWAXgEop+tztFq8NRfvcGjwXrY/k0U8qAD3VPO9lCICOc2D5fw6AbK90237TAsAzKyBgBVVADZc3s+1ZBU+A3gbgtSsDwFFOAMIVYGD24jgRcUfvvFPk6PcGjQkost4CmRmjFwQxG4BjukZGQUDsA7CKZ7sdPndHiQpdpviXsgjwqOAxwE4UgEYIQM9v2nsZAtDrOwQkaqP+PQDICoAALNPaGhBQXsN9Mm0MVnDR6A7cNuASbwkFANzOAEBWgANeAYwxsuwUgBFqkeUQDhpLYsVaASCINdBiUacKhbFZAOh6nQ0Ca9rntsPn7gSiYqc5PHfLqgAY9rYd5V42ACrUXn42EwAV6wIf3p4LgHpuAGj9JACsOvAajbFyDaCLNTgPuk0A8BIF4MwB4PYAPRiHIg9Em9wNaMiwGPo9NWhwFG6m7Vq5Jl51monNbADo1p0DAKPAYExZAcSeqxHYbrHXVEwmsofBnbLjq4voEtDYsdIPhBoHQH1UwfItAFQcAFENwLAXxIdU4LClJABnuQC4md7eKi8QOgHgCUsc69Amb4MRcCs6aDst9l6UoWgN6CSaiVkAjCXKVoJsSQF06LkVPM/fUs/1HiwCoBWCtxw4KuDOrZG1oFGJaYBGfFo3IgA0tkP8FFdzArA95xJAG5sAANQNECGLKmB1ZTmqAW4jALwbAF4DAC5vbq0bUEyAFjlLSHZNcTY0OnbQvLWwg84usysvrZu2hp6BUppfvS/hNXKV+EitAOa5y/LctnsuCaCfAI8nPY06LhhotmyH60UjAUDJAeBu9ugglm0fgIi5IABsLBoA3whcscfCeg3YXF/DCFk8IDODXsZIm30dFWTa5RwAODcgmgAQHM2BNW3yncLU8sZMmcNNMBnBV4sNvNWFtdkAIAESZetCLIpsyAXPdeBZYW23aNKj26lNfpPliLxSAfDepc9buzgAoQliWns2AFtJALZnAWB6areBeCji/EBuDYAzWXZ61fj2ylBpgMs4AK9pANywCcA2IAU7FfVJdyFhDLtLcb1eB00G3ZZyAkBhthIIjN71olp53HN7CVmx6lkqrNJcsz6RhMZOA8BDmfRZDIBN8hmFm/Z2i3x8eTXAVjsrMkj5AawjCGeA2duiK9ALDaSIhgbdYcOrbF0bEXBqAbiZGwA6CNolG3CFT09QdDAtOzs9dSPTXWCDYdsMTzwjBytedA3eFPbC7MUGlJXHPrcV7MKc5jGqBye9LMPgE1H2vTUXsgGw17HgTZohAMqLGwYTo4eZzj1yaIAmRTGluAPbAgC6gje9Qy46C9qs1sL4UPop1E59jLT6FAAXN95J4HiPbEAbWYegg0FIEd/+ZUz2iqobdVLDMM0IxOAKA8A+ZxTBDDB4DjTE5xZTnutdOhICaA3QMlAAaMuuFAdAvUwaAMXV9XLaXQKgLxOAOc4CmrZz9jCo2cADWD4NNks+QWCXA5E/imUoAJzNAODNA2DkAcCxQANwx+A1CTg/x0G2/jD/DqgM3E6r6YXZw3TZ2soCYCwAUJw1XkFBP7B5LsX0NtVze1HFg2tPqciHsG3cNsLa09nxtwz5AaDLLAoAuOGTepILV1OydwFb3mkgtThNm+Rya7I3w8YDVeDWhY0IwDsisPYTBHEAODL40wDwlRdYiiGSZxsDtGAFcPL3brHz1HFro43azQ0ArwDgfipa00Oe29Lgyb1TZ34CMgwAxtOqNSMTgOZOoM0GcsbvADAr20qxmBnHsbI+exuYSZEELG1yYBH4NgqrEmHRoCsAK2RZF/CWGKoCYmA2AKM8ANwEAFgbEAEwA7bM95Z4yGw+C02Ad6SyrTa1CQCcFXjoXbTgTUCJomPb6rn6we48ts9rAHhOWm2rbUPPXRoAPa3NhAAFALE/K5irZE99UwGYRZGLB5I1AOy+TY78xFsg63ATjiFYIggwGrO5lQBgciZxobkBuOYrwTYYBAFYkhsPdZJKY0flAhnp6KihsstDkzYSXjXw79kQACO1CbCB9iU06XaSmscmz6D9x6oKQnRdzQmA+00fAD503EwN4GCFXsVjqwwAGrMogpWEQ0LJ+bGEBFRqHALQgDgQhMBSQDYBnJC32tsWANwFTOSKqDHt5wHgIgQAVl8GQEbVpqfxMvIIAFoJZAHQjwCgA+29zceaBW/grzwoLTqQmQ+AbQfAYBYA263G7DAOjpdPBaCJgdQZrWxd2LAGNHQEhCDQgO3+JtwLLVkIJBoT3KQCwIG+JT4nAOIGEADQDRQCQEM2cvGxWQRkATDitGEkfwWA3XzQc9d8zbPrQ8eu2xX9DMtqHgASWkUDsBO5BeXdTLK2eBYAfJUi+1KA9RSpGCgOBCKnIENAqqDEdmEB783Vm7A4D7wbopgo4jMAcEC4ACAhEgoAUQB7NkLaXwTUKpAJwJ5NKygmgAOgtZ2Yznrp0WsAAeB5DkIA+pkAEFWiApIApLeAhlQA2lvZranPi1UUXAGj4Gq1huz9gYI6QgA3xFckWnSjZsykbwAAzC8LwLKKi9rddSkOfSugPw8AnIXx8CAAQIK12Auz2UzaHi4uNwRgs7SsABCv8QwA9E9+BwAzmh8wwHGwSAA5gOAiUEO2/ng7viY3xAvuUj68irsdlgbA228CgBgBkheTNgExAMy4zAFAIwSA/btNBmB7fgBmLgELB4AjoVcIASPfMvkAG+pmKNqFFC1fXIb4uQ5tAw7UceBXAfCWAEp+s7AlYIgZ7vRtawfA7CUgQwN4S8AgHwB6WQkBaDUbs43ARS4BQkC1zAiQrUc+QEUBMKBuiFcbPecIOP0CAHmMwN2oETjIMgLbSQCcCtgXAPoRI7CUZgQOZxqB7txIAbCTAsAoCkD6NtCa8Jt0fW1RRiCpHMy5IOs8MEAQVAQCd0NcLgnU5L6FOwyYG4DPbwOHUfnHAaBxJwBQB/B1axn94kr+bWA/fRuYDwD1LvSjwTYQgmFLMzx4Fb6/vJhtIK85W4xASRiAvGwYAYCqgBlAT/GqlyMArcBPAbAYR1Av7ghKAkCJbjmtus0ICoJachfurSOoN58jqOkDwHe41vF9dlIA4At/PgAzr/Xh1ZQZnsC5HEEWALoXXtEbf+cIrrp7IhW5JNDpezkCruYAwHMF74euYHLJltgjl+oKjjgCt9IBiORbEACKyikauoKHwQGEcgUruxFv8ugQ3vwABEvAjGt9dOVw5lnAPK5gBYD4gfliNFkEci4ouUPcDfGmXLu2rsD8ACzsMMg/DsoEgPMtcGZ0dd/aOkUb4WGQOO6H6izAHgbhAbIcBn0agEADUERC5nHwSmmRh0E+AGjr1cn7U1aOYHUjm2MG8HrowG0DeB/4aQC84+Cmdxw8yDgOpvwtfCysj4PbiXt2nIpdMuPLHgyfayQoJ6P+cXBwHszHwUvOj8653jq9IHooDsAgBwD6KD+RXC5/QEje4+AQAJUhZpN9gHxbfI1SBLQpFlauh+75AFzEAFhUQAgnxFcBIZuSEXozDAjBW28CgFIB2pcwwJ/BgBAKRoeob3zudjwgpO8CQvSiAdZKGLboAdBUAMiOZiQQJgGQkLCdtJCwPADkDghJ2zPa5IiyPVymC+K8xND10KEfF5wXgEWEhFF25HVpJT8kbEsDsGOT3ePYq4QLfTTpaEfk7lWnhYTZcICCjfhnc9WZAMO8ADCFPgA72zootPfVoNDMkDD6M+/sqWbjPnSGQMqOaC+Ic4pDuR7qAMDq3XMDYINCm35QaClXUOjyCuRyXl2BlMZeUOhWO3LTVitfAWCHDoDsnKLnUpBnMKv7LijUC18vykHvwI/yyABgbyYAO4sIC58dFNpuYCYk3cJrQZyYk29j1iUDxZIFQF0OmwOAmWHhK15YeDgT+WYIm7pFLyx8JVwCdtRewpc/O+3pRqKEedNzozH8sgLoG4yFZbYz1VXevjtm9gHwF6J0AJpW/v3IxZAFhYVzHHQQWlupJVYDyJGKBLhIOGM5fRmAxMWQpeTFkOZO2sUQvMJjE8cU1MUQubAd3rQNd/Xs1+NIcE5K4y6GhNd4OKeuvRjS3uZcLLQJ+CQAwzQAsq6GLehiiDhO1I6xYC8JcVrEtndBXEXCrS6FAEBAQAoAea6GdfTVMHo3d0MjcUWHr4bxbVVJXNXIAMCZkl6QJ63peDnOrgGsAry7QSSBbbk7iPsMXgvXMOu+H+uZD4BRNgALuhw6AwCzYyyoiWSvhvoHULT3IwAo/4UHwOmcAOS6HFrgS5q92OVQvsNXxTQ2SKe9HBoDIMWUJJT4cihd9VGXQ5OrMF8OXZNkInw51FsBVDqvFABchb9MAPqBa8G0xQNA6YtKZZpJJZoLHgCMgCQ23eLVtZQAAC6I3+QCIOf18KWM6+HVjOvhK+UIAKFNL/KndJKcIqO9lX49nBITuOvhesFo9fraZ5QNgNTrmwVAxPKZM0HErNvBLhB6vUo3vviWdcOTfwQATwOoTHEIwF5uADISRGx9IUHESrALaDhlru9k9d2FX04Qwffg7XO3gyvfzB1eouBsQuQR6/gJnbIA8N1RKirNuYLFd7GoFDGZWavRlUkposDNj5ugzYCALXsVnpcA3wawSWIAgMvcAExvfVdQMkVM6SspYtrJq9b9xJpKYgXvj86Sk/JcCZwqUppwvhNQ8EO9hIAZGkAD0I8DEFmyvgMA+Ky9GEaqlG8I+ABQipDoLiAbgPf3RSeJsiGMkq6LrrMFSaLa0avWnvR5a817zlW582+zxmckiQJ/mF0PKO6c95iZAPTlUNKWucwGQLTKMJEm7us2gDo+k8HjLBBLIQEuSRAD0LJ+AD9bbBYA0TRxV5lp4ja/kiau7d+03dH5uewZYqfTtku5JB1ruufaNHHbWEVSp4mz9iLtATwfQ04AxlkAtCIR5ItNE2fE2GKfgUoAhwnXluSOyBaLv1knO3uJndC4+1lKApByNeyLiSKXgkSRZZcoUuW2XCWGEyFh7BLnQxXJ/q+OWBotyZbs7sjPTBTZbstaYS+e+MFj6QBwCngujZ0JQDKAeKGJIsntyzfiK3Y19W8IBJ5AegAuFZIjRicMzw9A7lSxy8lUscuRVLFLkVSx7eyjVcnp3mAVUFi16d8qXBYGEEimiq3DAtCsbUCuLCq95+JWR8q7kwHAfh4AvOAhqmXdywQgkiq2kpUpFt2+Dcy0AVGeknqZ1S4XjqKKUXgWgIuf2/2UsRaLzRZMKULyAZA7WTQlbc5MFk35OiVjgw/Apg2uiBbuQ88nZAfjBKkuC+yMZNFtZxCWbWr/UejdSQdAhyUNOByS6hxt6/uMiqpRmC08O1dw/mTRkONiDbMd2vlENwSodFyl4k4DV1guLZsuuje098PJBLiNZAl7/1y6eJu2PW+6eDYcNACzs6bjCVKrTSUTCjYPMJaFSU8Xj3nsbe0UKMDM8lelHWBahwBgztqRi0uTC8oEQMsDoB87Nlh0uniEx0wTXngtATpd/JqNB+BiCjZhfDVhAkyncQAWXDCilFUwoqIybvkBlsnpr2smqIIR9o50omDEJmDHMdT4XF4AZHO/L+4dkmoGABCZqiuBWQAaQckYt2McDvMVjCAdna9gRKXeZqcrFr0R9UcFI9wBAUWFVVD5ASJITLnaZRPAlY4LUsVmAbCQkjFQ6HElpWRMjsoprmpKq85ZYLlUESGw6T2W4yJtPnl6riQf8PX6UACg+nUJAHRsur4SEQIQbBgGOSuG5CgZg+k+EQDZztoFsF7lkjFrjL6tlsLyF+OnRxU5J1I1aJoE4FUB8A1Fo0T+stXb3varp824ZOHqJjkCbNyTPLZGT63ZS7k2eTaWC+3aKwdOqjKtV2IA7LnbCeNxNgChvThH0ahGvqJRUHFMrofjkDfE7A/qJW1yNUHz8hvgDQXjR8rxzcgW/r6IsnFbaWXjVjLKxu3MLp4ppfO2xb2IpRNpnZfH2rJxUjnQOiKwkPLAu3IiFf5SAfAuqLlqoAKA1DqMmgvpAKSXjWun3DGUl5eTNwt/06YE0xXT6nItwLw8J4pt2lzRbgW4SwHgy4Uj258qHDlX8cyWV5+AZ3tm4chQ/njr1Kvxmg5AWA42BoDVK27H0EsvHKmMn6wKtuIGbDsArPrDuqEqJCyoHCkvD8fHJaoYE9YOvYtXDPkJpWN3gtKxcwNgj5ikdGxdEAiea0vHivyDKs+QeUCqPPNJpAVgoLMU4HohH5U4RA8AVzp2X2e0CQCIlI7NUcJ0O6L+zEuVbIZIewrU9i4OkTesVK51XJI4VzbuLl417FPFo4uZxaOxkrG9pxopHj1H/WQZJkuAGYeU57ri0SVf/jb/HM9rvOdAqW53djoEQLIkuPtoWPAahpcTGhwcutusXDuYzstaeDWdZ/EnikdH1B96OjarNZ0WTseGlmjOlcD45ZLcLj3I9O4+NwCfLR/fkPLxUsu8xOXj8aCnRzn3sIp6ozG7hjsbgcIP1tBG9R6Wj3fP5SvUWD5e6f9DnYFy3wIAdd7N1+vgNRQA9lWqKo8V/iiVj+9EWeGVhZOk1zFTsaoersrHB9eJXTV5zwgs0X18v3w43w6n7zVt/fgKvjwXkK+1pHIsKQAqG3h/f58LgMmFAGBVgEeAsQOatQ0WRVHcwMYWr8LmhuqYLfE03Kg1Ojo5DwCwtFbeyNVgJ0T3j5iATr26QTWpiuR+rsDNA9gI4HPxSp1M/xY6gDgBKWWhVjlIUaowVNTM9EIArFCPnFjpo9LnsvWy8y+rpEZyhXLV+sVXiw6AUnFpbT3nm7s9cEfBv45KFWcdvDq+O475RplenrQy5gYA+fsJomIAPKUC8PjIKmDK10TPqZQvveegJ6LAwN+V1bUSJqpe5ytrmBrYrA8bIAcVOkMAGEajrt9Ew4t2ENFpU7x2mvJccv2tUYJsvipX5BHA6S9n+yx/lYYe7rsPO1XsyCo9aHUJpTpMpCsFDzh/dI37RB8d2LR2R+FygWcj8lm8lULGYzH3i8vLOwDg1WFXza72ZfR8uTFfQ4c8acaNarNL8mcfAF0JQfl75eNV7eBUAGQR0ASQruu1UBSrK8ucCnoZ4v/5X0A2Tvw6cApn0zwNNYCKEmEE7KOW8eLB8lL4XM/8k0okXInCbGeG3URHStXuUJkL1mBAAMKPdsDLfnAQfJS1RXDFEy5Ro/FTLM737uTItGmNYNJtiAfcvvvKsv1XWo/rrR4Zs24BYAUAMzpePTwOgF0EeC/oETDsd1vUoZJyyhetPthg8ft5hOE4qTRXWzcAePe6ex157lr6c9lTT/Jn8dtiVPAKu91ER9ar3V0tfxbrXspHSVfoj0JlrdhnYafBF6XmbOsb9Z5Kh99tAf1lfHXkX45BQCOYj6NVBEufVF8g+YsFaAB4yARg3wPgyS4CtBd0OuBA3Kk4Hat4GOuJDAyCehPE4F0bpdzP4C/cyN+qaMz5wZed1qznyl1jlj9K/4TKERIBI2PIhq3eHY3VeiEGQ9pHbXkDZVvs0mf9EuWwhOnc2XO8fK0VFGIwsw5/yH91scLqLaMth07+YADQAkAKwABwEwHgRwyApye7CFgCWAccHqgEDiCMuvfKGKZDk3+oEgfQsXm/S+UY463pNf5DlKcKvuSBAIvceyya6fhcCv8BNy1WIzXyP5WCpCdMwHjYNc31pdMx/zrc0/bCMZsBe0Pvk+az+FFObn/swzLodoOXadEKBgOV1tIHBC0Z/fKoAcNXJ/DNk7o21cqBkz8mCSYF8IgAjDUA73EAHh6fnhUBN04HnExoFR3bI1CjmjrupbHXKmBu5KKs+SBOxf2mNC881E8IrC8Cd/0xdc/d5WwjB1SN1lakPiMCqCCRzWTC1xoxl4LIn+yF4yPJWeo+yHkxlW1Jn8RRoTJXibdjavthkaF+dhtQ+qVd/fbCf4CTkb28u933WvnbBcDI8/7mwgFwmQrA7cOTAeDJMwQdAawExiojXD8UmRP+eCwOcz6MpRAqEadfaMBvQ7d+jBLjYNNRu9EcOOrQSXcYyJ8I4L2A7Q1dSUZ9oe1FliuKdSzJr3btwbL/Wbe/sFnSkq+T0obRJg8becmXdr0ZoEHiV7frHhRid/JHAMCsMwBMkgD8EAA2NQCoAjwCcC9wJlp03zIQClO6vkeHZQcHLv3fPkXbjpxAd9NwkFEYRUchMr4KO/bn4vLvxO/rgAPuDDZxAGuDcSIfPLC9ZnWGpwWiKuiz7jfjY5LZdhNNsJTzZtuB2PsPvTdnvefk7xTAPAA8P3sETBUBp8EQjka+QEe+8A9dEwgkOMsTa7SNXAJC96UUYnat0hFpQilikv85NkSACGDJ7o9ZQx0olX5CTSY2I4AywE8fqOkvH9UE7GW+Uupr6ra3t+cpT6tB9eTR5OgXNz0z4o/JPw2AjwQAz88vz7IIeDrAImBfWM0jO594mMhRohqP5v6+DKiWa7TpSWAtifj4Ou5kMqP15+SvCLCSte2Q3YUo09MEAQdWDAdK/rS34M9KqROlLyKN4ogj4vaGT2tOf/KMo+MlSow3vTT9k/KfA4CXl+dnZwbQXoC9wmAJMAJKom4y+bI/1o2+ob5D3xp7OASTwJ8GGoIRmAb+IIzlMJfHAeV/7pqsAhPbFWlHR3ZOy47hZCJ7gQxU7GcndjxCvCOQ+9LGdxx7iIngvclzKEeP+vcdLu61cfpfXCn5AwBGnAqA/VkAvLhF4MERcOUQcAz4A3RwqIU/USp1EkBgLQPNgj8y/pgEyiPUmP44+OpfqYBTmdwWgiPurZK/fOr4yENAPu7Jnz7rCOC5Gra9UOyhyNWEd33Ssycxct63jrD6+imL30z/QP5GnA8pALxZADZhf0gAKB2gCIDdACJwJo6VIzWSFtnj42CI1KRS7xInYT8m++BTekjH3kpOAyHT35O/T8CxWpwcq7qz8n5RXeG9m2PKztNEG/t6cj8hc2/Kc49k+qiR87vj94nEL/K/pSMgnv9Gmg+3CQB+xAB4RACSOsAqgQtRp6cysal78g8TEoA2wM/48/YLPjhJCqLiT2Lgr81uevLDz2MAnLKVr9pEBjsEIBhzTbcP9ySi3vbjLRS8J3VP8sEj/LHzvoX9OdPi5/2flb8GYDMA4NkDYMoA+AQ4JYBa4MINJw8WN+73WXL8/Y97L6IpsMO0H9GMerkJKRHx28EIxX/uMGQloKTvj7jsBH0Nd+QWi5NAAzilcphcFZMtRfS6H2fJJj3n4XNDfnrqpI/iJ/Vv9f8j7evyALCvALCrgDMFeR1ANXBhx/RMBgL+yZP8hW2eBNwA+6pASTY6YPKpw+iIHh0dpS7/0cdPfG7B/hd5Rhg9OvKMBX96TgIADvNKP0DwVGnOWO9lrHkDcurNNZC+FX8ofyPNdADuQgBeEwTgMmAR0Azo3qlOXyRbqAi0ltW2Ft/NSAMgMcS+flbaMFX+Menr5uvaw0Bkk5CAk3kAiEjfKZ+zlPmjhvo8oRnsZ1H6kBGQpr/s/1D+RppJAH5EALicPr68vEYJ8BAABi4vg755nb7ULaoIToOFNrJ0ZiFwGDOcUSWmTSBFnhP/JBR+XPxaah4B7jt5AIigFJn2yTG8SA61/qz5xBWL/xYjQN30Z/nPA8DrqyKADAEPAWGAIFDytZ2+koYf4o8pCLRVGACQZjRFGMC1IAlAZAXQy39c/Z9462tS+qEVoL83+YwC0BtPJftQ9DSMkWEOx1smP2v/hPxfnh9vIfp70wHw4QA4VQA8vby+xQkQBIgBhuD6KtmuXVOf8hg4E8ec2m15biW9W05hgP7sILYbCj1AzoQKjX/RAfYQKGWHknAaKFewv184OIjuAzxj1RqTEelr0auRvPJYSH4Gpc+zX8Sv5D8fAAEBWgkQAggBUHBznWg3kRYwoF2zgVsxcJtYCqLmYGJixQiw4o9tWrUu8PbaeeavPjpM2DEpW0DX06Crvv6MjKYmITHgII7p1InfTX9r0z9OfQDe0gB4NQA4AgIEWAsgA0QBceDabdg8BhgBcc0GbtT9ND/fOLmDjm8EjvUZoNumJL0/dj0/nviaPzRGAqddzM3tT/7Qz+2pM6+n2lxh8VvhJkb0Jq3dsvBB+hj96U1/lv8rAaDk/JwOwNtbggBeBwQBZIAhmN6mtoARRIDXMgHAnacf8FGhf3SiXP1J19D+QSCaI30MoDfQMbde4G/Vc3g/oYrG6pQmPOZKfnEv2nyPpdMAsoUTSy5tJmUNs5a+Fr/I3whzDgDeYwQoBJwisBRkN4HAKQHrUD7OcZaqGdj3F1g3u9RJnSbABoMFC/RhUoyHah1KnrslzxyCg84DJfuMw019pKz9d9qUsyo9HMOE3K3wRfXL2Z+n/tGsf9T+nsu7TAACAnwEhAGnCrx2b1tCTSACQIAFQOQv4o+e9icY8E8H9Sm0iwQ4TYo/PNn1/HFW+i5aKB6fwAyE1sj+vhdnlB7nMXYRK8faf0/T39PpWr7SPCjUmD9o6fviB/m/zQnA+1smApYBxiCraXuBdACvAaQAOPKKq07uRuJ9/IiPvWR8gILjQEJBT/xY0DAMyFPn6sxdR4ClxShFlqNEzFsy1knpsvFYo0rndxe+/KeeWp/VHnzp+9qf5G8AeMoNwDu2gABBwDKgIYi2kAEhgDXAmY0x1OF0yahRHfPlHad7U82L7XME2MgOMTH8+IH9QIi+ForF70UY2He/PDMczEYW2o6eWgKu3F6OxZ9T7qH0ffGT/I0oNQCn2QB8aAICBDwGZmMgCDAB6kRRwsts5g6XdjcRP+uH/XlqVg2tCouT4C4Xr+WCGHeDqcym5FjVvh3GAk+D2ENOPDQW03U3R0DoMAhc5TN8BoBO8cSoS5FztDlxxMTPVv08AHzElQAjEDIQaQkGUAmQFUAaABTAsdw2Grnij8mo58Twu7smSltw0PaeEODH7MI9v8SUdnl+VMSdFBXo9ZKR5z0X9hzqoiAkvBdtLnhZBXDLInB5qeQvsz+34Fn4JH0nfp7+YNXNB4BPgEJAMTCTAr1lYAJkCbBB5u6iyQDvPzWCew/Var3ebHa6fqEwmm39buIyBdza2Fe3Nqz8h93wWkoHKuupAhG6CHHs7kmd7570k8oIEg914zdcEjDbEkl0g+dIB/GJL5enf5qgo80KPyJ+NOrmBIAJiCEgDDAFMzhwBJAK4MgyDDHnu2Z0yYRuv8UufeG1t46oAZemvRuICO9t7Y71vR25tsP3tqr6w7WuVytY0j7TNbyM22c9r26puvc2+6obXJzq9NTtRb7EcXZOR7kR+eeTfUz6Tvu/5wfg1AKglQAi4DHgIHAc+C1KwK26a3hC97Q4KzNeN+W7j+raJ1x7xcE3Q98JCjVEbpuu21u+6ORxF/eiNzdbg2TR01439f5pyb/36tVLzHfzFSGqyQVWqwO8OF51mBMRf2yklSRe0sRvWl4A7iwAWglEEPAhCJuGQBxHaAVeX+nLpvq2cXj72V5+XqKL37Vmx6tRRRkngrvb9vK+d8t3NGxF7nm3+gPvblKfxa9vvq+k3XwfeJUrKTtEdvJPusRMt7jh4p+9xz0JAfA8elruGQMeyOb11a7+2D7yA/BsAfCUQJyBbBCefAcyA3AZpp3pd23KiQKnfiitr3PKCclHDClo61w4WKUcSWZv2B3JNluOGOTyvsvegEkhNiDp70BdNxPxhzko1lUOimXKQdEJlFF29os1DRHB3OoO/Jvc5zaUcxrIP1vwSdkr6Yv8Pz7mAeDNAeBrgXQEUpmw8SSoAgSAc8o/Sdc0Bz2X/6coeZA3peZsuYwX4ilB2CYqgZ5kDcHy8rH8LS6Bj5e+Q+dvWbL1pNTV8zALDa3bNc5+42WhaXasMpqd/waSOkMGXA9mvssPiwDl8rhgDXDne/SfMmX9Epe+J36y6eYA4MNr70k18JqfAR1RRDZAIvlgs7qx7tJOcSZ0TppUw4ToknZq3U87prNOSQYnA4AmgOVPCXwwMRR+dqO0LEVlnULpzMhDVeE0ZJyHquOVDrR9iWf/RwuxzAn1ubJBp0dXjSWbD17mcABon14uyadIXyw6AOA0JwDmTyMI+AzkwUBdMgQCaBt4qXIPjigFcYlyYJXKlAAuLJBLiefsuO3E8s5JDjed8fPQZfETAOodTr6IAOzoOjWdZAa8LS8TXS3IRNfRZcviOfDslym9p4HZpjKEdF7QV9PVoyN1m1tuczxEfLo5ZC/SD8Q/NwA+Au/vAQOKgkwQnr3Y8qlKPcgKAMznsmTBq3DS20SpbJV6UieabATVHPw8rgeJ/MDLpQ0uGb/JdcWVBDsqB6bORdlWYsRkhCLAqi2bGGbBjOZ/5R/A8iaU93Gj1h3wInB8cnYWAJA80pkpeit8K321mBsA7uYC4Ed0IdAMBBhEcfAuGE1VHQquSDjgMgQwpyQZbjIRarMhSWCheIIUDPOSD+tk/pIgXOVxdWk84wDszJ+N1suC6gDYTisDRMniG2BnrGFKz/WNenewq1I6hgBE5P+a2pxQ3iPiNwKMyjkLgBABxYAHQdB8CkQFOACuvEIkPaqGhoVw6jYXck3nQm5wnnTJhLyZzD69HQCwK6m89yWZu5/It0F5XG0ZcFfuMCMfNZZ+po4k8iDvBKnQkwmUbWLXVoNLXEH5nabN6UhrQABAcKQ3Q/AZ0v/4SJFzNgAJBDwI0jDwFYFKNXCHAFzacnRckZJqQVZqLGivFADsB2r4N03Mhb/Epeu2txOD7pXzsDUiglzum3UNgE1Ea+UfyUhfqyoQKRV2JZoJWyWEbsdT4UtOe2SoiOuZzerLed2vb9WN7qek/DNmnpZMQm4/PgnATAbSWNBBpQkAvKLEVIW2wlVmapteDRJbDkEIKFAt6W2vAkVbA0BlwsIqMbFc7olMxFIJgRadWpCW32bpxZoEXJOisR0BAESMdZ/8RsU0SAlsqMIOwRowvbtLKIBM0SeEkZT+jy8AEGMgDkG4VRACJNPAXaIiKZWkxcJojS2q9kIVUQpSD6XA1YCIAFuJvJ0AQCpRqpJecsCnCvqEAPBakrMqDZGoCLAs2r5IQaRypCoM1rXB5UXqqa2UbW0fV93LAyDw6r69z2ofUel/EYAYAlkgMAGeCpAiFGQC8ApARamLXO6xyVVGbE0krAZnGGAFwWUosQpdctClsnNPEmaNVK1IXdHJz+VONXqhLq+qS1XPrEvlKrhx5UtVEAkr18Pvr4SpQe0vwKu2GlLatt61Rb4xs/t1BIBc4o8LSInwSwBkMBBlQqmAEABVlRxWACkIzwVpcfaDpwyLoq1TVSypl8qFaFeDKlQxAPxisf0oABUGQCrTreatTEcErBStQeIBgDViIDmwLg3nfqFCBNTR+KHc42AGOiNAAyAKIE342aLw5fdVAPIyoE+R4wDIHsDsAWEFoLJisABQlSF0lILVRc43WwHGL0XdTlaiXNOFfb1DPl3WU1d0YosNZ2PR1abctLUpS5HalLaA5YqURGsHlcF0cUhu8gto7hp82y1jBixH6rtd3yQAeFUAfORuCeEtAIBcELhlIBuAseTXxjUdFQBvCMtsbjUbxhIji5trJ+LEs1Vy4wDseMf1icrOQVE/2LC56rQNkf+M6rRcFXclAmNKeVjFMlSEhHJQoAKWwgqPYAQkAXibE4Co4BYEwGwMLAEaAEhVe+c2AQQAuOeKxTUpSI0L8Sr69pUTkMTBFZRJBazNACBS3D1Z2Lco5dZT61NTXew2Ffqyniiui75FBknB9T4AwBWIZgeQK6yOK1irDioAK/xxfSfJ7Xx3d+8DYOX/OdF/BwCzaEgDgDcBtig91nDdqIvedEUybWtQbWxUAc0m1VCfCUBfH9VjpKEBYC0OwBa5olSF+rVIhfq2V6FeqnmLCkipEK5qxEjB7WW7i2EV4Ko82wJfMQCyhZ9LSt8HQASHKAC0C5SK1HgOAP7Tdbdw4oRq+CcBDbH80CGD+7XVcqQWrQPAP6qnzKoWgK0AAFWfl8uTq3KHupBTi5VRccXaCmUis54DALI1NtH/w7uYFmgeBmCMAJxFAYAVwM7+L8jlVwNAboArBuDAAmBE3rIAgN4Mz4IaKCFjfpXRmHILr1+RUwHgEUBHfTv1EAAp7U0WwKpV7OSjC+VP1Zs2xYEniwXtSRoagK00AIiAlYI1PsAJxvXKrBX4dwNw6wHAR3RYZkvmsyhUr3H5VOcaYEGmA+Dn4gb5bxMAG0kAjOmx5CmAZdwQkPzdYZEQQLWc604FcJe93scB2KZC2EUpptyQioWpALz8HQD4fiA8Cz70AGhvaw3QjKiAVe0clG1AFIDGjk8Aiy8VgCb+uCiABu/OSf5eTD8SwBtGthdlTwLs1HIAQB6HovgxmlQcLAHA9K8G4CIEwFsC0AYICcB5g9GZ0FZWlleyAAgFNwMAXAHQMrfKBqeo+xl7ywN9xuvkvhGDcVXWgHwAoMtBAMDygP+PAHAwgCvO3tS7ACiQGq4BlfW1tXU+llk3DdxCCSOwYgHYCcWfAQBuPsX0oB2hwshTJHhqpCY9LxgroD2auQDY5i6LJxMVYOAJsgBoN8BfBsAJnwXyNtDuzdAhv24LpLo1oEpnadiqm5ubVLw8HQCNAFlw7RgANZKVXgHMltMYabXWTmhKsAoAtY317JpsMq4U4AWa/wGQDcBDAIDbBYgjiEcfj34MAl5oIJzNYjQnB2jywXqwBIjzJSjMS7q3xc65EAAQHDgWZAVAdSAKwF1BEFtyB4UGToOGs07g283FLwF/NQCiAdgVzBty3PDzWRBMeCiP2pQQMX9VaEcBUO7XoBgrBA0LAFseAEaEy7L5JB9+mRVAX+0m7W6iKZNerJMMAOpfMgL/OgCm+jAQPYGDjmeBYajNMp8Gr8O2v0ozP7QKJcwqsQQUV9dTI/NBbg6A0jJNe7TjZAVokmsGAkV6UXcSxI7hGsDeH0tSPgBgG1iy28D2jG2g7wj8OwHo1mjXtVHjMAw4hl+2EQF4R2SzVgtCxdsJANitEhzBeg0udYj/QAGAEuRtHaoDtiO8G+l8oCAOBVi2GACUIXy9kQOAbEfQsQPgXgPw+gcD8KIAuHfhABQTvod3AjgibLPmAnFKFBNE0UBo/VfQJmika4A2nfGvZNZnLaJMfAB8EwADg2QF6HtnStaljGtAkQxIMgKWYBsQB2CrrcOCm+TIjrmC9VmAVPn6SwB4dgDcCgASEIR3alfohI2DrejcFAJBGIIlgoAC6pqpALRTwrD8mCw6rYsBYBV6UWrW+2eKUrSvh2uAXbXw+8twbpQCQLst4ge/UTk8DCr8fwJwwsfBQ7kVsLwmlwIwBqBaQQgsBWwT2HPiqAZoYjgxtnD9t61Kh0hJAMQGLHHJdy8ZiL063qcLJBtkyHtWYByAprVXo8fBYAGXa3wcPKF4AK7z8v8CQI/vBWGgDM9wZACkiZXhBQK8N8gheVEAUkKxI3HZIQD+JoAB6HtZIKRCJp8qLDmzMRWAWQEhaH5KQIgLC9aOwD8agDcHwKMHgAoKpovhQoByABkGGuj0YQiIAYoVo/D8RBjWdjuzedtHHwBfbgEALrJMAgsQgJXZAGSGhIFBsFHSIWHHiU2Afxj4BwPwFAcAfMFGBXT4SlaRQwHtpg99PgLB2opEi1YwJi8CQGYLWPABkE0AyQ3vD+oKyF5omQCwPgOAHEGhEFXKQaEJEyDNDfDXAHBsc0NYAmwQXs26fwQCjNFnDwFcIWwmHEGLAwCuj+3s9ILQUhtc3Ot5ANBRQhKAWWHhHHhkb4Y4EyC0AQGARSiA3xcATA+xtkIIrIKtJz5AvRzg9nCZPMW0ZwwAaOdaAn4SADkuhlBIaVnigVJNgP8DAHYw3G5NLoHQxl98gGIW1umGuFwSQAICAD5rBH7DEtCEjUz8aliTroZV/KthRy4gbBqNB3r/qvx/YwAMAXBhltd5YMB3BPP2EK8NrvAlASswB4DBaHMjexsI8y8KQDUOwKeNwK3Uy6FbdDm0jPKny6GkACgm+EabAP8/AGzjnWmj5IWBJYCgJKqggU5AShOBlwTs4Z0GoFmja6UZfiAwwLcWug3cigLgNh2RQ4wW5Tqga0VNUQDhWXAIwJfl/xsAMPX9AAqAHbxQTdmA1pKOYGQAVla5mK1DsSUWH45WVpcyXcEra3FP4FJpPkdQZYYjKMsUYYzhjgkEokt6gIm9FDCNxoT/jQCMcRsoAGyTswzcP5STbSn0ATaDG+IBACTUzMOg4mr6WYDnCt6e1xWcBwDJd7RhM4xQghCdHUDtAWJuoD8UgGcbEMTpIThB0IjubfcoaMOOEDqCy8oRjHYhnRXgDfFlPxRb38aZcRy8UlrgYVAz7TBI+5221HaWkkS5jGecImifFUDCBPx7AHhSAFz5APQVAG1nP2FyBloPSA9w4g65IR6GYsfv4/mZ2vyAkMzj4OaM4+Cl5HFw0wMgNAIhTdwmZguUNHGYZaw/lBRRvAc0ANwnNoGL8AP/XgBIPMDQzv9wzjQlMV+Zt4fLfEGc7wnxqh27jcMhYdsZIWHfGxBCBqnaBm5gnhFMPiyJIjHZaHdA8j+ULIHX3kHQQqNBfg8A7N1QAmAX8oOzclT7JC9DIGXm03s/uR5aiwNgc7bEg0IXFRJWSg8Jo4vOgSNoVScfpnTDPU4RZ+R/wgmibqYxBbCYFeCXAfASBQCPAlS2d9mn8za6rRjYpFO0ZXsdx4z0LACyw8K3vhAUuk2HuBlBoRx0GrqCJde0JIvu20zBkxOVHihUAIvyAvxEAH44ALyQsESKqGGHM6zbebIOijU4vjMI0N7PCclMwWwAkvdC4vcCarKRFyMAb4lkhoW3JCy84cLCQR0EAASHQbF08b78cQGQYLBvMAF/PgCJmMBEegDK3+zmSYF214kTXL4g7hneGQCw/ONXwzazL4Y0OEZx1sWQNe9iyLp3MSRjR0IFIyDVPCayw6T2XC8E80SHW4C/CIDHNACMQSxpeFy6jQQBFZr0AsDabADCuZsKgHc1jO+Gwm01q0j6+a6Gpe9IKEBd6g71+37BGMkUf3PjEgQ6BaBWgB9/OgD3IQBDDAqHDNEbfO+bjPsIAVWa9Nb3EgEgvB7e94y39Ovhc14OrejLoVodpO5IdpRRitLHHHbjMdS1OXaVAiLyX6gC+F0AUCmChhAUDtZ9DX0kdjIlD3FF62ctAUGCiER+gEZafoDwevha9Hp4L/f18KhBqsqaSdlAqRhm5Y8LQCI72PvfC8BuX45VWjCbeC3WVkBbJwjw3HWzAJB6LoNEipj5E0TITjI9QUQz5pX0LYmBy13HVSOhXtgp2n+XYgB8pwL4lQA4T1AAQIuyu6ocYas+ATpFiBhe/sVOASCRIyjw4abmCMqdIqbdjKSIWbInSd7BlGeP9L1qgVzd9EgKBl54OcKD/KALVAC/AgAiwAFwFwOgCBlCrByWfAJ0kiBneaMfoJ4FgFfRa0aWsGSSqNWcSaI24+lqEvaILhY5dnWjT12pKC3/RHrIjz8cgJcIAMcMQLVEV63FeSID33RHKuQHKNg7+S5RnDfrEokih14kx1fTxEGlh3xp4sJ0NbY3XC5WiZ/rBKEH0EsR76eH/DsAeMS7YZwqmD2BA7wXpBLF2hsCNRseXsf80ZyWd8tLFbkVH/Qwmi+ZKtYDwEsUySrmK4kiAwBsnVAR/9GRrhlO1UJvI/JftAL4+QC8xwC4YQCOKFMsu9U4VbSbehUadzo/XaO8UGU+DAos7wgALP+RLQcbAtBOpoot6lSxFZsqdj1fqtjtrKyldtsvk5/rRZ9jrdBZ8v97AAitQMoVDcq3tIEZIiTepyCl4zAugEJDlnni8cJLLsPooKt08SOuCJyWLj5MFr02X7Lo1aVosugwbzGVrxDLz0x+mv3nUi/+Fm6CROTvKYA/FoA0K9AaAeB7B/vLEqDTxcMhWkEq9cHE0wnj27MA2JOq7sPUghEuXTxkB3Xp4mufSBefmrpeVQmmYvEkfkgMjdM/qBEU5If+gwFIWQMoIODQrQEy9do49Sg42B0QUFSYLRqiF94QAFUxZEQbrrFUDBlkVgyZt2AE3PNecwUjMKIhtXjFLkb9yMbPiZ+0/+30LiL/l8XL/zcB4JaNgAnnim2S/VViAqhkzPpapGaMDQks0ri3A7WbqBk0jtUMYgAaGSVjsCOIQLRkTJtOp9bSSsZsJ8rXSH1YMf1E/Ne2ULSqEfcSVQA//jgAQiPgKXYasLc7pOvhauC5aJRXNapK18KbuCFUVRriNYP4qC1P1bCMolFtWzQK6szPUTRqO9EXlj+KHxw/IH6Z/lr+z6ny/xMBiPoCaSN4ceGSxPQ7VvkmqnVJ2ThbqqlaVoXjNACJsnEjKhvIdSPDuoHbrm7gTlA2MLNsHFcOnFE2LlnDcF+OfWjnZ8UP6j/P/F+I/H8lAP4aoFXACAvHysBXVZbYoHKkVGws6tKR/qA3ZNDtrotKh3qVQ9MLR3Y+VzgyXjm07vrCYZ/i96XZf43iV8t/XP5/PgBpG0FVONAlCChJ+L86BlDx9MoT09zeiQIQ1A6m2tGqdrArHVuV2sHuwD9aOta7X0z+QCkd69WxDgCQvgyGI7j7bwvEy87fyv8hYf59l/x/IgCWgLfIGnDLviCjAmztWFc8uOwyAVkIXGyoq9oc1A72ikfjoLP8k9XD6/zZ9bB6uCIAWCSTr+UVj65HikfLt7GSOfVlZ0f60hno6uAsfxG/KhEcX//fP/50AILA0KQK4PLxdVs+fJWzQWEcDccL022hNbMpt3Xbd9Ssi5SP5223mXiIABKA4Ude+fglAkBFDcwqH78hQepSPl6HHcVK2XcGsgCcnpLn54oWfyoRHysR/eoKBC1W/r8JAPeufLQioFndWMfC7UW+Hb6xsWlsb7xMQbcpVjie3sy7lq3aDrOuuLTGkXcUUmQAcG5X00gHjDgAUcL04HgBARjoyH8gYGOdEMDo7TLF8ZmO4NUizDUo07/V6emoEwQg7EsHykG5uE9U/3b2+yXiX75b/r8MgHjlMM4ZzgRgjgj0vCEDuPlfx8TgWEUWblPQrOTpr2edmY4UdAs+YwBgSH4Xkj8TsMcBiCBW/Cwe5G+gz2Coigt1W4wAB3GjFwJvddDlQneroxNcHMIlJuxLd6gXAHH8p8v/NVYg7g8EIOkJ8FWA3BCcHB8SAUPIFbWxId5XHn24TOH+dY3i6VnvqlnnNRz0MbvdoREC471ht5r8rDEY/QwQgx4jYO9xYD9WpR/Lcquja+8NUswBpT1O9GV3rAyAKzr498UfKxD8LfL/VQC8vabXjwV/IBMw6HWg4DJdoMKx929TlHBF9tXuAI4SSn5bx0HnYxdogIDRAePdbuSzxkjjQ8ORMNAnBDawI/YwgG70mNm9voHi7/npQ8DTlOxLudod7ePFLwr8vhLfz2z1/w3y/6kA/EisAfE8EUTAPrtqzMjjVaGyP5K8EtebHRx3XRGwC266KofcU6ubQWe/OzaDABCwPzI6RtqG/exwxGdGeyNVZ7Zr9nvws35H8FJHFeL6dW1aqVA8VL/v+rJ3cHikAn9R/sr6S5X/fAXhfkMAfkTWAK0CHAHHYqbv0uzDvXZwmwIscYqoF63L067f7bRU63RNG45F/qfQDAJEwC78Zcf/9IBOjKhJqXFSAx3sSNXrhhE+3eoQ6cu3AaBBt6t/HPuy6xkANxz5FVX/LzH1v0Dx/zoAIiqACTgnAkA6Y0IAIjdAqE3dYNB7Ug42nHfaiqfAq3117mba6akQMJIIQffp0Zg9xuQ05rNDYqCPOLZatheIYF/H9tK38cQB1zH769TR0Z42APjsJ1T/QX34b5v/vwKANBXgzABDAO4FUAnsjVQunr51sfVdjDcLf48nHitukBn9P4ZcqmN3RwA9Y38s3+DZu2/9RYdJBrgjrikC6cvibebv7ulfh98+DORvfb/5lv/Fyv9XAmBVgN0JMAFQQez0BMRzQFrADv5Qz2vR+lZs0oQDbC7i0h67Q3NKgPzC9AUlfN4sEgRWjrIzUE36MOJnMTo+QObnx2PVFRf5faOP/mPqP1Ed+sefDkBoBioVYAngZcCboTo7o/wTT3x/2tGocyOJuIBb2wCBiSBwYD8s0j+2+8UjhsBTLruuByPbCYWO+ypBcGB7Yi/+XEjk311U/ceX/0XLPy8AJwsGwFMBehEAAq6QgDN/hmpN6vS1zCuZdnba0rg7aeqoG2hIADxicuy+cuiEb7aKsFeADeOxY4A7YjcIrGRcH5icycR99Sj47YmO/Pfn/89W/xaAk1kAjE8up09vi4hAi6sAXwfYZUAQkCmqdftYhj02Z3HcjxgICrg90+K3CJwiAvY78nHZLMB2ATBQioCVBdn5Mb3BG03ebXo/fswgSuwfR/7eR9X/6/erfwLgaXp5Mo4CcH9FAFS648nFggGIqQAhwC4DYqq5wWcByD8eWhnbaYdTb3Js2wTDrV3QFQReXJn/FQZAyjxd6csnJPwzay0SBL5K1+1QgTbRX7b80O/T357J+T/Zf9Po2c9L1Pj/DvkjABeTcbdCAFzdRwHYMwA8vi4kBjGpAmIEWARooU7oUqXxRV/bSYvDbhvL41yibmxDBmglOOMv0bfPzpSt4ESJ3Tg+OvJ7Yqf2MQlfuIFvnst3bXMdCSN/czh/vkP8RiCvjwaAvTgA12cHCEBndHx++/jy/mOxKkAR8OQIuHMEiJr2R19r1OPklJWZC/9x0ryQkEvXLAJWziK783O7Wti/ddNZrTROyZychOicx3/5QsI/bn35P2ee/X2b/H+8vzzenh+POgjAwdm1BuABAfj3n0pzeHR+87AQAAIV4JsBj0JAgIBMUaviRV9ryZ+ntgsbcYlhN9QQAWJAKPC/4JsLmgKn1ydWy7geRL7qd8RGfk/vvcjv9Nif7xM/APBwc340bFb++RcBeLAA4N8c9qv//vNvtWfRWLQKiBLASgAExGNoNfGpVvWnkSkLX4D/wB/In11eOvHfciMErq8sBLpd6qYEeaYsg1NP7XgS9r4Z/O7VtQ39vI9E/v9U9f/DKvoeirl/iPOcAEDdcHG8S2jsn4ARsLg1ILkIWAK0EkjX03rOu+GF/3H/aG2+K5n6t7qJImCLQJqzEtzfBLM50gfF2qVvang/LdK3sT/x6f/2k8RvpAwmAOwCQdHvHl/wSl9A68DsD07H+FddYwQYNha3DxAANAHPmgCHwPXVZVxRK9knhWeFeC2KH4U+Vc1CcKPsgutYu3Ibh0g3HH/ugcH35R9ubODnPco/tP5iu78f39k+3kDPH4+6OM3Hp2a3j7Z+wSqHQ1AOxgg4PLu6f3p9/1i8Cgh1AC4DFgFR1FGNKqK/Sht5u+JzxFXQCILbG93Ulzx7waqDaCdcDyJfVp24jYpfyf97j/4Ssnh/fbq/OjtEE6DaO7QLfcGaB7QGVMETcPvwvGAVEBLwLAQIAtNbB0GGqo6PvBr0aUT6jIDTBrww3CbajacnrrLUTLjMeL9Ij7q/vw98P5nL/zcrgOeHW/ACVGkFsKZ+we4QeQ1AFXD3+PLtBMgyYBGwmvo6raUNvFL4eNUmrU3jbQYHeboQ/KLrBYv/MZj+Cfn/+PHdC8DjnSgAWAGst6fww18DQAUYPO6fXhZ2IBAhwFsGBIE7J4ybm6xZHg67neVO1A+J5qMQqoa7CAr5O5DSCXvwl5j+r98W+Jcq/6d7o+RRAXgrAAFA+4DJqFX5x6iAwcHJxTcQ8BYSECBw78Rxm92So54h+GwUvC/rlSK9E96zNU/JTnDUlyf+qPr/KfK/ODkYNEHCrdHkwnr7CuwlfCIVUDZ8tIaHp0TA+8eCF4E4AY9WMP5yrcUQiNwOfEz0j9EWBSGhGvhnp5kttrw8hPw9uqA/Jf5fIP+Pd5L/6eGwZRRAmRTAE/v7C9pE2OughmjtIgGPz6/Qx+8igBHwGHjw1HN0UfVHPWiPOdts1eAvEzH9nqJx9GOeYuL/ueYfDP3r8yPKf7eF4u3saTO/IG5CowLOj3YJke4ICJg+PL1A0cqPr7R3bm8sfSt+MgNE/o8JIdzNHP+Hz4o/lYCHuO14l2ZZPmTLHwB4evJ2/w4B8QF8cXxnDv/b68vTwxTkP+qSgt89OjcKQNz9BWclTi/JSvynUu/uHp6cX92aDeyzjVf5ZHv1Gk98alb6j2mjnz71MgQxn9zvF9QytMCTe2XJAa7a2/c1GO/nx4f726vzk8Pdbh1NPLPLu5y6XV7BOgqfH27AUwiUVIwdcHB8dnF1M71/UJrsU+3Ja6EkAlnPMsGS262cjNzn0O8z9oixv52xRESp8AfkK0M7c+AhJ8P05uri7PjArP8V1O77xsR/eLbO/oK1FF4ezSIw2YONwj9Q0nh8ODk7v+BY1q+0rEFNuuF8t2zEC5PhBfxku/56u8m1cc2D0CIbRl1dnJ9NDseDlpnZsMnfm5gF4PHF2vcF7SqYXsN5MX2y2R2OD49P+Fb7AttFtJ0vpF3kbuff1GY99/Jntgsq0Xc4HnabNK8h3uN6qt18BbVZeH6YXp0d7XZBV5hloIl3W90dm0+3k0ibBO14IW2Ssx1/X0t7ZGwQvjius8d9ghlZQfwk0u7u0dnV1OwA3pMAwHEBEnC8R7ggAl2obawj8RfTxrG29/k2/krbW0TL9aT9n9vGmIOpS+JHpb53jPLXR30F7TB4AQLOj/eHuGAYBCqbAMF/7c9tTci5Z8QPZt1w//gc5P+iHXyFH0kCzJahY6CBr/37L9XzW3yr/J+1zZ/f8LkixmoHt/ah/D0AjCEIq8D1xdnRXq9l0MEv/9f+8Gakv1lt9faOzi6uQf/7ZzyFMGwA3IaX55PDUQ/Vx38M/AXSb3Z7o8PJ+SW5933PcyFycIBKYHK4v4sMfL+W+k/zf2MD6e/uH05w+kcO+AqJw4O3F6MEbg0Cp8cHwMB/7Y9uRvoHx6dG/Ldm+pv9f3jyVIhFjxklYBC4PDcM4KXd79+v/G1t/zdpUJv89Oz80ogfjvYi5/uF6Pnx6/PT4/2UsvqeLdg5ka/N4U3589rpT2twPfHy+mZ6//hkrL9YfEchfob89orHSHR/0w/R/F7f5bzt1z7+8jdudHwC9xLxUBfEHws8KKSGEby9vBg9IOd1iz2j+D9udz+r2aDUp+eXl7fU0J5CVijJm39+v+Czyv/D9vzTG4QfvGUFdhWyw7lsMM93RCv8X7W3X9I44ihDyIWccb3fFLCU2RKv8ue2j1/XZgi48OPXto+/v/34rVvhN+jDf3L/he1/p3Ns/xXWMM8AAAAASUVORK5CYII=",
            ["hardware"] = "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",
            ["farmers"] = "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",
        };

        #region Store window

        private const string Root = "SandboxStore.Root";
        private const string Main = Root + ".Main";
        private const string Toast = "SandboxStore.Toast";
        private const int Cols = 5, Rows = 3, PerPage = Cols * Rows, MaxLines = 8;

        private class CartLine
        {
            public int Index;  // into shop.Items
            public int Qty;
        }

        private class View
        {
            public string Shop;
            public string Keeper;
            public string Category = "";
            public int Page;
            public string Notice;
            public bool NoticeGood;
            public Dictionary<string, List<CartLine>> Carts = new Dictionary<string, List<CartLine>>();
        }

        private readonly Dictionary<ulong, View> _views = new Dictionary<ulong, View>();
        private readonly HashSet<ulong> _shown = new HashSet<ulong>();
        private readonly Dictionary<ulong, int> _draws = new Dictionary<ulong, int>();

        private List<CartLine> CartOf(View v)
        {
            List<CartLine> c;
            if (!v.Carts.TryGetValue(v.Shop, out c)) v.Carts[v.Shop] = c = new List<CartLine>();
            return c;
        }

        private void Open(BasePlayer player, Shop shop, string keeper, string notice = null, bool good = false)
        {
            if (!_config.Enabled) { Say(player, "The stores are closed right now."); return; }
            View v;
            if (!_views.TryGetValue(player.userID.Get(), out v)) _views[player.userID.Get()] = v = new View();
            if (v.Shop != shop.Id) { v.Category = ""; v.Page = 0; }
            v.Shop = shop.Id;
            v.Keeper = keeper;
            v.Notice = notice;
            v.NoticeGood = good;
            Draw(player);
        }

        private void Close(BasePlayer player)
        {
            _shown.Remove(player.userID.Get());
            CuiHelper.DestroyUi(player, Root);
        }

        private List<int> Visible(Shop shop, string category)
        {
            var list = new List<int>();
            for (int i = 0; i < shop.Items.Count; i++)
            {
                var it = shop.Items[i];
                if (ItemManager.FindItemDefinition(it.Item) == null) continue;
                if (category.Length > 0 && !string.Equals(it.Category ?? "", category, StringComparison.OrdinalIgnoreCase)) continue;
                list.Add(i);
            }
            return list;
        }

        private static List<string> Categories(Shop shop) =>
            shop.Items.Select(i => (i.Category ?? "").Trim()).Where(c => c.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        private static string NameOf(StoreItem it, ItemDefinition def)
        {
            var name = !string.IsNullOrEmpty(it.Name) ? it.Name : def.displayName.english;
            return it.Blueprint ? name + " (blueprint)" : name;
        }

        private static long Total(Shop shop, List<CartLine> cart) =>
            cart.Where(l => l.Index < shop.Items.Count).Sum(l => (long)shop.Items[l.Index].Price * l.Qty);

        private void Draw(BasePlayer player)
        {
            View v;
            if (!_views.TryGetValue(player.userID.Get(), out v)) return;
            var shop = FindShop(v.Shop);
            if (shop == null) { Close(player); return; }
            var fresh = _shown.Add(player.userID.Get());
            var acct = AccountOf(player);
            var cart = CartOf(v);
            cart.RemoveAll(l => l.Index >= shop.Items.Count);
            var cats = Categories(shop);
            if (v.Category.Length > 0 && !cats.Contains(v.Category, StringComparer.OrdinalIgnoreCase)) v.Category = "";
            var items = Visible(shop, v.Category);
            var pages = Math.Max(1, (items.Count + PerPage - 1) / PerPage);
            v.Page = Mathf.Clamp(v.Page, 0, pages - 1);

            var ui = new CuiElementContainer();
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
                RectTransform = { AnchorMin = "0.5 0.5", AnchorMax = "0.5 0.5", OffsetMin = "-470 -280", OffsetMax = "470 280" },
            }, Root, Main);

            // Header: shop name, keeper, the credit card with the balance, close.
            Box(ui, Main, "0.07 0.07 0.07 1", 0, 512, 940, 560);
            Box(ui, Main, Rgba(shop.Color, 1), 0, 509, 940, 512);
            Label(ui, Main, $"{shop.Name.ToUpperInvariant()}", 20, 18, 520, 420, 556, TextAnchor.MiddleLeft, true);
            Label(ui, Main, $"{v.Keeper} · press an item to add it to your cart", 11, 18, 512, 520, 528, TextAnchor.MiddleLeft, false, "1 1 1 0.5");
            // the card: a picture of a rusted-steel credit card, with the balance beside it
            Box(ui, Main, "0.16 0.11 0.09 1", 640, 515, 886, 557);
            if (_cardImage != 0)
                ui.Add(new CuiElement
                {
                    Parent = Main,
                    Components =
                    {
                        new CuiRawImageComponent { Png = _cardImage.ToString(), Color = "1 1 1 1" },
                        new CuiRectTransformComponent { AnchorMin = "0 0", AnchorMax = "0 0", OffsetMin = "644 517", OffsetMax = "704 555" },
                    },
                });
            else Box(ui, Main, "0.72 0.36 0.16 1", 644, 517, 704, 555);
            Label(ui, Main, _config.CurrencyName.ToUpperInvariant(), 9, 712, 540, 882, 555, TextAnchor.MiddleLeft, true, "1 0.85 0.7 0.6");
            Label(ui, Main, CardDigits(player.userID.Get()), 9, 712, 540, 880, 555, TextAnchor.MiddleRight, false, "1 1 1 0.4");
            Label(ui, Main, $"{Money(acct.Balance)}", 16, 712, 516, 882, 541, TextAnchor.MiddleLeft, true, "0.98 0.78 0.45 1");
            Button(ui, Main, "X", 16, "sandboxstore.ui close", "0.70 0.22 0.17 0.95", 896, 520, 930, 552);

            // Category tabs
            float tx = 12;
            Button(ui, Main, "ALL", 11, "sandboxstore.ui cat", v.Category.Length == 0 ? Rgba(shop.Color, 0.95f) : "0.22 0.22 0.22 0.95", tx, 474, tx + 70, 500);
            tx += 76;
            foreach (var c in cats)
            {
                var w = Math.Max(70, 14 + c.Length * 8);
                if (tx + w > 652) break;
                var on = string.Equals(c, v.Category, StringComparison.OrdinalIgnoreCase);
                Button(ui, Main, c.ToUpperInvariant(), 11, "sandboxstore.ui cat " + c, on ? Rgba(shop.Color, 0.95f) : "0.22 0.22 0.22 0.95", tx, 474, tx + w, 500);
                tx += w + 6;
            }

            // Item grid
            const float cw = 122, ch = 130, gx = 7, gy = 8;
            var shown = items.Skip(v.Page * PerPage).Take(PerPage).ToList();
            if (shown.Count == 0) Label(ui, Main, "Nothing for sale here yet.", 13, 12, 300, 652, 460, TextAnchor.MiddleCenter, false, "1 1 1 0.6");
            for (int n = 0; n < shown.Count; n++)
            {
                var idx = shown[n];
                var it = shop.Items[idx];
                var def = ItemManager.FindItemDefinition(it.Item);
                float x1 = 12 + (n % Cols) * (cw + gx), y2 = 466 - (n / Cols) * (ch + gy);
                var cell = CuiHelper.GetGuid();
                var inCart = cart.FirstOrDefault(l => l.Index == idx);
                ui.Add(new CuiButton
                {
                    Button = { Command = "sandboxstore.ui add " + idx, Color = inCart != null ? "0.20 0.24 0.18 0.98" : "0.17 0.17 0.17 0.98" },
                    RectTransform = { AnchorMin = "0 0", AnchorMax = "0 0", OffsetMin = $"{x1} {y2 - ch}", OffsetMax = $"{x1 + cw} {y2}" },
                    Text = { Text = "" },
                }, Main, cell);
                ui.Add(new CuiElement
                {
                    Parent = cell,
                    Components =
                    {
                        new CuiImageComponent { ItemId = it.Blueprint ? ItemManager.FindItemDefinition("blueprintbase").itemid : def.itemid, SkinId = it.Skin },
                        new CuiRectTransformComponent { AnchorMin = "0.5 1", AnchorMax = "0.5 1", OffsetMin = "-30 -66", OffsetMax = "30 -6" },
                    },
                });
                if (it.Blueprint)
                    ui.Add(new CuiElement
                    {
                        Parent = cell,
                        Components =
                        {
                            new CuiImageComponent { ItemId = def.itemid },
                            new CuiRectTransformComponent { AnchorMin = "0.5 1", AnchorMax = "0.5 1", OffsetMin = "-20 -56", OffsetMax = "20 -16" },
                        },
                    });
                if (it.Amount > 1) Label(ui, cell, "x" + it.Amount.ToString("N0", CultureInfo.InvariantCulture), 11, 4, 64, cw - 6, 80, TextAnchor.MiddleRight, true, "1 1 1 0.8");
                if (inCart != null) Label(ui, cell, "IN CART " + inCart.Qty, 9, 4, ch - 18, cw - 4, ch - 4, TextAnchor.UpperLeft, true, "0.6 0.9 0.5 1");
                Label(ui, cell, NameOf(it, def), 11, 4, 30, cw - 4, 62, TextAnchor.MiddleCenter, false, "1 1 1 0.9");
                Box(ui, cell, "0 0 0 0.35", 0, 0, cw, 26);
                Label(ui, cell, it.Price == 0 ? "FREE" : it.Price.ToString("N0", CultureInfo.InvariantCulture) + " " + _config.Short, 12, 6, 0, cw - 30, 26, TextAnchor.MiddleLeft, true,
                      acct.Balance + 1e-9 >= it.Price ? "0.98 0.85 0.45 1" : "0.85 0.45 0.40 1");
                Label(ui, cell, "+", 16, cw - 26, 0, cw - 4, 26, TextAnchor.MiddleCenter, true, "1 1 1 0.8");
            }

            // Pages
            if (string.IsNullOrEmpty(v.Notice)) Label(ui, Main, $"Page {v.Page + 1} of {pages}", 11, 230, 14, 434, 44, TextAnchor.MiddleCenter, false, "1 1 1 0.7");
            Button(ui, Main, "◀ PREV", 12, "sandboxstore.ui page -1", v.Page > 0 ? "0.28 0.28 0.28 0.95" : "0.18 0.18 0.18 0.6", 12, 14, 142, 44);
            Button(ui, Main, "NEXT ▶", 12, "sandboxstore.ui page 1", v.Page < pages - 1 ? "0.28 0.28 0.28 0.95" : "0.18 0.18 0.18 0.6", 522, 14, 652, 44);

            // Cart
            Box(ui, Main, "0.07 0.07 0.07 1", 664, 14, 928, 500);
            Label(ui, Main, $"CART  <color=#9a9a9a>({cart.Sum(l => l.Qty)})</color>", 14, 676, 470, 860, 498, TextAnchor.MiddleLeft, true);
            if (cart.Count > 0) Button(ui, Main, "CLEAR", 10, "sandboxstore.ui clear", "0.25 0.25 0.25 0.95", 862, 474, 920, 494);
            if (cart.Count == 0) Label(ui, Main, "Your cart is empty.\nClick items on the left to add them.", 12, 676, 330, 920, 460, TextAnchor.UpperLeft, false, "1 1 1 0.5");
            for (int n = 0; n < cart.Count; n++)
            {
                var line = cart[n];
                var it = shop.Items[line.Index];
                var def = ItemManager.FindItemDefinition(it.Item);
                if (def == null) continue;
                float y2 = 466 - n * 42, y1 = y2 - 38;
                Box(ui, Main, "0.14 0.14 0.14 1", 672, y1, 920, y2);
                ui.Add(new CuiElement
                {
                    Parent = Main,
                    Components =
                    {
                        new CuiImageComponent { ItemId = def.itemid, SkinId = it.Skin },
                        new CuiRectTransformComponent { AnchorMin = "0 0", AnchorMax = "0 0", OffsetMin = $"676 {y1 + 3}", OffsetMax = $"708 {y1 + 35}" },
                    },
                });
                var amount = it.Amount * line.Qty;
                Label(ui, Main, NameOf(it, def), 11, 714, y1 + 18, 842, y2, TextAnchor.MiddleLeft, false);
                Label(ui, Main, $"x{amount:N0}  ·  {(long)it.Price * line.Qty:N0} {_config.Short}", 10, 714, y1, 842, y1 + 19, TextAnchor.MiddleLeft, false, "0.98 0.85 0.45 0.9");
                Button(ui, Main, "−", 14, "sandboxstore.ui dec " + n, "0.30 0.22 0.20 0.95", 846, y1 + 7, 870, y1 + 31);
                Label(ui, Main, line.Qty.ToString(), 12, 870, y1 + 7, 892, y1 + 31, TextAnchor.MiddleCenter, true);
                Button(ui, Main, "+", 14, "sandboxstore.ui inc " + n, "0.20 0.30 0.20 0.95", 892, y1 + 7, 916, y1 + 31);
            }

            var total = Total(shop, cart);
            var after = acct.Balance - total;
            Box(ui, Main, "1 1 1 0.08", 672, 120, 920, 121);
            Label(ui, Main, "Total", 13, 676, 94, 800, 118, TextAnchor.MiddleLeft, false, "1 1 1 0.7");
            Label(ui, Main, $"{total:N0} {_config.Short}", 15, 790, 94, 916, 118, TextAnchor.MiddleRight, true, "0.98 0.85 0.45 1");
            Label(ui, Main, "After checkout", 11, 676, 74, 800, 94, TextAnchor.MiddleLeft, false, "1 1 1 0.5");
            Label(ui, Main, after >= -1e-9 ? Money(after) : "not enough", 11, 790, 74, 916, 94, TextAnchor.MiddleRight, false, after >= -1e-9 ? "1 1 1 0.6" : "0.95 0.45 0.40 1");
            var canBuy = cart.Count > 0 && after >= -1e-9;
            Button(ui, Main, "CHECKOUT", 15, "sandboxstore.ui checkout", canBuy ? "0.30 0.55 0.25 0.98" : "0.20 0.20 0.20 0.8", 672, 22, 920, 66);

            if (!string.IsNullOrEmpty(v.Notice))
                Label(ui, Main, v.Notice, 12, 148, 14, 516, 44, TextAnchor.MiddleCenter, true, v.NoticeGood ? "0.6 0.9 0.5 1" : "0.95 0.55 0.45 1");

            CuiHelper.DestroyUi(player, fresh ? Root : Main);
            CuiHelper.AddUi(player, ui);
            Drawn(player, ui, fresh);
        }

        // The first draw (window together with the backdrop that frees the mouse) can come out blurry until it's
        // drawn again, so the window is sent once more a moment after it opens unless something newer was drawn.
        private void Drawn(BasePlayer player, CuiElementContainer ui, bool fresh)
        {
            var id = player.userID.Get();
            int n;
            _draws.TryGetValue(id, out n);
            _draws[id] = ++n;
            if (!fresh) return;
            var again = new CuiElementContainer();
            again.AddRange(ui.Where(e => e.Name == Main || e.Name != Root && e.Parent != Root));
            timer.Once(0.2f, () =>
            {
                int now;
                if (player == null || !player.IsConnected || !_draws.TryGetValue(id, out now) || now != n || !_shown.Contains(id)) return;
                CuiHelper.DestroyUi(player, Main);
                CuiHelper.AddUi(player, again);
            });
        }

        // A made-up card number from the player's id, for the look of it.
        private static string CardDigits(ulong id) => "•••• " + (id % 10000).ToString("0000");

        [ConsoleCommand("sandboxstore.ui")]
        private void CcUi(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player == null) return;
            View v;
            if (!_views.TryGetValue(player.userID.Get(), out v)) { Close(player); return; }
            var shop = FindShop(v.Shop);
            if (shop == null) { Close(player); return; }
            var cart = CartOf(v);
            v.Notice = null;
            switch (arg.GetString(0))
            {
                case "close":
                    Close(player);
                    return;
                case "cat":
                    v.Category = arg.HasArgs(2) ? string.Join(" ", arg.Args.Skip(1)) : "";
                    v.Page = 0;
                    break;
                case "page":
                    v.Page += arg.GetInt(1);
                    break;
                case "add":
                {
                    var idx = arg.GetInt(1, -1);
                    if (idx < 0 || idx >= shop.Items.Count) break;
                    var line = cart.FirstOrDefault(l => l.Index == idx);
                    if (line != null)
                    {
                        if (line.Qty < _config.MaxPerLine) line.Qty++;
                        else { v.Notice = $"You can buy up to {_config.MaxPerLine} of one thing at a time."; }
                    }
                    else if (cart.Count >= MaxLines) v.Notice = $"The cart holds {MaxLines} different items. Check out first.";
                    else cart.Add(new CartLine { Index = idx, Qty = 1 });
                    break;
                }
                case "inc":
                case "dec":
                {
                    var n = arg.GetInt(1, -1);
                    if (n < 0 || n >= cart.Count) break;
                    if (arg.GetString(0) == "inc") cart[n].Qty = Math.Min(_config.MaxPerLine, cart[n].Qty + 1);
                    else if (--cart[n].Qty <= 0) cart.RemoveAt(n);
                    break;
                }
                case "clear":
                    cart.Clear();
                    break;
                case "checkout":
                    Checkout(player, shop, v, cart);
                    break;
            }
            Draw(player);
        }

        private static int FreeSlots(BasePlayer p) =>
            p.inventory.containerMain.capacity - p.inventory.containerMain.itemList.Count
            + p.inventory.containerBelt.capacity - p.inventory.containerBelt.itemList.Count;

        private void Checkout(BasePlayer player, Shop shop, View v, List<CartLine> cart)
        {
            if (cart.Count == 0) return;
            if (player.IsDead() || player.IsSleeping()) return;
            var acct = AccountOf(player);
            var total = Total(shop, cart);
            if (Whole(acct.Balance) < total)
            {
                v.Notice = $"You need {total - Whole(acct.Balance):N0} more {_config.Short}.";
                return;
            }
            // Room check: each line takes ceil(amount / stack) slots (a little pessimistic, stacks don't merge here).
            var slots = 0;
            foreach (var l in cart)
            {
                var it = shop.Items[l.Index];
                var def = ItemManager.FindItemDefinition(it.Item);
                if (def == null) continue;
                var stack = it.Blueprint ? 1 : Math.Max(1, def.stackable);
                slots += it.Blueprint ? l.Qty : (int)Math.Ceiling(it.Amount * l.Qty / (double)stack);
            }
            var free = FreeSlots(player);
            if (slots > free)
            {
                v.Notice = $"Make room first: this needs {slots} free inventory slots and you have {free}.";
                return;
            }
            if (Interface.CallHook("OnSandboxStorePurchase", player, shop.Id, total) != null) return;

            acct.Balance -= total;
            acct.Spent += total;
            var bought = new List<string>();
            foreach (var l in cart)
            {
                var it = shop.Items[l.Index];
                var def = ItemManager.FindItemDefinition(it.Item);
                if (def == null) continue;
                if (it.Blueprint)
                {
                    for (int q = 0; q < l.Qty; q++)
                    {
                        var bp = ItemManager.CreateByName("blueprintbase", 1);
                        if (bp == null) continue;
                        bp.blueprintTarget = def.itemid;
                        player.GiveItem(bp, BaseEntity.GiveItemReason.PickedUp);
                    }
                }
                else
                {
                    var left = it.Amount * l.Qty;
                    var stack = Math.Max(1, def.stackable);
                    while (left > 0)
                    {
                        var n = Math.Min(left, stack);
                        var item = ItemManager.Create(def, n, it.Skin);
                        if (item == null) break;
                        player.GiveItem(item, BaseEntity.GiveItemReason.PickedUp);
                        left -= n;
                    }
                }
                bought.Add($"{it.Amount * l.Qty}x {NameOf(it, def)}");
            }
            Log(player.userID.Get(), player.displayName, "buy", -total, shop.Name + ": " + string.Join(", ", bought));
            cart.Clear();
            Effect.server.Run("assets/prefabs/deployable/vendingmachine/effects/vending-machine-purchase-human.prefab", player.transform.position);
            v.Notice = $"Thanks for shopping! {total:N0} {_config.Short} paid.";
            v.NoticeGood = true;
            _dirty = true;
        }

        private static string Rgba(string rgb, float alpha)
        {
            var parts = (rgb ?? "").Split(' ');
            return parts.Length >= 3 ? $"{parts[0]} {parts[1]} {parts[2]} {alpha.ToString(CultureInfo.InvariantCulture)}" : $"0.5 0.5 0.5 {alpha.ToString(CultureInfo.InvariantCulture)}";
        }

        private static void Box(CuiElementContainer ui, string parent, string color, float x1, float y1, float x2, float y2) =>
            ui.Add(new CuiPanel
            {
                Image = { Color = color },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "0 0", OffsetMin = $"{x1} {y1}", OffsetMax = $"{x2} {y2}" },
            }, parent);

        private static void Label(CuiElementContainer ui, string parent, string text, int size, float x1, float y1, float x2, float y2,
                                  TextAnchor align = TextAnchor.MiddleLeft, bool bold = false, string color = "1 1 1 0.95") =>
            ui.Add(new CuiLabel
            {
                Text = { Text = text ?? "", FontSize = size, Align = align, Color = color, Font = bold ? "robotocondensed-bold.ttf" : "robotocondensed-regular.ttf" },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "0 0", OffsetMin = $"{x1} {y1}", OffsetMax = $"{x2} {y2}" },
            }, parent);

        private static void Button(CuiElementContainer ui, string parent, string text, int size, string command, string color, float x1, float y1, float x2, float y2) =>
            ui.Add(new CuiButton
            {
                Button = { Command = command, Color = color },
                RectTransform = { AnchorMin = "0 0", AnchorMax = "0 0", OffsetMin = $"{x1} {y1}", OffsetMax = $"{x2} {y2}" },
                Text = { Text = text, FontSize = size, Align = TextAnchor.MiddleCenter, Color = "1 1 1 0.92", Font = "robotocondensed-bold.ttf" },
            }, parent);

        #endregion

        #region Chat commands

        private bool IsAdmin(BasePlayer player) =>
            player != null && (player.IsAdmin || permission.UserHasPermission(player.UserIDString, AdminPerm));

        private void Say(BasePlayer player, string text) => Player.Message(player, text, _config.ChatPrefix, 0);

        private static string Grid(Vector3 pos)
        {
            try { return MapHelper.PositionToString(pos); }
            catch { return $"{pos.x:0}, {pos.z:0}"; }
        }

        private static string Direction(Vector3 from, Vector3 to)
        {
            var d = to - from;
            var angle = Mathf.Repeat(Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg, 360f);
            string[] names = { "N", "NE", "E", "SE", "S", "SW", "W", "NW" };
            return names[(int)Math.Round(angle / 45f) % 8];
        }

        private BasePlayer FindPlayer(string text)
        {
            ulong id;
            if (ulong.TryParse(text, out id)) return BasePlayer.FindAwakeOrSleeping(text) ?? BasePlayer.FindByID(id);
            return BasePlayer.activePlayerList.FirstOrDefault(p => p.displayName.Equals(text, StringComparison.OrdinalIgnoreCase))
                   ?? BasePlayer.activePlayerList.FirstOrDefault(p => p.displayName.IndexOf(text, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        [ChatCommand("credits")]
        private void CmdCredits(BasePlayer player, string command, string[] args) => Credits(player, args);

        [ChatCommand("rc")]
        private void CmdRc(BasePlayer player, string command, string[] args) => Credits(player, args);

        [ChatCommand("balance")]
        private void CmdBalance(BasePlayer player, string command, string[] args) => Credits(player, args);

        private void Credits(BasePlayer player, string[] args)
        {
            var sub = args.Length > 0 ? args[0].ToLowerInvariant() : "";
            int amount;
            if (sub == "pay")
            {
                if (!_config.AllowPay) { Say(player, "Paying other players is turned off on this server."); return; }
                if (args.Length < 3 || !int.TryParse(args[args.Length - 1], out amount) || amount <= 0) { Say(player, "Usage: /credits pay <player> <amount>"); return; }
                var target = FindPlayer(string.Join(" ", args.Skip(1).Take(args.Length - 2)));
                if (target == null || target == player) { Say(player, "Couldn't find that player online."); return; }
                var mine = AccountOf(player);
                if (Whole(mine.Balance) < amount) { Say(player, $"You only have {Money(mine.Balance)}."); return; }
                mine.Balance -= amount;
                var theirs = AccountOf(target);
                theirs.Balance += amount;
                Log(player.userID.Get(), player.displayName, "pay", -amount, "to " + target.displayName);
                Say(player, $"You paid <color=#f0c040>{amount:N0} {_config.Short}</color> to {target.displayName}. Balance: {Money(mine.Balance)}.");
                Say(target, $"{player.displayName} paid you <color=#f0c040>{amount:N0} {_config.Short}</color>. Balance: {Money(theirs.Balance)}.");
                if (_config.ShowEarnings) ShowToast(target, amount, "pay");
                return;
            }
            if (sub == "give" || sub == "take" || sub == "set")
            {
                if (!IsAdmin(player)) { Say(player, "Only admins can do that."); return; }
                if (args.Length < 3 || !int.TryParse(args[args.Length - 1], out amount) || amount < 0) { Say(player, $"Usage: /credits {sub} <player> <amount>"); return; }
                var target = FindPlayer(string.Join(" ", args.Skip(1).Take(args.Length - 2)));
                if (target == null) { Say(player, "Couldn't find that player."); return; }
                Say(player, AdminChange(target.userID.Get(), target.displayName, sub, amount));
                return;
            }

            var a = AccountOf(player);
            var lines = new List<string>
            {
                $"You have <color=#f0c040>{Money(a.Balance)}</color> ({_config.CurrencyName}). Earned {Whole(a.Earned):N0}, spent {Whole(a.Spent):N0}.",
                "Earn them from kills, gathering wood, stone and ore, farming, fishing and keycard puzzles.",
            };
            var here = player.transform.position;
            var stores = new List<string>();
            foreach (var kv in _spawned)
            {
                var vend = FindVendor(kv.Key);
                var shop = vend != null ? FindShop(vend.Shop) : null;
                if (shop == null || kv.Value == null) continue;
                var p = kv.Value.transform.position;
                var m = MonumentAt(p);
                stores.Add($"{shop.Name} at {(m != null ? MonumentName(m) + " " : "")}{Grid(p)} ({Vector3.Distance(here, p):0} m {Direction(here, p)})");
            }
            lines.Add(stores.Count > 0 ? "Stores: " + string.Join("; ", stores.Distinct()) : "No stores are open on this map yet.");
            if (_config.AllowPay) lines.Add("Send credits with /credits pay <player> <amount>.");
            Say(player, string.Join("\n", lines));
        }

        private string AdminChange(ulong id, string name, string kind, double amount)
        {
            var a = AccountOf(id, name);
            if (kind == "give") { a.Balance += amount; a.Earned += amount; }
            else if (kind == "take") a.Balance = Math.Max(0, a.Balance - amount);
            else a.Balance = amount;
            Log(id, a.Name, "admin-" + kind, kind == "take" ? -amount : amount, "");
            _dirty = true;
            var online = BasePlayer.FindByID(id);
            if (online != null && kind == "give" && _config.ShowEarnings) ShowToast(online, amount, "admin");
            return $"{(string.IsNullOrEmpty(a.Name) ? id.ToString() : a.Name)} now has {Money(a.Balance)}.";
        }

        [ChatCommand("store")]
        private void CmdStore(BasePlayer player, string command, string[] args)
        {
            var sub = args.Length > 0 ? args[0].ToLowerInvariant() : "";
            if (!IsAdmin(player))
            {
                Credits(player, new string[0]);
                return;
            }
            switch (sub)
            {
                case "add":
                {
                    if (args.Length < 2) { Say(player, "Usage: /store add <" + string.Join("|", _config.Shops.Select(s => s.Id)) + "|all> [name]"); return; }
                    var yaw = FacingYaw(player);
                    if (args[1].ToLowerInvariant() == "all")
                    {
                        // One of each in a row to your right, facing you, 2.5 m apart.
                        var right = Quaternion.Euler(0, yaw + 180f, 0) * Vector3.right;
                        var start = player.transform.position;
                        var placed = 0;
                        foreach (var s in _config.Shops.Where(s => s.Enabled))
                        {
                            var pos = start + right * (2.5f * placed);
                            pos.y = Math.Max(pos.y, TerrainMeta.HeightMap != null ? TerrainMeta.HeightMap.GetHeight(pos) : pos.y);
                            AddVendor(s, pos, yaw, null);
                            placed++;
                        }
                        Say(player, $"Placed {placed} shopkeepers in a row at {Grid(start)}. Step back to talk to them.");
                        return;
                    }
                    var shop = FindShop(args[1]);
                    if (shop == null) { Say(player, $"No shop called {args[1]}. Shops: {string.Join(", ", _config.Shops.Select(s => s.Id))}"); return; }
                    var v = AddVendor(shop, player.transform.position, yaw, string.Join(" ", args.Skip(2)));
                    var where = string.IsNullOrEmpty(v.Monument) ? "this map only" : "saved relative to the monument, so it comes back after a wipe";
                    Say(player, $"Placed {KeeperName(v, shop)} ({shop.Name}, id {v.Id}) at {Grid(player.transform.position)}, {where}. Step back to talk to them.");
                    return;
                }
                case "build":
                {
                    // /store build [shop ids...]: a strip mall of the listed shops (all open shops by default), in front of you.
                    var shops = args.Length > 1
                        ? args.Skip(1).Select(FindShop).Where(x => x != null).Distinct().ToList()
                        : _config.Shops.Where(x => x.Enabled).ToList();
                    if (shops.Count == 0) { Say(player, "No shops to build. Shops: " + string.Join(", ", _config.Shops.Select(x => x.Id))); return; }
                    if (shops.Count > 8) shops = shops.Take(8).ToList();
                    Say(player, BuildMall(player, shops));
                    return;
                }
                case "unbuild":
                {
                    var m = NearestMall(player, 60f);
                    if (m == null) { Say(player, "No strip mall within 60 m."); return; }
                    RemoveMall(m);
                    Say(player, $"Removed strip mall {m.Id} and its shopkeepers.");
                    return;
                }
                case "rebuild":
                {
                    foreach (var m in _vendors.Malls) SpawnMall(m);
                    SaveVendors();
                    SpawnAllVendors();
                    Say(player, $"Rebuilt {_vendors.Malls.Count(x => x.Map == MapKey())} strip mall(s) with the current displays.");
                    return;
                }
                case "remove":
                {
                    var v = NearestVendor(player, 8f);
                    if (v == null) { Say(player, "No shopkeeper within 8 m. Stand next to the one to remove."); return; }
                    DespawnVendor(v);
                    _vendors.Vendors.Remove(v);
                    SaveVendors();
                    Say(player, $"Removed shopkeeper {v.Id}.");
                    return;
                }
                case "move":
                {
                    var v = args.Length > 1 ? FindVendor(args[1]) : null;
                    if (v == null) { Say(player, "Usage: /store move <id> (moves them to where you stand; /store list shows ids)"); return; }
                    SetPlace(v, player.transform.position, FacingYaw(player));
                    SaveVendors();
                    SpawnVendor(v);
                    Say(player, $"Moved {v.Id} to {Grid(player.transform.position)}.");
                    return;
                }
                case "outfit":
                {
                    var v = args.Length > 1 ? FindVendor(args[1]) : NearestVendor(player, 8f);
                    if (v == null) { Say(player, "Usage: /store outfit <id> [clear]"); return; }
                    if (args.Length > 2 && args[2].ToLowerInvariant() == "clear") v.Outfit = new List<OutfitItem>();
                    else
                    {
                        var worn = player.inventory.containerWear.itemList;
                        if (worn.Count == 0) { Say(player, "Put on the clothes you want them to wear first, then run this again."); return; }
                        v.Outfit = worn.Select(i => new OutfitItem { Item = i.info.shortname, Skin = i.skin }).ToList();
                    }
                    SaveVendors();
                    SpawnVendor(v);
                    Say(player, $"Updated {v.Id}'s outfit.");
                    return;
                }
                case "open":
                {
                    var shop = args.Length > 1 ? FindShop(args[1]) : null;
                    if (shop == null) { Say(player, "Usage: /store open <" + string.Join("|", _config.Shops.Select(s => s.Id)) + ">"); return; }
                    Open(player, shop, shop.Keeper);
                    return;
                }
                case "list":
                {
                    if (_vendors.Vendors.Count == 0) { Say(player, "No shopkeepers yet. Stand where one should go and type /store add <shop>, or /store add all."); return; }
                    var lines = _vendors.Vendors.Select(v =>
                    {
                        var shop = FindShop(v.Shop);
                        NPCTalking npc;
                        var here = _spawned.TryGetValue(v.Id, out npc) && npc != null;
                        var where = here ? Grid(npc.transform.position) : "not on this map";
                        return $"{v.Id}: {(shop != null ? KeeperName(v, shop) + " - " + shop.Name : v.Shop + " (shop missing)")} at {(string.IsNullOrEmpty(v.Monument) ? "" : v.Monument + " ")}{where}";
                    });
                    Say(player, string.Join("\n", lines));
                    return;
                }
                default:
                    Say(player, "Store admin: /store build [shops] (a strip mall in front of you), /store unbuild, /store rebuild, /store add <shop|all> [name], /store remove, /store move <id>, /store outfit <id> [clear], /store list, /store open <shop>.\n"
                                + "Credits: /credits give|take|set <player> <amount>. Shops: " + string.Join(", ", _config.Shops.Select(s => s.Id)));
                    return;
            }
        }

        #endregion

        #region RCON (used by the Rust Sandbox manager)

        [ConsoleCommand("sandboxstore.status")]
        private void CcStatus(ConsoleSystem.Arg arg)
        {
            if (arg.Connection != null && !arg.IsAdmin) return;
            var vendors = _vendors.Vendors.Select(v =>
            {
                NPCTalking npc;
                var here = _spawned.TryGetValue(v.Id, out npc) && npc != null;
                var shop = FindShop(v.Shop);
                return new
                {
                    id = v.Id, shop = v.Shop, name = shop != null ? KeeperName(v, shop) : v.Name, monument = v.Monument,
                    onMap = here, grid = here ? Grid(npc.transform.position) : "",
                };
            }).ToList();
            var online = new HashSet<ulong>(BasePlayer.activePlayerList.Select(p => p.userID.Get()));
            var players = _data.Players.OrderByDescending(kv => kv.Value.Balance).Take(200).Select(kv => new
            {
                id = kv.Key.ToString(), name = kv.Value.Name, balance = Whole(kv.Value.Balance), earned = Whole(kv.Value.Earned),
                spent = Whole(kv.Value.Spent), online = online.Contains(kv.Key),
                earnedBy = kv.Value.EarnedBy.ToDictionary(e => e.Key, e => Math.Round(e.Value, 1)),
            }).ToList();
            var log = _data.Log.AsEnumerable().Reverse().Take(50).Select(l => new
            {
                time = l.Time.ToString("o"), id = l.Id.ToString(), name = l.Name, kind = l.Kind, amount = l.Amount, text = l.Text,
            }).ToList();
            var missing = _config.Shops.SelectMany(s => s.Items.Where(i => ItemManager.FindItemDefinition(i.Item) == null).Select(i => s.Id + ":" + i.Item)).ToList();
            arg.ReplyWith(JsonConvert.SerializeObject(new
            {
                version = Version.ToString(), enabled = _config.Enabled, currency = _config.CurrencyName, @short = _config.Short,
                vendors, players, log, missingItems = missing, signSize = $"{_neonSize.x}x{_neonSize.y}",
                malls = _vendors.Malls.Select(m => new
                {
                    id = m.Id, shops = m.Shops, onMap = m.Map == MapKey(),
                    grid = m.Map == MapKey() ? Grid(MallPoint(m, MallWidth(m) / 2f, 0f, 0f)) : "",
                }).ToList(),
                totalCredits = Whole(_data.Players.Values.Sum(a => a.Balance)),
            }));
        }

        [ConsoleCommand("sandboxstore.credits")]
        private void CcCredits(ConsoleSystem.Arg arg)
        {
            if (arg.Connection != null && !arg.IsAdmin) return;
            var kind = arg.GetString(0).ToLowerInvariant();
            var id = arg.GetULong(1);
            var amount = arg.GetInt(2, -1);
            if ((kind != "give" && kind != "take" && kind != "set") || id == 0 || amount < 0)
            {
                arg.ReplyWith("Usage: sandboxstore.credits give|take|set <steamid> <amount>");
                return;
            }
            var online = BasePlayer.FindByID(id);
            arg.ReplyWith(AdminChange(id, online != null ? online.displayName : null, kind, amount));
        }

        [ConsoleCommand("sandboxstore.mall.remove")]
        private void CcMallRemove(ConsoleSystem.Arg arg)
        {
            if (arg.Connection != null && !arg.IsAdmin) return;
            var m = _vendors.Malls.FirstOrDefault(x => string.Equals(x.Id, arg.GetString(0), StringComparison.OrdinalIgnoreCase));
            if (m == null) { arg.ReplyWith("Usage: sandboxstore.mall.remove <id> (ids are in sandboxstore.status)"); return; }
            RemoveMall(m);
            arg.ReplyWith($"Removed strip mall {m.Id}.");
        }

        [ConsoleCommand("sandboxstore.respawn")]
        private void CcRespawn(ConsoleSystem.Arg arg)
        {
            if (arg.Connection != null && !arg.IsAdmin) return;
            DespawnAllVendors();
            foreach (var m in _vendors.Malls) SpawnMall(m);
            SpawnAllVendors();
            arg.ReplyWith($"Respawned {_spawned.Count} shopkeepers and {_mallEntities.Count} strip mall(s).");
        }

        #endregion
    }
}
