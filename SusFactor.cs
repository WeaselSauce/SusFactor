using Oxide.Core;
using Oxide.Core.Libraries.Covalence;
using Oxide.Core.Plugins;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using UnityEngine;

/*
====================================================================================================
SUSFACTOR PLUGIN DOCUMENTATION
====================================================================================================

HOW IT WORKS:
SusFactor is a statistical anti-cheat plugin that monitors player aiming behavior to detect anomalies
that are common with aim-assistance cheats. It does not look for specific cheats, but rather for
player performance that is statistically improbable compared to the server's population.

The core logic revolves around two key metrics, tracked on a per-weapon basis:

1.  HEADSHOT RATIO (HSR):
    - The plugin calculates the average HSR for each weapon type across all players on the server.
    - It then compares an individual's HSR for a specific weapon to the server's average for that same weapon.
    - If a player's HSR is a statistically significant number of standard deviations above the server
      average, their suspicion level increases. This detects impossibly high accuracy.

2.  AIM SMOOTHNESS (AIM ANGLE DELTA):
    - This is the "secret sauce" for detecting aimbots. Human aim has a natural, slight "wobble"
      or inconsistency. Aimbots are often robotically smooth.
    - The plugin measures the average angle change (the "wobble") between a player's shots.
    - It then calculates the standard deviation of this wobble for each player and compares it to the
      server's average aim wobble for that weapon.
    - If a player's aim is significantly *smoother* (i.e., has a lower standard deviation) than the
      server average, it's a major red flag, and their suspicion level increases.

SUSPICION LEVEL:
- This is a score that goes up when a player trips one of the checks above. The more improbable their
  stats, the more the score increases.
- The score decays over time, so a player must be *consistently* suspicious to trigger an alert.
- When the score passes the "Notification Threshold", an action is taken (admin notification, ban, etc.).

---

CONFIGURATION OPTIONS:

- Headshot Ratio Threshold (Std Devs):
  How many standard deviations above the server average a player's HSR must be to trigger suspicion.
  Lower is more strict. (Default: 3.0)

- Smooth Aim Threshold (Std Devs):
  How many standard deviations *smoother* than the server average a player's aim must be to trigger
  suspicion. Lower is more strict. (Default: 2.5)

- Suspicion Decay Rate (Per Minute):
  How much suspicion score a player loses every minute. Prevents a single lucky streak from flagging them.
  (Default: 0.5)

- Notification/Action Threshold:
  The suspicion score at which the plugin will notify admins or take other actions. (Default: 10.0)

- Notification Cooldown (Minutes):
  How long to wait before sending another notification for the same player to prevent spam. (Default: 10)

- Minimum Hits Per Weapon For Evaluation:
  A player must have at least this many hits with a specific weapon before the plugin will start
  calculating suspicion for them with that weapon. (Default: 30)

- Minimum Hits For Baseline Inclusion:
  A player must have at least this many hits with a weapon for their stats to be included in the
  server's average (the baseline). This ensures the baseline is accurate. (Default: 50)

- Baseline Update Interval (Minutes):
  How often the plugin recalculates the server-wide average stats. (Default: 60)

- Log Detections To Console:
  If true, prints detection alerts to the F1 server console. (Default: true)

- Notify Admins In Game Chat:
  If true, sends a message to online admins when a player is flagged. (Default: true)

- Action On Detection (None, Notify, Ban, Both):
  What to do when a player is flagged.
    - None: Does nothing beyond the in-game/console log.
    - Notify: Sends a detailed alert to your Discord webhook.
    - Ban: Bans the player from the server.
    - Both: Sends a Discord notification and then bans the player.
  (Default: "Notify")

- Discord Webhook URL:
  The URL for your Discord webhook to receive alerts.

- Ban Reason:
  The message shown to a player if they are banned by the plugin.

- Exclude Friendly Fire (Team/Clan):
  If true, hits on teammates or clan members will not be counted. (Default: true)

- Minimum Distance For Aim Checks (Meters):
  Aim-related stats will not be recorded for hits closer than this distance, as point-blank aim
  is not indicative of skill. (Default: 2.0)

====================================================================================================
*/

namespace Oxide.Plugins
{
    [Info("SusFactor", "KaboomRust", "2.0.5")]
    [Description("Monitors player aim patterns per weapon for statistical outliers, takes automated action, and provides admin/player commands.")]
    public class SusFactor : RustPlugin
    {
        #region Fields

        [PluginReference] private Plugin Clans;

