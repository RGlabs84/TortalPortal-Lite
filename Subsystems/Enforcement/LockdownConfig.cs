using BepInEx.Configuration;
using ServerSync;
using TortalPortalLite.Core;

namespace TortalPortalLite.Subsystems.Enforcement
{
    /// <summary>
    /// Config surface for the whole `lockdown` domain (wave2_lockdown.json, options #108-125/#247-259,
    /// plus the two targeted-domain options #197/#208 that live here because their dependency does -
    /// see Subsystems/Enforcement/LockdownTwoHopRelayEngine.cs). One subsystem, one config file, same
    /// rule Core/ConfigBinder.cs documents - every subsystem owns its own <Group>Config.cs.
    /// </summary>
    public static class LockdownConfig
    {
        // --- #112 Connection Vault / #124 Mass-Rewrite Spike Control (shared plumbing) ---
        public static ConfigEntry<string>? VaultFileName;
        public static ConfigEntry<int>? MaxWritesPerTick;
        public static ConfigEntry<float>? ForceSendTargetedRadius;

        // --- #108/#110/#120 Global key levers ---
        public static ConfigEntry<bool>? SessionOnlyKeyWrites;

        // --- #109/#255 Global-key RPC guard (classification logic only - see its own NEEDS NEW HOOK BROKER note) ---
        public static ConfigEntry<bool>? GlobalKeyGuardEnabled;

        // --- #111 Force-Disconnect Lockdown ---
        public static ConfigEntry<bool>? ForceDisconnectSuppressReconnectPortals;

        // --- #113 Save-Boundary Transparency (logic ready, wiring needs a new broker) ---
        public static ConfigEntry<bool>? SaveBoundarySandwichEnabled;

        // --- #114 Scheduled Blackout Windows ---
        public static ConfigEntry<string>? ScheduleFile;
        public static ConfigEntry<float>? ScheduleReconcileSeconds;

        // --- #115 Graceful Lockdown and Announcement ---
        public static ConfigEntry<float>? AnnouncePreWarnSeconds1;
        public static ConfigEntry<float>? AnnouncePreWarnSeconds2;
        public static ConfigEntry<float>? AnnouncePreWarnSeconds3;
        public static ConfigEntry<float>? AnnounceProximityRadius;
        public static ConfigEntry<float>? AnnounceDwellSampleSeconds;

        // --- #257/#258 Lockdown Legibility Rule + Locked-Gate Overlay ---
        public static ConfigEntry<bool>? LegibilityEnabled;
        public static ConfigEntry<float>? LegibilityThresholdSeconds;
        public static ConfigEntry<string>? LegibilityTagMarker;
        public static ConfigEntry<float>? OverlayIntervalSeconds;
        public static ConfigEntry<float>? OverlayRadius;
        public static ConfigEntry<string>? OverlayText;

        // --- #116/#249 Boss Auto-Lockdown + activeBosses watchdog ---
        public static ConfigEntry<bool>? BossLockdownEnabled;
        public static ConfigEntry<float>? BossWatchdogPollSeconds;
        public static ConfigEntry<int>? BossWatchdogRequiredSamples;

        // --- #117 Raid-Time Geofenced Restriction ---
        public static ConfigEntry<bool>? RaidGeofenceEnabled;
        public static ConfigEntry<bool>? RaidGeofenceLockWholeTagGroup;
        public static ConfigEntry<float>? RaidGeofencePollSeconds;

        // --- #247/#248/#259 Raid Beacon + Event Slot Governor + Beacon Cleanup Sweep ---
        public static ConfigEntry<bool>? RaidBeaconEnabled;
        public static ConfigEntry<string>? RaidBeaconEventName;
        public static ConfigEntry<float>? EventSlotHubRadius;
        public static ConfigEntry<bool>? EventSlotGuardOrganicNearHubs;
        public static ConfigEntry<int>? BeaconCleanupBudgetPerTick;

        // --- #118 Biome and Region Restricted Networks ---
        public static ConfigEntry<string>? RegionRulesFile;
        public static ConfigEntry<float>? RegionReconcileSeconds;
        public static ConfigEntry<int>? RegionHysteresisSamples;

        // --- #119 Per-Network Quarantine-Tag Lockdown ---
        public static ConfigEntry<string>? QuarantineMarkerPrefix;

