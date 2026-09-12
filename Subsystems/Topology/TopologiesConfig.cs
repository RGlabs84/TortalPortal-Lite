using BepInEx.Configuration;
using ServerSync;
using TortalPortalLite.Core;

namespace TortalPortalLite.Subsystems.Topology
{
    /// <summary>
    /// Config surface for the Topologies subsystem (catalog #s 1,2,4,6,7,8,9,10,11,12,13,14,15,16,17,
    /// 18,19,20,21 - the "topology core" domain). Own subsystem, own config file section prefix
    /// ("Topology: ..."), matching Core/ConfigBinder.cs's stated reason for the per-subsystem pattern:
    /// several concurrent subagents editing this codebase at once must never share one config class.
    /// </summary>
    public static class TopologiesConfig
    {
        public static ConfigEntry<string>? ShapesFile;
        public static ConfigEntry<float>? ShapeReassertSeconds;
        public static ConfigEntry<int>? MaxWritesPerTick;
        public static ConfigEntry<string>? DefaultAnchorPrefab;
        public static ConfigEntry<bool>? AutoProvisionPortals;

        public static ConfigEntry<float>? SwitchboardLockSeconds;
        public static ConfigEntry<string>? SwitchboardSelectEmote;
        public static ConfigEntry<bool>? SwitchboardUseMapPing;

        public static ConfigEntry<float>? CarouselProximityPauseRadius;

        public static ConfigEntry<bool>? LockdownActive;
        public static ConfigEntry<bool>? LockdownToastPlayers;

        public static ConfigEntry<bool>? RouletteForcedRerollEnabled;
        public static ConfigEntry<float>? RouletteForcedRerollSeconds;
        public static ConfigEntry<bool>? RouletteDeterministic;

        public static ConfigEntry<float>? DisplacedRoutingReassertSeconds;

