using BepInEx.Configuration;
using ServerSync;
using TortalPortalLite.Core;

namespace TortalPortalLite.Subsystems.WorldOps
{
    /// <summary>
    /// Config surface for the "wildcard cluster A" domain (data-store/signage/puzzle/lore mechanisms,
    /// Wave 3, catalog #128/#132/#133/#134/#135/#139/#140/#141/#142/#146/#149/#151/#241/#242/#243).
    /// Own subsystem, own config file - see Core/ConfigBinder.cs for why this is not one central class.
    /// </summary>
    public static class WildcardAConfig
    {
        public static ConfigEntry<bool>? Enabled;

        // --- #128 Void Anchor ---
        public static ConfigEntry<float>? VoidAnchorReassertSeconds;

        // --- #132 Decoy Gate ---
        public static ConfigEntry<float>? DecoyReassertSeconds;

        // --- #134/#243 Adamant Gate ---
        public static ConfigEntry<float>? AdamantHealthMultiplier;
        public static ConfigEntry<float>? AdamantWatchdogSeconds;

        // --- #140 Self-Organising Networks ---
        public static ConfigEntry<bool>? SelfOrganizeEnabled;
        public static ConfigEntry<float>? SelfOrganizeSampleSeconds;
        public static ConfigEntry<float>? SelfOrganizeProximityRadius;
        public static ConfigEntry<float>? SelfOrganizeRewireSeconds;
        public static ConfigEntry<bool>? SelfOrganizeRewireEnabled;

        // --- #142 Puzzle / progression gates ---
        public static ConfigEntry<string>? PuzzlesFile;
        public static ConfigEntry<float>? PuzzleReassertSeconds;

        // --- #146 Portal Scaling ---
        public static ConfigEntry<bool>? PortalScalingEnabled;

        // --- #149 Cross-Mod Integration ---
        // (admin-facing-only, no tunables beyond Enabled)

        // --- #151 Minimal Optional Client Mod capability handshake ---
        public static ConfigEntry<bool>? ClientCapabilityHandshakeEnabled;

        // --- #242 Non-Portals As Portals migration ---
        public static ConfigEntry<bool>? MigrationAllowMobilePrefabs;

        public static void Bind(ConfigFile config, ConfigSync configSync)
        {
            const string sGeneral = "20 - WorldOps: General";
            const string sVoidAnchor = "21 - WorldOps: Void Anchor";
            const string sDecoy = "22 - WorldOps: Decoy Gate";
            const string sAdamant = "23 - WorldOps: Adamant Gate";
            const string sSelfOrg = "24 - WorldOps: Self-Organising Networks";
            const string sPuzzle = "25 - WorldOps: Puzzle Gates";
            const string sScaling = "26 - WorldOps: Portal Scaling";
            const string sClient = "27 - WorldOps: Client Capability";
            const string sMigration = "28 - WorldOps: Non-Portal Migration";

            Enabled = ConfigBinder.BindSynced(config, configSync, sGeneral, "Enabled", true,
                "Master switch for the wildcard cluster A domain (data-store/signage/puzzle/lore mechanisms).");

            VoidAnchorReassertSeconds = ConfigBinder.BindSynced(config, configSync, sVoidAnchor, "ReassertSeconds", 2.0f,
                "How often a Void Anchor's self-loop connection is re-checked and re-asserted (survives ZDOExtraData.RegenerateConnectionHashData wiping it at save). Must stay under vanilla's 5s ConnectPortals pass.", 0.5f, 4.5f);

            DecoyReassertSeconds = ConfigBinder.BindSynced(config, configSync, sDecoy, "ReassertSeconds", 2.0f,
                "How often a Decoy Gate's fake connection is re-asserted (Game.ConnectPortals phase 1 nulls a dangling connection within 5s).", 0.5f, 4.5f);

            AdamantHealthMultiplier = ConfigBinder.BindSynced(config, configSync, sAdamant, "HealthMultiplier", 1e9f,
                "s_health value written onto a fortified portal - large enough that vanilla-client damage can never zero it in practice.", 1000f, 1e12f);
            AdamantWatchdogSeconds = ConfigBinder.BindSynced(config, configSync, sAdamant, "WatchdogSeconds", 5.0f,
                "How often the Adamant Gate watchdog re-checks a fortified portal's health/ownership/LoadFields overrides.", 1f, 60f);

            SelfOrganizeEnabled = ConfigBinder.BindSynced(config, configSync, sSelfOrg, "Enabled", true,
                "Track portal transit counts from sampled player positions (#140).");
            SelfOrganizeSampleSeconds = ConfigBinder.BindSynced(config, configSync, sSelfOrg, "SampleSeconds", 3.0f,
                "How often connected-character positions are sampled for transit detection.", 0.5f, 15f);
            SelfOrganizeProximityRadius = ConfigBinder.BindSynced(config, configSync, sSelfOrg, "ProximityRadius", 40f,
                "Radius (metres) used to decide a sampled position is 'at' a portal, mirroring Wonderland PositionWatch.IsPortalTransit.", 5f, 200f);
            SelfOrganizeRewireSeconds = ConfigBinder.BindSynced(config, configSync, sSelfOrg, "RewireEvalSeconds", 60f,
                "How often the most-used-destination override re-evaluates its candidate pick.", 5f, 600f);
            SelfOrganizeRewireEnabled = ConfigBinder.BindSynced(config, configSync, sSelfOrg, "RewireEnabled", false,
                "If on, FindRandomUnconnectedPortalHook is overridden to prefer the most-used same-tag candidate instead of vanilla's uniform random pick. Off by default - this changes live pairing behaviour for every same-tag group.");

            PuzzlesFile = ConfigBinder.BindLocal(config, sPuzzle, "File", "wildcardA_puzzles.json",
                "Path (relative to the plugin folder) to the hot-reloaded puzzle/progression-gate definitions. Local, not synced - a file path is server-machine-specific.");
            PuzzleReassertSeconds = ConfigBinder.BindSynced(config, configSync, sPuzzle, "ReassertSeconds", 2.0f,
                "How often puzzle gate state (lock/tag/connection) is re-checked and re-asserted.", 0.5f, 4.5f);

            PortalScalingEnabled = ConfigBinder.BindSynced(config, configSync, sScaling, "Enabled", true,
                "Attempt ZDOVars.s_scaleHash writes on portals (#146). Needs-ingame-check feasibility - see WildcardAPortalScalingEngine's boot-time capability probe log line.");

            ClientCapabilityHandshakeEnabled = ConfigBinder.BindSynced(config, configSync, sClient, "Enabled", true,
                "Listen for an optional companion client mod's announce RPC (#151) so other engines can gate an enhanced path on it. Safe to leave on even if no such client mod exists - it is a passive Register() with no vanilla behaviour changed.");

            MigrationAllowMobilePrefabs = ConfigBinder.BindLocal(config, sMigration, "AllowMobilePrefabs", false,
                "If off (default, and STRONGLY recommended per #242's own citation), WildcardANonPortalMigrationEngine refuses to enroll a prefab that carries a ZSyncTransform - ZDO.SetSector's portal early-return would stop it from ever re-sectoring once moved.");
        }
    }
}
