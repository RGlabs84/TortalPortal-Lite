using BepInEx.Configuration;
using ServerSync;
using TortalPortalLite.Core;

namespace TortalPortalLite.Subsystems.WorldOps
{
    /// <summary>
    /// Config surface for the "wildcard cluster B" domain (motion/companion/world-event/Sector-Zero
    /// mechanisms, Wave 3, catalog #129/#130/#131/#136/#143+#244/#148/#233/#234/#235/#236/#237/#238/
    /// #239/#240; #246 is a documented duplicate of Wave 2's LockdownBossWatchdogEngine - see that
    /// engine's own header - and is not implemented here). Own subsystem, own config file - see
    /// Core/ConfigBinder.cs for why this is not one central class.
    /// </summary>
    public static class WildcardBConfig
    {
        public static ConfigEntry<bool>? Enabled;

        // --- #129 Skyfall/Seabed/Underworld arrival ladder (pure classification, no state) ---
        public static ConfigEntry<bool>? WarnOnDangerousAltitude;

        // --- #143/#244 Sector Zero guard ---
        public static ConfigEntry<float>? SectorZeroProbeIntervalSeconds;

        // --- #130 Crypt Ingress ---
        public static ConfigEntry<float>? CryptIngressPollSeconds;
        public static ConfigEntry<float>? CryptIngressSearchRadius;

        // --- #131 Ephemeral Event Gates ---
        public static ConfigEntry<bool>? EventGateEnabled;
        public static ConfigEntry<string>? EventGateTriggerGlobalKey;
        public static ConfigEntry<string>? EventGateAtPosition;
        public static ConfigEntry<string>? EventGateDestPosition;
        public static ConfigEntry<string>? EventGateLabel;
        public static ConfigEntry<float>? EventGateDurationSeconds;
        public static ConfigEntry<bool>? EventGateSpawnBeacon;
        public static ConfigEntry<string>? EventGateBeaconEventName;
        public static ConfigEntry<float>? EventGateBeaconRadius;

        // --- #136 Portal As Trigger, RPC As Transport ---
        public static ConfigEntry<bool>? TriggerTransportEnabled;
        public static ConfigEntry<float>? TriggerTransportPollSeconds;
        public static ConfigEntry<float>? TriggerTransportRadius;
        public static ConfigEntry<float>? TriggerTransportCooldownSeconds;
        public static ConfigEntry<string>? TriggerTransportBookmarkEmote;

        // --- #148 Server-Enforced Portal Caps ---
        public static ConfigEntry<bool>? PortalCapEnabled;
        public static ConfigEntry<int>? PortalCapPerCreator;
        public static ConfigEntry<float>? PortalCapSweepSeconds;

        // --- #233 The Event Beacon ---
        public static ConfigEntry<bool>? EventBeaconApiEnabled;

        // --- #234 The Landing Pad Engine ---
        public static ConfigEntry<bool>? LandingPadEnabled;
        public static ConfigEntry<float>? LandingPadRadius;

        // --- #235 Phantom Survival ---
        public static ConfigEntry<bool>? PhantomSurvivalHardenOnCreate;

        // --- #236 The Ferry Route ---
        public static ConfigEntry<bool>? FerryRouteEnabled;
        public static ConfigEntry<float>? FerryWaypointArriveRadius;
        public static ConfigEntry<float>? FerryRudderGain;
        public static ConfigEntry<float>? FerryCommandIntervalSeconds;

        // --- #237 Player's Ship, Corrected (kinematic tow) ---
        public static ConfigEntry<bool>? KinematicShipEnabled;
        public static ConfigEntry<float>? KinematicShipMaxStepMeters;

        // --- #238 Tame And Cart Follow-Through ---
        public static ConfigEntry<bool>? CompanionTransitEnabled;
        public static ConfigEntry<float>? CompanionTransitRadius;
        public static ConfigEntry<int>? CompanionTransitMaxFollowers;
        public static ConfigEntry<float>? CompanionTransitCooldownSeconds;
        public static ConfigEntry<float>? CompanionTransitPollSeconds;

        // --- #239 Per-Peer Location Icons ---
        public static ConfigEntry<bool>? FactionSpawnEnabled;
        public static ConfigEntry<string>? FactionSpawnDefaultHub;
        public static ConfigEntry<float>? FactionSpawnReassertSeconds;

