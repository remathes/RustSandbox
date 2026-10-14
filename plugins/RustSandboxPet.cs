// Rust Sandbox — Pets
// A pet animal that follows you, fights with you, carries a backpack and fetches loot. Pick from a wolf, bear,
// polar bear, tiger, panther, boar or crocodile with /pet choose. Made for solo play: one pet per player, and it
// never attacks players unless the config allows it.
//   Fighters (wolf, tiger, panther): /pet roar makes you hit harder and heals you over time.
//   Tanks (bear, polar bear, boar, crocodile): tougher, and /pet roar pulls every enemy nearby onto the pet and you take less damage.
// Pets can be hurt. At no health a pet goes down: look at it and press E with a bandage, syringe or medkit
// (in your hand or your inventory) to get it back up. Left alone too long it goes home to rest for a while.
//   /pet            call your pet (or bring it back to you)      /pet dismiss   send it away
//   /pet choose     pick an animal                               /pet roar      its roar (see above)
//   /pet bag        open its backpack (or look at it and press E)  /pet stay      sit / follow again
//   /pet fetch      pick up what you look at, or loot around you   /pet attack    attack what you look at
//   /pet patrol     roam around you (out from under your feet while you build), /pet again to call it back
//   /pet heal       patch it up with a medical item              /pet keys      how to put these on keys
// The backpack is kept in oxide/data/RustSandboxPet.json, so it survives restarts, dismissing and changing animals.
// With the Skills plugin, fights and fetching earn Companion XP, and its perks make the roar stronger, unlock
// /pet sniff, getting you up when you're downed, extra meat from your pet's kills and a bigger backpack.
// Other plugins can ask for the owner's active buffs with API_Buffs(ulong) (the Event HUD shows them).

