using BepInEx.Configuration;
using ServerSync;
using TortalPortalLite.Core;

namespace TortalPortalLite.Subsystems.Topology
{
    /// <summary>
    /// Config surface for the whole `targeted` domain (wave1_targeted.json, options #178-196/#204-207).
    /// One subsystem, one config file, same rule Core/ConfigBinder.cs documents - every other subsystem
    /// in this mod owns its own `&lt;Group&gt;Config.cs` rather than editing a shared central class.
    /// </summary>
    public static class TargetedConfig
    {
        // --- foundation: Phantom/Anchor factories (#178/#179) ---
        public static ConfigEntry<float>? MaintenanceIntervalSeconds;
        public static ConfigEntry<bool>? LockPhantomPieceRemoval;
        public static ConfigEntry<float>? PhantomHealthOverride;
        public static ConfigEntry<float>? PhantomExitDistance;

        // --- route store (#180 Admin Coordinates + every declared static destination) ---
        public static ConfigEntry<string>? RoutesFile;

        // --- #180 Admin Coordinates ---
        public static ConfigEntry<bool>? AllowUnderwaterCoordinates;

        // --- #181 World Spawn ---
        public static ConfigEntry<float>? WorldSpawnOffsetMeters;

        // --- #182 Boss Altars ---
        public static ConfigEntry<float>? BossAltarOffsetMeters;
        public static ConfigEntry<float>? NextBossPollSeconds;

        // --- #183 Traders ---
        public static ConfigEntry<float>? TraderOffsetMeters;
        public static ConfigEntry<bool>? TraderDiscoveredOnly;

        // --- #184 Dungeon / Crypt Entrances ---
        public static ConfigEntry<float>? DungeonOffsetMeters;

        // --- #185 Biome Targets ---
        public static ConfigEntry<float>? BiomeOffsetMeters;
        public static ConfigEntry<float>? BiomeRandomRerollSeconds;

        // --- #186 Named Locations ---
        public static ConfigEntry<float>? LocationOffsetMeters;

        // --- #187 Player's Bed ---
        public static ConfigEntry<float>? BedOffsetMeters;
        public static ConfigEntry<string>? ClaimBedEmote;

        // --- #188 Player's Custom Spawn Point (inference) ---
        public static ConfigEntry<float>? SpawnPointSampleWindowSeconds;

        // --- #189 Player's Death Spot / Tombstone ---
        public static ConfigEntry<float>? TombstoneOffsetMeters;
        public static ConfigEntry<string>? ClaimGraveEmote;
        public static ConfigEntry<float>? TombstoneSafeRadius;

        // --- #190 Player's Ship ---
        public static ConfigEntry<float>? ShipMooredSpeedThreshold;
        public static ConfigEntry<float>? ShipOffsetMeters;
        public static ConfigEntry<float>? ShipReleaseGraceSeconds;
        public static ConfigEntry<string>? ClaimShipEmote;

        // --- #191 Nearest Player ---
        public static ConfigEntry<string>? CyclePlayerEmote;
        public static ConfigEntry<bool>? NearestPlayerRequireConsent;

        // --- #192 Player-Placed Anchor ---
        public static ConfigEntry<float>? PlayerAnchorPollSeconds;
        public static ConfigEntry<float>? PlayerAnchorOffsetMeters;
        public static ConfigEntry<string>? PlayerAnchorSignPrefix;

        // --- #193 Map Ping -> Destination ---
        public static ConfigEntry<float>? MapPingDebounceSeconds;
        public static ConfigEntry<float>? MapPingClaimRadius;

        // --- #194 Last Place the Builder Stood ---
        public static ConfigEntry<bool>? BuilderTraceLiveVariant;
        public static ConfigEntry<float>? BuilderTraceMinMoveMeters;

        // --- #195 Geometric Centre of a Base ---
        public static ConfigEntry<float>? BaseCentroidRecomputeSeconds;
        public static ConfigEntry<int>? BaseCentroidMinPieces;

        // --- #196 Moving Target (general engine) ---
        public static ConfigEntry<float>? MovingTargetTickSeconds;
        public static ConfigEntry<float>? MovingTargetForceSendRadius;
        public static ConfigEntry<float>? MovingTargetMinDeltaMeters;

        // --- #204 Terminal Architecture ---
        public static ConfigEntry<bool>? TerminalArchitectureEnabled;

        // --- #205 Destination Zone Governor ---
        public static ConfigEntry<bool>? ZoneGovernorPreGenerate;
        public static ConfigEntry<bool>? ZoneGovernorBarrenPad;
        public static ConfigEntry<int>? ZoneGovernorZonesPerTick;

        // --- #206 Target Materialiser ---
        public static ConfigEntry<bool>? MaterialiserEnabled;

