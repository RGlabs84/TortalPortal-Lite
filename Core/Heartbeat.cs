using System.Linq;
using UnityEngine;
using TortalPortalLite.Core.Data;

namespace TortalPortalLite.Core
{
    /// <summary>
    /// One periodic summary - a server log line always, and optionally a matching Discord post - so an
    /// admin (or their community) can confirm TortalPortal Lite is still alive and see who's currently
    /// online, without needing VerboseLogging's full per-event detail turned on. Ticked directly from
    /// Plugin.Update, independent of the subsystem registry - this is a mod-wide concern, not any one
    /// subsystem's (catalog #84's own architecture spec places it exactly here).
    /// </summary>
    public static class Heartbeat
    {
        private static float _uptimeSeconds;
        private static float _timer;

        public static void OnUpdate(float dt)
        {
            _uptimeSeconds += dt;

            bool logHeartbeat = GlobalConfig.HeartbeatEnabled?.Value == true;
            bool discordHeartbeat = GlobalConfig.DiscordNotifyHeartbeat?.Value == true;
            if (!logHeartbeat && !discordHeartbeat)
            {
                return;
            }

            _timer += dt;
            float intervalSeconds = (GlobalConfig.HeartbeatIntervalMinutes?.Value ?? 15f) * 60f;
            if (_timer < intervalSeconds)
            {
                return;
            }
            _timer = 0f;

            var characters = ZNet.instance != null ? ConnectedCharacters.All() : new System.Collections.Generic.List<ConnectedCharacter>();
            int playerCount = characters.Count;
            string playerNames = string.Join(", ", characters.Select(c => c.Name));
            string world = ZNet.instance != null ? ZNet.instance.GetWorldName() : "(no world)";
            string uptime = FormatUptime(_uptimeSeconds);

            if (logHeartbeat)
            {
                string who = playerCount > 0 ? playerNames : "none";
                PortalDebug.LogAlways($"[Heartbeat] '{world}' up {uptime} | {playerCount} player(s) online: {who} | TortalPortalLite {Plugin.ModVersion} running normally.");
            }

            if (discordHeartbeat)
            {
                DiscordWebhook.Send($"Heartbeat: '{world}' up {uptime} | {playerCount} player(s) online: {(playerCount > 0 ? playerNames : "none")}");
            }
        }

        private static string FormatUptime(float seconds)
        {
            int totalMinutes = Mathf.FloorToInt(seconds / 60f);
            int hours = totalMinutes / 60;
            int minutes = totalMinutes % 60;
            return hours > 0 ? $"{hours}h{minutes}m" : $"{minutes}m";
        }
    }
}