        private const string AdminPermission = "susfactor.admin";
        private StoredData _storedData;
        private readonly Dictionary<ulong, Vector3> _lastPlayerViewAngles = new Dictionary<ulong, Vector3>();
        private readonly Dictionary<ulong, DateTime> _notificationCooldowns = new Dictionary<ulong, DateTime>();
        private PluginConfig _config;

        #endregion

        #region Data Structures V2

        // Main data container
        private class StoredData
        {
            public Dictionary<ulong, PlayerData> Players { get; set; } = new Dictionary<ulong, PlayerData>();
            public ServerBaseline Baseline { get; set; } = new ServerBaseline();
        }

        // Holds stats for a specific weapon
        private class WeaponStats
        {
            public int Hits { get; set; }
            public int Headshots { get; set; }
            public List<float> AimAngleDeltas { get; set; } = new List<float>();
            public float HeadshotRatio => Hits > 0 ? (float)Headshots / Hits : 0f;
        }

        // Overall data for a single player
        private class PlayerData
        {
            public string DisplayName { get; set; }
            public float SuspicionLevel { get; set; }
            public Dictionary<string, WeaponStats> PerWeaponStats { get; set; } = new Dictionary<string, WeaponStats>();
        }

        // Holds the calculated baseline for a specific weapon
        private class WeaponBaseline
        {
            public float AverageHeadshotRatio { get; set; }
            public float StdDevHeadshotRatio { get; set; }
            public float AverageAimAngleDelta { get; set; }
            public float StdDevAimAngleDelta { get; set; }
        }

        // Overall server baseline, containing baselines for each weapon
        private class ServerBaseline
        {
            public Dictionary<string, WeaponBaseline> PerWeaponBaselines { get; set; } = new Dictionary<string, WeaponBaseline>();
            public int TotalPlayersSampled { get; set; }
            public int TotalHitsSampled { get; set; }
        }
        
        #endregion

        #region Configuration

        private class PluginConfig
        {
            [JsonProperty("Headshot Ratio Threshold (Std Devs)")]
            public float HeadshotRatioThreshold { get; set; } = 3.0f;

            [JsonProperty("Smooth Aim Threshold (Std Devs)")]
            public float SmoothAimThreshold { get; set; } = 2.5f;

            [JsonProperty("Suspicion Decay Rate (Per Minute)")]
            public float SuspicionDecayRate { get; set; } = 0.5f;

            [JsonProperty("Notification/Action Threshold")]
            public float NotificationThreshold { get; set; } = 10.0f;

            [JsonProperty("Notification Cooldown (Minutes)")]
            public int NotificationCooldownMinutes { get; set; } = 10;
            
            [JsonProperty("Minimum Hits Per Weapon For Evaluation")]
            public int MinimumHitsForEvaluation { get; set; } = 30;
            
            [JsonProperty("Minimum Hits For Baseline Inclusion")]
            public int MinimumHitsForBaseline { get; set; } = 50;

            [JsonProperty("Baseline Update Interval (Minutes)")]
            public float BaselineUpdateIntervalMinutes { get; set; } = 60.0f;

            [JsonProperty("Log Detections To Console")]
            public bool LogToConsole { get; set; } = true;

            [JsonProperty("Notify Admins In Game Chat")]
            public bool NotifyAdminsInChat { get; set; } = true;

            [JsonProperty("Action On Detection (None, Notify, Ban, Both)")]
            public string ActionOnDetection { get; set; } = "Notify";

            [JsonProperty("Discord Webhook URL")]
            public string DiscordWebhookUrl { get; set; } = "";

            [JsonProperty("Ban Reason")]
            public string BanReason { get; set; } = "Banned by SusFactor for suspicious activity.";

            [JsonProperty("Exclude Friendly Fire (Team/Clan)")]
            public bool ExcludeFriendlyFire { get; set; } = true;

            [JsonProperty("Minimum Distance For Aim Checks (Meters)")]
            public float MinimumDistanceForChecks { get; set; } = 2.0f;
        }

        protected override void LoadDefaultConfig()
        {
            _config = new PluginConfig();
            Config.WriteObject(_config, true);
        }

        private void LoadConfigValues() => _config = Config.ReadObject<PluginConfig>();

        #endregion

        #region Hooks