        public static void Bind(ConfigFile config, ConfigSync configSync)
        {
            const string sShapes = "9 - Topology: Shapes";
            const string sSwitchboard = "10 - Topology: Switchboard-Carousel";
            const string sLockdown = "11 - Topology: Lockdown-Sink";
            const string sRoulette = "12 - Topology: Roulette";
            const string sDisplaced = "13 - Topology: Displaced Routing";

            ShapesFile = ConfigBinder.BindLocal(config, sShapes, "File", "topologies.json",
                "Path (relative to the plugin folder) to the hot-reloaded topology-shape declaration file (pairs/stars/rings/chains/trees/banks/sinks/asymmetric round-trips, plus faction rosters and vestibule wings). Local, not synced - a file path is server-machine-specific.");
            ShapeReassertSeconds = ConfigBinder.BindSynced(config, configSync, sShapes, "ReassertSeconds", 2.0f,
                "How often TopologiesShapeEngine re-checks and, if needed, re-writes each declared shape's tag/connection. Must stay under vanilla's own 5s ConnectPortals pass, same reasoning as Foundations' NetworkReassertEngine.", 0.5f, 4.5f);
            MaxWritesPerTick = ConfigBinder.BindSyncedInt(config, configSync, sShapes, "MaxWritesPerTick", 50,
                "Caps how many portal/anchor ZDOs any single Topologies engine will rewrite in one tick.", 1, 2000);
            DefaultAnchorPrefab = ConfigBinder.BindLocal(config, sShapes, "DefaultAnchorPrefab", "guard_stone",
                "Prefab name used for fabricated Anchor ZDOs (Anchor-Terminated One-Way / Dead Drop, #8) when a shape does not name its own. Must be a real, ZNetView-capable, persistence-safe vanilla prefab - validated against ZNetScene at use time and skipped (loudly logged) if it does not resolve. 'guard_stone' is confirmed present in this exact codebase (see Foundations/CapabilityProbe.cs's own ward probe) and is small/inert; override if you would rather anchors resolve to something else on your client mods/asset pack.");
            AutoProvisionPortals = ConfigBinder.BindLocal(config, sShapes, "AutoProvisionPortals", false,
                "If on, a shape node declared with AutoProvisionPortals=true and no resolvable portal at its position will have a real portal ZDO fabricated there (Portal Bank / Departures Hall, #11's 'provisioning' feature). Off by default because spawning game objects automatically is more invasive than the rest of this engine's read-mostly reassert behaviour.");

            SwitchboardLockSeconds = ConfigBinder.BindSynced(config, configSync, sSwitchboard, "SelectLockSeconds", 15f,
                "Switchboard (#12): how long a hub is locked to the requesting player's own selection before another player's request is accepted, avoiding the 'two players fight over one hub' race the catalog names.", 1f, 120f);
            SwitchboardSelectEmote = ConfigBinder.BindLocal(config, sSwitchboard, "SelectEmote", "point",
                "Switchboard (#12) fallback input when the map-ping channel is unavailable (see SwitchboardUseMapPing): the emote name that cycles a switchboard hub to its next declared destination. Case-insensitive.");
            SwitchboardUseMapPing = ConfigBinder.BindLocal(config, sSwitchboard, "UseMapPing", true,
                "Switchboard (#12): try to register a ChatMessage/map-ping listener for direct destination selection (Chat.SendPing). Automatically falls back to emote-cycling if Chat already owns that RPC name server-side (see TopologiesSwitchboardEngine's own doc comment) or registration otherwise fails.");

            CarouselProximityPauseRadius = ConfigBinder.BindSynced(config, configSync, sSwitchboard, "CarouselProximityPauseRadius", 8f,
                "Carousel (#13): pause the rotation timer whenever any connected player is within this many metres of the hub, so the destination cannot flip out from under someone mid-approach.", 0f, 50f);

            LockdownActive = ConfigBinder.BindSynced(config, configSync, sLockdown, "Active", false,
                "Tag-Scramble Lockdown (#17): when true, every portal named in topologies.json's Lockdown.Portals (or every census portal if that list is empty) is given a unique invisible tag suffix and nulled, so vanilla's own reconciler can never re-pair it. Flip back to false to restore instantly.");
            LockdownToastPlayers = ConfigBinder.BindSynced(config, configSync, sLockdown, "ToastPlayers", true,
                "Broadcast a toast to every connected player when a lockdown engages/lifts - the catalog's own failure-mode warning ('absolutely silent to the player' otherwise).");

            RouletteForcedRerollEnabled = ConfigBinder.BindSynced(config, configSync, sRoulette, "ForcedRerollEnabled", false,
                "Roulette / Periodic Shuffle (#19): if on, every declared roulette group is nulled and immediately re-paired (via Game.instance.ConnectPortals()) on a timer, instead of freezing at whatever vanilla's pass-2 randomly picked once.");
            RouletteForcedRerollSeconds = ConfigBinder.BindSynced(config, configSync, sRoulette, "ForcedRerollSeconds", 3600f,
                "Roulette (#19): seconds between forced re-rolls when ForcedRerollEnabled is on.", 10f, 86400f);
            RouletteDeterministic = ConfigBinder.BindSynced(config, configSync, sRoulette, "Deterministic", false,
                "Roulette (#19): if on, registers a FindRandomUnconnectedPortalHook handler that replaces vanilla's UnityEngine.Random.Range pick with a deterministic (oldest-ZDOID-first) one for declared roulette tags, per the catalog's own 'if you need deterministic pairing' mitigation.");

            DisplacedRoutingReassertSeconds = ConfigBinder.BindSynced(config, configSync, sDisplaced, "ReassertSeconds", 2.0f,
                "Displaced Routing (#20): how often declared displaced routes are re-asserted, IN ADDITION to the ConnectPortalsHook postfix that re-asserts them immediately after every vanilla 5s pass (see TopologiesDisplacedRoutingEngine's own doc comment on why a postfix-only mitigation, not a true prefix veto, is what's available here).", 0.5f, 4.5f);
        }
    }
}