        // --- #121 Ironman and Hardcore Rulesets ---
        public static ConfigEntry<string>? ActiveRuleset;

        // --- #122 Modifier Badge Honesty ---
        public static ConfigEntry<bool>? ModifierBadgeAnnounceOnJoin;

        // --- #123 Destination Safety Validator ---
        public static ConfigEntry<float>? ValidatorMaxSlopeNormalY;
        public static ConfigEntry<float>? ValidatorUndergroundToleranceMeters;

        // --- #125 Invariant Assertion Harness ---
        public static ConfigEntry<float>? InvariantFastCheckSeconds;
        public static ConfigEntry<float>? InvariantSlowCheckSeconds;
        public static ConfigEntry<int>? InvariantTamperThreshold;
        public static ConfigEntry<bool>? InvariantKickOnTamper;

        // --- #250 Teleport-Area Placement Gate Audit ---
        public static ConfigEntry<bool>? PlacementGateAuditOnBoot;

        // --- #251 Fabricated Location Pad ---
        public static ConfigEntry<string>? LocationPadFile;

        // --- #252 Placement Policy Engine ---
        public static ConfigEntry<bool>? PlacementPolicyEnabled;
        public static ConfigEntry<float>? PlacementMinSpacingMeters;
        public static ConfigEntry<int>? PlacementMaxPerPlayer;
        public static ConfigEntry<string>? PlacementBiomeBanList;
        public static ConfigEntry<bool>? PlacementBanInteriors;
        public static ConfigEntry<bool>? PlacementRespectWards;

        // --- #253 Ground-Drop Refund ---
        public static ConfigEntry<bool>? GroundDropRefundEnabled;
        public static ConfigEntry<int>? GroundDropRefundMaxPerSecond;

        // --- #254 Portal Sanctuary ---
        public static ConfigEntry<bool>? SanctuaryEnabled;
        public static ConfigEntry<float>? SanctuaryRadius;
        public static ConfigEntry<bool>? SanctuaryIncludeEventCreatures;
        public static ConfigEntry<int>? SanctuaryMaxDestroysPerSecond;

        // --- #256 Managed-Portal Relay Shield ---
        public static ConfigEntry<bool>? RelayShieldProtectDestroy;
        public static ConfigEntry<bool>? RelayShieldAllowCreatorDestroy;

        // --- #197/#208 Two-Hop Relay ---
        public static ConfigEntry<string>? RelayFile;
        public static ConfigEntry<string>? RelaySelectEmote;
        public static ConfigEntry<float>? RelayLobbyScanRadius;
        public static ConfigEntry<float>? RelaySamplePeriodSeconds;
        public static ConfigEntry<float>? RelaySecondHopHoldSeconds;
        public static ConfigEntry<float>? RelayRetryAfterSeconds;
        public static ConfigEntry<float>? RelayPerPlayerCooldownSeconds;

