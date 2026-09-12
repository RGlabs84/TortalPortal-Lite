using BepInEx.Configuration;
using ServerSync;

namespace TortalPortalLite.Core
{
    /// <summary>
    /// The small set of mod-wide scalars (catalog #84's `[General]` section) that don't belong to any
    /// one subsystem - PortalDebug's VerboseLogging gate, the ServerSync locking handshake, and the
    /// mod-wide Heartbeat. Everything else lives in its own subsystem's `<Group>Config.cs`.
    /// </summary>
    public static class GlobalConfig
    {
        public static ConfigEntry<bool>? Enabled;
        public static ConfigEntry<bool>? VerboseLogging;
        public static ConfigEntry<bool>? ServerConfigLocked;

        /// <summary>
        /// Catalog #84's [General] "AcceptUnverifiedBuild" - PatchSelfTest (Core/Hooks/PatchSelfTest.cs)
        /// hard-disables any broker whose vanilla patch target failed to resolve, which normally means
        /// refusing to enable the engines registered against it. Setting this true downgrades that
        /// refusal to a loud warning instead, for an admin who wants to run ahead on a newer/older
        /// Valheim build and accepts the risk.
        /// </summary>
        public static ConfigEntry<bool>? AcceptUnverifiedBuild;

        public static ConfigEntry<bool>? HeartbeatEnabled;
        public static ConfigEntry<float>? HeartbeatIntervalMinutes;

        public static ConfigEntry<string>? DiscordWebhookUrl;
        public static ConfigEntry<bool>? DiscordNotifyHeartbeat;

        public static void Bind(ConfigFile config, ConfigSync configSync)
        {
            ServerConfigLocked = ConfigBinder.BindSynced(config, configSync, "1 - General", "ServerConfigLocked", true,
                "If on, only the server admin can change synced settings - clients get read-only copies.");
            configSync.AddLockingConfigEntry(ServerConfigLocked);

            Enabled = ConfigBinder.BindSynced(config, configSync, "1 - General", "Enabled", true,
                "Master switch. If off, every subsystem still registers but IsEnabled-gated engines no-op.");
            VerboseLogging = ConfigBinder.BindLocal(config, "1 - General", "VerboseLogging", false,
                "Log every subsystem action, not just warnings/errors and always-on lines. Local (not synced) - a client's own log is theirs.");
            AcceptUnverifiedBuild = ConfigBinder.BindLocal(config, "1 - General", "AcceptUnverifiedBuild", false,
                "If true, a Harmony patch target that failed to resolve at boot only warns instead of disabling the engines that depend on it. Default false: an unresolved patch target usually means the game build changed underneath this mod.");

            HeartbeatEnabled = ConfigBinder.BindLocal(config, "1 - General", "HeartbeatEnabled", true,
                "Periodic 'still alive' log line with uptime and connected-player count.");
            HeartbeatIntervalMinutes = ConfigBinder.BindLocal(config, "1 - General", "HeartbeatIntervalMinutes", 15f,
                "Minutes between heartbeat log lines.");

            DiscordWebhookUrl = ConfigBinder.BindLocal(config, "15 - Discord", "WebhookUrl", "",
                "Discord webhook URL. Local (not synced) - this is a server secret, never sent to clients. Blank disables all Discord posting.");
            DiscordNotifyHeartbeat = ConfigBinder.BindLocal(config, "15 - Discord", "Heartbeat", false,
                "Also post the heartbeat line to Discord, not just the server log.");
        }
    }
}