        // --- #240 Ambush Gates ---
        public static ConfigEntry<bool>? AmbushGatesEnabled;
        public static ConfigEntry<string>? AmbushGateEventName;
        public static ConfigEntry<float>? AmbushGateArriveGraceSeconds;

        public static void Bind(ConfigFile config, ConfigSync configSync)
        {
            const string sGeneral = "150 - WildcardB: General";
            const string sSectorZero = "151 - WildcardB: Sector Zero Guard";
            const string sCrypt = "152 - WildcardB: Crypt Ingress";
            const string sEventGate = "153 - WildcardB: Ephemeral Event Gates";
            const string sTrigger = "154 - WildcardB: Trigger Transport";
            const string sCap = "155 - WildcardB: Portal Caps";
            const string sBeacon = "156 - WildcardB: Event Beacon";
            const string sPad = "157 - WildcardB: Landing Pad";
            const string sSurvival = "158 - WildcardB: Phantom Survival";
            const string sFerry = "159 - WildcardB: Ferry Route";
            const string sKinShip = "160 - WildcardB: Kinematic Ship Tow";
            const string sCompanion = "161 - WildcardB: Companion Transit";
            const string sFaction = "162 - WildcardB: Faction Spawn Icons";
            const string sAmbush = "163 - WildcardB: Ambush Gates";

            Enabled = ConfigBinder.BindSynced(config, configSync, sGeneral, "Enabled", true,
                "Master switch for the whole wildcard cluster B domain (motion/companion/world-event/Sector-Zero engines).");
            WarnOnDangerousAltitude = ConfigBinder.BindLocal(config, sGeneral, "WarnOnDangerousAltitude", true,
                "Log a warning when another engine in this mod computes a destination anchor whose arrival ladder (#129) would impose a 15s black screen or a lethal fall.");

            SectorZeroProbeIntervalSeconds = ConfigBinder.BindLocal(config, sSectorZero, "ProbeIntervalSeconds", 60f,
                "How often (seconds) to log the #244 decisive-test probe (IsActiveAreaLoaded/m_zones/NrOfInstances at the server's own pinned reference zone). Diagnostic only - the safety guard itself runs on every placement, not on this timer.");

            CryptIngressPollSeconds = ConfigBinder.BindLocal(config, sCrypt, "PollSeconds", 5f,
                "How often (seconds) #130 Crypt Ingress re-checks its configured dungeon routes for a newly-generated room list.");
            CryptIngressSearchRadius = ConfigBinder.BindLocal(config, sCrypt, "SearchRadius", 80f,
                "Radius (metres) around the dungeon interior probe point (zone centre, entrance Y + 5000) to search for the DungeonGenerator ZDO.");

            EventGateEnabled = ConfigBinder.BindSynced(config, configSync, sEventGate, "Enabled", false,
                "Master switch for #131 Ephemeral Event Gates (the built-in single demo gate driven by TriggerGlobalKey; the OpenGate/CloseGate API is always available to other WildcardB engines regardless of this switch).");
            EventGateTriggerGlobalKey = ConfigBinder.BindSynced(config, configSync, sEventGate, "TriggerGlobalKey", "defeated_eikthyr",
                "Global key name (ZoneSystem.GetGlobalKey) whose false->true transition opens the demo event gate - e.g. a boss-defeated key.");
            EventGateAtPosition = ConfigBinder.BindSynced(config, configSync, sEventGate, "GatePosition", "0,0,0",
                "World position 'x,y,z' where the ephemeral portal appears when the trigger fires.");
            EventGateDestPosition = ConfigBinder.BindSynced(config, configSync, sEventGate, "DestPosition", "0,0,0",
                "World position 'x,y,z' the ephemeral portal leads to.");
            EventGateLabel = ConfigBinder.BindSynced(config, configSync, sEventGate, "Label", "Event Gate",
                "s_tag text (max 10 chars after this domain's own truncation) shown on the ephemeral portal pair.");
            EventGateDurationSeconds = ConfigBinder.BindSynced(config, configSync, sEventGate, "DurationSeconds", 1200f,
                "How long (seconds) the ephemeral gate stays open before this domain destroys both ends.");
            EventGateSpawnBeacon = ConfigBinder.BindSynced(config, configSync, sEventGate, "SpawnBeacon", false,
                "Also inject a #233 persistent-event beacon (removable map marker + radius) centred on the gate while it is open.");
            EventGateBeaconEventName = ConfigBinder.BindSynced(config, configSync, sEventGate, "BeaconEventName", "",
                "PersistentEventSystem.m_possibleEvents internalName to inject as the gate's beacon. Blank disables the beacon even if SpawnBeacon is true (dumped names are asset data, not guessed).");
            EventGateBeaconRadius = ConfigBinder.BindSynced(config, configSync, sEventGate, "BeaconRadius", 20f,
                "Radius (metres) of the injected beacon's map ring and environment-override area.");

            TriggerTransportEnabled = ConfigBinder.BindSynced(config, configSync, sTrigger, "Enabled", false,
                "Master switch for #136 Portal As Trigger, RPC As Transport - marked waygate portals ignore their Connection field and route per-player via a position poll + RPC_TeleportTo instead.");
            TriggerTransportPollSeconds = ConfigBinder.BindLocal(config, sTrigger, "PollSeconds", 0.3f,
                "How often (seconds) connected character positions are compared against waygate portal positions.");
            TriggerTransportRadius = ConfigBinder.BindSynced(config, configSync, sTrigger, "TriggerRadius", 3.5f,
                "Distance (metres) from a waygate portal at which a player standing there is teleported.");
            TriggerTransportCooldownSeconds = ConfigBinder.BindSynced(config, configSync, sTrigger, "CooldownSeconds", 12f,
                "Minimum seconds between two trigger-transport teleports for the same player - below vanilla's own m_teleportCooldown window would double-fire against a still-teleporting player.");
            TriggerTransportBookmarkEmote = ConfigBinder.BindSynced(config, configSync, sTrigger, "BookmarkEmote", "wave",
                "Emote a player performs to bookmark their current position/rotation as their own personal waygate destination.");

            PortalCapEnabled = ConfigBinder.BindSynced(config, configSync, sCap, "Enabled", false,
                "Master switch for #148 Server-Enforced Portal Caps - destroys a creator's newest portal ZDOs past the configured per-creator cap on arrival, unlike a client-side placement cap this cannot be bypassed by an unmodified client.");
            PortalCapPerCreator = ConfigBinder.BindSyncedInt(config, configSync, sCap, "PerCreatorCap", 6,
                "Maximum live portals a single s_creator (player id) may own before the newest excess are destroyed.", 1, 999);
            PortalCapSweepSeconds = ConfigBinder.BindLocal(config, sCap, "SweepSeconds", 2f,
                "How often (seconds) the per-creator portal census tally is recomputed and enforced.");

            EventBeaconApiEnabled = ConfigBinder.BindSynced(config, configSync, sBeacon, "Enabled", true,
                "Master switch for the #233 Event Beacon injection API other WildcardB engines call into (e.g. the Event Gate's optional beacon). Does not itself inject anything on its own.");

            LandingPadEnabled = ConfigBinder.BindSynced(config, configSync, sPad, "Enabled", false,
                "Master switch for #234 The Landing Pad Engine's TryFlattenDisc API.");
            LandingPadRadius = ConfigBinder.BindSynced(config, configSync, sPad, "DefaultRadius", 6f,
                "Default disc radius (metres) when a caller does not specify one.");

            PhantomSurvivalHardenOnCreate = ConfigBinder.BindSynced(config, configSync, sSurvival, "HardenOnCreate", true,
                "Whether this domain's own fabricated phantoms/anchors (Crypt Ingress interior anchors, Event Gate portals) get the #235 WearNTear LoadFields hardening applied automatically right after creation.");

            FerryRouteEnabled = ConfigBinder.BindSynced(config, configSync, sFerry, "Enabled", false,
                "Master switch for #236 The Ferry Route - server-steered ship RPC command API and route runner.");
            FerryWaypointArriveRadius = ConfigBinder.BindSynced(config, configSync, sFerry, "WaypointArriveRadius", 8f,
                "Distance (metres, XZ) from a waypoint at which the ferry controller considers it arrived and issues Stop.");
            FerryRudderGain = ConfigBinder.BindSynced(config, configSync, sFerry, "RudderGain", 1f,
                "Proportional gain applied to the heading error (radians) when computing the clamped Rudder value sent to the ship.");
            FerryCommandIntervalSeconds = ConfigBinder.BindLocal(config, sFerry, "CommandIntervalSeconds", 0.25f,
                "How often (seconds) the ferry controller re-sends Rudder/Forward/Stop RPCs to the owning peer.");

            KinematicShipEnabled = ConfigBinder.BindSynced(config, configSync, sKinShip, "Enabled", false,
                "Master switch for #237 Player's Ship, Corrected - server-owned kinematic ship tow API (SetPosition/SetRotation steps small enough to keep riders under the 4m attach-correction limit).");
            KinematicShipMaxStepMeters = ConfigBinder.BindSynced(config, configSync, sKinShip, "MaxStepMeters", 0.4f,
                "Maximum per-call SetPosition displacement (metres) - kept comfortably under the ~4m rider-detach threshold given ZSyncTransform's own 20%-per-tick lerp settling behaviour.", 0.01f, 3.5f);

            CompanionTransitEnabled = ConfigBinder.BindSynced(config, configSync, sCompanion, "Enabled", false,
                "Master switch for #238 Tame And Cart Follow-Through - relocates a travelling player's nearby tamed followers and unpulled carts to their destination portal's exit.");
            CompanionTransitRadius = ConfigBinder.BindSynced(config, configSync, sCompanion, "SourceRadius", 12f,
                "Radius (metres) around the source portal searched for eligible followers/carts at the moment of transit.");
            CompanionTransitMaxFollowers = ConfigBinder.BindSyncedInt(config, configSync, sCompanion, "MaxFollowers", 6,
                "Maximum number of creature/cart ZDOs relocated per single transit.", 1, 32);
            CompanionTransitCooldownSeconds = ConfigBinder.BindSynced(config, configSync, sCompanion, "PerPlayerCooldownSeconds", 20f,
                "Minimum seconds between two companion-transit relocations for the same travelling player.");
            CompanionTransitPollSeconds = ConfigBinder.BindLocal(config, sCompanion, "PollSeconds", 1f,
                "How often (seconds) connected character positions are sampled for the transit-detection heuristic (portal-adjacent, then reappearing at that portal's paired exit).");

            FactionSpawnEnabled = ConfigBinder.BindSynced(config, configSync, sFaction, "Enabled", false,
                "Master switch for #239 Per-Peer Location Icons - re-sends a corrected LocationIcons payload to hubbed peers so their StartTemple icon (and therefore bed-less/new-character spawn) points at an assigned hub instead of the real start temple.");
            FactionSpawnDefaultHub = ConfigBinder.BindSynced(config, configSync, sFaction, "DefaultHubPosition", "",
                "World position 'x,y,z' applied to every connected player with no per-player hub override (set via the engine's own SetPlayerHub API). Blank disables the default - only explicitly-assigned players are affected.");
            FactionSpawnReassertSeconds = ConfigBinder.BindLocal(config, sFaction, "ReassertSeconds", 8f,
                "How often (seconds) each hubbed peer's corrected icon list is re-sent, since a vanilla location-placement broadcast (peer 0) would otherwise silently restore the real temple icon for them.");

            AmbushGatesEnabled = ConfigBinder.BindSynced(config, configSync, sAmbush, "Enabled", false,
                "Master switch for #240 Ambush Gates - fires a RandEventSystem raid at a portal's exit on arrival and locks that portal's Connection until the event ends.");
            AmbushGateEventName = ConfigBinder.BindSynced(config, configSync, sAmbush, "EventName", "",
                "RandEventSystem.instance.m_events internalName to fire on ambush. Blank disables ambush firing even if AmbushGatesEnabled is true (dumped names are asset data, not guessed).");
            AmbushGateArriveGraceSeconds = ConfigBinder.BindSynced(config, configSync, sAmbush, "ArriveGraceSeconds", 8f,
                "Seconds to wait after a detected arrival before firing the ambush (covers the tail of the vanilla teleport black screen).");
        }
    }
}