        private void Init()
        {
            LoadConfigValues();
            permission.RegisterPermission(AdminPermission, this);
            _storedData = Interface.Oxide.DataFileSystem.ReadObject<StoredData>(Name);

            timer.Every(60f, () =>
            {
                foreach (var playerData in _storedData.Players.Values)
                {
                    if (playerData.SuspicionLevel > 0)
                    {
                        playerData.SuspicionLevel = Mathf.Max(0, playerData.SuspicionLevel - _config.SuspicionDecayRate);
                    }
                }
            });

            timer.Every(_config.BaselineUpdateIntervalMinutes * 60, RecalculateServerBaseline);
        }

        private void Unload() => SaveData();

        private void OnPlayerConnected(BasePlayer player)
        {
            if (player == null) return;
            _lastPlayerViewAngles[player.userID] = player.eyes.HeadForward();
        }

        private void OnPlayerDisconnected(BasePlayer player)
        {
            if (player == null) return;
            _lastPlayerViewAngles.Remove(player.userID);
            _notificationCooldowns.Remove(player.userID);
        }

        private void OnPlayerAttack(BasePlayer attacker, HitInfo hitInfo)
        {
            if (attacker == null || hitInfo == null || attacker.IsNpc) return;

            var victim = hitInfo.HitEntity as BasePlayer;
            if (victim == null || victim.IsNpc || victim == attacker) return;
            
            var weaponItem = hitInfo.Weapon?.GetItem();
            if (weaponItem == null) return;
            string weaponShortName = weaponItem.info.shortname;

            if (_config.ExcludeFriendlyFire)
            {
                if (attacker.currentTeam != 0 && attacker.currentTeam == victim.currentTeam) return;
                if (Clans != null && (bool)Clans.Call("IsClanMember", attacker.UserIDString, victim.UserIDString)) return;
            }

            var playerData = GetPlayerData(attacker);
            if (!playerData.PerWeaponStats.TryGetValue(weaponShortName, out var weaponStats))
            {
                weaponStats = new WeaponStats();
                playerData.PerWeaponStats[weaponShortName] = weaponStats;
            }
            
            weaponStats.Hits++;
            if (hitInfo.isHeadshot) weaponStats.Headshots++;

            var distance = Vector3.Distance(attacker.transform.position, victim.transform.position);
            if (distance < _config.MinimumDistanceForChecks)
            {
                _lastPlayerViewAngles[attacker.userID] = attacker.eyes.HeadForward();
                return;
            }

            var currentViewAngle = attacker.eyes.HeadForward();
            if (_lastPlayerViewAngles.TryGetValue(attacker.userID, out var lastViewAngle))
            {
                var angleDelta = Vector3.Angle(lastViewAngle, currentViewAngle);
                if (angleDelta > 0.01f)
                {
                    weaponStats.AimAngleDeltas.Add(angleDelta);
                    if (weaponStats.AimAngleDeltas.Count > 200)
                    {
                        weaponStats.AimAngleDeltas.RemoveAt(0);
                    }
                }
            }
            _lastPlayerViewAngles[attacker.userID] = currentViewAngle;

            if (weaponStats.Hits > _config.MinimumHitsForEvaluation)
            {
                CheckForAnomalies(attacker, playerData, weaponShortName, weaponStats);
            }
        }

        #endregion

        #region Commands
        
        [ChatCommand("sus")]
        private void CmdSus(BasePlayer player, string command, string[] args)
        {
            IPlayer targetPlayer;
            bool isSelfCheck = args.Length == 0;

            if (isSelfCheck)
            {
                targetPlayer = player.IPlayer;
            }
            else
            {
                var targetIdentifier = string.Join(" ", args);
                targetPlayer = covalence.Players.FindPlayer(targetIdentifier);
            }
            
            if (targetPlayer == null)
            {
                SendReply(player, "Player not found.");
                return;
            }

            var statsOutput = GetPlayerStatString(targetPlayer, true, !isSelfCheck);
            SendReply(player, statsOutput);
            
            if(isSelfCheck)
            {
                SendReply(player, "<color=#AAAAAA>Tip: Use</color> <color=#00FFFF>/sus <player name></color> <color=#AAAAAA>to check another player.</color>");
            }
        }

