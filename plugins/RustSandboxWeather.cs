// Rust Sandbox — Weather
// Random weather using Rust's own weather system: it only changes how often each kind of weather comes up
// (clear, overcast, fog, rain, storm, dust), so the sky, clouds and fades look exactly like vanilla.
//   /weather                 everyone: what the weather is now and what's coming
//   /weather <kind> [min]    admin: clear, overcast, fog, rain, storm or dust right now (for 20 minutes by default)
//   /weather random          admin: back to random weather
//   /weather on | off        admin: turn the plugin on or off (off = Rust's own weather)
// RCON: sandboxweather.status (JSON), sandboxweather.now <kind|random> [minutes]

using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Oxide.Core;
using UnityEngine;

namespace Oxide.Plugins
{
    [Info("Rust Sandbox Weather", "RustSandbox", "1.0.0")]
    [Description("Random weather with a simple on/off, a weather mix, a forecast and a 'change it now' command.")]
    public class RustSandboxWeather : RustPlugin
    {
        private const string AdminPerm = "rustsandboxweather.admin";

        private static readonly string[] Kinds = { "Clear", "Overcast", "Fog", "Rain", "Storm", "Dust" };

        // How often each kind of weather comes up, in % (Rust picks a new weather roughly every 45 minutes of play).
        private static readonly Dictionary<string, Dictionary<string, float>> Mixes = new Dictionary<string, Dictionary<string, float>>(StringComparer.OrdinalIgnoreCase)
        {
            ["MostlyClear"] = Mix(70, 12, 5, 8, 3, 2),
            ["Varied"] = Mix(40, 18, 12, 15, 8, 7),
            ["Stormy"] = Mix(15, 20, 10, 30, 20, 5),
        };

        private static Dictionary<string, float> Mix(float clear, float overcast, float fog, float rain, float storm, float dust) =>
            new Dictionary<string, float> { ["Clear"] = clear, ["Overcast"] = overcast, ["Fog"] = fog, ["Rain"] = rain, ["Storm"] = storm, ["Dust"] = dust };

        #region Config

        private class PluginConfig
        {
            /// <summary>Off = Rust's own weather.</summary>
            public bool Enabled = true;
            /// <summary>Vanilla, MostlyClear, Varied, Stormy or Custom.</summary>
            public string Mix = "Varied";
            /// <summary>Used when Mix is Custom: % for each kind of weather.</summary>
            [JsonProperty(ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public Dictionary<string, float> Custom = Mix(40, 18, 12, 15, 8, 7);
            /// <summary>Tell players in chat when the weather starts to change.</summary>
            public bool Announce = true;
            /// <summary>How long "change it now" lasts before random weather takes over again.</summary>
            public int ForceMinutes = 20;
        }

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
            if (_config.Custom == null) _config.Custom = Mix(40, 18, 12, 15, 8, 7);
            foreach (var k in Kinds) if (!_config.Custom.ContainsKey(k)) _config.Custom[k] = 0;
            if (_config.Mix != "Vanilla" && _config.Mix != "Custom" && !Mixes.ContainsKey(_config.Mix ?? "")) _config.Mix = "Varied";
            _config.ForceMinutes = Math.Max(1, _config.ForceMinutes);
            SaveConfig();
        }

        protected override void SaveConfig() => Config.WriteObject(_config, true);

        #endregion

        #region Weather

        private Dictionary<string, float> _vanilla;
        private string _forced;
        private DateTime _forcedUntil;
        private string _lastTarget;
        private Timer _watch;

        private static Climate Climate => SingletonComponent<Climate>.Instance;

        private static Dictionary<string, float> Read()
        {
            var w = Climate.Weather;
            return new Dictionary<string, float>
            {
                ["Clear"] = w.ClearChance, ["Overcast"] = w.OvercastChance, ["Fog"] = w.FogChance,
                ["Rain"] = w.RainChance, ["Storm"] = w.StormChance, ["Dust"] = w.DustChance,
            };
        }

        /// <summary>Sets Rust's weather chances (0-1, relative to each other) and sends them to players.</summary>
        private static void Write(Dictionary<string, float> chances, bool percent)
        {
            var w = Climate.Weather;
            Func<string, float> get = k =>
            {
                float v;
                if (!chances.TryGetValue(k, out v)) v = 0;
                return UnityEngine.Mathf.Clamp01(percent ? v / 100f : v);
            };
            w.ClearChance = get("Clear");
            w.OvercastChance = get("Overcast");
            w.FogChance = get("Fog");
            w.RainChance = get("Rain");
            w.StormChance = get("Storm");
            w.DustChance = get("Dust");
            // All zero would always be clear anyway; keep it explicit.
            if (w.ClearChance + w.OvercastChance + w.FogChance + w.RainChance + w.StormChance + w.DustChance <= 0) w.ClearChance = 1;
            ServerMgr.SendReplicatedVars("weather.");
        }