using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Libraries.Covalence;
using Oxide.Core.Plugins;
using Rust;
using Rust.Ai.Gen2;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("Rust Sandbox Pet", "RustSandbox", "1.4.2")]
    [Description("A pet wolf, bear, tiger, panther, boar or crocodile: it follows you, fights with you, carries a backpack and fetches loot, and goes down until you patch it up.")]
    public class RustSandboxPet : RustPlugin
    {
        private const string UsePerm = "rustsandboxpet.use";
        private const float TickSeconds = 0.25f;

        #region Config

        private class PluginConfig
        {
            [JsonProperty("Needs the rustsandboxpet.use permission (off = everyone can have a wolf)")] public bool NeedsPermission;
            [JsonProperty("Backpack slots (6 to 48)")] public int BagSlots = 24;
            [JsonProperty("Open the backpack only within this many metres of the wolf (0 = anywhere)")] public float BagRange = 10f;
            [JsonProperty("Fetch / attack button: FIRE_THIRD (middle mouse), RELOAD, USE... or None")] public string FetchButton = "FIRE_THIRD";
            [JsonProperty("Look at the wolf and press E to open its backpack")] public bool UseOpensBag = true;
            [JsonProperty("Follow distance (metres)")] public float FollowDistance = 2.5f;
            [JsonProperty("Teleport back to you past this distance (metres)")] public float TeleportDistance = 70f;
            [JsonProperty("Patrol: roams this far from you (metres)")] public float PatrolRadius = 30f;
            [JsonProperty("Patrol: keeps at least this far from you (metres)")] public float PatrolMinDistance = 8f;
            [JsonProperty("Fetch: look for loot within this many metres of you")] public float FetchRadius = 20f;
            [JsonProperty("Fetch: most things fetched per key press")] public int FetchMax = 10;
            [JsonProperty("Fetch: break barrels to get their loot")] public bool FetchBarrels = true;
            [JsonProperty("Fetch: empty crates")] public bool FetchCrates = true;
            [JsonProperty("Fetch: empty bodies (NPCs and your own)")] public bool FetchBodies = true;
            [JsonProperty("Fetch: pick up plants, stones, ore and wood on the ground")] public bool FetchCollectibles = true;
            [JsonProperty("Fetch: pick up dropped items")] public bool FetchDropped = true;
            [JsonProperty("Animals (wolf, bear, polarbear, tiger, panther, boar, crocodile)")] public Dictionary<string, AnimalConfig> Animals = DefaultAnimals();
            [JsonProperty("Pets can be hurt and go down (off = they can't be hurt)")] public bool CanGoDown = true;
            [JsonProperty("Downed: goes home to rest after this many minutes if you don't help it (0 = waits for you)")] public float DownedMinutes = 10f;
            [JsonProperty("Downed: minutes it rests at home before you can call it again")] public float RestMinutes = 5f;
            [JsonProperty("Heals this share of its health per second out of a fight")] public float RegenPerSecond = 0.02f;
            [JsonProperty("Seconds after being hurt before it starts healing")] public float RegenDelay = 10f;
            [JsonProperty("Medical items: share of the pet's health each one gives back")] public Dictionary<string, float> MedicalItems = new Dictionary<string, float>
            {
                ["bandage"] = 0.3f, ["syringe.medical"] = 0.6f, ["largemedkit"] = 1f,
            };
            [JsonProperty("Helps when something attacks you")] public bool DefendOwner = true;
            [JsonProperty("Attacks what you attack")] public bool AssistOwner = true;
            [JsonProperty("Tank: pulls enemies off you onto itself")] public bool Tank = true;
            [JsonProperty("Tank: enemies this close to the wolf leave you alone (metres)")] public float TauntRange = 15f;
            [JsonProperty("Tank: goes for enemies after you within this many metres")] public float GuardRadius = 25f;
            [JsonProperty("Can attack players (off for solo play)")] public bool AttackPlayers;
            [JsonProperty("Leaves farm animals alone (cows, bulls, sheep, rams, chickens)")] public bool SpareFarmAnimals = true;
            [JsonProperty("Its kills and bites count as yours (quests, skills XP)")] public bool CreditOwner = true;
            [JsonProperty("Gives up a chase past this distance from you (metres)")] public float ChaseDistance = 45f;
            [JsonProperty("Comes back by itself when you log in")] public bool ResummonOnLogin = true;
            [JsonProperty("Roar: seconds it lasts")] public float RoarSeconds = 30f;
            [JsonProperty("Roar: seconds between roars")] public float RoarCooldown = 120f;
            [JsonProperty("Roar (wolf, tiger, panther): you hit this much harder (0.15 = 15%, Howl perk adds more)")] public float RoarDamage = 0.15f;
            [JsonProperty("Roar (wolf, tiger, panther): health you heal per second")] public float RoarRegen = 1f;
            [JsonProperty("Roar (wolf, tiger, panther): extra health healed at once per Howl perk rank")] public float HowlHeal = 5f;
            [JsonProperty("Roar (bear, polar bear, boar, crocodile): damage you take is cut by this much (0.3 = 30%)")] public float RoarGuard = 0.3f;
            [JsonProperty("Roar (bear, polar bear, boar, crocodile): pulls enemies within this many metres onto the pet")] public float RoarPullRange = 25f;
            [JsonProperty("Loyal Friend perk: minutes between revives")] public float ReviveMinutes = 10f;
            [JsonProperty("Loyal Friend perk: seconds it takes to get you up")] public float ReviveSeconds = 3f;
            [JsonProperty("Keen Nose perk: seconds between sniffs")] public float SniffCooldown = 20f;
        }

        private class AnimalConfig
        {
            [JsonProperty("Can be picked")] public bool Enabled = true;
            [JsonProperty("Health (0 = the wild animal's)")] public float Health;
            [JsonProperty("Bite damage (0 = the wild animal's bite)")] public float BiteDamage;
            [JsonProperty("Bite damage multiplier")] public float DamageMultiplier = 1f;
            [JsonProperty("Damage it takes (1 = full, 0.5 = half)")] public float DamageTaken = 1f;
            [JsonProperty("Seconds between bites")] public float BiteCooldown = 1.4f;
        }

        private static Dictionary<string, AnimalConfig> DefaultAnimals() => new Dictionary<string, AnimalConfig>
        {
            ["wolf"] = new AnimalConfig { Health = 300f, DamageMultiplier = 1.5f, DamageTaken = 0.6f, BiteCooldown = 1.4f },
            ["tiger"] = new AnimalConfig { Health = 400f, DamageMultiplier = 1.2f, DamageTaken = 0.6f, BiteCooldown = 1.5f },
            ["panther"] = new AnimalConfig { Health = 300f, DamageMultiplier = 1.1f, DamageTaken = 0.7f, BiteCooldown = 1.0f },
            ["bear"] = new AnimalConfig { Health = 800f, DamageMultiplier = 1f, DamageTaken = 0.4f, BiteCooldown = 1.8f },
            ["polarbear"] = new AnimalConfig { Health = 900f, DamageMultiplier = 1.1f, DamageTaken = 0.35f, BiteCooldown = 1.9f },
            ["boar"] = new AnimalConfig { Health = 500f, DamageMultiplier = 1.3f, DamageTaken = 0.5f, BiteCooldown = 1.3f },
            ["crocodile"] = new AnimalConfig { Health = 700f, DamageMultiplier = 1.2f, DamageTaken = 0.45f, BiteCooldown = 2.0f },
        };

        private PluginConfig _config;

        protected override void LoadDefaultConfig() => _config = new PluginConfig();

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                _config = Config.ReadObject<PluginConfig>();
                if (_config == null) throw new Exception("empty");
            }
            catch
            {
                PrintWarning("Config file was invalid, using defaults.");
                _config = new PluginConfig();
            }
            _config.BagSlots = Mathf.Clamp(_config.BagSlots, 6, 48);
            _config.FetchMax = Mathf.Clamp(_config.FetchMax, 1, 50);
            if (_config.Animals == null) _config.Animals = DefaultAnimals();
            foreach (var kv in DefaultAnimals())
                if (!_config.Animals.ContainsKey(kv.Key) || _config.Animals[kv.Key] == null) _config.Animals[kv.Key] = kv.Value;
            foreach (var a in _config.Animals.Values) a.BiteCooldown = Mathf.Max(0.5f, a.BiteCooldown);
            if (_config.MedicalItems == null) _config.MedicalItems = new Dictionary<string, float>();
            SaveConfig();
        }

        protected override void SaveConfig() => Config.WriteObject(_config, true);

        #endregion

        #region Data

        private class ItemData
        {
            public int Id;
            public int Amount;
            public ulong Skin;
            public float Condition;
            public float MaxCondition;
            public int Ammo;
            public int AmmoType;
            public int Blueprint;
            public string Name;
            public string Text;
            public int Flags;
            public List<ItemData> Contents;

            public static ItemData From(Item item)
            {
                var data = new ItemData
                {
                    Id = item.info.itemid, Amount = item.amount, Skin = item.skin, Name = item.name, Text = item.text,
                    Blueprint = item.blueprintTarget, Flags = (int)item.flags,
                };
                if (item.hasCondition)
                {
                    data.Condition = item.condition;
                    data.MaxCondition = item.maxCondition;
                }
                var gun = item.GetHeldEntity() as BaseProjectile;
                if (gun != null && gun.primaryMagazine != null)
                {
                    data.Ammo = gun.primaryMagazine.contents;
                    data.AmmoType = gun.primaryMagazine.ammoType != null ? gun.primaryMagazine.ammoType.itemid : 0;
                }
                if (item.contents != null && item.contents.itemList.Count > 0)
                    data.Contents = item.contents.itemList.Select(From).ToList();
                return data;
            }

            public Item Create()
            {
                var item = ItemManager.CreateByItemID(Id, Math.Max(1, Amount), Skin);
                if (item == null) return null;
                item.name = Name;
                item.text = Text;
                item.blueprintTarget = Blueprint;
                item.flags = (Item.Flag)Flags;
                if (item.hasCondition && MaxCondition > 0)
                {
                    item.maxCondition = MaxCondition;
                    item.condition = Condition;
                }
                var gun = item.GetHeldEntity() as BaseProjectile;
                if (gun != null && gun.primaryMagazine != null)
                {
                    gun.primaryMagazine.contents = Ammo;
                    if (AmmoType != 0)
                    {
                        var ammo = ItemManager.FindItemDefinition(AmmoType);
                        if (ammo != null) gun.primaryMagazine.ammoType = ammo;
                    }
                }
                if (Contents != null && item.contents != null)
                {
                    foreach (var c in Contents)
                    {
                        var child = c.Create();
                        if (child != null && !child.MoveToContainer(item.contents)) child.Remove();
                    }
                }
                item.MarkDirty();
                return item;
            }
        }

        private class OwnerData
        {
            public bool Active;
            /// <summary>The animal picked with /pet choose.</summary>
            public string Animal = "wolf";
            /// <summary>A pet that went down and was left: resting at home until this time (unix seconds).</summary>
            public double RestUntil;
            public List<ItemData> Bag = new List<ItemData>();
        }

        private Dictionary<ulong, OwnerData> _data = new Dictionary<ulong, OwnerData>();

        private OwnerData DataFor(ulong id)
        {
            OwnerData d;
            if (!_data.TryGetValue(id, out d)) _data[id] = d = new OwnerData();
            return d;
        }

        private void LoadData()
        {
            try { _data = Interface.Oxide.DataFileSystem.ReadObject<Dictionary<ulong, OwnerData>>(Name) ?? new Dictionary<ulong, OwnerData>(); }
            catch { _data = new Dictionary<ulong, OwnerData>(); }
        }

        private void SaveData()
        {
            foreach (var kv in _bags) DataFor(kv.Key).Bag = kv.Value.itemList.Select(ItemData.From).ToList();
            Interface.Oxide.DataFileSystem.WriteObject(Name, _data);
        }

        #endregion

        #region Backpacks

        // One backpack per player. It lives here rather than on the wolf so it isn't lost when the wolf is dismissed.
        private readonly Dictionary<ulong, ItemContainer> _bags = new Dictionary<ulong, ItemContainer>();

        private ItemContainer BagFor(ulong id)
        {
            ItemContainer bag;
            if (_bags.TryGetValue(id, out bag))
            {
                // Big Pack perk: grows the bag (it never shrinks below what's in it).
                var want = Math.Max(BagCapacity(id), bag.itemList.Count > 0 ? bag.itemList.Max(i => i.position) + 1 : 0);
                if (bag.capacity != want) { bag.capacity = want; bag.MarkDirty(); }
                return bag;
            }
            bag = new ItemContainer();
            bag.ServerInitialize(null, Math.Max(BagCapacity(id), DataFor(id).Bag.Count));
            bag.GiveUID();
            foreach (var d in DataFor(id).Bag)
            {
                var item = d.Create();
                if (item != null && !item.MoveToContainer(bag)) item.Remove();
            }
            _bags[id] = bag;
            return bag;
        }

        private void OpenBag(BasePlayer player)
        {
            var pet = PetOf(player);
            if (pet == null) { Reply(player, "Pet.NoPet"); return; }
            if (_config.BagRange > 0 && Vector3.Distance(player.transform.position, pet.Wolf.transform.position) > _config.BagRange)
            {
                Reply(player, "Pet.TooFar", _config.BagRange);
                return;
            }
            var bag = BagFor(player.userID);
            bag.entityOwner = pet.Wolf;
            player.EndLooting();
            NextTick(() =>
            {
                if (player == null || pet.Wolf == null || pet.Wolf.IsDestroyed) return;
                if (!player.inventory.loot.StartLootingEntity(pet.Wolf, false)) return;
                player.inventory.loot.AddContainer(bag);
                player.inventory.loot.SendImmediate();
                player.ClientRPC(RpcTarget.Player("RPC_OpenLootPanel", player), "generic_resizable");
                // The Tweaks plugin's Sort / Quick stack / Take all buttons, now that the backpack is in the loot panel.
                RustSandboxTweaks?.Call("ShowSortButtons", player);
            });
        }

        private int BagCapacity(ulong id) => Mathf.Clamp(_config.BagSlots + (int)Math.Round(PerkBonus(id, "bigpack")), 6, 48);

        /// <summary>Puts an item in the wolf's backpack, else in the owner's inventory. False if neither has room.</summary>
        private bool Stash(BasePlayer owner, Item item)
        {
            if (item.MoveToContainer(BagFor(owner.userID))) return true;
            return owner.inventory.GiveItem(item);
        }

        private bool HasRoom(BasePlayer owner, Item item)
        {
            var bag = BagFor(owner.userID);
            if (bag.itemList.Count < bag.capacity) return true;
            if (bag.itemList.Any(i => i.info == item.info && i.skin == item.skin && i.amount < i.MaxStackable())) return true;
            return !owner.inventory.containerMain.IsFull() || !owner.inventory.containerBelt.IsFull();
        }

        #endregion

        #region Animals

        private enum Role { Fighter, Tank }

        private class Species
        {
            public string Key, Name, Plural, Prefab, Folder, Blurb;
            public Role Role;
        }

        // Fighters' roar buffs the owner; tanks' roar pulls enemies onto the pet and shields the owner.
        private static readonly List<Species> AllSpecies = new List<Species>
        {
            new Species { Key = "wolf", Name = "wolf", Plural = "wolves", Prefab = "wolf2", Folder = "wolf", Role = Role.Fighter, Blurb = "quick and loyal" },
            new Species { Key = "tiger", Name = "tiger", Plural = "tigers", Prefab = "tiger", Folder = "tiger", Role = Role.Fighter, Blurb = "hits hardest" },
            new Species { Key = "panther", Name = "panther", Plural = "panthers", Prefab = "panther", Folder = "panther", Role = Role.Fighter, Blurb = "fastest bites" },
            new Species { Key = "bear", Name = "bear", Plural = "bears", Prefab = "bear", Folder = "bear", Role = Role.Tank, Blurb = "very tough" },
            new Species { Key = "polarbear", Name = "polar bear", Plural = "polar bears", Prefab = "polarbear", Folder = "bear", Role = Role.Tank, Blurb = "toughest of all" },
            new Species { Key = "boar", Name = "boar", Plural = "boars", Prefab = "boar", Folder = "boar", Role = Role.Tank, Blurb = "sturdy, fetches from twice as far" },
            new Species { Key = "crocodile", Name = "crocodile", Plural = "crocodiles", Prefab = "crocodile", Folder = "crocodile", Role = Role.Tank, Blurb = "big bites, at home in water" },
        };

        private static Species FindSpecies(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            key = key.ToLowerInvariant().Replace(" ", "").Replace("_", "");
            if (key == "polar") key = "polarbear";
            if (key == "croc" || key == "gator" || key == "alligator") key = "crocodile";
            if (key == "pig" || key == "hog") key = "boar";
            if (key == "cat" || key == "jaguar") key = "panther";
            return AllSpecies.FirstOrDefault(sp => sp.Key == key || sp.Plural.Replace(" ", "") == key);
        }

        private Species SpeciesOf(ulong id) => FindSpecies(DataFor(id).Animal) ?? AllSpecies[0];

        private AnimalConfig ConfigOf(Species sp)
        {
            AnimalConfig c;
            return _config.Animals.TryGetValue(sp.Key, out c) && c != null ? c : new AnimalConfig();
        }

        private bool Allowed(Species sp) => ConfigOf(sp).Enabled;

        private readonly Dictionary<string, string> _prefabs = new Dictionary<string, string>();

        // The real prefab path, looked up in the game's list so a moved folder doesn't break it.
        private string PrefabOf(Species sp)
        {
            string path;
            if (_prefabs.TryGetValue(sp.Key, out path)) return path;
            var end = "/" + sp.Prefab + ".prefab";
            path = GameManifest.Current?.entities?.FirstOrDefault(e => e.StartsWith("assets/rust.ai/agents/") && e.EndsWith(end))
                   ?? $"assets/rust.ai/agents/{sp.Folder}/{sp.Prefab}.prefab";
            return _prefabs[sp.Key] = path;
        }

        #endregion

        #region Pet

        private enum Mode { Follow, Stay, Attack, Fetch }

        private class Pet
        {
            public BasePlayer Owner;
            public ulong OwnerId;
            /// <summary>The pet animal (named Wolf from when it could only be a wolf).</summary>
            public BaseNPC2 Wolf;
            public Species Species;
            public RustNavMeshAgent Agent;
            public FSMComponent Fsm;
            public RootMotionPlayer Anim;
            /// <summary>The bite animation: RootMotionData for wolves, tigers and crocodiles, an AnimationClip for bears and boars.</summary>
            public object BiteAnim;
            public object RoarAnim;
            /// <summary>Lying down while downed: an optional lie-down clip, then a clip looped until it's back up.</summary>
            public object DownStartAnim, DownLoopAnim;
            public float BiteDamage = 30f;
            public float BiteCooldown = 1.4f;
            public float DamageTaken = 1f;
            public DamageType BiteType = DamageType.Bite;
            public RootMotionPlayer.PlayServerState BiteState;
            /// <summary>At no health: lies still and waits for a medical item.</summary>
            public bool Down;
            public float DownSince;
            public bool DownLooping;
            public float LastHurt;
            public readonly State_Nothing Idle = new State_Nothing { Name = "SandboxPet idle" };

            public Mode Mode = Mode.Follow;
            public BaseCombatEntity Target;
            public readonly Queue<BaseEntity> FetchQueue = new Queue<BaseEntity>();
            public BaseEntity FetchTarget;
            public float FetchStarted;
            /// <summary>When the wolf got close to its target without being able to bite (a target backing away).</summary>
            public float CloseSince;
            public float NextBite;
            public float LastProgress;
            public float BestDistance = float.MaxValue;
            public Vector3 LastDestination;
            public RustNavMeshAgent.Speeds LastGait;
            public Vector3 LastOwnerPos;
            public Vector3 OwnerVelocity;
            /// <summary>Patrol mode: roams around the owner instead of sticking to them. Fights and fetches return to it.</summary>
            public bool Patrol;
            public Vector3 PatrolPoint;
            public float NextPatrolMove;
            public float NextScan;
            /// <summary>NPCs and animals that are after the owner (or that the owner or wolf is fighting), and when each is forgotten.</summary>
            public readonly Dictionary<BaseCombatEntity, float> Threats = new Dictionary<BaseCombatEntity, float>();
            /// <summary>What the wolf has bitten and when, so a kill it helped with earns Companion XP.</summary>
            public readonly Dictionary<BaseCombatEntity, float> Bitten = new Dictionary<BaseCombatEntity, float>();
            /// <summary>Hit-and-run enemies (panthers, tigers) that bolted: left alone until they come back close, and when that was.</summary>
            public readonly Dictionary<BaseCombatEntity, float> LetGo = new Dictionary<BaseCombatEntity, float>();
            public float RoarUntil, NextRoar, NextRevive, ReviveStarted, NextSniff, HoldUntil;
            public bool Despawning;
        }

        private readonly Dictionary<ulong, Pet> _pets = new Dictionary<ulong, Pet>();
        private readonly Dictionary<ulong, Pet> _byWolf = new Dictionary<ulong, Pet>();
        private Timer _tick;
        private BUTTON? _fetchButton;

        private Pet PetOf(BasePlayer player)
        {
            Pet pet;
            return player != null && _pets.TryGetValue(player.userID, out pet) && pet.Wolf != null && !pet.Wolf.IsDestroyed ? pet : null;
        }

        private Pet PetByEntity(BaseEntity entity)
        {
            Pet pet;
            return entity != null && entity.net != null && _byWolf.TryGetValue(entity.net.ID.Value, out pet) ? pet : null;
        }

        private bool CanHave(BasePlayer player) => !_config.NeedsPermission || permission.UserHasPermission(player.UserIDString, UsePerm);

        private Pet Summon(BasePlayer owner)
        {
            var existing = PetOf(owner);
            if (existing != null) return existing;

            var sp = SpeciesOf(owner.userID);
            if (!Allowed(sp)) sp = AllSpecies.FirstOrDefault(Allowed) ?? AllSpecies[0];
            var cfg = ConfigOf(sp);
            var spot = SpawnSpot(owner);
            var entity = GameManager.server.CreateEntity(PrefabOf(sp), spot, Quaternion.LookRotation(owner.transform.position - spot == Vector3.zero ? Vector3.forward : (owner.transform.position - spot).WithY(0)));
            var wolf = entity as BaseNPC2;
            if (wolf == null)
            {
                if (entity != null) entity.Kill();
                PrintWarning($"Couldn't spawn a {sp.Name} from {PrefabOf(sp)}.");
                return null;
            }
            wolf.enableSaving = false;
            wolf.Spawn();

            var pet = new Pet
            {
                Owner = owner, OwnerId = owner.userID, Wolf = wolf, Species = sp,
                Agent = wolf.GetComponent<RustNavMeshAgent>(),
                Fsm = wolf.GetComponent<FSMComponent>(),
                Anim = wolf.GetComponent<RootMotionPlayer>(),
                BiteCooldown = cfg.BiteCooldown,
                DamageTaken = Mathf.Max(0f, cfg.DamageTaken),
            };
            ReadAnimations(pet);
            if (cfg.BiteDamage > 0) pet.BiteDamage = cfg.BiteDamage;
            pet.BiteDamage *= Mathf.Max(0.1f, cfg.DamageMultiplier);

            Park(pet);
            GiveOwnTeam(wolf);
            pet.LastOwnerPos = owner.transform.position;
            if (cfg.Health > 0) wolf.SetMaxHealth(cfg.Health);
            wolf.SetHealth(wolf.MaxHealth());
            _pets[owner.userID] = pet;
            _byWolf[wolf.net.ID.Value] = pet;
            DataFor(owner.userID).Active = true;
            return pet;
        }

        private void Dismiss(Pet pet, bool keepActive)
        {
            if (pet == null) return;
            _pets.Remove(pet.OwnerId);
            if (pet.Wolf != null && pet.Wolf.net != null) _byWolf.Remove(pet.Wolf.net.ID.Value);
            ItemContainer bag;
            if (_bags.TryGetValue(pet.OwnerId, out bag))
            {
                if (pet.Owner != null && pet.Owner.inventory.loot.containers.Contains(bag)) pet.Owner.EndLooting();
                bag.entityOwner = null;
            }
            if (!keepActive) DataFor(pet.OwnerId).Active = false;
            pet.Despawning = true;
            if (pet.Anim != null && pet.BiteState != null) pet.Anim.StopServerAndReturnToPool(ref pet.BiteState, false);
            if (pet.Wolf != null && !pet.Wolf.IsDestroyed) pet.Wolf.Kill();
        }

        // Keep the wolf's own brain running (it drives the walk animations) but in a state with no way out, so
        // it never roams, flees or picks its own fights. The plugin moves it with the navmesh agent instead.
        private static void Park(Pet pet)
        {
            if (pet.Fsm == null) return;
            pet.Fsm.SetFsmActive(true);
            if (pet.Fsm.CurrentState != pet.Idle) pet.Fsm.SetState(pet.Idle);
        }

        private static Vector3 SpawnSpot(BasePlayer owner)
        {
            var back = -owner.eyes.HeadForward().WithY(0).normalized;
            if (back == Vector3.zero) back = Vector3.back;
            var pos = owner.transform.position + back * 2.5f;
            RaycastHit hit;
            if (Physics.Raycast(pos + Vector3.up * 3f, Vector3.down, out hit, 10f, LayerMask.GetMask("Terrain", "World", "Construction", "Default"), QueryTriggerInteraction.Ignore))
                pos = hit.point;
            else
                pos = owner.transform.position;
            return pos;
        }

        #endregion

        #region Brain

        private void TickAll()
        {
            foreach (var pet in _pets.Values.ToList())
            {
                try { Tick(pet); }
                catch (Exception ex) { PrintError($"Pet tick failed for {pet.OwnerId}: {ex.Message}"); }
            }
        }

        private void Tick(Pet pet)
        {
            var wolf = pet.Wolf;
            if (wolf == null || wolf.IsDestroyed) return;
            var owner = pet.Owner;
            if (owner == null || !owner.IsConnected) { Dismiss(pet, true); return; }

            Park(pet);
            var now = Time.realtimeSinceStartup;

            // Downed: lies still until the owner patches it up (or it goes home to rest).
            if (pet.Down) { TickDown(pet, now); return; }

            // Heals slowly once it's out of the fight (or can't be hurt at all).
            var max = wolf.MaxHealth();
            if (!_config.CanGoDown) { if (wolf.health < max) wolf.SetHealth(max); }
            else if (wolf.health < max && now - pet.LastHurt > _config.RegenDelay)
                wolf.SetHealth(Mathf.Min(max, wolf.health + max * Mathf.Max(0f, _config.RegenPerSecond) * TickSeconds));

            // A fighter's roar heals the owner over time.
            if (HowlBonus(pet) > 0f && !owner.IsDead() && !owner.IsWounded() && owner.health < owner.MaxHealth())
                owner.Heal(Mathf.Max(0f, _config.RoarRegen) * TickSeconds);

            // A dead owner: the wolf waits by their body.
            if (owner.IsDead()) { Stop(pet); pet.Threats.Clear(); return; }

            // Loyal Friend perk: a downed owner gets licked back onto their feet.
            if (owner.IsWounded()) { if (TickRevive(pet, now)) return; }
            else pet.ReviveStarted = 0f;

            // Roaring: stand still for the animation.
            if (now < pet.HoldUntil) { Stop(pet); return; }

            // How fast the owner is going, so the wolf can keep pace instead of trailing.
            var ownerPos = owner.transform.position;
            var moved = (ownerPos - pet.LastOwnerPos).WithY(0) / TickSeconds;
            pet.LastOwnerPos = ownerPos;
            if (moved.sqrMagnitude > 25f * 25f) moved = Vector3.zero; // a teleport or respawn, not running
            pet.OwnerVelocity = Vector3.Lerp(pet.OwnerVelocity, moved, 0.6f);

            if (now >= pet.NextScan)
            {
                pet.NextScan = now + 1f;
                ScanThreats(pet, now);
            }

            var ownerDist = Vector3.Distance(wolf.transform.position, owner.transform.position);
            if (ownerDist > _config.TeleportDistance && !owner.isMounted)
            {
                TeleportToOwner(pet);
                return;
            }

            switch (pet.Mode)
            {
                case Mode.Attack:
                    if (!ValidTarget(pet, pet.Target) || Vector3.Distance(pet.Target.transform.position, owner.transform.position) > _config.ChaseDistance)
                    {
                        pet.Threats.Remove(pet.Target);
                        pet.Target = null;
                        pet.Mode = Mode.Follow;
                        if (!TakeNextThreat(pet)) break;
                    }
                    TickAttack(pet, now);
                    return;

                case Mode.Fetch:
                    if (TakeNextThreat(pet)) { TickAttack(pet, now); return; }
                    if (TickFetch(pet, now)) return;
                    pet.Mode = Mode.Follow;
                    break;

                case Mode.Stay:
                    Stop(pet);
                    return;

                case Mode.Follow:
                    if (TakeNextThreat(pet)) { TickAttack(pet, now); return; }
                    if (pet.Patrol) { TickPatrol(pet, now, ownerDist); return; }
                    break;
            }

            // Follow: run at the owner's side, aiming a little ahead of where they're going, at their pace or faster.
            var speed = pet.OwnerVelocity.magnitude;
            var moving = speed > 0.8f;
            if (!moving && ownerDist <= _config.FollowDistance + 0.5f) { Stop(pet); pet.BestDistance = float.MaxValue; return; }
            var heading = moving ? pet.OwnerVelocity / speed : owner.eyes.HeadForward().WithY(0).normalized;
            if (heading == Vector3.zero) heading = Vector3.forward;
            var side = Vector3.Cross(Vector3.up, heading);
            var dest = moving
                ? ownerPos + side * 1.4f + pet.OwnerVelocity * 0.8f
                : ownerPos + side * 0.8f - heading * (_config.FollowDistance * 0.6f);
            RustNavMeshAgent.Speeds gait;
            if (ownerDist > 8f || (speed > 4.5f && ownerDist > 4f)) gait = RustNavMeshAgent.Speeds.FullSprint;
            else if (speed > 4.5f || ownerDist > 5f) gait = RustNavMeshAgent.Speeds.Sprint;
            else if (speed > 1.5f || ownerDist > 3.5f) gait = RustNavMeshAgent.Speeds.Run;
            else gait = RustNavMeshAgent.Speeds.Jog;
            MoveTo(pet, dest, gait, moving ? 0.3f : _config.FollowDistance * 0.5f);

            // Stuck (a wall, a base, water): after a few seconds without getting closer, hop over.
            if (ownerDist < pet.BestDistance - 0.5f) { pet.BestDistance = ownerDist; pet.LastProgress = now; }
            else if (ownerDist > 12f && now - pet.LastProgress > 5f && !owner.isMounted) TeleportToOwner(pet);
        }

        // Patrol: stroll from point to point within the patrol radius of the owner, staying out from under their feet.
        private void TickPatrol(Pet pet, float now, float ownerDist)
        {
            var owner = pet.Owner;
            var wolf = pet.Wolf;
            var radius = Mathf.Max(10f, _config.PatrolRadius);
            var minDist = Mathf.Clamp(_config.PatrolMinDistance, 0f, radius * 0.6f);
            var ownerPos = owner.transform.position;

            // Too far out (the owner walked off): head back in at a run.
            var tooFar = ownerDist > radius + 5f;
            var pointGone = (pet.PatrolPoint - ownerPos).WithY(0).magnitude > radius + 2f;
            var arrived = Vector3.Distance(wolf.transform.position, pet.PatrolPoint) < 2.5f;
            if (tooFar || pointGone || now >= pet.NextPatrolMove)
            {
                // Trot to the next spot, then sniff around there for a few seconds.
                pet.NextPatrolMove = now + UnityEngine.Random.Range(9f, 16f);
                pet.PatrolPoint = PatrolSpot(pet, ownerPos, minDist, radius);
                pet.BestDistance = float.MaxValue;
                pet.LastProgress = now;
                arrived = false;
            }
            if (arrived) { Stop(pet); return; }

            var left = Vector3.Distance(wolf.transform.position, pet.PatrolPoint);
            RustNavMeshAgent.Speeds gait;
            if (tooFar) gait = ownerDist > radius + 20f ? RustNavMeshAgent.Speeds.Sprint : RustNavMeshAgent.Speeds.Run;
            else gait = left > 12f ? RustNavMeshAgent.Speeds.Run : RustNavMeshAgent.Speeds.Jog;
            MoveTo(pet, pet.PatrolPoint, gait, 1.5f);

            // Pushing into a wall: give up on that spot quickly and pick another; stranded far away, hop back.
            if (left < pet.BestDistance - 0.5f) { pet.BestDistance = left; pet.LastProgress = now; }
            else if (now - pet.LastProgress > 2.5f)
            {
                if (ownerDist > radius * 1.5f && !owner.isMounted) { TeleportToOwner(pet); return; }
                pet.NextPatrolMove = 0f;
            }
        }

        private static readonly int PatrolBlockers = LayerMask.GetMask("Construction", "Deployed", "World");

        // The next patrol spot: a short hop from where the wolf is (so it doesn't try to cut through the base), inside
        // the ring around the owner, with nothing built or solid in a straight line between the wolf and the spot.
        private Vector3 PatrolSpot(Pet pet, Vector3 ownerPos, float minDist, float radius)
        {
            var from = pet.Wolf.transform.position;
            Vector3? fallback = null;
            for (var i = 0; i < 20; i++)
            {
                var dir = UnityEngine.Random.insideUnitCircle.normalized;
                var p = i < 14
                    ? from + new Vector3(dir.x, 0f, dir.y) * UnityEngine.Random.Range(6f, 18f)
                    : ownerPos + new Vector3(dir.x, 0f, dir.y) * UnityEngine.Random.Range(minDist, radius);
                var flat = (p - ownerPos).WithY(0);
                var d = flat.magnitude;
                if (d > radius) p = ownerPos + flat / d * radius;
                else if (d < minDist && d > 0.01f) p = ownerPos + flat / d * minDist;
                if (TerrainMeta.HeightMap != null) p.y = TerrainMeta.HeightMap.GetHeight(p);
                if (TerrainMeta.WaterMap != null && TerrainMeta.WaterMap.GetHeight(p) > p.y + 0.3f) continue;
                UnityEngine.AI.NavMeshHit hit;
                if (pet.Agent == null || !pet.Agent.SamplePosition(p, out hit, 3f, false)) continue;
                if (fallback == null) fallback = hit.position;
                if (Physics.Linecast(from + Vector3.up * 0.7f, hit.position + Vector3.up * 0.7f, PatrolBlockers, QueryTriggerInteraction.Ignore)) continue;
                return hit.position;
            }
            return fallback ?? from;
        }

        private const float BiteRange = 3f;
        private const float LetGoDistance = 15f;
        private const float ComeBackDistance = 12f;

        private void TickAttack(Pet pet, float now)
        {
            var wolf = pet.Wolf;
            var target = pet.Target;
            var dist = Vector3.Distance(wolf.transform.position, target.transform.position);
            // Close but not quite in reach for a while (a scientist backing off keeps just out of range, and the
            // agent slows down as it arrives): lunge and bite anyway.
            if (dist > BiteRange && dist < BiteRange + 2f) { if (pet.CloseSince <= 0f) pet.CloseSince = now; }
            else pet.CloseSince = 0f;
            var lunge = pet.CloseSince > 0f && now - pet.CloseSince > 1.2f;
            if (dist > BiteRange && !lunge)
            {
                // Aim a little past where the target is heading so the pet doesn't ease off as it arrives.
                var vel = target.GetWorldVelocity().WithY(0);
                var toward = (target.transform.position - wolf.transform.position).WithY(0).normalized;
                // Hit and run (big cats bite, bolt and circle back): once it's well away and still running off,
                // don't chase it across the map. Go back to the owner and meet it when it returns.
                if (dist > LetGoDistance && Vector3.Dot(vel, toward) > 2f)
                {
                    pet.LetGo[target] = now;
                    pet.Target = null;
                    pet.Mode = Mode.Follow;
                    pet.CloseSince = 0f;
                    return;
                }
                var dest = target.transform.position + vel * 0.5f + toward * 1f;
                var gait = dist > 6f || vel.sqrMagnitude > 1f ? RustNavMeshAgent.Speeds.FullSprint : RustNavMeshAgent.Speeds.Sprint;
                // Re-path less often the further away it is, so the pet runs smoothly instead of stuttering.
                MoveTo(pet, dest, gait, 0.2f, Mathf.Clamp(dist * 0.2f, 0.8f, 4f));
                return;
            }
            if (now < pet.NextBite)
            {
                // Keep on the target between bites instead of standing still while it walks away.
                if (dist > BiteRange * 0.8f) MoveTo(pet, target.transform.position, RustNavMeshAgent.Speeds.Sprint, 0.5f);
                return;
            }
            pet.CloseSince = 0f;
            pet.NextBite = now + pet.BiteCooldown;
            Stop(pet);
            Face(wolf, target.transform.position);
            if (pet.Anim != null && pet.BiteAnim != null)
            {
                pet.Anim.StopServerAndReturnToPool(ref pet.BiteState, false);
                pet.BiteState = PlayAnim(pet.Anim, pet.BiteAnim, false);
            }
            timer.Once(0.45f, () =>
            {
                if (pet.Wolf == null || pet.Wolf.IsDestroyed || target == null || target.IsDestroyed || target.IsDead()) return;
                if (Vector3.Distance(pet.Wolf.transform.position, target.transform.position) > BiteRange + 2.5f) return;
                pet.Bitten[target] = Time.realtimeSinceStartup;
                var damage = pet.BiteDamage;
                var howl = HowlBonus(pet);
                if (howl > 0 && pet.Owner != null)
                {
                    damage *= 1f + howl;
                    NotePerk(pet.Owner, "howl", damage - pet.BiteDamage);
                }
                // The wolf bites as itself so the target turns on it. Only the killing bite is the owner's, so
                // quests and skills still count the kill.
                var lethal = _config.CreditOwner && pet.Owner != null && pet.Owner.IsConnected && target.health <= damage;
                var attacker = lethal ? (BaseEntity)pet.Owner : pet.Wolf;
                var hit = new HitInfo(attacker, target, pet.BiteType, damage, target.transform.position);
                hit.WeaponPrefab = pet.Wolf;
                target.OnAttacked(hit);
                if (target.IsDead())
                {
                    pet.Threats.Remove(target);
                    if (pet.Target == target)
                    {
                        pet.Target = null;
                        pet.Mode = Mode.Follow;
                    }
                }
            });
        }

        // Oxide doesn't reference Unity's animation module, and the direct call's other overload takes an
        // AnimationClip, so the plugin can't compile it. Look the RootMotionData overload up once instead.
        private static System.Reflection.MethodInfo _playRootMotion;

        private static System.Reflection.MethodInfo _playClip;

        /// <summary>Plays a RootMotionData or AnimationClip on the pet (each animal's brain uses one or the other).</summary>
        private static RootMotionPlayer.PlayServerState PlayAnim(RootMotionPlayer player, object anim, bool pauseAgent)
        {
            if (player == null || anim == null) return null;
            try
            {
                if (anim is RootMotionData)
                {
                    if (_playRootMotion == null)
                        _playRootMotion = typeof(RootMotionPlayer).GetMethod("PlayServerAndTakeFromPool", new[] { typeof(RootMotionData) });
                    return _playRootMotion?.Invoke(player, new[] { anim }) as RootMotionPlayer.PlayServerState;
                }
                if (_playClip == null)
                    _playClip = typeof(RootMotionPlayer).GetMethods().FirstOrDefault(m => m.Name == "PlayServerAndTakeFromPool"
                        && m.GetParameters().Length == 3 && m.GetParameters()[0].ParameterType.Name == "AnimationClip");
                return _playClip?.Invoke(player, new[] { anim, (object)1f, pauseAgent }) as RootMotionPlayer.PlayServerState;
            }
            catch { return null; }
        }

        private static object Field(object o, string name) =>
            o?.GetType().GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)?.GetValue(o);

        private static object FirstField(object o, params string[] names)
        {
            foreach (var n in names)
            {
                var v = Field(o, n);
                if (v != null) return v;
            }
            return null;
        }

        /// <summary>The animation if it's really there (Unity objects can be "null" without being null).</summary>
        private static object Usable(object anim)
        {
            var obj = anim as UnityEngine.Object;
            if (obj == null) return null;
            if (anim is RootMotionData && Field(anim, "inPlaceAnimation") as UnityEngine.Object == null) return null;
            return anim;
        }

        // Each animal's brain keeps its bite, roar and sleep in differently named states; read what's there.
        private static void ReadAnimations(Pet pet)
        {
            var fsm = pet.Fsm;
            if (fsm == null) return;
            try
            {
                var attack = FirstField(fsm, "attack", "staticAttack");
                if (attack != null)
                {
                    pet.BiteAnim = Usable(Field(attack, "Animation"));
                    var dmg = Field(attack, "Damage") ?? Field(attack, "damage");
                    if (dmg is float && (float)dmg > 0f) pet.BiteDamage = (float)dmg;
                    var type = Field(attack, "DamageType");
                    if (type is DamageType) pet.BiteType = (DamageType)type;
                }
                var roar = FirstField(fsm, "howl", "roar");
                if (roar != null) pet.RoarAnim = Usable(Field(roar, "Animation"));
                var sleep = Field(fsm, "sleep");
                if (sleep != null)
                {
                    var loop = Usable(Field(sleep, "Loop"));
                    if (loop != null)
                    {
                        pet.DownStartAnim = Usable(Field(sleep, "Start"));
                        pet.DownLoopAnim = loop;
                    }
                    else pet.DownLoopAnim = Usable(Field(sleep, "Animation"));
                }
            }
            catch { }
        }

        private bool ValidTarget(Pet pet, BaseCombatEntity target)
        {
            if (target == null || target.IsDestroyed || target.IsDead()) return false;
            if (target == pet.Wolf || target == pet.Owner || PetByEntity(target) != null) return false;
            if (target.InSafeZone()) return false;
            if (_config.SpareFarmAnimals && (target is LivestockAnimal || target is FarmableAnimal)) return false;
            var player = target as BasePlayer;
            if (player != null)
            {
                if (player.IsNpc || !player.userID.IsSteamId()) return true;
                return _config.AttackPlayers && !player.IsSleeping();
            }
            return target is BaseNPC2 || target is BaseNpc; // animals and zombies; not helicopters, Bradley or turrets
        }

        private void SetTarget(Pet pet, BaseCombatEntity target)
        {
            if (!ValidTarget(pet, target)) return;
            if (pet.Mode == Mode.Attack && pet.Target == target) return;
            pet.Target = target;
            pet.CloseSince = 0f;
            pet.Mode = Mode.Attack;
            pet.FetchQueue.Clear();
            pet.FetchTarget = null;
            AddThreat(pet, target);
        }

        #region Tank

        private const float ThreatSeconds = 30f;

        private static NPCTeam _petTeam;
        private static System.Reflection.FieldInfo _senseTeam, _senseTarget;
        private readonly List<BaseCombatEntity> _nearby = new List<BaseCombatEntity>();

        private static System.Reflection.FieldInfo SenseField(string name) =>
            typeof(SenseComponent).GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        // Wild wolves won't fight a wolf from their own pack, so the pet gets a team of its own.
        private static void GiveOwnTeam(BaseNPC2 wolf)
        {
            SenseComponent sense;
            if (!wolf.TryGetComponent(out sense)) return;
            if (_senseTeam == null) _senseTeam = SenseField("team");
            if (_senseTeam == null) return;
            if (_petTeam == null)
            {
                _petTeam = ScriptableObject.CreateInstance<NPCTeam>();
                _petTeam.name = "SandboxPet";
                _petTeam.players = NPCStance.Ignore;
            }
            _senseTeam.SetValue(sense, _petTeam);
        }

        private static BaseEntity SenseTarget(SenseComponent sense)
        {
            if (_senseTarget == null) _senseTarget = SenseField("Target");
            return _senseTarget?.GetValue(sense) as BaseEntity;
        }

        /// <summary>Remembers an NPC or animal the wolf should keep off its owner. Players never count.</summary>
        private void AddThreat(Pet pet, BaseCombatEntity npc)
        {
            if (npc == null || npc is BasePlayer && !((BasePlayer)npc).IsNpc && ((BasePlayer)npc).userID.IsSteamId()) return;
            if (!ValidTarget(pet, npc)) return;
            pet.Threats[npc] = Time.realtimeSinceStartup + ThreatSeconds;
        }

        private static bool IsThreat(Pet pet, BaseEntity npc)
        {
            var combat = npc as BaseCombatEntity;
            return combat != null && pet.Threats.ContainsKey(combat);
        }

        private bool Taunted(Pet pet, BaseEntity npc) =>
            pet.Mode != Mode.Stay && !pet.Down && IsThreat(pet, npc) && pet.Wolf != null &&
            Vector3.Distance(pet.Wolf.transform.position, npc.transform.position) <= TauntRange(pet);

        // Tanks hold enemies from further away, and further still while they roar.
        private float TauntRange(Pet pet)
        {
            var range = _config.TauntRange * (pet.Species.Role == Role.Tank ? 1.5f : 1f);
            return GuardBonus(pet) > 0f ? Mathf.Max(range, _config.RoarPullRange) : range;
        }

        /// <summary>Finds what's after the owner, forgets what's gone, and turns animals near the wolf onto it.</summary>
        private void ScanThreats(Pet pet, float now)
        {
            var owner = pet.Owner;
            if (_config.DefendOwner && pet.Mode != Mode.Stay && _config.GuardRadius > 0f)
            {
                _nearby.Clear();
                Vis.Entities(owner.transform.position, _config.GuardRadius, _nearby);
                foreach (var e in _nearby.Distinct())
                    if (e != null && e != pet.Wolf && !pet.Threats.ContainsKey(e) && IsAfter(e, owner)) AddThreat(pet, e);
                _nearby.Clear();
            }

            foreach (var e in pet.Bitten.Keys.ToList())
                if (e == null || e.IsDestroyed || now - pet.Bitten[e] > ThreatSeconds) pet.Bitten.Remove(e);

            foreach (var e in pet.Threats.Keys.ToList())
            {
                if (e == null || e.IsDestroyed || e.IsDead() || pet.Threats[e] < now
                    || Vector3.Distance(e.transform.position, owner.transform.position) > _config.ChaseDistance)
                {
                    pet.Threats.Remove(e);
                    continue;
                }
                if (IsAfter(e, owner)) pet.Threats[e] = now + ThreatSeconds; // still after the owner: keep it in mind

                // Taunt animals close to the wolf: they take it as the one that hurt them and go for it.
                if (!_config.Tank || !Taunted(pet, e)) continue;
                var animal = e as BaseNPC2;
                SenseComponent sense;
                if (animal != null && animal.TryGetComponent(out sense) && SenseTarget(sense) != pet.Wolf)
                {
                    sense.NoteAttacker(pet.Wolf);
                    sense.TrySetTarget(pet.Wolf);
                }
                var old = e as BaseNpc;
                if (old != null && old.AttackTarget == owner) old.AttackTarget = pet.Wolf;
            }
        }

        /// <summary>True when an NPC or animal has its sights on the player.</summary>
        private static bool IsAfter(BaseCombatEntity e, BasePlayer owner)
        {
            var animal = e as BaseNPC2;
            if (animal != null)
            {
                SenseComponent sense;
                return animal.TryGetComponent(out sense) && SenseTarget(sense) == owner;
            }
            var human = e as HumanNPC;
            if (human != null) return human.Brain != null && human.Brain.Senses != null && human.Brain.Senses.Players.Contains(owner);
            var old = e as BaseNpc;
            return old != null && old.AttackTarget == owner;
        }

        /// <summary>Goes for the threat closest to the owner, if there is one.</summary>
        private bool TakeNextThreat(Pet pet)
        {
            if (!_config.DefendOwner || pet.Threats.Count == 0) return false;
            BaseCombatEntity best = null;
            var bestDist = float.MaxValue;
            var ownerPos = pet.Owner.transform.position;
            var petPos = pet.Wolf.transform.position;
            foreach (var e in pet.Threats.Keys)
            {
                if (!ValidTarget(pet, e)) continue;
                var d = (e.transform.position - ownerPos).sqrMagnitude;
                // One that ran off is met when it comes back close to the owner or the pet.
                float letGo;
                if (pet.LetGo.TryGetValue(e, out letGo))
                {
                    var back = d < ComeBackDistance * ComeBackDistance
                               || (e.transform.position - petPos).sqrMagnitude < ComeBackDistance * ComeBackDistance;
                    if (!back && Time.realtimeSinceStartup - letGo < ThreatSeconds) continue;
                    pet.LetGo.Remove(e);
                }
                if (d < bestDist) { bestDist = d; best = e; }
            }
            if (best == null) return false;
            SetTarget(pet, best);
            return pet.Mode == Mode.Attack;
        }

        #endregion

        private static void Face(BaseEntity wolf, Vector3 pos)
        {
            var dir = (pos - wolf.transform.position).WithY(0);
            if (dir.sqrMagnitude < 0.0001f) return;
            wolf.transform.rotation = Quaternion.LookRotation(dir.normalized);
        }

        private static void MoveTo(Pet pet, Vector3 dest, RustNavMeshAgent.Speeds gait, float stopAt, float repathAt = 0.775f)
        {
            if (pet.Agent == null) return;
            if (pet.Agent.hasPath && pet.LastGait == gait && (pet.LastDestination - dest).sqrMagnitude < repathAt * repathAt) return;
            pet.LastDestination = dest;
            pet.LastGait = gait;
            if (pet.Agent.SetDestinationWithParams(dest, true, gait, null, null, null, null, stopAt)) return;
            UnityEngine.AI.NavMeshHit hit;
            if (pet.Agent.SamplePosition(dest, out hit, 4f, false))
                pet.Agent.SetDestinationWithParams(hit.position, true, gait, null, null, null, null, stopAt);
        }

        private static void Stop(Pet pet)
        {
            pet.LastDestination = Vector3.zero;
            if (pet.Agent != null && pet.Agent.hasPath) pet.Agent.ResetPath();
        }

        private void TeleportToOwner(Pet pet)
        {
            var owner = pet.Owner;
            if (owner == null || pet.Agent == null) return;
            pet.BestDistance = float.MaxValue;
            pet.LastProgress = Time.realtimeSinceStartup;
            UnityEngine.AI.NavMeshHit hit;
            var spot = SpawnSpot(owner);
            if (!pet.Agent.SamplePosition(spot, out hit, 6f, false) && !pet.Agent.SamplePosition(owner.transform.position, out hit, 6f, false))
                return; // nowhere to stand near the owner (inside a base, on a boat...): wait until there is
            Stop(pet);
            if (!pet.Agent.Warp(hit.position))
            {
                // The agent refused: start over with a fresh wolf next to the owner.
                Dismiss(pet, true);
                Summon(owner);
            }
        }

        #endregion

        #region Downed

        private void GoDown(Pet pet)
        {
            var now = Time.realtimeSinceStartup;
            pet.Down = true;
            pet.DownSince = now;
            pet.DownLooping = false;
            pet.Mode = Mode.Follow;
            pet.Target = null;
            pet.FetchQueue.Clear();
            pet.FetchTarget = null;
            pet.Threats.Clear();
            pet.RoarUntil = 0f;
            pet.HoldUntil = 0f;
            Stop(pet);
            pet.Wolf.SetHealth(1f);
            if (pet.Anim != null)
            {
                pet.Anim.StopServerAndReturnToPool(ref pet.BiteState, false);
                pet.BiteState = PlayAnim(pet.Anim, pet.DownStartAnim ?? pet.DownLoopAnim, true);
            }
            if (_config.DownedMinutes > 0f) Reply(pet.Owner, "Pet.Down", MedicalNames(), Math.Round(_config.DownedMinutes));
            else Reply(pet.Owner, "Pet.DownWaits", MedicalNames());
            Interface.CallHook("OnSandboxPetBuff", pet.Owner);
        }

        private void TickDown(Pet pet, float now)
        {
            Stop(pet);
            pet.Target = null;
            pet.Threats.Clear();
            // Keep it lying down: once the lie-down (or the last loop) finishes, play the loop again.
            if (pet.Anim != null && pet.DownLoopAnim != null && (pet.BiteState == null || !pet.BiteState.isPlaying))
            {
                pet.Anim.StopServerAndReturnToPool(ref pet.BiteState, false);
                pet.BiteState = PlayAnim(pet.Anim, pet.DownLoopAnim, true);
                pet.DownLooping = true;
            }
            if (_config.DownedMinutes > 0f && now - pet.DownSince > _config.DownedMinutes * 60f) SendHome(pet);
        }

        private static double UnixNow => (DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;

        /// <summary>A downed pet nobody helped: it goes home and rests before it can be called again.</summary>
        private void SendHome(Pet pet)
        {
            var data = DataFor(pet.OwnerId);
            data.RestUntil = UnixNow + Math.Max(0f, _config.RestMinutes) * 60f;
            Dismiss(pet, false);
            if (_config.RestMinutes > 0f) Reply(pet.Owner, "Pet.WentHome", Math.Round(_config.RestMinutes));
            Interface.CallHook("OnSandboxPetBuff", pet.Owner);
        }

        /// <summary>Minutes left before a pet that was left downed can be called again (0 = it can).</summary>
        private double RestMinutesLeft(ulong id)
        {
            var left = DataFor(id).RestUntil - UnixNow;
            return left > 0 ? Math.Ceiling(left / 60.0) : 0;
        }

        private string MedicalNames() => string.Join(", ", _config.MedicalItems.Keys.Select(k =>
        {
            var def = ItemManager.FindItemDefinition(k);
            return def != null ? def.displayName.english.ToLower() : k;
        }));

        /// <summary>The medical item to use on the pet: the one in the owner's hand, else the first one in their inventory.</summary>
        private Item FindMedical(BasePlayer player)
        {
            var held = player.GetActiveItem();
            if (held != null && _config.MedicalItems.ContainsKey(held.info.shortname)) return held;
            foreach (var name in _config.MedicalItems.Keys)
            {
                var item = player.inventory.FindItemByItemName(name);
                if (item != null) return item;
            }
            return null;
        }

        private bool HoldingMedical(BasePlayer player)
        {
            var held = player.GetActiveItem();
            return held != null && _config.MedicalItems.ContainsKey(held.info.shortname);
        }

        /// <summary>Uses one medical item on the pet: a downed pet gets back up, a hurt one heals.</summary>
        private void Patch(BasePlayer player, Pet pet)
        {
            var wolf = pet.Wolf;
            var max = wolf.MaxHealth();
            if (!pet.Down && wolf.health >= max - 0.5f) { Reply(player, "Pet.NotHurt"); return; }
            var item = FindMedical(player);
            if (item == null) { Reply(player, "Pet.NeedMedical", MedicalNames()); return; }
            var share = Mathf.Clamp01(_config.MedicalItems[item.info.shortname]);
            var name = item.info.displayName.english.ToLower();
            item.UseItem(1);
            var now = Time.realtimeSinceStartup;
            if (!pet.Down)
            {
                wolf.SetHealth(Mathf.Min(max, wolf.health + max * share));
                Reply(player, "Pet.Healed", name, Math.Round(wolf.health / max * 100f));
                return;
            }
            pet.Down = false;
            pet.LastHurt = now;
            pet.Mode = Mode.Follow;
            wolf.SetHealth(Mathf.Max(1f, max * share));
            if (pet.Anim != null) pet.Anim.StopServerAndReturnToPool(ref pet.BiteState, false);
            Reply(player, "Pet.Revived", name, Math.Round(wolf.health / max * 100f));
            Interface.CallHook("OnSandboxPetBuff", player);
        }

        /// <summary>For other plugins (the Event HUD): the owner's active pet effects and seconds left on each
        /// ("roar" = hitting harder and healing, "guard" = taking less damage, "down" = the pet is down, -1 = no limit).</summary>
        private Dictionary<string, float> API_Buffs(ulong id)
        {
            Pet pet;
            if (!_pets.TryGetValue(id, out pet) || pet.Wolf == null || pet.Wolf.IsDestroyed) return null;
            var now = Time.realtimeSinceStartup;
            var buffs = new Dictionary<string, float>();
            if (pet.Down) buffs["down"] = _config.DownedMinutes > 0f ? Mathf.Max(0f, pet.DownSince + _config.DownedMinutes * 60f - now) : -1f;
            else if (now < pet.RoarUntil) buffs[pet.Species.Role == Role.Fighter ? "roar" : "guard"] = pet.RoarUntil - now;
            return buffs;
        }

        /// <summary>For other plugins: the owner's pet animal ("wolf", "bear"...), or null without one out.</summary>
        private string API_PetAnimal(ulong id)
        {
            Pet pet;
            return _pets.TryGetValue(id, out pet) && pet.Species != null ? pet.Species.Key : null;
        }

        #endregion

        #region Fetch

        private bool Fetchable(BasePlayer owner, BaseEntity ent)
        {
            if (ent == null || ent.IsDestroyed) return false;
            if (ent is DroppedItem) return _config.FetchDropped && ((DroppedItem)ent).item != null && ((DroppedItem)ent).allowPickup;
            if (ent is CollectibleEntity) return _config.FetchCollectibles && ((CollectibleEntity)ent).itemList != null;
            var crate = ent as LootContainer;
            if (crate != null)
            {
                if (crate.inventory == null || crate.inventory.itemList.Count == 0 || crate.IsOpen()) return false;
                var hack = crate as HackableLockedCrate;
                if (hack != null && !hack.IsFullyHacked()) return false;
                if (crate.IsLocked()) return false;
                return IsBarrel(crate) ? _config.FetchBarrels : _config.FetchCrates;
            }
            var corpse = ent as LootableCorpse;
            if (corpse != null)
            {
                if (!_config.FetchBodies || corpse.containers == null || corpse.containers.All(c => c == null || c.itemList.Count == 0)) return false;
                return corpse.playerSteamID == owner.userID || !corpse.playerSteamID.IsSteamId();
            }
            var pack = ent as DroppedItemContainer;
            if (pack != null)
            {
                if (!_config.FetchBodies || pack.inventory == null || pack.inventory.itemList.Count == 0) return false;
                return pack.playerSteamID == owner.userID || !pack.playerSteamID.IsSteamId();
            }
            return false;
        }

        private static bool IsBarrel(LootContainer crate) => crate.ShortPrefabName.Contains("barrel") || crate.ShortPrefabName.Contains("roadsign");

        private void StartFetch(Pet pet, BaseEntity looked)
        {
            var owner = pet.Owner;
            pet.FetchQueue.Clear();
            pet.FetchTarget = null;
            if (looked != null && Fetchable(owner, looked))
            {
                pet.FetchQueue.Enqueue(looked);
            }
            else
            {
                var found = new List<BaseEntity>();
                Vis.Entities(owner.transform.position, _config.FetchRadius * (pet.Species.Key == "boar" ? 2f : 1f), found);
                foreach (var ent in found.Distinct().Where(e => Fetchable(owner, e)).OrderBy(e => Vector3.Distance(e.transform.position, owner.transform.position)).Take(_config.FetchMax))
                    pet.FetchQueue.Enqueue(ent);
            }
            if (pet.FetchQueue.Count == 0) { Reply(owner, "Pet.NothingToFetch"); return; }
            pet.Target = null;
            pet.Mode = Mode.Fetch;
            Reply(owner, "Pet.Fetching", pet.FetchQueue.Count);
        }

        /// <summary>Runs one fetch step. False when there's nothing left to fetch.</summary>
        private bool TickFetch(Pet pet, float now)
        {
            var owner = pet.Owner;
            if (pet.FetchTarget == null || pet.FetchTarget.IsDestroyed || !Fetchable(owner, pet.FetchTarget))
            {
                pet.FetchTarget = null;
                while (pet.FetchQueue.Count > 0 && pet.FetchTarget == null)
                {
                    var next = pet.FetchQueue.Dequeue();
                    if (Fetchable(owner, next)) pet.FetchTarget = next;
                }
                if (pet.FetchTarget == null) return false;
                pet.FetchStarted = now;
            }
            var target = pet.FetchTarget;
            var dist = Vector3.Distance(pet.Wolf.transform.position, target.transform.position);
            if (dist > 2.2f)
            {
                if (now - pet.FetchStarted > 15f) { pet.FetchTarget = null; return true; } // can't get there, try the next one
                MoveTo(pet, target.transform.position, dist > 8f ? RustNavMeshAgent.Speeds.Sprint : RustNavMeshAgent.Speeds.Run, 1f);
                return true;
            }
            Stop(pet);
            Face(pet.Wolf, target.transform.position);
            if (!Grab(owner, target)) Reply(owner, "Pet.BagFull");
            else RustSandboxSkills?.Call("API_CompanionFetch", owner, 1);
            pet.FetchTarget = null;
            if (pet.FetchQueue.Count == 0) Reply(owner, "Pet.FetchDone");
            return true;
        }

        /// <summary>Takes what's in or on the entity into the backpack (then the owner's inventory). False if it ran out of room.</summary>
        private bool Grab(BasePlayer owner, BaseEntity ent)
        {
            var dropped = ent as DroppedItem;
            if (dropped != null)
            {
                var item = dropped.item;
                if (item == null) return true;
                if (!HasRoom(owner, item)) return false;
                if (Interface.CallHook("OnItemPickup", item, owner, dropped) != null) return true;
                dropped.RemoveItem();
                if (!Stash(owner, item)) item.Drop(owner.transform.position + Vector3.up, Vector3.up);
                if (!dropped.IsDestroyed) dropped.Kill();
                return true;
            }

            var collectible = ent as CollectibleEntity;
            if (collectible != null)
            {
                if (Interface.CallHook("OnCollectiblePickup", collectible, owner, false) != null || collectible.itemList == null) return true;
                foreach (var ia in collectible.itemList)
                {
                    var amount = Mathf.RoundToInt(ia.amount);
                    if (ia.itemDef == null || amount <= 0) continue;
                    var item = ItemManager.Create(ia.itemDef, amount);
                    if (item == null) continue;
                    Interface.CallHook("OnCollectiblePickedup", collectible, owner, item);
                    if (!Stash(owner, item)) item.Drop(owner.transform.position + Vector3.up, Vector3.up);
                }
                collectible.itemList = null;
                if (collectible.pickupEffect.isValid) Effect.server.Run(collectible.pickupEffect.resourcePath, collectible.transform.position, collectible.transform.up);
                var dispenser = PrefabAttribute.server.Find<RandomItemDispenser>(collectible.prefabID);
                if (dispenser != null) dispenser.DistributeItems(owner, collectible.transform.position);
                collectible.Kill();
                return true;
            }

            var crate = ent as LootContainer;
            if (crate != null)
            {
                var barrel = IsBarrel(crate);
                if (!barrel) Interface.CallHook("OnLootEntity", owner, crate); // quests and the event HUD count it as looted by the owner
                var ok = Empty(owner, crate.inventory);
                if (crate.inventory.itemList.Count == 0)
                {
                    // Barrels are broken as if the owner did it, so barrel quests and skills still count.
                    if (barrel) crate.Hurt(crate.MaxHealth() * 10f, DamageType.Blunt, owner, false);
                    else if (crate.destroyOnEmpty) crate.Invoke(crate.RemoveMe, 0.1f);
                }
                return ok;
            }

            var corpse = ent as LootableCorpse;
            if (corpse != null)
            {
                Interface.CallHook("OnLootEntity", owner, corpse);
                var ok = true;
                foreach (var c in corpse.containers) if (c != null && !Empty(owner, c)) ok = false;
                return ok;
            }

            var pack = ent as DroppedItemContainer;
            if (pack != null)
            {
                var ok = Empty(owner, pack.inventory);
                if (pack.inventory.itemList.Count == 0) pack.Kill();
                return ok;
            }
            return true;
        }

        private bool Empty(BasePlayer owner, ItemContainer from)
        {
            if (from == null) return true;
            foreach (var item in from.itemList.ToList())
            {
                if (!HasRoom(owner, item)) return false;
                if (!Stash(owner, item)) return false;
            }
            return true;
        }

        #endregion

        #region Hooks

        private void Init()
        {
            permission.RegisterPermission(UsePerm, this);
            LoadData();
            BUTTON b;
            _fetchButton = Enum.TryParse(_config.FetchButton, true, out b) ? b : (BUTTON?)null;
        }

        private void OnServerInitialized()
        {
            _tick = timer.Every(TickSeconds, TickAll);
            if (!_config.ResummonOnLogin) return;
            foreach (var p in BasePlayer.activePlayerList)
                if (!p.IsSleeping() && !p.IsDead() && CanHave(p) && DataFor(p.userID).Active) Summon(p);
        }

        private void Unload()
        {
            _tick?.Destroy();
            SaveData();
            if (_petTeam != null) { UnityEngine.Object.Destroy(_petTeam); _petTeam = null; }
            foreach (var pet in _pets.Values.ToList()) Dismiss(pet, true);
            foreach (var bag in _bags.Values) bag.Kill();
            _bags.Clear();
        }

        private void OnServerSave() => SaveData();

        private void OnPlayerSleepEnded(BasePlayer player)
        {
            if (player == null || !_config.ResummonOnLogin || PetOf(player) != null) return;
            if (CanHave(player) && DataFor(player.userID).Active) Summon(player);
        }

        private void OnPlayerDisconnected(BasePlayer player)
        {
            var pet = PetOf(player);
            if (pet != null && pet.Down) SendHome(pet);
            else if (pet != null) Dismiss(pet, true);
            SaveData();
        }

        private void OnPlayerRespawned(BasePlayer player)
        {
            var pet = PetOf(player);
            if (pet == null || pet.Down) return;
            pet.Mode = Mode.Follow;
            pet.Target = null;
            timer.Once(1f, () => { if (PetOf(player) == pet && !pet.Down) TeleportToOwner(pet); });
        }

        private object OnEntityTakeDamage(BaseCombatEntity entity, HitInfo info)
        {
            if (entity == null) return null;
            var hurtPet = PetByEntity(entity);
            if (hurtPet != null)
            {
                if (info == null) return null;
                var from = info.Initiator as BaseCombatEntity;
                var fromPlayer = info.Initiator as BasePlayer;
                // No harm from its owner, other pets, other players (unless pets fight players), or while it's down.
                if (hurtPet.Down || !_config.CanGoDown || fromPlayer == hurtPet.Owner || PetByEntity(info.Initiator) != null
                    || fromPlayer != null && !fromPlayer.IsNpc && fromPlayer.userID.IsSteamId() && !_config.AttackPlayers)
                {
                    info.damageTypes.ScaleAll(0f);
                    if (!hurtPet.Down) AddThreat(hurtPet, from);
                    return true;
                }
                AddThreat(hurtPet, from); // keep whatever is biting the pet busy with it
                info.damageTypes.ScaleAll(hurtPet.DamageTaken);
                hurtPet.LastHurt = Time.realtimeSinceStartup;
                if (entity.health - info.damageTypes.Total() <= 1f)
                {
                    info.damageTypes.ScaleAll(0f);
                    GoDown(hurtPet);
                    return true;
                }
                return null;
            }
            if (info == null) return null;
            var attacker = info.Initiator as BaseCombatEntity;

            // A tank's roar: its owner takes less damage.
            var guarded = entity as BasePlayer;
            if (guarded != null && !guarded.IsNpc && attacker != null && attacker != guarded)
            {
                var guard = GuardBonus(PetOf(guarded));
                if (guard > 0f) info.damageTypes.ScaleAll(1f - guard);
            }

            // Something hurt an owner: their wolf goes for it.
            var victim = entity as BasePlayer;
            if (victim != null && _config.DefendOwner && attacker != null && attacker != victim)
            {
                var pet = PetOf(victim);
                if (pet != null && pet.Mode != Mode.Stay && !pet.Down)
                {
                    AddThreat(pet, attacker);
                    if (pet.Mode != Mode.Attack) SetTarget(pet, attacker);
                }
            }

            // An owner hurt something: their wolf joins in.
            var owner = info.InitiatorPlayer;
            if (owner != null && entity != owner && PetByEntity(info.WeaponPrefab) == null) // not the wolf's own bite
            {
                var howlPet = PetOf(owner);
                var howl = howlPet != null ? HowlBonus(howlPet) : 0f;
                if (howl > 0 && (entity is BasePlayer || entity is BaseNpc || entity is BaseNPC2))
                {
                    var before = info.damageTypes.Total();
                    info.damageTypes.ScaleAll(1f + howl);
                    NotePerk(owner, "howl", info.damageTypes.Total() - before);
                }
            }
            if (owner != null && _config.AssistOwner && entity != owner && !owner.IsNpc)
            {
                var pet = PetOf(owner);
                if (pet != null && pet.Mode != Mode.Stay && !pet.Down) SetTarget(pet, entity);
            }
            return null;
        }

        private void OnEntityKill(BaseNPC2 entity)
        {
            var pet = PetByEntity(entity);
            if (pet == null) return;
            _byWolf.Remove(entity.net.ID.Value);
            if (pet.Despawning) return;
            // Killed some other way (admin kill, fell off the navmesh): it comes back.
            _pets.Remove(pet.OwnerId);
            var owner = pet.Owner;
            timer.Once(3f, () => { if (owner != null && owner.IsConnected && !owner.IsDead() && PetOf(owner) == null) Summon(owner); });
        }

        private object OnNpcTarget(BaseEntity npc, BaseEntity target)
        {
            if (npc == null || target == null) return null;
            // The pet never picks its own targets.
            if (PetByEntity(npc) != null) return true;
            // Only what the wolf is fighting goes for the wolf.
            var pet = PetByEntity(target);
            if (pet != null) return !pet.Down && IsThreat(pet, npc) ? null : (object)true;
            // Taunt: an enemy the wolf is on leaves its owner alone.
            if (!_config.Tank) return null;
            var player = target as BasePlayer;
            if (player == null || player.IsNpc) return null;
            pet = PetOf(player);
            return pet != null && Taunted(pet, npc) ? (object)true : null;
        }

        private object CanLootEntity(BasePlayer player, BaseNPC2 entity)
        {
            var pet = PetByEntity(entity);
            if (pet == null) return null;
            return pet.OwnerId == player.userID ? (object)null : false;
        }

        private void OnPlayerInput(BasePlayer player, InputState input)
        {
            if (player == null || input == null) return;
            var fetch = _fetchButton.HasValue && input.WasJustPressed(_fetchButton.Value);
            var use = _config.UseOpensBag && input.WasJustPressed(BUTTON.USE);
            if (!fetch && !use) return;
            var pet = PetOf(player);
            if (pet == null) return;
            var looked = LookedAt(player, 40f);
            if (use)
            {
                if (looked != pet.Wolf || Vector3.Distance(player.transform.position, pet.Wolf.transform.position) >= 4f) return;
                // Downed, or hurt with a medical item in hand: patch it up. Otherwise open the backpack.
                if (pet.Down || HoldingMedical(player) && pet.Wolf.health < pet.Wolf.MaxHealth() - 0.5f) Patch(player, pet);
                else OpenBag(player);
                return;
            }
            if (pet.Down) return;
            Command(player, pet, looked);
        }

        /// <summary>The fetch/attack key: an enemy you look at gets attacked; anything else is fetched.</summary>
        private void Command(BasePlayer player, Pet pet, BaseEntity looked)
        {
            var enemy = looked as BaseCombatEntity;
            if (enemy != null && looked != pet.Wolf && ValidTarget(pet, enemy))
            {
                SetTarget(pet, enemy);
                Reply(player, "Pet.Attacking");
                return;
            }
            StartFetch(pet, looked);
        }

        private static BaseEntity LookedAt(BasePlayer player, float range)
        {
            RaycastHit hit;
            var mask = LayerMask.GetMask("Default", "Deployed", "AI", "Player (Server)", "Ragdoll", "Physics Debris", "Debris", "Construction", "World", "Terrain", "Tree", "Vehicle Large", "Vehicle World");
            if (!Physics.SphereCast(player.eyes.HeadRay(), 0.25f, out hit, range, mask, QueryTriggerInteraction.Ignore)) return null;
            return hit.GetEntity();
        }

        #endregion

        #region Companion perks (ranks come from the Skills plugin)

        [PluginReference] private Plugin RustSandboxSkills, RustSandboxTweaks;

        private double PerkBonus(ulong id, string perk)
        {
            if (RustSandboxSkills == null || !RustSandboxSkills.IsLoaded) return 0;
            var v = RustSandboxSkills.Call("API_PerkBonus", id, perk);
            return v is double ? (double)v : 0;
        }

        private void NotePerk(BasePlayer player, string perk, double amount)
        {
            if (player != null && amount > 0) RustSandboxSkills?.Call("API_NotePerk", player, perk, amount);
        }

        private string PerkName(string id) => id == "howl" ? "Howl" : id == "loyalfriend" ? "Loyal Friend" : id == "keennose" ? "Keen Nose" : id;

        private static bool Roaring(Pet pet) => pet != null && !pet.Down && Time.realtimeSinceStartup < pet.RoarUntil;

        /// <summary>A fighter's roar: how much harder the owner (and the pet) hits right now. 0 when it isn't roaring.</summary>
        private float HowlBonus(Pet pet) => Roaring(pet) && pet.Species.Role == Role.Fighter
            ? Mathf.Max(0f, _config.RoarDamage) + (float)PerkBonus(pet.OwnerId, "howl") : 0f;

        /// <summary>A tank's roar: how much less damage the owner takes right now. 0 when it isn't roaring.</summary>
        private float GuardBonus(Pet pet) => Roaring(pet) && pet.Species.Role == Role.Tank ? Mathf.Clamp01(_config.RoarGuard) : 0f;

        // Kills the wolf helped with: Companion XP for the owner (plus Combat XP when the wolf landed the kill),
        // and Pack Hunter remembers where animals fell so their corpses give more.
        private class WolfKill { public Vector3 Pos; public float Time; public ulong Owner; }
        private readonly List<WolfKill> _wolfKills = new List<WolfKill>();

        private void OnEntityDeath(BaseCombatEntity entity, HitInfo info)
        {
            if (entity == null || PetByEntity(entity) != null || entity.net == null) return;
            var now = Time.realtimeSinceStartup;
            foreach (var pet in _pets.Values)
            {
                float bitten;
                if (!pet.Bitten.TryGetValue(entity, out bitten)) continue;
                pet.Bitten.Remove(entity);
                pet.Threats.Remove(entity);
                if (now - bitten > ThreatSeconds || pet.Owner == null || !pet.Owner.IsConnected) continue;
                var ownerKilled = info != null && info.InitiatorPlayer == pet.Owner;
                RustSandboxSkills?.Call("API_CompanionKill", pet.Owner, entity, ownerKilled || !_config.CreditOwner);
                if ((entity is BaseNPC2 || entity is BaseNpc) && PerkBonus(pet.OwnerId, "packhunter") > 0)
                    _wolfKills.Add(new WolfKill { Pos = entity.transform.position, Time = now, Owner = pet.OwnerId });
            }
        }

        private void OnDispenserGather(ResourceDispenser dispenser, BasePlayer player, Item item)
        {
            if (_wolfKills.Count == 0 || dispenser == null || player == null || item == null) return;
            var corpse = dispenser.baseEntity as BaseCorpse;
            if (corpse == null) return;
            var now = Time.realtimeSinceStartup;
            _wolfKills.RemoveAll(k => now - k.Time > 900f);
            var pos = corpse.transform.position;
            if (!_wolfKills.Any(k => k.Owner == player.userID && (k.Pos - pos).sqrMagnitude < 36f)) return;
            var bonus = PerkBonus(player.userID, "packhunter");
            if (bonus <= 0) return;
            var extra = item.amount * bonus;
            var add = (int)extra + (UnityEngine.Random.value < extra - Math.Floor(extra) ? 1 : 0);
            if (add <= 0) return;
            item.amount += add;
            NotePerk(player, "packhunter", add);
        }

        private bool TickRevive(Pet pet, float now)
        {
            if (now < pet.NextRevive) return false;
            var bonus = PerkBonus(pet.OwnerId, "loyalfriend");
            if (bonus <= 0) return false;
            var owner = pet.Owner;
            var dist = Vector3.Distance(pet.Wolf.transform.position, owner.transform.position);
            if (dist > 2.2f)
            {
                pet.ReviveStarted = 0f;
                if (dist > 40f) TeleportToOwner(pet);
                else MoveTo(pet, owner.transform.position, dist > 6f ? RustNavMeshAgent.Speeds.FullSprint : RustNavMeshAgent.Speeds.Sprint, 1f);
                return true;
            }
            Stop(pet);
            Face(pet.Wolf, owner.transform.position);
            if (pet.ReviveStarted <= 0f)
            {
                pet.ReviveStarted = now;
                Reply(owner, "Pet.Licking");
                return true;
            }
            if (now - pet.ReviveStarted < _config.ReviveSeconds) return true;
            pet.ReviveStarted = 0f;
            pet.NextRevive = now + _config.ReviveMinutes * 60f;
            owner.StopWounded();
            owner.SetHealth(Mathf.Max(owner.health, owner.MaxHealth() * Mathf.Clamp01((float)bonus)));
            owner.metabolism.bleeding.Set(0f);
            owner.SendNetworkUpdate();
            NotePerk(owner, "loyalfriend", 1);
            Reply(owner, "Pet.LickedUp", Math.Round(_config.ReviveMinutes));
            return true;
        }

        private void Roar(BasePlayer player, Pet pet)
        {
            if (pet.Down) { Reply(player, "Pet.CantWhileDown"); return; }
            var now = Time.realtimeSinceStartup;
            if (now < pet.NextRoar) { Reply(player, "Pet.RoarWait", Math.Ceiling(pet.NextRoar - now)); return; }
            pet.NextRoar = now + _config.RoarCooldown;
            pet.RoarUntil = now + _config.RoarSeconds;
            pet.HoldUntil = now + 2.5f;
            Stop(pet);
            if (pet.Anim != null && pet.RoarAnim != null)
            {
                pet.Anim.StopServerAndReturnToPool(ref pet.BiteState, false);
                pet.BiteState = PlayAnim(pet.Anim, pet.RoarAnim, true);
            }
            if (pet.Species.Role == Role.Tank)
            {
                PullEnemies(pet);
                Reply(player, "Pet.RoaredTank", Math.Round(GuardBonus(pet) * 100), Math.Round(_config.RoarSeconds));
            }
            else
            {
                var ranks = RustSandboxSkills?.Call("API_PerkRank", player.userID.Get(), "howl");
                if (ranks is int && (int)ranks > 0) player.metabolism.pending_health.Add(_config.HowlHeal * (int)ranks);
                Reply(player, "Pet.RoaredFighter", Math.Round(HowlBonus(pet) * 100), Math.Round(_config.RoarSeconds));
            }
            Interface.CallHook("OnSandboxPetBuff", player);
        }

        // A tank's roar: every animal and NPC near the pet that's fighting you (or just near) goes for the pet instead.
        private void PullEnemies(Pet pet)
        {
            _nearby.Clear();
            Vis.Entities(pet.Wolf.transform.position, Mathf.Max(5f, _config.RoarPullRange), _nearby);
            foreach (var e in _nearby.Distinct())
            {
                if (e == null || e == pet.Wolf || !ValidTarget(pet, e)) continue;
                var human = e as BasePlayer;
                if (human != null && (!human.IsNpc || !IsAfter(e, pet.Owner))) continue; // NPCs only when they're after you
                pet.Threats[e] = Time.realtimeSinceStartup + ThreatSeconds;
                var animal = e as BaseNPC2;
                SenseComponent sense;
                if (animal != null && animal.TryGetComponent(out sense))
                {
                    sense.NoteAttacker(pet.Wolf);
                    sense.TrySetTarget(pet.Wolf);
                }
                var old = e as BaseNpc;
                if (old != null) old.AttackTarget = pet.Wolf;
            }
            _nearby.Clear();
        }

        private void Sniff(BasePlayer player, Pet pet)
        {
            var radius = (float)PerkBonus(player.userID, "keennose");
            if (radius <= 0) { Reply(player, "Pet.NeedPerk", PerkName("keennose")); return; }
            var now = Time.realtimeSinceStartup;
            if (now < pet.NextSniff) { Reply(player, "Pet.SniffWait", Math.Ceiling(pet.NextSniff - now)); return; }
            pet.NextSniff = now + _config.SniffCooldown;

            var from = player.transform.position;
            var found = new Dictionary<string, List<float>>();
            var nearest = new Dictionary<string, Vector3>();
            var list = new List<BaseEntity>();
            Vis.Entities(from, radius, list);
            foreach (var e in list.Distinct())
            {
                if (e == null || e == pet.Wolf || PetByEntity(e) != null) continue;
                var name = SniffName(e);
                if (name == null) continue;
                var d = Vector3.Distance(from, e.transform.position);
                List<float> dists;
                if (!found.TryGetValue(name, out dists)) found[name] = dists = new List<float>();
                if (dists.Count == 0 || d < dists.Min()) nearest[name] = e.transform.position;
                dists.Add(d);
            }
            if (found.Count == 0) { Reply(player, "Pet.SniffNothing", Math.Round(radius)); return; }
            var sb = new System.Text.StringBuilder(string.Format(lang.GetMessage("Pet.SniffHeader", this, player.UserIDString).Replace("{pet}", SpeciesOf(player.userID).Name), Math.Round(radius)));
            foreach (var kv in found.OrderBy(x => x.Value.Min()).Take(8))
            {
                var n = kv.Value.Count;
                sb.Append($"\n<color=#c8a165>{n} {(n == 1 ? kv.Key : Plural(kv.Key))}</color>, nearest {Math.Round(kv.Value.Min())} m {Compass(from, nearest[kv.Key])}");
            }
            player.ChatMessage(Prefix(player) + sb.ToString().Replace("{pet}", SpeciesOf(player.userID).Name));
        }

        private static string SniffName(BaseEntity e)
        {
            var combat = e as BaseCombatEntity;
            if (combat != null && combat.IsDead()) return null;
            if (e is BaseNPC2 || e is BaseNpc)
            {
                var n = e.ShortPrefabName.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9', '_', '.');
                if (n.Contains("polarbear")) return "polar bear";
                if (n.Contains("shark")) return "shark";
                if (n.Contains("horse")) return "horse";
                if (n.Contains("croc")) return "crocodile";
                if (n.Contains("tiger")) return "tiger";
                if (n.Contains("panther")) return "panther";
                if (n.Contains("snake")) return "snake";
                return n.Replace('_', ' ');
            }
            var bp = e as BasePlayer;
            if (bp != null) return bp.IsNpc && !bp.InSafeZone() && !bp.IsDead() ? "scientist" : null;
            var crate = e as LootContainer;
            if (crate == null || crate.inventory == null || crate.inventory.itemList.Count == 0) return null;
            var c = crate.ShortPrefabName;
            if (crate is HackableLockedCrate) return "locked crate";
            if (c.Contains("barrel")) return "barrel";
            if (c.Contains("elite")) return "elite crate";
            if (c.StartsWith("crate_normal_2")) return "crate";
            if (c.StartsWith("crate_normal")) return "military crate";
            if (c.Contains("tools")) return "toolbox";
            if (c.Contains("food")) return "food box";
            if (c.Contains("medical")) return "medical crate";
            return "loot box";
        }

        private static string Plural(string name)
        {
            if (name == "wolf") return "wolves";
            if (name.EndsWith("x") || name.EndsWith("s")) return name + "es";
            return name + "s";
        }

        private static string Compass(Vector3 from, Vector3 to)
        {
            var d = to - from;
            var a = Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
            if (a < 0) a += 360f;
            return new[] { "N", "NE", "E", "SE", "S", "SW", "W", "NW" }[(int)Mathf.Round(a / 45f) % 8];
        }

        #endregion

        #region Commands

        [ChatCommand("pet")]
        private void PetCommand(BasePlayer player, string command, string[] args) =>
            Run(player, args.Length > 0 ? args[0].ToLower() : "", args.Length > 1 ? string.Join(" ", args.Skip(1)) : "");

        [ConsoleCommand("pet")]
        private void PetConsole(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            if (player != null) Run(player, arg.GetString(0, "").ToLower(), arg.HasArgs(2) ? string.Join(" ", arg.Args.Skip(1)) : "");
        }

        [ConsoleCommand("pet.summon")] private void CSummon(ConsoleSystem.Arg arg) { if (arg.Player() != null) Run(arg.Player(), "toggle"); }
        [ConsoleCommand("pet.bag")] private void CBag(ConsoleSystem.Arg arg) { if (arg.Player() != null) Run(arg.Player(), "bag"); }
        [ConsoleCommand("pet.fetch")] private void CFetch(ConsoleSystem.Arg arg) { if (arg.Player() != null) Run(arg.Player(), "fetch"); }
        [ConsoleCommand("pet.attack")] private void CAttack(ConsoleSystem.Arg arg) { if (arg.Player() != null) Run(arg.Player(), "attack"); }
        [ConsoleCommand("pet.stay")] private void CStay(ConsoleSystem.Arg arg) { if (arg.Player() != null) Run(arg.Player(), "stay"); }
        [ConsoleCommand("pet.patrol")] private void CPatrol(ConsoleSystem.Arg arg) { if (arg.Player() != null) Run(arg.Player(), "patrol"); }
        [ConsoleCommand("pet.roar")] private void CRoar(ConsoleSystem.Arg arg) { if (arg.Player() != null) Run(arg.Player(), "roar"); }
        [ConsoleCommand("pet.howl")] private void CHowl(ConsoleSystem.Arg arg) { if (arg.Player() != null) Run(arg.Player(), "roar"); }
        [ConsoleCommand("pet.sniff")] private void CSniff(ConsoleSystem.Arg arg) { if (arg.Player() != null) Run(arg.Player(), "sniff"); }
        [ConsoleCommand("pet.heal")] private void CHeal(ConsoleSystem.Arg arg) { if (arg.Player() != null) Run(arg.Player(), "heal"); }

        private void Run(BasePlayer player, string sub, string arg = "")
        {
            if (!CanHave(player)) { Reply(player, "Pet.NoPermission"); return; }
            var pet = PetOf(player);
            // An animal's name works as a command too: /pet bear.
            if (FindSpecies(sub) != null) { arg = sub; sub = "choose"; }
            switch (sub)
            {
                case "":
                case "call":
                case "summon":
                case "toggle":
                    if (pet != null && sub == "toggle" && !pet.Down) { Dismiss(pet, false); Reply(player, "Pet.Dismissed"); return; }
                    if (player.IsDead()) return;
                    if (pet == null)
                    {
                        var rest = RestMinutesLeft(player.userID);
                        if (rest > 0) { Reply(player, "Pet.Resting", rest); return; }
                        pet = Summon(player);
                        Reply(player, pet != null ? "Pet.Summoned" : "Pet.SummonFailed");
                        if (pet != null && sub == "") Reply(player, "Pet.Commands", KeyName());
                        return;
                    }
                    if (pet.Down) { DownWhere(player, pet); return; }
                    pet.Mode = Mode.Follow;
                    pet.Patrol = false;
                    pet.Target = null;
                    if (Vector3.Distance(pet.Wolf.transform.position, player.transform.position) > 25f) TeleportToOwner(pet);
                    Reply(player, sub == "" ? "Pet.Commands" : "Pet.Coming", KeyName());
                    return;
                case "choose":
                case "pick":
                case "animal":
                case "animals":
                case "change":
                case "switch":
                    Choose(player, pet, arg);
                    return;
                case "dismiss":
                case "away":
                    if (pet == null) { Reply(player, "Pet.NoPet"); return; }
                    if (pet.Down) { SendHome(pet); return; }
                    Dismiss(pet, false);
                    Reply(player, "Pet.Dismissed");
                    return;
                case "bag":
                case "backpack":
                case "inv":
                    OpenBag(player);
                    return;
                case "heal":
                case "revive":
                case "patch":
                case "medic":
                    if (pet == null) { Reply(player, "Pet.NoPet"); return; }
                    if (Vector3.Distance(player.transform.position, pet.Wolf.transform.position) > 5f) { Reply(player, "Pet.GetCloser"); return; }
                    Patch(player, pet);
                    return;
                case "keys":
                case "bind":
                case "binds":
                    Reply(player, "Pet.Keys", KeyName());
                    return;
            }

            // Everything else needs a pet that's up.
            if (pet == null) { Reply(player, sub == "help" ? "Pet.Commands" : "Pet.NoPet", KeyName()); return; }
            if (pet.Down) { DownWhere(player, pet); return; }
            switch (sub)
            {
                case "stay":
                case "sit":
                    var staying = pet.Mode == Mode.Stay;
                    pet.Mode = staying ? Mode.Follow : Mode.Stay;
                    pet.Patrol = false;
                    pet.Target = null;
                    pet.FetchQueue.Clear();
                    Reply(player, staying ? "Pet.Following" : "Pet.Staying");
                    return;
                case "patrol":
                case "guard":
                case "roam":
                    pet.Patrol = !pet.Patrol || pet.Mode == Mode.Stay;
                    pet.Mode = Mode.Follow;
                    pet.Target = null;
                    pet.FetchQueue.Clear();
                    pet.NextPatrolMove = 0f;
                    if (pet.Patrol) Reply(player, "Pet.Patrolling", Mathf.RoundToInt(_config.PatrolRadius));
                    else Reply(player, "Pet.Following");
                    return;
                case "follow":
                    pet.Patrol = false;
                    pet.Mode = Mode.Follow;
                    pet.Target = null;
                    pet.FetchQueue.Clear();
                    Reply(player, "Pet.Following");
                    return;
                case "fetch":
                    var looked = LookedAt(player, 40f);
                    StartFetch(pet, looked == pet.Wolf ? null : looked);
                    return;
                case "attack":
                    var enemy = LookedAt(player, 60f) as BaseCombatEntity;
                    if (enemy == null || !ValidTarget(pet, enemy)) { Reply(player, "Pet.NoEnemy"); return; }
                    SetTarget(pet, enemy);
                    Reply(player, "Pet.Attacking");
                    return;
                case "roar":
                case "howl":
                case "buff":
                    Roar(player, pet);
                    return;
                case "sniff":
                case "smell":
                    Sniff(player, pet);
                    return;
                default:
                    Reply(player, "Pet.Commands", KeyName());
                    return;
            }
        }

        private void Choose(BasePlayer player, Pet pet, string arg)
        {
            var sp = FindSpecies(arg);
            if (sp == null || !Allowed(sp))
            {
                var sb = new System.Text.StringBuilder(lang.GetMessage("Pet.ChooseHeader", this, player.UserIDString));
                foreach (var a in AllSpecies.Where(Allowed))
                {
                    var role = lang.GetMessage(a.Role == Role.Fighter ? "Pet.RoleFighter" : "Pet.RoleTank", this, player.UserIDString);
                    sb.Append($"\n<color=#c8a165>/pet choose {a.Key}</color>  {role}, {a.Blurb}");
                }
                sb.Append("\n" + lang.GetMessage("Pet.ChooseFooter", this, player.UserIDString));
                player.ChatMessage(Prefix(player) + sb.ToString().Replace("{pet}", SpeciesOf(player.userID).Name));
                return;
            }
            if (pet != null && pet.Down) { Reply(player, "Pet.CantWhileDown"); return; }
            DataFor(player.userID).Animal = sp.Key;
            var role2 = lang.GetMessage(sp.Role == Role.Fighter ? "Pet.RoleFighter" : "Pet.RoleTank", this, player.UserIDString);
            if (pet == null || pet.Species == sp)
            {
                Reply(player, "Pet.Chosen", sp.Name, role2);
                return;
            }
            // Swap the animal where the old one stood: same backpack, same mode.
            var patrol = pet.Patrol;
            Dismiss(pet, true);
            var rest = RestMinutesLeft(player.userID);
            if (rest > 0) { Reply(player, "Pet.Resting", rest); return; }
            var fresh = Summon(player);
            if (fresh == null) { Reply(player, "Pet.SummonFailed"); return; }
            fresh.Patrol = patrol;
            Reply(player, "Pet.Swapped", sp.Name, role2);
        }

        private void DownWhere(BasePlayer player, Pet pet)
        {
            var d = Vector3.Distance(player.transform.position, pet.Wolf.transform.position);
            Reply(player, "Pet.IsDown", Math.Round(d), Compass(player.transform.position, pet.Wolf.transform.position), MedicalNames());
        }

        private string KeyName()
        {
            if (!_fetchButton.HasValue) return "none";
            switch (_fetchButton.Value)
            {
                case BUTTON.FIRE_THIRD: return "middle mouse";
                case BUTTON.USE: return "E";
                case BUTTON.RELOAD: return "R";
                default: return _fetchButton.Value.ToString();
            }
        }

        #endregion

        #region Lang

        protected override void LoadDefaultMessages()
        {
            // Keys start with "Pet." since 1.4.0 so the new wording replaces the wolf-only lines in old lang files.
            // {pet} is swapped for the player's animal (wolf, bear...).
            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["Pet.Prefix"] = "<color=#c8a165>Pet</color>: ",
                ["Pet.NoPermission"] = "You don't have permission to have a pet.",
                ["Pet.NoPet"] = "You don't have a pet with you. Type /pet to call your {pet}, or /pet choose to pick an animal.",
                ["Pet.Summoned"] = "Your {pet} is here. It follows you and fights with you. If it goes down, look at it and press E with a bandage, syringe or medkit.",
                ["Pet.SummonFailed"] = "Couldn't call your {pet} here. Try again somewhere on open ground.",
                ["Pet.Coming"] = "Your {pet} is coming.",
                ["Pet.Dismissed"] = "Your {pet} has gone home. Its backpack keeps everything until you call it again.",
                ["Pet.Staying"] = "Your {pet} sits and waits.",
                ["Pet.Following"] = "Your {pet} follows you.",
                ["Pet.Patrolling"] = "Your {pet} patrols within {0} m of you and still jumps in when something attacks. /pet patrol again (or /pet) calls it back to your side.",
                ["Pet.Attacking"] = "Your {pet} attacks!",
                ["Pet.NoEnemy"] = "Look at an animal or NPC to send your {pet} after it.",
                ["Pet.Fetching"] = "Your {pet} goes to fetch {0} thing(s).",
                ["Pet.FetchDone"] = "Your {pet} is done fetching. Look at it and press E (or /pet bag) to open its backpack.",
                ["Pet.NothingToFetch"] = "Nothing to fetch around here.",
                ["Pet.BagFull"] = "The backpack and your inventory are full.",
                ["Pet.TooFar"] = "Get within {0}m of your {pet} to open its backpack.",
                ["Pet.Commands"] = "/pet choose  pick an animal\n/pet bag  open its backpack (or look at it and press E)\n/pet fetch  pick up what you look at, or loot nearby ({0} does this too)\n/pet attack  attack what you look at\n/pet stay  sit or follow again\n/pet patrol  roam around you while you build\n/pet roar  wolf, tiger, panther: you hit harder and heal; bear, boar, crocodile: enemies go for it and you take less damage\n/pet heal  patch it up with a bandage, syringe or medkit\n/pet sniff  what's around you (Keen Nose perk)\n/pet dismiss  send it home\n/pet keys  put these on keys\nLevel the Companion skill in /skills by fighting and fetching together.",
                ["Pet.Keys"] = "Bind keys in the F1 console, for example:\n<color=#c8a165>bind g pet.fetch</color>  fetch / attack what you look at\n<color=#c8a165>bind b pet.bag</color>  open the backpack\n<color=#c8a165>bind h pet.stay</color>  sit / follow\n<color=#c8a165>bind k pet.patrol</color>  patrol / follow\n<color=#c8a165>bind j pet.summon</color>  call / dismiss\n<color=#c8a165>bind v pet.roar</color>  roar\n<color=#c8a165>bind n pet.sniff</color>  sniff\n<color=#c8a165>bind x pet.heal</color>  patch it up\nFetch key without binding: {0}.",
                ["Pet.NeedPerk"] = "Your {pet} needs the {0} perk first: open /skills, Companion.",
                ["Pet.RoaredFighter"] = "Your {pet} roars! You hit {0}% harder and heal for {1} seconds.",
                ["Pet.RoaredTank"] = "Your {pet} roars! Enemies nearby go for it, and you take {0}% less damage for {1} seconds.",
                ["Pet.RoarWait"] = "Your {pet} can roar again in {0} seconds.",
                ["Pet.Licking"] = "Your {pet} is nudging you back onto your feet...",
                ["Pet.LickedUp"] = "Your {pet} got you up! It can do that again in {0} minutes.",
                ["Pet.SniffWait"] = "Your {pet} can sniff again in {0} seconds.",
                ["Pet.SniffNothing"] = "Your {pet} sniffs around: nothing within {0} m.",
                ["Pet.SniffHeader"] = "Your {pet} smells (within {0} m):",
                ["Pet.ChooseHeader"] = "Pick your pet:",
                ["Pet.ChooseFooter"] = "Your backpack and Companion perks come along whichever you pick. Right now it's a {pet}.",
                ["Pet.RoleFighter"] = "fighter (roar: you hit harder and heal)",
                ["Pet.RoleTank"] = "tank (roar: enemies go for it, you take less damage)",
                ["Pet.Chosen"] = "You picked a {0}, a {1}. Type /pet to call it.",
                ["Pet.Swapped"] = "Your pet is now a {0}, a {1}. Same backpack as before.",
                ["Pet.Down"] = "<color=#ff6b5b>Your {pet} is down!</color> Look at it and press E with a {0} to get it back up. If you leave it, it goes home to rest in {1} minutes.",
                ["Pet.DownWaits"] = "<color=#ff6b5b>Your {pet} is down!</color> Look at it and press E with a {0} to get it back up.",
                ["Pet.IsDown"] = "Your {pet} is down {0} m {1} of you. Look at it and press E with a {2} to get it up.",
                ["Pet.CantWhileDown"] = "Your {pet} is down. Patch it up first (look at it and press E with a bandage, syringe or medkit).",
                ["Pet.NeedMedical"] = "You need a {0} to patch up your {pet}.",
                ["Pet.NotHurt"] = "Your {pet} isn't hurt.",
                ["Pet.GetCloser"] = "Get right next to your {pet} to patch it up.",
                ["Pet.Healed"] = "You used a {0} on your {pet}: it's at {1}% health.",
                ["Pet.Revived"] = "You used a {0} and your {pet} is back on its feet at {1}% health!",
                ["Pet.WentHome"] = "Nobody helped your {pet}, so it limped home. It needs {0} minutes of rest before you can call it.",
                ["Pet.Resting"] = "Your {pet} is resting at home. You can call it again in {0} minute(s).",
            }, this);
        }

        private string Prefix(BasePlayer player) => lang.GetMessage("Pet.Prefix", this, player.UserIDString);

        private void Reply(BasePlayer player, string key, params object[] args)
        {
            if (player == null) return;
            var msg = lang.GetMessage(key, this, player.UserIDString).Replace("{pet}", SpeciesOf(player.userID).Name);
            if (args != null && args.Length > 0) msg = string.Format(msg, args);
            player.ChatMessage(Prefix(player) + msg);
        }

        #endregion
    }
}