        [ConsoleCommand("susfactor.status")]
        private void CmdSusFactorStatus(ConsoleSystem.Arg arg)
        {
            if (arg.Player() != null && !arg.Player().IsAdmin)
            {
                arg.ReplyWith("You do not have permission to use this command.");
                return;
            }

            var baseline = _storedData.Baseline;
            var sb = new StringBuilder();
            sb.AppendLine("--- SusFactor Status ---");
            sb.AppendLine($"Baseline calculated from {baseline.TotalPlayersSampled} players & {string.Format("{0:N0}", baseline.TotalHitsSampled)} total hits across {baseline.PerWeaponBaselines.Count} weapon types.");
            
            var topWeapons = baseline.PerWeaponBaselines.OrderByDescending(kvp => kvp.Value.AverageHeadshotRatio).Take(5);

            sb.AppendLine("Top 5 Weapons by Avg HSR:");
            foreach (var weapon in topWeapons)
            {
                sb.AppendLine($" - {weapon.Key}: HSR {weapon.Value.AverageHeadshotRatio:P2}, AimDelta {weapon.Value.AverageAimAngleDelta:F2}° (StdDev: {weapon.Value.StdDevAimAngleDelta:F2})");
            }
            sb.AppendLine("--------------------------------");

            arg.ReplyWith(sb.ToString());
        }

        [ConsoleCommand("susfactor.check")]
        private void CmdSusFactorCheck(ConsoleSystem.Arg arg)
        {
            if (arg.Player() != null && !permission.UserHasPermission(arg.Player().UserIDString, AdminPermission))
            {
                arg.ReplyWith("You do not have permission to use this command.");
                return;
            }

            if (!arg.HasArgs())
            {
                arg.ReplyWith("Usage: susfactor.check <player name or steamID>");
                return;
            }

            var player = covalence.Players.FindPlayer(arg.GetString(0));
            if (player == null)
            {
                arg.ReplyWith("Player not found.");
                return;
            }
            
            var statsOutput = GetPlayerStatString(player, false, true);
            arg.ReplyWith(statsOutput);
        }

        #endregion

        #region Core Logic

        private void CheckForAnomalies(BasePlayer player, PlayerData playerData, string weaponShortName, WeaponStats weaponStats)
        {
            if (!_storedData.Baseline.PerWeaponBaselines.TryGetValue(weaponShortName, out var weaponBaseline))
            {
                return;
            }

            float suspicionIncrease = 0f;
            string reason = "";

            if (weaponBaseline.StdDevHeadshotRatio > 0)
            {
                float headshotRatioStdDevs = (weaponStats.HeadshotRatio - weaponBaseline.AverageHeadshotRatio) / weaponBaseline.StdDevHeadshotRatio;
                if (headshotRatioStdDevs > _config.HeadshotRatioThreshold)
                {
                    suspicionIncrease += headshotRatioStdDevs;
                    reason += $"High HSR ({weaponShortName}) ";
                }
            }

            if (weaponStats.AimAngleDeltas.Count > 20 && weaponBaseline.StdDevAimAngleDelta > 0)
            {
                var playerAimStdDev = CalculateStandardDeviation(weaponStats.AimAngleDeltas);
                float smoothAimStdDevs = (weaponBaseline.AverageAimAngleDelta - playerAimStdDev) / weaponBaseline.StdDevAimAngleDelta;
                if (smoothAimStdDevs > _config.SmoothAimThreshold)
                {
                    suspicionIncrease += smoothAimStdDevs;
                    reason += $"Smooth Aim ({weaponShortName}) ";
                }
            }

            if (suspicionIncrease > 0)
            {
                playerData.SuspicionLevel += suspicionIncrease;
                if (playerData.SuspicionLevel > _config.NotificationThreshold)
                {
                    HandleDetection(player, playerData, reason.Trim());
                }
            }
        }

        private void HandleDetection(BasePlayer suspect, PlayerData playerData, string reason)
        {
            if (_notificationCooldowns.TryGetValue(suspect.userID, out var lastNotificationTime))
            {
                if (DateTime.UtcNow < lastNotificationTime.AddMinutes(_config.NotificationCooldownMinutes)) return;
            }

            var message = $"[SusFactor] Player {suspect.displayName} ({suspect.userID}) flagged with suspicion level {playerData.SuspicionLevel:F1}. Reason: {reason}";

            if (_config.LogToConsole) Puts(message);

            if (_config.NotifyAdminsInChat)
            {
                foreach (var admin in covalence.Players.Connected.Where(p => permission.UserHasPermission(p.Id, AdminPermission)))
                {
                    admin.Message(message, "[<color=#FF0000>SusFactor</color>]");
                }
            }
            
            _notificationCooldowns[suspect.userID] = DateTime.UtcNow;

            switch (_config.ActionOnDetection.ToLower())
            {
                case "notify": SendDiscordNotification(suspect, playerData.SuspicionLevel, reason); break;
                case "ban": BanPlayer(suspect); break;
                case "both":
                    SendDiscordNotification(suspect, playerData.SuspicionLevel, reason);
                    BanPlayer(suspect);
                    break;
            }
            
            playerData.SuspicionLevel = _config.NotificationThreshold / 2;
        }

