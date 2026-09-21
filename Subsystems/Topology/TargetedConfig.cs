using BepInEx.Configuration;
using ServerSync;
using TortalPortalLite.Core;

namespace TortalPortalLite.Subsystems.Topology
{
    /// <summary>
    /// Config surface for the `targeted` domain, trimmed to this build's wanted set: #207 Corpse-Run
    /// Gate, plus the shared phantom-portal factory (#178) and the bed/tombstone lookup substrate #207
    /// depends on (#187/#189). One subsystem, one config file, same rule Core/ConfigBinder.cs documents.
    /// </summary>
    public static class TargetedConfig
    {
        // --- foundation: Phantom factory (#178), shared by every engine below ---
        public static ConfigEntry<float>? MaintenanceIntervalSeconds;
        public static ConfigEntry<bool>? LockPhantomPieceRemoval;
        public static ConfigEntry<float>? PhantomHealthOverride;
        public static ConfigEntry<float>? PhantomExitDistance;

        // --- #187 Player's Bed (lookup substrate only - #207's own origin resolution) ---
        public static ConfigEntry<float>? BedOffsetMeters;

        // --- #207 Corpse-Run Gate ---
        public static ConfigEntry<bool>? CorpseRunEnabled;
        public static ConfigEntry<float>? CorpseRunTtlMinutes;
        public static ConfigEntry<float>? CorpseRunOffsetMeters;
        public static ConfigEntry<float>? CorpseRunSearchRadiusMeters;
        public static ConfigEntry<float>? CorpseRunClearanceMeters;

        public static void Bind(ConfigFile config, ConfigSync configSync)
        {
            const string sFoundation = "20 - Targeted: Foundation";
            const string sBed = "28 - Targeted: Bed";
            const string sCorpseRun = "41 - Targeted: Corpse Run";

            MaintenanceIntervalSeconds = ConfigBinder.BindSynced(config, configSync, sFoundation, "MaintenanceIntervalSeconds", 2.0f,
                "How often the phantom maintenance tick re-asserts tag+connection and reaps orphans. Must stay under vanilla's own 5s ConnectPortals pass.", 0.5f, 4.5f);
            LockPhantomPieceRemoval = ConfigBinder.BindSynced(config, configSync, sFoundation, "LockPhantomPieceRemoval", true,
                "Write LoadFields Piece.m_canBeRemoved=false on every phantom so a client can't hammer it down for free portal materials.");
            PhantomHealthOverride = ConfigBinder.BindSynced(config, configSync, sFoundation, "PhantomHealthOverride", 1000000000f,
                "LoadFields WearNTear-style health override written on every phantom (belt-and-suspenders alongside LockPhantomPieceRemoval).", 1f, 1e9f);
            PhantomExitDistance = ConfigBinder.BindSynced(config, configSync, sFoundation, "PhantomExitDistance", 1.5f,
                "LoadFields TeleportWorld.m_exitDistance written on every phantom - how far in front of it a traveller steps out.", 0.5f, 5f);

            BedOffsetMeters = ConfigBinder.BindSynced(config, configSync, sBed, "OffsetMeters", 3f,
                "Minimum distance from a player's claimed bed at which the corpse-run gate's origin side may stand. The search then works outward to SearchRadiusMeters (section 41) for a spot honouring ClearanceMeters.", 1f, 20f);

            CorpseRunEnabled = ConfigBinder.BindSynced(config, configSync, sCorpseRun, "Enabled", true,
                "On a tracked player's death, automatically raise a private ephemeral one-way portal from their bed (or the world hub) to their tombstone.");
            CorpseRunTtlMinutes = ConfigBinder.BindSynced(config, configSync, sCorpseRun, "TtlMinutes", 30f,
                "Maximum lifetime of a corpse-run gate even if the tombstone is never emptied.", 1f, 240f);
            CorpseRunOffsetMeters = ConfigBinder.BindSynced(config, configSync, sCorpseRun, "OffsetMeters", 2f,
                "Minimum distance from the tombstone at which the grave-side gate may stand.", 1f, 20f);
            CorpseRunSearchRadiusMeters = ConfigBinder.BindSynced(config, configSync, sCorpseRun, "SearchRadiusMeters", 40f,
                "How far out from the tombstone (and from the bed) to look for a spot that honours ClearanceMeters on dry, level ground. Rings are scanned outward, so the gate lands as close as the clearance allows; a bigger radius only matters in dense forest or built-up ground, at the cost of a longer walk from gate to grave.", 5f, 150f);
            CorpseRunClearanceMeters = ConfigBinder.BindSynced(config, configSync, sCorpseRun, "ClearanceMeters", 10f,
                "Every corpse-run gate (both ends) must have no tree, rock, building piece, portal or generated location within this many metres of the portal frame, measured edge-to-edge from each object's collider extents rather than its pivot (tiny props like mushrooms and flowers only need 2 m). If no spot inside SearchRadiusMeters can honour it, the most open dry spot found is used and a warning is logged.", 1f, 30f);
        }
    }
}