        // --- #207 Corpse-Run Gate ---
        public static ConfigEntry<bool>? CorpseRunEnabled;
        public static ConfigEntry<float>? CorpseRunTtlMinutes;
        public static ConfigEntry<float>? CorpseRunOffsetMeters;

        public static void Bind(ConfigFile config, ConfigSync configSync)
        {
            const string sFoundation = "20 - Targeted: Foundation";
            const string sAdmin = "21 - Targeted: Admin Coordinates";
            const string sSpawn = "22 - Targeted: World Spawn";
            const string sBoss = "23 - Targeted: Boss Altars";
            const string sTrader = "24 - Targeted: Traders";
            const string sDungeon = "25 - Targeted: Dungeons";
            const string sBiome = "26 - Targeted: Biomes";
            const string sLocation = "27 - Targeted: Locations";
            const string sBed = "28 - Targeted: Bed";
            const string sSpawnPoint = "29 - Targeted: Custom Spawn";
            const string sGrave = "30 - Targeted: Grave";
            const string sShip = "31 - Targeted: Ship";
            const string sPlayer = "32 - Targeted: Nearest Player";
            const string sAnchor = "33 - Targeted: Player Anchor";
            const string sPing = "34 - Targeted: Map Ping";
            const string sBuilder = "35 - Targeted: Builder Trace";
            const string sBase = "36 - Targeted: Base Centroid";
            const string sMoving = "37 - Targeted: Moving Target";
            const string sTerminal = "38 - Targeted: Terminal Architecture";
            const string sZoneGov = "39 - Targeted: Zone Governor";
            const string sMaterialiser = "40 - Targeted: Materialiser";
            const string sCorpseRun = "41 - Targeted: Corpse Run";

            MaintenanceIntervalSeconds = ConfigBinder.BindSynced(config, configSync, sFoundation, "MaintenanceIntervalSeconds", 2.0f,
                "How often the phantom/anchor maintenance tick re-asserts tag+connection and reaps orphans. Must stay under vanilla's own 5s ConnectPortals pass.", 0.5f, 4.5f);
            LockPhantomPieceRemoval = ConfigBinder.BindSynced(config, configSync, sFoundation, "LockPhantomPieceRemoval", true,
                "Write LoadFields Piece.m_canBeRemoved=false on every phantom so a client can't hammer it down for free portal materials.");
            PhantomHealthOverride = ConfigBinder.BindSynced(config, configSync, sFoundation, "PhantomHealthOverride", 1000000000f,
                "LoadFields WearNTear-style health override written on every phantom (belt-and-suspenders alongside LockPhantomPieceRemoval).", 1f, 1e9f);
            PhantomExitDistance = ConfigBinder.BindSynced(config, configSync, sFoundation, "PhantomExitDistance", 1.5f,
                "LoadFields TeleportWorld.m_exitDistance written on every phantom - how far in front of it a traveller steps out.", 0.5f, 5f);

            RoutesFile = ConfigBinder.BindLocal(config, sAdmin, "RoutesFile", "targeted_routes.json",
                "Path (relative to the plugin folder) to the hot-reloaded admin-declared destination file (Admin Coordinates and every other static/declared targeted destination). Local, not synced - a file path is server-machine-specific.");
            AllowUnderwaterCoordinates = ConfigBinder.BindLocal(config, sAdmin, "AllowUnderwaterCoordinates", false,
                "If off, an admin-declared coordinate below sea level (height < 30) is rejected instead of accepted.");

            WorldSpawnOffsetMeters = ConfigBinder.BindSynced(config, configSync, sSpawn, "OffsetMeters", 7f,
                "Distance from the Start Temple centre to place the phantom.", 4f, 20f);

            BossAltarOffsetMeters = ConfigBinder.BindSynced(config, configSync, sBoss, "OffsetMeters", 18f,
                "Distance from a boss altar's exterior radius to place the phantom - kept wide so boss combat doesn't destroy it.", 4f, 40f);
            NextBossPollSeconds = ConfigBinder.BindSynced(config, configSync, sBoss, "NextBossPollSeconds", 30f,
                "How often 'next undefeated boss' mode re-checks global keys and re-targets.", 5f, 120f);

            TraderOffsetMeters = ConfigBinder.BindSynced(config, configSync, sTrader, "OffsetMeters", 6f,
                "Distance from a trader camp's exterior radius to place the phantom.", 3f, 20f);
            TraderDiscoveredOnly = ConfigBinder.BindSynced(config, configSync, sTrader, "DiscoveredOnly", true,
                "If on (vanilla-matching), a trader is only offered as a destination once some player has generated its zone (m_placed). If off, hub portals can lead players to an undiscovered trader.");

            DungeonOffsetMeters = ConfigBinder.BindSynced(config, configSync, sDungeon, "OffsetMeters", 5f,
                "Distance from a dungeon/crypt entrance's exterior radius to place the phantom.", 3f, 20f);

            BiomeOffsetMeters = ConfigBinder.BindSynced(config, configSync, sBiome, "OffsetMeters", 0f,
                "Reserved for future edge-hugging offset; biome targets already validate slope/water/lava directly at the chosen point.", 0f, 20f);
            BiomeRandomRerollSeconds = ConfigBinder.BindSynced(config, configSync, sBiome, "RandomRerollSeconds", 1800f,
                "How often 'random-in-biome' mode re-rolls its destination (destroy-and-recreate, only when no player is within 100 m of the old phantom).", 60f, 86400f);

            LocationOffsetMeters = ConfigBinder.BindSynced(config, configSync, sLocation, "OffsetMeters", 5f,
                "Distance from a named location's exterior radius to place the phantom.", 3f, 20f);

            BedOffsetMeters = ConfigBinder.BindSynced(config, configSync, sBed, "OffsetMeters", 3f,
                "Distance from a player's claimed bed to place their phantom.", 1f, 10f);
            ClaimBedEmote = ConfigBinder.BindSynced(config, configSync, sBed, "ClaimEmote", "wave",
                "Emote a player performs at a 'Home' hub portal to link it to their own claimed bed.");

            SpawnPointSampleWindowSeconds = ConfigBinder.BindSynced(config, configSync, sSpawnPoint, "SampleWindowSeconds", 6f,
                "How long after a death/respawn is detected the mod samples peer.m_refPos, looking for the stable custom-spawn-point value Game.UpdateRespawn leaks into it.", 1f, 20f);

            TombstoneOffsetMeters = ConfigBinder.BindSynced(config, configSync, sGrave, "OffsetMeters", 3f,
                "Distance from a player's newest tombstone to place their phantom.", 1f, 10f);
            ClaimGraveEmote = ConfigBinder.BindSynced(config, configSync, sGrave, "ClaimEmote", "point",
                "Emote a player performs at a 'Grave' hub portal to link it to their own newest tombstone.");
            TombstoneSafeRadius = ConfigBinder.BindSynced(config, configSync, sGrave, "SafeRadius", 15f,
                "If the death spot has no safe flat/dry land within this radius, the grave phantom is displaced further before giving up.", 5f, 50f);

            ShipMooredSpeedThreshold = ConfigBinder.BindSynced(config, configSync, sShip, "MooredSpeedThreshold", 0.5f,
                "A ship whose position moves less than this many m/s (sampled once per maintenance tick) is treated as 'moored' and safe to target directly.", 0.05f, 5f);
            ShipOffsetMeters = ConfigBinder.BindSynced(config, configSync, sShip, "OffsetMeters", 6f,
                "Distance astern of the ship to place the tracking anchor.", 2f, 20f);
            ShipReleaseGraceSeconds = ConfigBinder.BindSynced(config, configSync, sShip, "ReleaseGraceSeconds", 30f,
                "How long to keep tracking a ship after its helmsman lets go of the tiller (s_user -> 0) before unlinking.", 0f, 300f);
            ClaimShipEmote = ConfigBinder.BindSynced(config, configSync, sShip, "ClaimEmote", "point",
                "Emote a player performs at a 'Ship' hub portal, while steering, to link it to their own ship.");

            CyclePlayerEmote = ConfigBinder.BindSynced(config, configSync, sPlayer, "CycleEmote", "wave",
                "Emote a player performs at a 'Player' hub portal to cycle through connected-player targets (nearest first).");
            NearestPlayerRequireConsent = ConfigBinder.BindSynced(config, configSync, sPlayer, "RequireConsent", false,
                "If on, a player can only be targeted by another player's 'nearest player' hub after they have themselves performed the cycle/claim emote at some hub in the last 5 minutes (a lightweight anti-stalking gate - full ward-permission gating is the access domain's job).");

            PlayerAnchorPollSeconds = ConfigBinder.BindSynced(config, configSync, sAnchor, "PollSeconds", 3f,
                "How often sign/item-stand/ward/chest anchors are polled for new or changed registrations.", 0.5f, 15f);
            PlayerAnchorOffsetMeters = ConfigBinder.BindSynced(config, configSync, sAnchor, "OffsetMeters", 2.5f,
                "Distance from a player-placed anchor object to place the phantom.", 1f, 10f);
            PlayerAnchorSignPrefix = ConfigBinder.BindSynced(config, configSync, sAnchor, "SignPrefix", "#tpl ",
                "Sign text prefix that registers the sign as a named destination anchor, e.g. '#tpl Mine'.");

            MapPingDebounceSeconds = ConfigBinder.BindSynced(config, configSync, sPing, "DebounceSeconds", 10f,
                "Minimum time between accepted map pings from the same player, since pings are a public, spammable signal.", 1f, 60f);
            MapPingClaimRadius = ConfigBinder.BindSynced(config, configSync, sPing, "ClaimRadius", 10f,
                "A ping is applied to the managed hub portal nearest the pinging player, if within this radius.", 2f, 30f);

            BuilderTraceLiveVariant = ConfigBinder.BindSynced(config, configSync, sBuilder, "LiveVariant", true,
                "If on, a 'Back to <builder>' portal tracks the builder's current live position (moving anchor). If off, it is pinned forever at wherever the builder stood the moment the portal ZDO first arrived.");
            BuilderTraceMinMoveMeters = ConfigBinder.BindSynced(config, configSync, sBuilder, "MinMoveMeters", 1f,
                "Skip re-positioning the live-variant anchor for a builder movement smaller than this (avoids pointless SetPosition/ForceSendZDO churn).", 0.1f, 10f);

            BaseCentroidRecomputeSeconds = ConfigBinder.BindSynced(config, configSync, sBase, "RecomputeSeconds", 300f,
                "How often a player's base centroid is recomputed from their piece ZDOs.", 30f, 3600f);
            BaseCentroidMinPieces = ConfigBinder.BindSyncedInt(config, configSync, sBase, "MinPieces", 10,
                "Minimum piece count in a cluster before it is considered 'a base' worth targeting.", 1, 500);

            MovingTargetTickSeconds = ConfigBinder.BindSynced(config, configSync, sMoving, "TickSeconds", 1.0f,
                "How often every registered moving-target anchor is re-sampled and (if it moved enough) re-positioned.", 0.25f, 5f);
            MovingTargetForceSendRadius = ConfigBinder.BindSynced(config, configSync, sMoving, "ForceSendRadius", 25f,
                "Peers within this radius of a moving anchor's SOURCE portal get an explicit ForceSendZDO of the anchor on every move, since a distant destination ZDO is otherwise fetched once and never re-requested.", 5f, 100f);
            MovingTargetMinDeltaMeters = ConfigBinder.BindSynced(config, configSync, sMoving, "MinDeltaMeters", 1f,
                "Skip a tick's SetPosition/ForceSendZDO entirely if the tracked thing moved less than this since the last sample.", 0.1f, 10f);

            TerminalArchitectureEnabled = ConfigBinder.BindSynced(config, configSync, sTerminal, "Enabled", false,
                "Master switch for building real world-gen shells (LocationProxy / Ghost-spawned pieces / RegisterLocation) around managed destinations. Off by default - this is the heaviest, most invasive primitive in the domain (SERVER decompile ZoneSystem.SpawnLocation Ghost branch) and should be opted into per-destination via targeted_routes.json.");

            ZoneGovernorPreGenerate = ConfigBinder.BindSynced(config, configSync, sZoneGov, "PreGenerate", true,
                "Ghost-generate a managed destination's zone (and its near-ring) at route-write time so arrival never pays vanilla's own first-visit generation cost.");
            ZoneGovernorBarrenPad = ConfigBinder.BindLocal(config, sZoneGov, "BarrenPad", false,
                "Allow marking a managed destination's zone generated-without-generating (no vegetation/location/spawner) for a clean hub clearing. Off by default - see failure modes (can suppress a real location if the zone already had one queued).");
            ZoneGovernorZonesPerTick = ConfigBinder.BindSyncedInt(config, configSync, sZoneGov, "ZonesPerTick", 1,
                "Maximum zones ghost-generated per Update tick across the whole domain - vanilla's own SpawnZone is a main-thread instantiate+destroy pass and can cost tens of ms per zone.", 1, 4);

            MaterialiserEnabled = ConfigBinder.BindSynced(config, configSync, sMaterialiser, "Enabled", true,
                "Before routing a hub to a boss altar/trader/dungeon/location, force that target's zone to ghost-generate immediately (rather than waiting for a player to discover it), so the destination and its real pieces/NPCs always exist and can be validated.");

            CorpseRunEnabled = ConfigBinder.BindSynced(config, configSync, sCorpseRun, "Enabled", true,
                "On a tracked player's death, automatically raise a private ephemeral portal pair from their bed (or the world hub) to their tombstone.");
            CorpseRunTtlMinutes = ConfigBinder.BindSynced(config, configSync, sCorpseRun, "TtlMinutes", 30f,
                "Maximum lifetime of a corpse-run gate even if the tombstone is never emptied.", 1f, 240f);
            CorpseRunOffsetMeters = ConfigBinder.BindSynced(config, configSync, sCorpseRun, "OffsetMeters", 2f,
                "Distance from the tombstone to place the corpse-run gate's destination side.", 1f, 8f);
        }
    }
}