        private void RecalculateServerBaseline()
        {
            SaveData();

            var newBaseline = new ServerBaseline();
            var weaponDataCache = new Dictionary<string, Tuple<List<float>, List<float>>>();
            
            var validPlayers = _storedData.Players.Values
                .Where(p => p.PerWeaponStats.Any()).ToList();
            
            newBaseline.TotalPlayersSampled = validPlayers.Count;

            foreach (var pData in validPlayers)
            {
                newBaseline.TotalHitsSampled += pData.PerWeaponStats.Values.Sum(ws => ws.Hits);

                foreach (var weaponEntry in pData.PerWeaponStats)
                {
                    var weaponName = weaponEntry.Key;
                    var weaponStats = weaponEntry.Value;

                    if (weaponStats.Hits < _config.MinimumHitsForBaseline) continue;

                    if (!weaponDataCache.ContainsKey(weaponName))
                    {
                        weaponDataCache[weaponName] = new Tuple<List<float>, List<float>>(new List<float>(), new List<float>());
                    }
                    
                    weaponDataCache[weaponName].Item1.Add(weaponStats.HeadshotRatio);
                    weaponDataCache[weaponName].Item2.AddRange(weaponStats.AimAngleDeltas);
                }
            }

            foreach (var cacheEntry in weaponDataCache)
            {
                var weaponName = cacheEntry.Key;
                var hsrList = cacheEntry.Value.Item1;
                var aimDeltaList = cacheEntry.Value.Item2;

                if (hsrList.Count < 5) continue;

                var weaponBaseline = new WeaponBaseline();
                weaponBaseline.AverageHeadshotRatio = hsrList.Average();
                weaponBaseline.StdDevHeadshotRatio = CalculateStandardDeviation(hsrList);
                
                if (aimDeltaList.Any())
                {
                    weaponBaseline.AverageAimAngleDelta = aimDeltaList.Average();
                    weaponBaseline.StdDevAimAngleDelta = CalculateStandardDeviation(aimDeltaList);
                }
                
                newBaseline.PerWeaponBaselines[weaponName] = weaponBaseline;
            }

            _storedData.Baseline = newBaseline;
            Puts($"Server baseline recalculated. Tracking {newBaseline.PerWeaponBaselines.Count} weapon types from {newBaseline.TotalPlayersSampled} players.");
            SaveData();
        }

        #endregion

        #region Helper Methods
        
