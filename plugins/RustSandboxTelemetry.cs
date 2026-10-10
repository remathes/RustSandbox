// Rust Sandbox — telemetry plugin for Oxide (uMod) and Carbon.
// Reports deaths, PvE kills, weapon usage (shots/hits), gathering, player positions and connections
// to your Rust Sandbox so it can show combat stats, heat maps and a live map.
//
// Install: copy this file to oxide/plugins (or carbon/plugins) and the config downloaded from the
// manager to oxide/config/RustSandboxTelemetry.json (or carbon/configs).

using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Libraries;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("Rust Sandbox Telemetry", "RustSandbox", "1.5.0")]
    [Description("Sends kills, deaths, weapon usage, gathering and player positions to Rust Sandbox.")]
    public class RustSandboxTelemetry : RustPlugin
    {
        #region Config

        private PluginConfig _config;

        private class PluginConfig
        {
            [JsonProperty("Manager URL")] public string ManagerUrl = "http://127.0.0.1:5080";
            [JsonProperty("Server ID")] public int ServerId = 1;
            [JsonProperty("API Key")] public string ApiKey = "";
            [JsonProperty("Position interval (seconds)")] public float PositionInterval = 5f;
            [JsonProperty("Flush interval (seconds)")] public float FlushInterval = 5f;
            [JsonProperty("Track NPC and animal kills")] public bool TrackPve = true;
            [JsonProperty("Track weapon usage")] public bool TrackUsage = true;
            [JsonProperty("Track gathering")] public bool TrackGathering = true;
        }

        protected override void LoadDefaultConfig() => _config = new PluginConfig();

        private bool _configBroken;

        // This plugin used to be called RustServerManagerTelemetry (the project was renamed to Rust Sandbox).
        // The first time it loads, copy the old config and data files over so nothing is lost.
        private const string LegacyName = "RustServerManagerTelemetry";

        // Runs before Oxide loads the config, so it works on the files directly. A marker file makes it
        // a one-time copy: the old files win once, even if this plugin already created empty new ones.
        private void MigrateLegacyFiles(params string[] dataSuffixes)
        {
            const string newName = "RustSandboxTelemetry";
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
            MigrateLegacyFiles();
            base.LoadConfig();
            try
            {
                _config = Config.ReadObject<PluginConfig>();
                if (_config == null) throw new Exception("empty");
                SaveConfig(); // adds any new settings, keeps existing values
            }
            catch (Exception ex)
            {
                // Never overwrite a config we couldn't read — that would wipe the API key.
                _configBroken = true;
                _config = new PluginConfig();
                PrintError($"Couldn't read the config file ({ex.Message}). It was left untouched and reporting is paused. Fix it or reinstall the plugin from the manager.");
            }
        }

        protected override void SaveConfig() => Config.WriteObject(_config, true);

        #endregion

        #region Payload

        private class KillDto
        {
            [JsonProperty("killerId")] public string KillerId;
            [JsonProperty("killerName")] public string KillerName;
            [JsonProperty("killerType")] public string KillerType;
            [JsonProperty("victimId")] public string VictimId;
            [JsonProperty("victimName")] public string VictimName;
            [JsonProperty("victimType")] public string VictimType;
            [JsonProperty("weapon")] public string Weapon;
            [JsonProperty("damageType")] public string DamageType;
            [JsonProperty("bodyPart")] public string BodyPart;
            [JsonProperty("headshot")] public bool Headshot;
            [JsonProperty("distance")] public float Distance;
            [JsonProperty("vx")] public float Vx;
            [JsonProperty("vz")] public float Vz;
            [JsonProperty("kx")] public float Kx;
            [JsonProperty("kz")] public float Kz;
            [JsonProperty("time")] public long Time;
        }

        private class PositionDto
        {
            [JsonProperty("id")] public string Id;
            [JsonProperty("name")] public string Name;
            [JsonProperty("x")] public float X;
            [JsonProperty("z")] public float Z;
            [JsonProperty("health")] public float Health;
        }

        private class ConnectionDto
        {
            [JsonProperty("id")] public string Id;
            [JsonProperty("name")] public string Name;
            [JsonProperty("connected")] public bool Connected;
        }

        private class UsageDto
        {
            [JsonProperty("id")] public string Id;
            [JsonProperty("weapon")] public string Weapon;
            [JsonProperty("shots")] public int Shots;
            [JsonProperty("hits")] public int Hits;
        }

        private class GatherDto
        {
            [JsonProperty("id")] public string Id;
            [JsonProperty("item")] public string Item;
            [JsonProperty("code")] public string Code;
            [JsonProperty("source")] public string Source;
            [JsonProperty("amount")] public long Amount;
            [JsonProperty("times")] public int Times;
        }

        private class Batch
        {
            [JsonProperty("kills")] public List<KillDto> Kills;
            [JsonProperty("positions")] public List<PositionDto> Positions;
            [JsonProperty("connections")] public List<ConnectionDto> Connections;
            [JsonProperty("usage")] public List<UsageDto> Usage;
            [JsonProperty("gather")] public List<GatherDto> Gather;
            [JsonProperty("monuments")] public List<MonumentDto> Monuments;
        }

        private class MonumentDto
        {
            [JsonProperty("name")] public string Name;
            [JsonProperty("x")] public float X;
            [JsonProperty("z")] public float Z;
        }

        #endregion

        #region State

        private const int MaxQueuedKills = 1000;
        private List<KillDto> _kills = new List<KillDto>();
        private List<ConnectionDto> _connections = new List<ConnectionDto>();
        private Dictionary<string, UsageDto> _usage = new Dictionary<string, UsageDto>();
        private Dictionary<string, GatherDto> _gather = new Dictionary<string, GatherDto>();
        private List<PositionDto> _positions;
        private bool _sending;
        private float _sendStarted;
        private int _sendId;
        private bool _delivered;
        private float _lastFailureLog = -9999f;
        private bool _warnedAuth;
        private int _failures;
        private float _nextAttempt;

        private static readonly Dictionary<string, string> AnimalNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["bear"] = "Bear", ["polarbear"] = "Polar Bear", ["wolf"] = "Wolf", ["wolf2"] = "Wolf", ["boar"] = "Boar", ["stag"] = "Stag",
            ["chicken"] = "Chicken", ["testridablehorse"] = "Horse", ["ridablehorse"] = "Horse", ["horse"] = "Horse",
            ["simpleshark"] = "Shark", ["shark"] = "Shark", ["crocodile"] = "Crocodile", ["tiger"] = "Tiger", ["panther"] = "Panther", ["snake"] = "Snake",
        };

        #endregion

        #region Lifecycle

        private void OnServerInitialized()
        {
            if (_configBroken) return;
            if (string.IsNullOrEmpty(_config.ApiKey))
            {
                PrintError("Not reporting: no API key set. Reinstall the plugin from Rust Sandbox (Telemetry page).");
                return;
            }
            timer.Every(Mathf.Max(2f, _config.PositionInterval), CapturePositions);
            timer.Every(Mathf.Max(2f, _config.FlushInterval), Flush);
            Puts($"Starting — sending reports to {ManagerUrl()} as server #{_config.ServerId}.");
            CollectMonuments();
        }

        private void Unload() => Flush();

        #endregion

        #region Hooks

        private void OnPlayerConnected(BasePlayer player)
        {
            if (!IsRealPlayer(player)) return;
            _connections.Add(new ConnectionDto { Id = player.UserIDString, Name = player.displayName, Connected = true });
        }

        private void OnPlayerDisconnected(BasePlayer player, string reason)
        {
            if (!IsRealPlayer(player)) return;
            _connections.Add(new ConnectionDto { Id = player.UserIDString, Name = player.displayName, Connected = false });
        }

        private void OnEntityDeath(BaseCombatEntity entity, HitInfo info)
        {
            if (entity == null) return;
            try
            {
                var victimPlayer = entity as BasePlayer;
                if (victimPlayer != null && IsRealPlayer(victimPlayer))
                {
                    RecordPlayerDeath(victimPlayer, info);
                    return;
                }

                if (!_config.TrackPve || info == null) return;
                var attacker = info.InitiatorPlayer;
                if (attacker == null || !IsRealPlayer(attacker)) return;

                string victimType, victimName;
                if (!ClassifyVictim(entity, out victimType, out victimName)) return;
                var kill = NewKill(info, attacker, entity);
                kill.VictimType = victimType;
                kill.VictimName = victimName;
                Queue(kill);
            }
            catch (Exception ex)
            {
                PrintWarning($"Couldn't record a death: {ex.Message}");
            }
        }

        private void OnWeaponFired(BaseProjectile projectile, BasePlayer player, ItemModProjectile mod, ProtoBuf.ProjectileShoot projectiles)
        {
            if (!_config.TrackUsage || projectile == null || !IsRealPlayer(player)) return;
            var weapon = ItemName(projectile.GetItem(), projectile.ShortPrefabName);
            var count = projectiles?.projectiles?.Count ?? 1;
            Usage(player.UserIDString, weapon).Shots += Mathf.Max(1, count);
        }

        private void OnPlayerAttack(BasePlayer attacker, HitInfo info)
        {
            if (!_config.TrackUsage || info == null || !IsRealPlayer(attacker)) return;
            var gun = info.Weapon as BaseProjectile;
            if (gun == null) return;
            if (!(info.HitEntity is BasePlayer) && !(info.HitEntity is BaseNpc)) return;
            Usage(attacker.UserIDString, ItemName(gun.GetItem(), gun.ShortPrefabName)).Hits++;
        }

        // Gathering. The "…ed"/"…Received" hooks run after gather-rate plugins, so amounts are what the player actually got.

        // Hits on trees, ore nodes and animal corpses.
        private void OnDispenserGathered(ResourceDispenser dispenser, BasePlayer player, Item item)
        {
            if (dispenser == null) return;
            RecordGather(player, item, DispenserSource(dispenser), true);
        }

        // The bonus on the last hit of a tree or ore node.
        private void OnDispenserBonusReceived(ResourceDispenser dispenser, BasePlayer player, Item item)
        {
            if (dispenser == null) return;
            RecordGather(player, item, DispenserSource(dispenser), false);
        }

        // Ground pickups: hemp, mushrooms, wild berries, stone/metal/sulfur nodes, wood piles. Food eaten on the spot isn't reported.
        private void OnCollectiblePickedup(CollectibleEntity collectible, BasePlayer player, Item item) => RecordGather(player, item, "pickup", true);

        // Planted crops and berry bushes.
        private void OnGrowableGathered(GrowableEntity plant, Item item, BasePlayer player) => RecordGather(player, item, "farm", true);

        private void OnFishCaught(ItemDefinition fish, BaseFishingRod rod, BasePlayer player)
        {
            if (fish == null) return;
            Gather(player, fish, 1, "fishing", true);
        }

        #endregion

        #region Recording

        private void RecordPlayerDeath(BasePlayer victim, HitInfo info)
        {
            var initiator = info?.Initiator;
            var kill = NewKill(info, initiator, victim);
            kill.VictimId = victim.UserIDString;
            kill.VictimName = victim.displayName;
            kill.VictimType = "Player";

            var killerPlayer = initiator as BasePlayer;
            if (initiator == null)
            {
                kill.KillerType = "Environment";
                kill.DamageType = DamageName(victim.lastDamage.ToString());
                if (kill.DamageType == "Suicide") kill.KillerType = "Self";
                kill.KillerName = kill.DamageType;
            }
            else if (killerPlayer != null && killerPlayer == victim)
            {
                kill.KillerType = "Self";
                kill.KillerId = victim.UserIDString;
                kill.KillerName = victim.displayName;
                kill.DamageType = "Suicide";
            }
            else if (killerPlayer != null && IsRealPlayer(killerPlayer))
            {
                kill.KillerType = "Player";
                kill.KillerId = killerPlayer.UserIDString;
                kill.KillerName = killerPlayer.displayName;
            }
            else if (killerPlayer != null)
            {
                kill.KillerType = "Scientist";
                kill.KillerName = NpcName(killerPlayer);
            }
            else if (initiator is BaseNpc)
            {
                kill.KillerType = "Animal";
                kill.KillerName = AnimalName(initiator.ShortPrefabName);
                if (string.IsNullOrEmpty(kill.Weapon)) kill.DamageType = "Bite";
            }
            else
            {
                var prefab = initiator.ShortPrefabName ?? "";
                kill.KillerName = PrettyPrefab(prefab);
                if (prefab.Contains("patrolhelicopter") || prefab.Contains("heli")) { kill.KillerType = "Helicopter"; kill.KillerName = "Patrol Helicopter"; }
                else if (prefab.Contains("bradley")) { kill.KillerType = "Bradley"; kill.KillerName = "Bradley APC"; }
                else if (prefab.Contains("turret") || prefab.Contains("sam_site") || prefab.Contains("guntrap")) kill.KillerType = "Turret";
                else if (prefab.Contains("landmine") || prefab.Contains("beartrap") || prefab.Contains("spikes") || prefab.Contains("barricade")) kill.KillerType = "Trap";
                else if (prefab.Contains("explosive") || prefab.Contains("rocket") || prefab.Contains("grenade")) kill.KillerType = "Explosion";
                else kill.KillerType = "Unknown";
                if (string.IsNullOrEmpty(kill.Weapon)) kill.Weapon = kill.KillerName;
            }
            Queue(kill);
        }

        private KillDto NewKill(HitInfo info, BaseEntity attacker, BaseEntity victim)
        {
            var vp = victim.transform.position;
            var kill = new KillDto
            {
                Vx = vp.x,
                Vz = vp.z,
                Time = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            };
            var attackerPlayer = attacker as BasePlayer;
            if (attackerPlayer != null && IsRealPlayer(attackerPlayer))
            {
                kill.KillerId = attackerPlayer.UserIDString;
                kill.KillerName = attackerPlayer.displayName;
                kill.KillerType = "Player";
            }
            if (attacker != null)
            {
                var ap = attacker.transform.position;
                kill.Kx = ap.x;
                kill.Kz = ap.z;
                kill.Distance = Vector3.Distance(ap, vp);
            }
            if (info != null)
            {
                if (info.ProjectileDistance > 0f) kill.Distance = info.ProjectileDistance;
                kill.Weapon = WeaponName(info);
                kill.Headshot = info.isHeadshot;
                kill.BodyPart = info.boneArea.ToString();
                if (kill.BodyPart == "-1" || kill.BodyPart == "None") kill.BodyPart = "";
                if (info.damageTypes != null) kill.DamageType = DamageName(info.damageTypes.GetMajorityDamageType().ToString());
            }
            kill.Distance = Mathf.Round(kill.Distance * 10f) / 10f;
            return kill;
        }

        private bool ClassifyVictim(BaseCombatEntity entity, out string type, out string name)
        {
            type = name = null;
            var prefab = entity.ShortPrefabName ?? "";
            if (entity is BasePlayer)
            {
                type = "Scientist";
                name = NpcName((BasePlayer)entity);
                return true;
            }
            if (entity is BaseNpc || AnimalNames.ContainsKey(prefab))
            {
                type = "Animal";
                name = AnimalName(prefab);
                return true;
            }
            if (prefab.Contains("patrolhelicopter")) { type = "Helicopter"; name = "Patrol Helicopter"; return true; }
            if (prefab.Contains("bradleyapc")) { type = "Bradley"; name = "Bradley APC"; return true; }
            return false; // buildings, deployables, barrels…
        }

        private void Queue(KillDto kill)
        {
            if (_kills.Count >= MaxQueuedKills) _kills.RemoveAt(0);
            _kills.Add(kill);
        }

        private UsageDto Usage(string id, string weapon)
        {
            var key = id + "|" + weapon;
            UsageDto u;
            if (!_usage.TryGetValue(key, out u)) _usage[key] = u = new UsageDto { Id = id, Weapon = weapon };
            return u;
        }

        private void RecordGather(BasePlayer player, Item item, string source, bool countTimes)
        {
            if (item == null) return;
            Gather(player, item.info, item.amount, source, countTimes);
        }

        private void Gather(BasePlayer player, ItemDefinition def, int amount, string source, bool countTimes)
        {
            if (!_config.TrackGathering || def == null || amount <= 0 || !IsRealPlayer(player)) return;
            var g = GatherRow(player.UserIDString, def.shortname, source);
            if (g.Item == null) g.Item = def.displayName != null && !string.IsNullOrEmpty(def.displayName.english) ? def.displayName.english : PrettyPrefab(def.shortname);
            g.Amount += amount;
            if (countTimes) g.Times++;
        }

        private GatherDto GatherRow(string id, string code, string source)
        {
            var key = id + "|" + code + "|" + source;
            GatherDto g;
            if (!_gather.TryGetValue(key, out g)) _gather[key] = g = new GatherDto { Id = id, Code = code, Source = source };
            return g;
        }

        private static string DispenserSource(ResourceDispenser dispenser)
        {
            switch (dispenser.gatherType)
            {
                case ResourceDispenser.GatherType.Tree: return "tree";
                case ResourceDispenser.GatherType.Ore: return "ore";
                case ResourceDispenser.GatherType.Flesh: return "animal";
                default: return "other";
            }
        }

        private void CapturePositions()
        {
            var list = new List<PositionDto>(BasePlayer.activePlayerList.Count);
            foreach (var p in BasePlayer.activePlayerList)
            {
                if (p == null || !IsRealPlayer(p) || p.IsDead()) continue;
                var pos = p.transform.position;
                list.Add(new PositionDto { Id = p.UserIDString, Name = p.displayName, X = pos.x, Z = pos.z, Health = p.health });
            }
            _positions = list;
        }

        #endregion

        #region Sending

        private List<MonumentDto> _pendingMonuments;

        /// <summary>Monument names and positions, as labelled on the in-game map. Sent once per load.</summary>
        private void CollectMonuments()
        {
            try
            {
                var list = new List<MonumentDto>();
                var monuments = TerrainMeta.Path != null ? TerrainMeta.Path.Monuments : null;
                if (monuments == null) return;
                foreach (var m in monuments)
                {
                    if (m == null) continue;
                    var name = m.displayPhrase != null ? m.displayPhrase.english : null;
                    if (string.IsNullOrWhiteSpace(name)) continue; // the in-game map skips unnamed ones too
                    var pos = m.transform.position;
                    list.Add(new MonumentDto { Name = name.Trim(), X = pos.x, Z = pos.z });
                }
                _pendingMonuments = list;
            }
            catch (Exception ex)
            {
                PrintWarning($"Couldn't read monuments: {ex.Message}");
            }
        }

        /// <summary>Manager URL with "localhost" swapped for 127.0.0.1 (localhost can resolve to IPv6 and hang).</summary>
        private string ManagerUrl()
        {
            var url = (_config.ManagerUrl ?? "").Trim().TrimEnd('/');
            return url.Replace("://localhost", "://127.0.0.1");
        }

        private void LogFailure(string message)
        {
            // First failure right away, then at most every 2 minutes so the console isn't flooded.
            var now = UnityEngine.Time.realtimeSinceStartup;
            if (_failures == 1 || now - _lastFailureLog > 120f)
            {
                _lastFailureLog = now;
                PrintWarning($"Not reporting: {message} (attempt {_failures}). Events are kept and resent when it recovers.");
            }
        }

        private void Flush()
        {
            if (_configBroken || string.IsNullOrEmpty(_config.ApiKey) || string.IsNullOrEmpty(_config.ManagerUrl)) return;
            var now = UnityEngine.Time.realtimeSinceStartup;

            // Watchdog: a request that never answers must not block reporting forever.
            if (_sending)
            {
                if (now - _sendStarted < 20f) return;
                _sending = false;
                _sendId++;
                _failures++;
                LogFailure("the manager didn't answer within 20 seconds");
                _nextAttempt = now + Mathf.Min(60f, 5f * _failures);
            }
            if (now < _nextAttempt) return;
            if (_kills.Count == 0 && _connections.Count == 0 && _usage.Count == 0 && _gather.Count == 0 && _positions == null && _pendingMonuments == null) return;

            var kills = _kills; _kills = new List<KillDto>();
            var conns = _connections; _connections = new List<ConnectionDto>();
            var usage = new List<UsageDto>(_usage.Values); _usage = new Dictionary<string, UsageDto>();
            var gather = new List<GatherDto>(_gather.Values); _gather = new Dictionary<string, GatherDto>();
            var positions = _positions; _positions = null;

            var monuments = _pendingMonuments;
            var body = JsonConvert.SerializeObject(new Batch { Kills = kills, Connections = conns, Usage = usage, Gather = gather, Positions = positions, Monuments = monuments });
            var url = ManagerUrl() + "/api/telemetry/" + _config.ServerId;
            var headers = new Dictionary<string, string> { ["Content-Type"] = "application/json", ["X-Api-Key"] = _config.ApiKey };

            _sending = true;
            _sendStarted = now;
            var id = ++_sendId;
            webrequest.Enqueue(url, body, (code, response) =>
            {
                if (id != _sendId) return; // the watchdog already gave up on this one
                _sending = false;
                if (code == 200)
                {
                    if (monuments != null && ReferenceEquals(monuments, _pendingMonuments)) _pendingMonuments = null; // delivered once
                    if (!_delivered) Puts($"Reports are reaching the manager at {ManagerUrl()} ✓");
                    else if (_failures > 0) Puts("Connection to the manager restored ✓");
                    _delivered = true;
                    _failures = 0;
                    _warnedAuth = false;
                    return;
                }
                _failures++;
                if (code == 401)
                {
                    if (!_warnedAuth) PrintError("Not reporting: the manager rejected the API key. Reinstall the plugin from the Telemetry page.");
                    _warnedAuth = true;
                }
                else
                {
                    LogFailure(code == 0 ? $"can't connect to {url}" : $"{url} answered HTTP {code}");
                }
                // Back off up to a minute and keep events so nothing is lost while the manager is down.
                _nextAttempt = UnityEngine.Time.realtimeSinceStartup + Mathf.Min(60f, 5f * _failures);
                if (code != 401)
                {
                    kills.AddRange(_kills); _kills = kills;
                    if (_kills.Count > MaxQueuedKills) _kills.RemoveRange(0, _kills.Count - MaxQueuedKills);
                    conns.AddRange(_connections); _connections = conns;
                    foreach (var u in usage)
                    {
                        var merged = Usage(u.Id, u.Weapon);
                        merged.Shots += u.Shots;
                        merged.Hits += u.Hits;
                    }
                    foreach (var g in gather)
                    {
                        var merged = GatherRow(g.Id, g.Code, g.Source);
                        if (merged.Item == null) merged.Item = g.Item;
                        merged.Amount += g.Amount;
                        merged.Times += g.Times;
                    }
                }
            }, this, RequestMethod.POST, headers, 15f);
        }

        #endregion

        #region Helpers

        private static bool IsRealPlayer(BasePlayer p)
        {
            if (p == null || p.IsNpc) return false;
            var id = p.UserIDString;
            return id != null && id.Length == 17 && id.StartsWith("7656");
        }

        private static string NpcName(BasePlayer npc)
        {
            var prefab = npc.ShortPrefabName ?? "";
            if (prefab.Contains("heavy")) return "Heavy Scientist";
            if (prefab.Contains("tunneldweller")) return "Tunnel Dweller";
            if (prefab.Contains("underwaterdweller")) return "Underwater Dweller";
            if (prefab.Contains("scarecrow")) return "Scarecrow";
            if (prefab.Contains("bandit")) return "Bandit Guard";
            if (prefab.Contains("peacekeeper")) return "Outpost Guard";
            return "Scientist";
        }

        private static string AnimalName(string prefab)
        {
            string name;
            if (!string.IsNullOrEmpty(prefab) && AnimalNames.TryGetValue(prefab, out name)) return name;
            return PrettyPrefab(prefab);
        }

        private static string WeaponName(HitInfo info)
        {
            var item = info.Weapon != null ? info.Weapon.GetItem() : null;
            if (item != null) return ItemName(item, null);
            if (info.WeaponPrefab != null) return PrettyPrefab(info.WeaponPrefab.ShortPrefabName);
            return "";
        }

        private static string ItemName(Item item, string fallback)
        {
            if (item != null && item.info != null && item.info.displayName != null && !string.IsNullOrEmpty(item.info.displayName.english))
                return item.info.displayName.english;
            return PrettyPrefab(fallback);
        }

        private static string DamageName(string damageType)
        {
            switch (damageType)
            {
                case "Fall": return "Fall";
                case "Cold": case "ColdExposure": return "Cold";
                case "Heat": return "Heat";
                case "Drowned": return "Drowned";
                case "Radiation": case "RadiationExposure": return "Radiation";
                case "Hunger": return "Hunger";
                case "Thirst": return "Thirst";
                case "Bleeding": return "Bleeding";
                case "Poison": return "Poison";
                case "Suicide": return "Suicide";
                case "Explosion": return "Explosion";
                case "Bite": return "Bite";
                default: return damageType ?? "";
            }
        }

        private static string PrettyPrefab(string prefab)
        {
            if (string.IsNullOrEmpty(prefab)) return "";
            var s = prefab.Replace(".entity", "").Replace(".deployed", "").Replace("_deployed", "").Replace('_', ' ').Replace('.', ' ').Trim();
            return s.Length == 0 ? prefab : char.ToUpperInvariant(s[0]) + s.Substring(1);
        }

        #endregion
    }
}