        public static void Bind(ConfigFile config, ConfigSync configSync)
        {
            const string sVault = "50 - Lockdown: Vault and Budget";
            const string sKeys = "51 - Lockdown: Global Keys";
            const string sForce = "52 - Lockdown: Force-Disconnect";
            const string sSave = "53 - Lockdown: Save Boundary";
            const string sSchedule = "54 - Lockdown: Schedule";
            const string sAnnounce = "55 - Lockdown: Announcement";
            const string sLegibility = "56 - Lockdown: Legibility/Overlay";
            const string sBoss = "57 - Lockdown: Boss Watchdog";
            const string sRaid = "58 - Lockdown: Raid Geofence";
            const string sBeacon = "59 - Lockdown: Raid Beacon";
            const string sRegion = "60 - Lockdown: Regions";
            const string sQuarantine = "61 - Lockdown: Quarantine";
            const string sRuleset = "62 - Lockdown: Ruleset";
            const string sBadge = "63 - Lockdown: Modifier Badge";
            const string sValidator = "64 - Lockdown: Destination Validator";
            const string sInvariant = "65 - Lockdown: Invariant Harness";
            const string sAudit = "66 - Lockdown: Placement Gate Audit";
            const string sPad = "67 - Lockdown: Location Pad";
            const string sPlacement = "68 - Lockdown: Placement Policy";
            const string sRefund = "69 - Lockdown: Ground-Drop Refund";
            const string sSanctuary = "70 - Lockdown: Portal Sanctuary";
            const string sShield = "71 - Lockdown: Relay Shield";
            const string sRelay = "72 - Lockdown: Two-Hop Relay";

            VaultFileName = ConfigBinder.BindLocal(config, sVault, "VaultFileName", "lockdown_vault.json",
                "Position-keyed connection vault file (per world, suffixed automatically). Local - a file path is server-machine-specific.");
            MaxWritesPerTick = ConfigBinder.BindSyncedInt(config, configSync, sVault, "MaxWritesPerTick", 40,
                "Caps how many portal ZDOs any single lockdown engine rewrites in one tick (#124 spike control).", 1, 2000);
            ForceSendTargetedRadius = ConfigBinder.BindSynced(config, configSync, sVault, "ForceSendTargetedRadius", 60f,
                "Only portals within this radius of a connected player get an immediate targeted ForceSendZDO; farther ones ride the ordinary sync sweep (#124).", 5f, 500f);

            SessionOnlyKeyWrites = ConfigBinder.BindSynced(config, configSync, sKeys, "SessionOnlyKeyWrites", true,
                "#110: write world-modifier keys (noportals/nobossportals/dungeonbuild/teleportall) with canSaveToServerOptionKeys=false so a temporary lockdown never bakes into the world's .fwl.");

            GlobalKeyGuardEnabled = ConfigBinder.BindSynced(config, configSync, sKeys, "GlobalKeyGuardEnabled", true,
                "#109/#255: master switch for the global-key tamper classifier. The actual veto needs a new Core/Hooks broker (see LockdownGlobalKeyGuard.cs) - while unwired this only classifies and logs via AuditLog-style logging, it does not yet drop packets.");

            ForceDisconnectSuppressReconnectPortals = ConfigBinder.BindSynced(config, configSync, sForce, "SuppressReconnect", true,
                "#111: while a portal is force-disconnected, decline it from FindRandomUnconnectedPortalHook's random re-pairing and re-null it immediately after every Game.ConnectPortals postfix pass.");

            SaveBoundarySandwichEnabled = ConfigBinder.BindSynced(config, configSync, sSave, "Enabled", true,
                "#113: master switch for the pre-save-restore / post-save-relock sandwich. Logic is complete (LockdownVault.PreSaveRestoreAll/PostSaveRelockAll) but needs a new Core/Hooks broker on ZDOMan.PrepareSave to actually run automatically - see that file's NEEDS NEW HOOK BROKER note.");

            ScheduleFile = ConfigBinder.BindLocal(config, sSchedule, "File", "lockdown_schedule.json",
                "#114: hot-reloaded blackout-window schedule (days/startLocal/endLocal/mode), same pattern as networks.json.");
            ScheduleReconcileSeconds = ConfigBinder.BindSynced(config, configSync, sSchedule, "ReconcileSeconds", 1f,
                "How often the schedule reconciler compares desired vs actual state (level-triggered, never edge-triggered).", 0.5f, 10f);

            AnnouncePreWarnSeconds1 = ConfigBinder.BindSynced(config, configSync, sAnnounce, "PreWarnSeconds1", 300f, "#115: first broadcast warning, seconds before engage.", 10f, 3600f);
            AnnouncePreWarnSeconds2 = ConfigBinder.BindSynced(config, configSync, sAnnounce, "PreWarnSeconds2", 60f, "#115: second broadcast warning, seconds before engage.", 5f, 1800f);
            AnnouncePreWarnSeconds3 = ConfigBinder.BindSynced(config, configSync, sAnnounce, "PreWarnSeconds3", 10f, "#115: final per-player proximity warning, seconds before engage.", 1f, 120f);
            AnnounceProximityRadius = ConfigBinder.BindSynced(config, configSync, sAnnounce, "ProximityRadius", 40f, "#115: radius around a portal that counts as 'a player is standing here' for the final warning and post-lock explanation.", 5f, 150f);
            AnnounceDwellSampleSeconds = ConfigBinder.BindSynced(config, configSync, sAnnounce, "DwellSampleSeconds", 1f, "#115: sampling period for the proximity-dwell transit-attempt detector.", 0.25f, 5f);

            LegibilityEnabled = ConfigBinder.BindSynced(config, configSync, sLegibility, "Enabled", true, "#257: require every lockdown longer than the threshold to carry an in-world signal.");
            LegibilityThresholdSeconds = ConfigBinder.BindSynced(config, configSync, sLegibility, "ThresholdSeconds", 30f, "#257: a key-only (Tier-A) lockdown running longer than this must show a per-portal signal or is escalated.", 5f, 3600f);
            LegibilityTagMarker = ConfigBinder.BindSynced(config, configSync, sLegibility, "TagMarker", " [X]", "#258: suffix appended (group-atomically) to a locked network's shared tag - kept short, the client's rename box is only 10 characters.");
            OverlayIntervalSeconds = ConfigBinder.BindSynced(config, configSync, sLegibility, "OverlayIntervalSeconds", 1.2f, "#258: resend period for the floating 'locked' text per nearby peer.", 0.5f, 10f);
            OverlayRadius = ConfigBinder.BindSynced(config, configSync, sLegibility, "OverlayRadius", 30f, "#258: DamageText.m_maxTextDistance is 30m client-side - sending to a farther peer is wasted traffic.", 5f, 30f);
            OverlayText = ConfigBinder.BindSynced(config, configSync, sLegibility, "OverlayText", "Portal locked", "#258: floating text shown over a legibility-locked portal.");

            BossLockdownEnabled = ConfigBinder.BindSynced(config, configSync, sBoss, "Enabled", true, "#116: manage NoBossPortals for boss fights.");
            BossWatchdogPollSeconds = ConfigBinder.BindSynced(config, configSync, sBoss, "WatchdogPollSeconds", 30f, "#116/#249: how often the leaked-counter watchdog samples for a live alerted boss.", 5f, 300f);
            BossWatchdogRequiredSamples = ConfigBinder.BindSyncedInt(config, configSync, sBoss, "WatchdogRequiredSamples", 3, "#116: consecutive 'no live alerted boss found' samples required before the watchdog zeroes a leaked activeBosses counter (never on a single sample).", 1, 20);

            RaidGeofenceEnabled = ConfigBinder.BindSynced(config, configSync, sRaid, "Enabled", true, "#117: lock only the portals inside an active RandEventSystem event's radius.");
            RaidGeofenceLockWholeTagGroup = ConfigBinder.BindSynced(config, configSync, sRaid, "LockWholeTagGroup", false, "#117: also lock every same-tag portal elsewhere on the map, closing the 'walk to the second gate' loophole at the cost of locking unaffected portals.");
            RaidGeofencePollSeconds = ConfigBinder.BindSynced(config, configSync, sRaid, "PollSeconds", 1f, "#117: how often the current random event is re-read (level-triggered).", 0.25f, 5f);

            RaidBeaconEnabled = ConfigBinder.BindSynced(config, configSync, sBeacon, "Enabled", false, "#247: start a vanilla random event at a locked gate as its own announcement/hazard. Off by default - invasive.");
            RaidBeaconEventName = ConfigBinder.BindSynced(config, configSync, sBeacon, "EventName", "army_eikthyr", "#247: RandEventSystem event name to raise at a locked gate (must exist in RandEventSystem.instance.m_events).");
            EventSlotHubRadius = ConfigBinder.BindSynced(config, configSync, sBeacon, "EventSlotHubRadius", 150f, "#248: an organic raid rolled within this radius of a managed hub is corrected away on the next tick.", 10f, 1000f);
            EventSlotGuardOrganicNearHubs = ConfigBinder.BindSynced(config, configSync, sBeacon, "GuardOrganicNearHubs", false, "#248: also correct organic (non-beacon) raids that land near a managed hub, not just protect an active beacon.");
            BeaconCleanupBudgetPerTick = ConfigBinder.BindSyncedInt(config, configSync, sBeacon, "CleanupBudgetPerTick", 32, "#259: max leftover event-creature destroys per tick when a beacon/raid ends.", 1, 256);

            RegionRulesFile = ConfigBinder.BindLocal(config, sRegion, "File", "lockdown_regions.json", "#118: hot-reloaded region/biome pairing-restriction rules.");
            RegionReconcileSeconds = ConfigBinder.BindSynced(config, configSync, sRegion, "ReconcileSeconds", 30f, "#118: how often each portal's region is re-derived and its tag suffix reconciled.", 5f, 300f);
            RegionHysteresisSamples = ConfigBinder.BindSyncedInt(config, configSync, sRegion, "HysteresisSamples", 3, "#118: consecutive samples of a NEW region required before re-suffixing a portal, to avoid thrash on a biome seam.", 1, 10);

            QuarantineMarkerPrefix = ConfigBinder.BindSynced(config, configSync, sQuarantine, "MarkerPrefix", "!lk", "#119: prefix for the per-portal-unique quarantine tag string (kept short - vanilla's rename box is 10 characters).");

            ActiveRuleset = ConfigBinder.BindSynced(config, configSync, sRuleset, "ActiveRuleset", "None", "#121: named composite preset (None/NoPortals/OneWayOutboundOnly/TieredTravel/NightCurfew/RaidSealAndBossBlackout/CargoIronman). Applied by LockdownRulesetEngine on change.");

            ModifierBadgeAnnounceOnJoin = ConfigBinder.BindSynced(config, configSync, sBadge, "AnnounceOnJoin", true, "#122: tell a newly-joined player the CURRENT lockdown state, since the server-browser modifier badge is a stale boot-time snapshot.");

            ValidatorMaxSlopeNormalY = ConfigBinder.BindSynced(config, configSync, sValidator, "MaxSlopeNormalY", 0.6f, "#123: minimum acceptable terrain-normal Y component (1.0 = flat, lower = steeper) for a destination to validate.", 0.1f, 1f);
            ValidatorUndergroundToleranceMeters = ConfigBinder.BindSynced(config, configSync, sValidator, "UndergroundToleranceMeters", 2f, "#123: how far below the procedural terrain height a point may sit before being rejected as 'underground' (terraforming/location flattening tolerance).", 0f, 10f);

            InvariantFastCheckSeconds = ConfigBinder.BindSynced(config, configSync, sInvariant, "FastCheckSeconds", 1f, "#125: cheap invariants (I2 lock completeness, I3 self-loop, I8 target-bit) checked over the live zero-alloc portal dictionary.", 0.25f, 10f);
            InvariantSlowCheckSeconds = ConfigBinder.BindSynced(config, configSync, sInvariant, "SlowCheckSeconds", 30f, "#125: expensive invariants (I5 duplicate-entry, I10 out-of-range) checked over a full allocated portal list.", 5f, 300f);
            InvariantTamperThreshold = ConfigBinder.BindSyncedInt(config, configSync, sInvariant, "TamperThreshold", 8, "#125: relinks-per-minute on one locked portal before it is flagged as a sustained tamper pattern.", 1, 100);
            InvariantKickOnTamper = ConfigBinder.BindSynced(config, configSync, sInvariant, "KickOnTamper", false, "#125: escalate a sustained tamper pattern to ZNet.Kick instead of only logging. Off by default per the catalog's own recommendation (prefer logging).");

            PlacementGateAuditOnBoot = ConfigBinder.BindLocal(config, sAudit, "RunOnBoot", true, "#250: run the one-shot Piece.m_onlyInTeleportArea / EffectArea.Teleport prefab audit at OnWorldReady and log the report.");

            LocationPadFile = ConfigBinder.BindLocal(config, sPad, "File", "lockdown_location_pads.json", "#251: hot-reloaded list of fabricated LocationProxy pads (position, location prefab name, seed).");

            PlacementPolicyEnabled = ConfigBinder.BindSynced(config, configSync, sPlacement, "Enabled", false, "#252: master switch for the placement policy engine. Off by default - it destroys freshly-placed portals that violate a rule.");
            PlacementMinSpacingMeters = ConfigBinder.BindSynced(config, configSync, sPlacement, "MinSpacingMeters", 0f, "#252: minimum XZ distance to any existing portal. 0 disables the spacing rule.", 0f, 2000f);
            PlacementMaxPerPlayer = ConfigBinder.BindSyncedInt(config, configSync, sPlacement, "MaxPerPlayer", 0, "#252: maximum portals attributed (s_creator) to one player. 0 disables the cap rule.", 0, 500);
            PlacementBiomeBanList = ConfigBinder.BindSynced(config, configSync, sPlacement, "BiomeBanList", "", "#252: comma-separated Heightmap.Biome names where a new portal is refused (e.g. 'AshLands,DeepNorth'). Blank disables.");
            PlacementBanInteriors = ConfigBinder.BindSynced(config, configSync, sPlacement, "BanInteriors", false, "#252: refuse a new portal whose position resolves inside a dungeon interior (y>3000).");
            PlacementRespectWards = ConfigBinder.BindSynced(config, configSync, sPlacement, "RespectWards", true, "#252: refuse a new portal inside a guard_stone ward the placer isn't permitted on.");

            GroundDropRefundEnabled = ConfigBinder.BindSynced(config, configSync, sRefund, "Enabled", true, "#253: drop the full build cost on the ground when the placement policy engine rejects a portal.");
            GroundDropRefundMaxPerSecond = ConfigBinder.BindSyncedInt(config, configSync, sRefund, "MaxPerSecond", 5, "#253: rate-limits refunds (per player and globally) to blunt a repeat-send farming exploit.", 1, 50);

            SanctuaryEnabled = ConfigBinder.BindSynced(config, configSync, sSanctuary, "Enabled", false, "#254: destroy newly-arrived hostile creature ZDOs within range of a portal.");
            SanctuaryRadius = ConfigBinder.BindSynced(config, configSync, sSanctuary, "Radius", 24f, "#254: radius around any portal (or managed hub, depending on Scope) treated as sanctuary.", 2f, 200f);
            SanctuaryIncludeEventCreatures = ConfigBinder.BindSynced(config, configSync, sSanctuary, "IncludeEventCreatures", true, "#254: also destroy s_eventCreature spawns (e.g. from a Raid Beacon) that land in the sanctuary.");
            SanctuaryMaxDestroysPerSecond = ConfigBinder.BindSyncedInt(config, configSync, sSanctuary, "MaxDestroysPerSecond", 32, "#254: budget cap so a large spawn wave can't spike a frame.", 1, 256);

            RelayShieldProtectDestroy = ConfigBinder.BindSynced(config, configSync, sShield, "ProtectDestroy", false, "#256: veto client-originated destruction of a vaulted/managed portal (via HandleDestroyedZdoHook). Off by default - see RelayShieldAllowCreatorDestroy.");
            RelayShieldAllowCreatorDestroy = ConfigBinder.BindSynced(config, configSync, sShield, "AllowCreatorDestroy", true, "#256: still allow the portal's own s_creator (or an admin) to demolish it even while protected.");

            RelayFile = ConfigBinder.BindLocal(config, sRelay, "File", "lockdown_relay.json", "#197: hot-reloaded list of named relay destinations a player can cycle through at a relay hub.");
            RelaySelectEmote = ConfigBinder.BindSynced(config, configSync, sRelay, "SelectEmote", "point", "#197: emote a player performs near a relay hub's SOURCE portal (before stepping through) to cycle their chosen destination.");
            RelayLobbyScanRadius = ConfigBinder.BindSynced(config, configSync, sRelay, "LobbyScanRadius", 3f, "#197: distance from a relay lobby pad counted as 'arrived'.", 0.5f, 15f);
            RelaySamplePeriodSeconds = ConfigBinder.BindSynced(config, configSync, sRelay, "SamplePeriodSeconds", 0.5f, "#197: how often connected characters are sampled for lobby arrival.", 0.1f, 3f);
            RelaySecondHopHoldSeconds = ConfigBinder.BindSynced(config, configSync, sRelay, "SecondHopHoldSeconds", 8.5f, "#208: fixed hold after observed lobby arrival before the second RPC_TeleportTo is sent - must stay >= the 8s distant-teleport floor plus margin, never less.", 6f, 20f);
            RelayRetryAfterSeconds = ConfigBinder.BindSynced(config, configSync, sRelay, "RetryAfterSeconds", 4f, "#208: if the traveller hasn't left the lobby this long after the second hop was sent, resend once.", 1f, 15f);
            RelayPerPlayerCooldownSeconds = ConfigBinder.BindSynced(config, configSync, sRelay, "PerPlayerCooldownSeconds", 12f, "#208: minimum time between relay attempts for the same player.", 2f, 120f);
        }
    }
}