        private Dictionary<string, float> ActiveMix()
        {
            if (_config.Mix == "Custom") return _config.Custom;
            Dictionary<string, float> mix;
            return Mixes.TryGetValue(_config.Mix ?? "", out mix) ? mix : null; // null = Vanilla
        }

        private void Apply()
        {
            if (Climate == null || _vanilla == null) return;
            if (_forced != null && DateTime.UtcNow >= _forcedUntil) _forced = null;

            if (_forced != null)
            {
                // Only one kind of weather can come up, so Rust switches to it straight away.
                EndRainGrace();
                Write(Kinds.ToDictionary(k => k, k => k == _forced ? 1f : 0f), false);
                return;
            }
            var mix = _config.Enabled ? ActiveMix() : null;
            if (mix == null) Write(_vanilla, false);
            else Write(mix, true);
        }

        // Rust keeps rain and storms away for a while after a wipe; asking for rain now ends that early.
        private static void EndRainGrace()
        {
            if (!ConVar.Weather.rain_grace_active) return;
            Climate.RainGraceStartTicks = 0;
            ConVar.Weather.rain_grace_active = false;
        }

        private void Force(string kind, int minutes)
        {
            _forced = kind;
            _forcedUntil = DateTime.UtcNow.AddMinutes(Math.Max(1, minutes));
            Apply();
            if (_config.Announce) Broadcast("Changing", Lang("Kind." + kind, null));
        }

        private void Unforce()
        {
            _forced = null;
            Apply();
        }

        private static string Kind(WeatherPreset p) => p == null ? null : p.Type.ToString();

        /// <summary>Minutes of real time until Rust next starts fading to new weather (it rolls every 18 in-game hours).</summary>
        private static double MinutesToNextChange()
        {
            var sky = TOD_Sky.Instance;
            if (sky == null) return -1;
            const long period = 648000000000L; // 18 in-game hours
            var hoursLeft = (period - sky.Cycle.Ticks % period) / 36000000000.0;
            var dayMinutes = sky.Components.Time != null ? sky.Components.Time.DayLengthInMinutes : 60f;
            return Math.Round(hoursLeft / 24.0 * dayMinutes, 1);
        }

        private void Watch()
        {
            if (_forced != null && DateTime.UtcNow >= _forcedUntil)
            {
                _forced = null;
                Apply();
            }
            if (Climate == null) return;
            var target = Kind(Climate.WeatherStateTarget);
            if (target == null || target == _lastTarget) return;
            var first = _lastTarget == null;
            _lastTarget = target;
            if (first || !_config.Enabled || !_config.Announce || _forced != null) return;
            if (target != Kind(Climate.WeatherStatePrevious)) Broadcast("Changing", Lang("Kind." + target, null));
        }

        #endregion

        #region Hooks

        private void Init() => permission.RegisterPermission(AdminPerm, this);

        private void OnServerInitialized()
        {
            if (Climate == null) { PrintError("No weather system found."); return; }
            _vanilla = Read();
            Apply();
            _lastTarget = Kind(Climate.WeatherStateTarget);
            _watch = timer.Every(20f, Watch);
        }

        private void Unload()
        {
            _watch?.Destroy();
            if (Climate != null && _vanilla != null) Write(_vanilla, false);
        }

        #endregion

        #region Commands

        [ChatCommand("weather")]
        private void WeatherCommand(BasePlayer player, string command, string[] args)
        {
            if (args.Length == 0) { Reply(player, Forecast(player.UserIDString)); return; }
            if (!IsAdmin(player)) { Reply(player, Lang("NotAdmin", player.UserIDString)); return; }
            int minutes;
            Reply(player, Run(args[0], args.Length > 1 && int.TryParse(args[1], out minutes) ? minutes : _config.ForceMinutes, player.UserIDString));
        }

        [ConsoleCommand("sandboxweather.now")]
        private void NowConsole(ConsoleSystem.Arg arg)
        {
            if (arg.Connection != null && !IsAdmin(arg.Player())) return;
            arg.ReplyWith(Run(arg.GetString(0, "random"), arg.GetInt(1, _config.ForceMinutes), null));
        }

