using BepInEx.Configuration;
using ServerSync;
using TortalPortalLite.Core;

namespace TortalPortalLite.Subsystems.Topology
{
    /// <summary>
    /// Config surface for the `routing` domain, trimmed to this build's wanted set: #24 Phantom Anchor
    /// Fabrication, #29 Progression-Gated Sealed Gate, #224 DestinationPrewarm, #225 Fast-Transit Mode
    /// (#227 Parked Terminal is the same engine/config, its base mechanism). Own subsystem, own config
    /// file - see Core/ConfigBinder.cs for why this is not one central class.
    /// </summary>
    public static class RoutingConfig
    {
        // --- 9: core / pairing-authority reassertion (#23, load-bearing substrate under every engine below) ---
        public static ConfigEntry<bool>? TakeoverEnabled;
        public static ConfigEntry<float>? ReassertSeconds;
        public static ConfigEntry<string>? RegistryFile;
        public static ConfigEntry<int>? MaxWritesPerTick;

        // --- 11: sealed gate (#29) ---
        public static ConfigEntry<float>? SealedGateEvalSeconds;

        // --- 12: approach / JIT (#225/#227's parked-terminal watcher) ---
        public static ConfigEntry<float>? ApproachPollSeconds;

        // --- 16: delivery pipeline (#224, #225/#227, #228 - #228 is #225's own zone-readiness prerequisite) ---
        public static ConfigEntry<float>? PrewarmRadius;
        public static ConfigEntry<float>? FastTransitTriggerRadius;
        public static ConfigEntry<int>? ZoneGhostBudgetPerTick;

        public static void Bind(ConfigFile config, ConfigSync configSync)
        {
            const string sCore = "9 - Routing: Core";
            const string sCond = "11 - Routing: Schedules and Conditions";
            const string sJit = "12 - Routing: Approach and JIT";
            const string sDelivery = "16 - Routing: Delivery Pipeline";

            TakeoverEnabled = ConfigBinder.BindSynced(config, configSync, sCore, "TakeoverEnabled", true,
                "Master switch for every dynamic-routing engine in this domain (catalog #23). ConnectPortalsHook only exposes a postfix (no prefix-cancel broker exists yet), so 'takeover' here means: reassert every managed portal's tag/connection faster than vanilla's 5s reconciler, not a true prefix veto - see RoutingPairingAuthorityEngine's doc comment.");
            ReassertSeconds = ConfigBinder.BindSynced(config, configSync, sCore, "ReassertSeconds", 1.0f,
                "How often the pairing-authority engine re-checks and, if needed, re-writes each dynamically-managed portal's tag/connection. Must stay well under vanilla's own 5s Game.ConnectPortals pass.", 0.2f, 4.5f);
            RegistryFile = ConfigBinder.BindLocal(config, sCore, "File", "routing.json",
                "Path (relative to the plugin folder) to the hot-reloaded dynamic-routing declarations (sealed gates, parked terminals, ...). Local, not synced - a file path is server-machine-specific.");
            MaxWritesPerTick = ConfigBinder.BindSyncedInt(config, configSync, sCore, "MaxWritesPerTick", 64,
                "Caps how many portal ZDOs any single routing engine will rewrite in one tick, so a large managed set never spikes a frame (ZDOMan.SendZDOs re-serializes whole ZDOs with no delta encoding).", 1, 4000);

            SealedGateEvalSeconds = ConfigBinder.BindSynced(config, configSync, sCond, "SealedGateEvalSeconds", 1.0f,
                "Progression-Gated Sealed Gate (#29) global-key evaluation tick.", 0.2f, 10f);

            ApproachPollSeconds = ConfigBinder.BindSynced(config, configSync, sJit, "ApproachPollSeconds", 0.1f,
                "How often the shared approach/arm watcher (Parked Terminal, #225/#227) samples connected-character positions against managed portal positions.", 0.05f, 1f);

            PrewarmRadius = ConfigBinder.BindSynced(config, configSync, sDelivery, "PrewarmRadius", 30f,
                "DestinationPrewarm (#224): radius around a source portal within which nearby peers are force-sent the destination ZDO the instant a managed connection write happens.", 5f, 100f);
            FastTransitTriggerRadius = ConfigBinder.BindSynced(config, configSync, sDelivery, "FastTransitTriggerRadius", 1.2f,
                "Fast-Transit Mode (#225) / Parked Terminal (#227): XZ radius of the position-based trigger substituting for the (server-invisible) TeleportWorldTrigger collider.", 0.5f, 5f);
            ZoneGhostBudgetPerTick = ConfigBinder.BindSyncedInt(config, configSync, sDelivery, "ZoneGhostBudgetPerTick", 1,
                "Destination Zone Ghost Pre-Generation (#228): zones ghost-generated per tick (ZoneSystem.SpawnZone already budgets internally; this caps how many calls this engine makes per tick).", 1, 25);
        }
    }
}
