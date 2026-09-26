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
        public static ConfigEntry<float>? CorpseRunGateTtlMinutes;
        public static ConfigEntry<float>? CorpseRunOffsetMeters;
        public static ConfigEntry<float>? CorpseRunSearchRadiusMeters;
        public static ConfigEntry<float>? CorpseRunClearanceMeters;
        public static ConfigEntry<float>? CorpseRunMinRoomMeters;
        public static ConfigEntry<float>? CorpseRunMaxGraveAgeMinutes;

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
                "Minimum distance from a player's claimed bed at which the corpse-run gate's origin side may stand. The search then works outward to SearchRadiusMeters (section 41) for the closest spot with as much room as that patch of ground can offer.", 1f, 20f);

            CorpseRunEnabled = ConfigBinder.BindSynced(config, configSync, sCorpseRun, "Enabled", true,
                "On a tracked player's death, automatically raise a private ephemeral one-way portal from their bed (or the world hub) to their tombstone.");
            // Deliberately a NEW key rather than a new default on TtlMinutes: BepInEx keeps whatever value
            // an existing .cfg already holds, so changing a default in code never reaches a server that
            // has run before. Renaming is the only way a corrected default actually lands.
            CorpseRunGateTtlMinutes = ConfigBinder.BindSynced(config, configSync, sCorpseRun, "GateTtlMinutes", 0f,
                "Maximum lifetime of a corpse-run gate even if the tombstone is never emptied. 0 (the default) means no limit: the gate lives exactly as long as the grave does, and closes the moment the grave is recovered. Was TtlMinutes=30 through 1.0.7, which closed gates mid-run - a deep Mistlands or Ashlands corpse run routinely takes longer than half an hour.", 0f, 240f);
            CorpseRunOffsetMeters = ConfigBinder.BindSynced(config, configSync, sCorpseRun, "OffsetMeters", 2f,
                "Minimum distance from the tombstone at which the grave-side gate may stand.", 1f, 20f);
            CorpseRunSearchRadiusMeters = ConfigBinder.BindSynced(config, configSync, sCorpseRun, "SearchRadiusMeters", 40f,
                "How far out from the tombstone (and from the bed) to look for a spot on dry, level ground. Rings are scanned outward, so the gate lands as close as the ground and the obstacles allow. If nowhere inside this radius can offer even MinRoomMeters of room, the radius is widened automatically (up to 4x, capped at 200 m) rather than accepting a spot jammed into a rock.", 5f, 150f);
            CorpseRunClearanceMeters = ConfigBinder.BindSynced(config, configSync, sCorpseRun, "ClearanceMeters", 10f,
                "How much room a corpse-run gate PREFERS: no tree, rock, building piece, portal or generated location within this many metres of the portal frame, measured edge-to-edge from each object's collider extents rather than its pivot (tiny props like mushrooms and flowers only ever need 2 m). This is a preference, not a requirement - the search tries it first, then progressively smaller requirements down to MinRoomMeters, and takes the closest spot that honours the best one this patch of map can actually offer. Through 1.0.7 it was all-or-nothing, and since nowhere in a Valheim forest has 10-15 m of room from everything, every single gate fell through to a last-ditch 'most open spot' that could land 40 m from the bed.", 1f, 30f);
            CorpseRunMinRoomMeters = ConfigBinder.BindSynced(config, configSync, sCorpseRun, "MinRoomMeters", 2f,
                "The hard floor: real edge-to-edge room the portal frame must have, beyond its own 1.25 m footprint. The search never accepts less while any spot in the (automatically widened) radius can offer it - a gate 80 m away that a player can actually walk into beats one 20 m away wedged inside a boulder.", 0.5f, 10f);
            CorpseRunMaxGraveAgeMinutes = ConfigBinder.BindSynced(config, configSync, sCorpseRun, "MaxGraveAgeMinutes", 0f,
                "Skip raising a gate for a grave older than this, measured from the tombstone's own recorded time of death. 0 (the default) means no limit - any grave still standing was never recovered, so a gate to it is as useful as one raised at the moment of death. Raise it above 0 only if you would rather week-old graves were left alone after a restart.", 0f, 10080f);
        }
    }
}