        [ConsoleCommand("sandboxweather.status")]
        private void StatusConsole(ConsoleSystem.Arg arg)
        {
            if (arg.Connection != null && !IsAdmin(arg.Player())) return;
            if (Climate == null) { arg.ReplyWith("{}"); return; }
            var status = new Dictionary<string, object>
            {
                ["version"] = Version.ToString(),
                ["enabled"] = _config.Enabled,
                ["mix"] = _config.Mix,
                ["previous"] = Kind(Climate.WeatherStatePrevious),
                ["current"] = Kind(Climate.WeatherStateTarget),
                ["next"] = Kind(Climate.WeatherStateNext),
                ["blend"] = Math.Round(Climate.WeatherStateBlend, 2),
                ["nextChangeMinutes"] = MinutesToNextChange(),
                ["forced"] = _forced,
                ["forcedMinutesLeft"] = _forced == null ? 0 : Math.Max(0, Math.Round((_forcedUntil - DateTime.UtcNow).TotalMinutes, 1)),
                ["rainGrace"] = ConVar.Weather.rain_grace_active,
                ["chances"] = Read().ToDictionary(kv => kv.Key, kv => Math.Round(kv.Value, 3)),
                ["vanilla"] = _vanilla == null ? null : _vanilla.ToDictionary(kv => kv.Key, kv => Math.Round(kv.Value, 3)),
            };
            arg.ReplyWith(JsonConvert.SerializeObject(status));
        }

        private string Run(string what, int minutes, string userId)
        {
            what = (what ?? "").Trim();
            if (string.Equals(what, "on", StringComparison.OrdinalIgnoreCase) || string.Equals(what, "off", StringComparison.OrdinalIgnoreCase))
            {
                _config.Enabled = what.Equals("on", StringComparison.OrdinalIgnoreCase);
                SaveConfig();
                Apply();
                return Lang(_config.Enabled ? "TurnedOn" : "TurnedOff", userId);
            }
            if (string.Equals(what, "random", StringComparison.OrdinalIgnoreCase) || string.Equals(what, "auto", StringComparison.OrdinalIgnoreCase))
            {
                Unforce();
                return Lang("BackToRandom", userId);
            }
            var kind = Kinds.FirstOrDefault(k => k.Equals(what, StringComparison.OrdinalIgnoreCase));
            if (kind == null) return Lang("AdminHelp", userId);
            Force(kind, minutes);
            return string.Format(Lang("Forced", userId), Lang("Kind." + kind, userId), Math.Max(1, minutes));
        }

        private string Forecast(string userId)
        {
            if (Climate == null) return "?";
            var prev = Kind(Climate.WeatherStatePrevious);
            var now = Kind(Climate.WeatherStateTarget);
            var next = Kind(Climate.WeatherStateNext);
            var fading = Climate.WeatherStateBlend < 0.99f && prev != now;
            var text = fading
                ? string.Format(Lang("NowChanging", userId), Lang("Kind." + prev, userId), Lang("Kind." + now, userId))
                : string.Format(Lang("Now", userId), Lang("Kind." + now, userId));
            if (_forced != null)
                return text + "\n" + string.Format(Lang("ForcedFor", userId), Math.Max(1, Math.Ceiling((_forcedUntil - DateTime.UtcNow).TotalMinutes)));
            var mins = MinutesToNextChange();
            if (next != now && mins >= 0) text += "\n" + string.Format(Lang("Next", userId), Lang("Kind." + next, userId), Math.Max(1, Math.Round(mins)));
            else if (mins >= 0) text += "\n" + Lang("Staying", userId);
            return text;
        }

        private bool IsAdmin(BasePlayer player) => player == null || player.IsAdmin || permission.UserHasPermission(player.UserIDString, AdminPerm);

        #endregion

        #region Lang

        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(new Dictionary<string, string>
            {
                ["Prefix"] = "<color=#8fb8de>Weather</color>: ",
                ["Kind.Clear"] = "clear skies",
                ["Kind.Overcast"] = "overcast",
                ["Kind.Fog"] = "fog",
                ["Kind.Rain"] = "rain",
                ["Kind.Storm"] = "a storm",
                ["Kind.Dust"] = "a dusty haze",
                ["Now"] = "Right now: {0}.",
                ["NowChanging"] = "Changing from {0} to {1}.",
                ["Next"] = "Next up: {0} in about {1} min.",
                ["Staying"] = "It should stay like this for a while.",
                ["Changing"] = "The weather is turning: {0} is on the way.",
                ["Forced"] = "Weather set to {0} for {1} min, then back to random.",
                ["ForcedFor"] = "An admin set this; random weather comes back in about {0} min.",
                ["BackToRandom"] = "Back to random weather.",
                ["TurnedOn"] = "Random weather is on.",
                ["TurnedOff"] = "Random weather is off: Rust's own weather is back.",
                ["NotAdmin"] = "Type /weather to see the forecast.",
                ["AdminHelp"] = "/weather clear|overcast|fog|rain|storm|dust [minutes], /weather random, /weather on|off",
            }, this);
        }

        private string Lang(string key, string userId) => lang.GetMessage(key, this, userId);

        private void Reply(BasePlayer player, string text)
        {
            if (player == null) return;
            player.ChatMessage(Lang("Prefix", player.UserIDString) + text);
        }

        private void Broadcast(string key, params object[] args)
        {
            foreach (var p in BasePlayer.activePlayerList)
                Reply(p, string.Format(Lang(key, p.UserIDString), args));
        }

        #endregion
    }
}