        private string GetPlayerStatString(IPlayer player, bool useColor, bool isOtherPlayerCheck)
        {
            if (!_storedData.Players.TryGetValue(ulong.Parse(player.Id), out var playerData))
            {
                return useColor ? $"No aim data found for player <color=#FFD700>{player.Name}</color>." : $"No aim data found for player {player.Name}.";
            }
            
            var baseline = _storedData.Baseline;
            var sb = new StringBuilder();

            // -- Color Definitions --
            var cGrey = useColor ? "<color=#555555>" : "";
            var cLtGrey = useColor ? "<color=#AAAAAA>" : "";
            var cCyan = useColor ? "<color=#00FFFF>" : "";
            var cYellow = useColor ? "<color=#FFFF88>" : "";
            var cGreen = useColor ? "<color=#88FF88>" : "";
            var cRed = useColor ? "<color=#FF8888>" : "";
            var cEnd = useColor ? "</color>" : "";
            var cWarning = useColor ? "<color=#FFD700>" : "";


            // Add a global warning if the server baseline is very new
            if (baseline.TotalPlayersSampled < 10)
            {
                var warningText = "[SERVER IS CALIBRATING - STATS MAY BE INACCURATE]";
                sb.AppendLine($"{cWarning}{warningText}{cEnd}");
            }

            string suspicionColor;
            if (playerData.SuspicionLevel < _config.NotificationThreshold * 0.5f) suspicionColor = cGreen;
            else if (playerData.SuspicionLevel < _config.NotificationThreshold * 0.9f) suspicionColor = cYellow;
            else suspicionColor = cRed;
            
            sb.AppendLine($"{cGrey}---{cEnd} Aim Stats for {cCyan}{playerData.DisplayName}{cEnd} {cGrey}---{cEnd}");
            sb.AppendLine($" {cLtGrey}Suspicion:{cEnd} {suspicionColor}{playerData.SuspicionLevel:F2}{cEnd} / {_config.NotificationThreshold:F1}");
            
            var topWeapons = playerData.PerWeaponStats.OrderByDescending(kvp => kvp.Value.Hits).Take(3);
            if (!topWeapons.Any())
            {
                sb.AppendLine(" No weapon data recorded yet.");
            }

            foreach (var weaponEntry in topWeapons)
            {
                var weaponName = weaponEntry.Key;
                var stats = weaponEntry.Value;

                sb.AppendLine($"{cGrey}--- {cCyan}{weaponName}{cEnd} ({stats.Hits} hits) ---{cEnd}");

                // Hide stats for other players if data is insufficient
                if (isOtherPlayerCheck && stats.Hits < 50)
                {
                    sb.AppendLine($"  {cLtGrey}Not enough data recorded for this weapon.{cEnd}");
                    continue;
                }

                baseline.PerWeaponBaselines.TryGetValue(weaponName, out var weaponBaseline);
                string serverHSR = weaponBaseline != null ? $"(Avg: {weaponBaseline.AverageHeadshotRatio:P2})" : "(Calibrating...)";
                string serverAim = weaponBaseline != null ? $"(Avg: {weaponBaseline.AverageAimAngleDelta:F2}°)" : "(Calibrating...)";

                sb.AppendLine($"  {cLtGrey}HS Ratio:{cEnd} {cCyan}{stats.HeadshotRatio:P2}{cEnd} {cLtGrey}{serverHSR}{cEnd}");
                if (stats.AimAngleDeltas.Any())
                {
                    var playerAimAvg = stats.AimAngleDeltas.Average();
                    var playerAimStdDev = CalculateStandardDeviation(stats.AimAngleDeltas);
                    var stdDevText = useColor ? $" (StdDev: {playerAimStdDev:F2})" : $", StdDev: {playerAimStdDev:F2}";
                    sb.AppendLine($"  {cLtGrey}Aim Delta:{cEnd} {cCyan}{playerAimAvg:F2}°{stdDevText}{cEnd} {cLtGrey}{serverAim}{cEnd}");
                }
            }

            sb.AppendLine($"{cGrey}-------------------------------------{cEnd}");
            return sb.ToString();
        }

        private void SendDiscordNotification(BasePlayer suspect, float suspicionLevel, string reason)
        {
            if (string.IsNullOrEmpty(_config.DiscordWebhookUrl)) return;

            var embed = new
            {
                title = "SusFactor Alert",
                description = $"Player **{suspect.displayName}** has been flagged for suspicious activity.",
                color = 15158332,
                fields = new[]
                {
                    new { name = "SteamID", value = suspect.userID.ToString(), inline = true },
                    new { name = "Suspicion Level", value = $"{suspicionLevel:F1}", inline = true },
                    new { name = "Reason", value = reason, inline = false }
                },
                footer = new { text = $"SusFactor | {DateTime.UtcNow:R}" }
            };

            var payload = new { embeds = new[] { embed } };
            var jsonPayload = JsonConvert.SerializeObject(payload);

            webrequest.Enqueue(_config.DiscordWebhookUrl, jsonPayload, null, this, Core.Libraries.RequestMethod.POST, new Dictionary<string, string>
            {
                ["Content-Type"] = "application/json"
            });
        }

        private void BanPlayer(BasePlayer suspect)
        {
            if (!suspect.IsConnected) return;
            suspect.IPlayer.Ban(_config.BanReason, TimeSpan.Zero);
            Puts($"Banned player {suspect.displayName} ({suspect.userID}). Reason: {_config.BanReason}");
        }

        private PlayerData GetPlayerData(BasePlayer player)
        {
            if (!_storedData.Players.TryGetValue(player.userID, out var playerData))
            {
                playerData = new PlayerData { DisplayName = player.displayName };
                _storedData.Players[player.userID] = playerData;
            }
            playerData.DisplayName = player.displayName;
            return playerData;
        }

        private void SaveData() => Interface.Oxide.DataFileSystem.WriteObject(Name, _storedData);

        private float CalculateStandardDeviation(IEnumerable<float> values)
        {
            var valueList = values.ToList();
            if (valueList.Count < 2) return 0;
            float avg = valueList.Average();
            return (float)Math.Sqrt(valueList.Average(v => Math.Pow(v - avg, 2)));
        }

        #endregion
    }
}
