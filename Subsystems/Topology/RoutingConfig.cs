using BepInEx.Configuration;
using ServerSync;
using TortalPortalLite.Core;

namespace TortalPortalLite.Subsystems.Topology
{
    /// <summary>
    /// Config surface for the `routing` domain (Wave 1 catalog #23-#40, #222-#232 - dynamic/conditional
    /// portal routing). Own subsystem, own config file - see Core/ConfigBinder.cs for why this is not
    /// one central class. Sections are grouped by mechanism family rather than one section per engine
    /// (30 engines would mean 30 near-empty sections); each engine reads only the keys it needs.
    /// </summary>
    public static class RoutingConfig
    {
        // --- 9: core / pairing-authority reassertion (#23) ---
        public static ConfigEntry<bool>? TakeoverEnabled;
        public static ConfigEntry<float>? ReassertSeconds;
        public static ConfigEntry<string>? RegistryFile;
        public static ConfigEntry<int>? MaxWritesPerTick;

        // --- 10: hubs / rings / blackout (#25, #26, #27) ---
        public static ConfigEntry<float>? HubDefaultRotateSeconds;
        public static ConfigEntry<float>? HubPrewarmRadius;
        public static ConfigEntry<float>? BlackoutAnnounceSeconds;
        public static ConfigEntry<float>? BlackoutDwellSeconds;
        public static ConfigEntry<float>? RingReassertSeconds;

        // --- 11: schedules / conditions / sweeps (#28, #29, #30, #38, #39) ---
        public static ConfigEntry<float>? ScheduleEvalSeconds;
        public static ConfigEntry<float>? SealedGateEvalSeconds;
        public static ConfigEntry<float>? EventRetargetEvalSeconds;
        public static ConfigEntry<int>? SweepWriteBudgetPerTick;
        public static ConfigEntry<float>? WorldStateEvalSeconds;
        public static ConfigEntry<int>? WorldStatePopulationHysteresis;

        /// <summary>#31 Deterministic Assignment Replacement policy - see RoutingDeterministicAssignmentEngine.cs for RoutingAssignmentPolicy's values.</summary>
        public static ConfigEntry<RoutingAssignmentPolicy>? AssignmentPolicy;

        // --- 12: approach / JIT / contention (#32, #34, #35, #226, #227) ---
        public static ConfigEntry<float>? ApproachPollSeconds;
        public static ConfigEntry<float>? ArmRadius;
        public static ConfigEntry<float>? DisarmHysteresis;
        public static ConfigEntry<float>? InnerCommitRadius;
        public static ConfigEntry<float>? ContentionLockSeconds;
        public static ConfigEntry<bool>? BlackoutOnContention;

        // --- 13: player interaction (#33, #36) ---
        public static ConfigEntry<float>? PingDebounceSeconds;
        public static ConfigEntry<float>? ItemStandScanRadius;
        public static ConfigEntry<float>? EmotePollSecondsNote; // documentation knob only, EmoteSignals owns the real poll

        // --- 14: anchors / world-gen (#24, #37, #40) ---
        public static ConfigEntry<float>? RerollIntervalSeconds;
        public static ConfigEntry<float>? RerollHeightMargin;
        public static ConfigEntry<float>? RerollMaxWorldRadius;
        public static ConfigEntry<int>? RerollMaxAttemptsPerTick;
        public static ConfigEntry<float>? MovingAnchorRefreshSeconds;

        // --- 16: delivery pipeline (#222, #223, #224, #225, #228, #229, #231) ---
        public static ConfigEntry<float>? PreStreamRadius;
        public static ConfigEntry<int>? PreStreamMaxZdosPerCycle;
        public static ConfigEntry<float>? JoinBroadcastPollSeconds;
        public static ConfigEntry<float>? PrewarmRadius;
        public static ConfigEntry<float>? FastTransitTriggerRadius;
        public static ConfigEntry<int>? ZoneGhostBudgetPerTick;
        public static ConfigEntry<float>? PeerProbeIntervalSeconds;

        /// <summary>Int, not uint - ConfigBinder has no synced-uint helper (see Core/ConfigBinder.cs). Cast to uint at each call site; MaxWritesPerTick-style bounds keep it positive.</summary>
        public static ConfigEntry<int>? RevisionTouchStride;

        public static void Bind(ConfigFile config, ConfigSync configSync)
        {
            const string sCore = "9 - Routing: Core";
            const string sHub = "10 - Routing: Hubs and Rings";
            const string sCond = "11 - Routing: Schedules and Conditions";
            const string sJit = "12 - Routing: Approach and JIT";
            const string sPlayer = "13 - Routing: Player Interaction";
            const string sAnchor = "14 - Routing: Anchors and World Gen";
            const string sDelivery = "16 - Routing: Delivery Pipeline";

            TakeoverEnabled = ConfigBinder.BindSynced(config, configSync, sCore, "TakeoverEnabled", true,
                "Master switch for every dynamic-routing engine in this domain (catalog #23). ConnectPortalsHook only exposes a postfix (no prefix-cancel broker exists yet), so 'takeover' here means: reassert every managed portal's tag/connection faster than vanilla's 5s reconciler, not a true prefix veto - see RoutingPairingAuthorityEngine's doc comment.");
            ReassertSeconds = ConfigBinder.BindSynced(config, configSync, sCore, "ReassertSeconds", 1.0f,
                "How often the pairing-authority engine re-checks and, if needed, re-writes each dynamically-managed portal's tag/connection. Must stay well under vanilla's own 5s Game.ConnectPortals pass.", 0.2f, 4.5f);
            RegistryFile = ConfigBinder.BindLocal(config, sCore, "File", "routing.json",
                "Path (relative to the plugin folder) to the hot-reloaded dynamic-routing declarations (hubs, rings, schedules, sealed gates, ...). Local, not synced - a file path is server-machine-specific.");
            MaxWritesPerTick = ConfigBinder.BindSyncedInt(config, configSync, sCore, "MaxWritesPerTick", 64,
                "Caps how many portal ZDOs any single routing engine will rewrite in one tick, so a large managed set never spikes a frame (ZDOMan.SendZDOs re-serializes whole ZDOs with no delta encoding).", 1, 4000);

            HubDefaultRotateSeconds = ConfigBinder.BindSynced(config, configSync, sHub, "DefaultRotateSeconds", 60f,
                "Default dwell time before a Rotating Hub (#25) advances to its next destination, for hubs that don't override it in routing.json.", 8f, 86400f);
            HubPrewarmRadius = ConfigBinder.BindSynced(config, configSync, sHub, "PrewarmRadius", 200f,
                "Radius around a hub within which every connected peer is pre-warmed (ForceSendZDO) with the new destination on rotation.", 10f, 2000f);
            BlackoutAnnounceSeconds = ConfigBinder.BindSynced(config, configSync, sHub, "BlackoutAnnounceSeconds", 30f,
                "Blackout Swap Protocol (#26) phase 1 dwell: how long the announce tag is shown before the blackout.", 0f, 600f);
            BlackoutDwellSeconds = ConfigBinder.BindSynced(config, configSync, sHub, "BlackoutDwellSeconds", 3f,
                "Blackout Swap Protocol (#26) phase 2 dwell: how long the portal sits disconnected before the re-point, long enough for the client's 0.5s UpdatePortal poll to observe the flip.", 1f, 30f);
            RingReassertSeconds = ConfigBinder.BindSynced(config, configSync, sHub, "RingReassertSeconds", 30f,
                "One-Way Ring Rotation (#27) slow heartbeat reassertion - idempotent (SetConnection no-ops on an unchanged value) so this is cheap even at a short interval.", 5f, 300f);

            ScheduleEvalSeconds = ConfigBinder.BindSynced(config, configSync, sCond, "ScheduleEvalSeconds", 1.0f,
                "Scheduled Routing (#28) clock-evaluation tick - level-triggered (compute target, compare, write) so a sleep-skip never skips a transition.", 0.2f, 10f);
            SealedGateEvalSeconds = ConfigBinder.BindSynced(config, configSync, sCond, "SealedGateEvalSeconds", 1.0f,
                "Progression-Gated Sealed Gate (#29) global-key evaluation tick.", 0.2f, 10f);
            EventRetargetEvalSeconds = ConfigBinder.BindSynced(config, configSync, sCond, "EventRetargetEvalSeconds", 1.0f,
                "Event-Driven Retarget (#30) rule evaluation tick (global keys / random events / boss activity).", 0.2f, 10f);
            SweepWriteBudgetPerTick = ConfigBinder.BindSyncedInt(config, configSync, sCond, "SweepWriteBudgetPerTick", 16,
                "Seasonal/Event Network Swap (#38) and other mass-rewrite sweeps: portals rewritten per tick, to avoid monopolising every peer's send queue (no delta encoding, 10240-byte cap).", 1, 512);
            WorldStateEvalSeconds = ConfigBinder.BindSynced(config, configSync, sCond, "WorldStateEvalSeconds", 1.0f,
                "World-State Conditional Routing (#39) population/occupancy evaluation tick.", 0.2f, 10f);
            WorldStatePopulationHysteresis = ConfigBinder.BindSyncedInt(config, configSync, sCond, "PopulationHysteresis", 1,
                "Minimum player-count change required before a population-gated portal flips state again, so one login/logout doesn't flap it.", 0, 10);
            AssignmentPolicy = ConfigBinder.BindLocal(config, sCond, "AssignmentPolicy", RoutingAssignmentPolicy.Nearest,
                "Deterministic Assignment Replacement (#31): the policy FindRandomUnconnectedPortalHook uses in place of vanilla's uniformly-random same-tag pairing. VanillaRandom disables this option entirely.");

            ApproachPollSeconds = ConfigBinder.BindSynced(config, configSync, sJit, "ApproachPollSeconds", 0.1f,
                "How often the shared approach/arm watcher (#32 and dependents) samples connected-character positions against managed portal positions.", 0.05f, 1f);
            ArmRadius = ConfigBinder.BindSynced(config, configSync, sJit, "ArmRadius", 12f,
                "Outer radius at which a Just-In-Time gate arms for an approaching player (#32).", 2f, 60f);
            DisarmHysteresis = ConfigBinder.BindSynced(config, configSync, sJit, "DisarmHysteresis", 3f,
                "Extra distance beyond ArmRadius a player must cross outward before a gate disarms - prevents flicker for a player hovering at the boundary.", 0f, 30f);
            InnerCommitRadius = ConfigBinder.BindSynced(config, configSync, sJit, "InnerCommitRadius", 3f,
                "Inner radius at which an approach is treated as 'committed' (Self-Disconnecting Portals speculative blackout, #35).", 0.5f, 20f);
            ContentionLockSeconds = ConfigBinder.BindSynced(config, configSync, sJit, "ContentionLockSeconds", 6f,
                "Queue Dispatch (#34) / JIT contention: how long the first claimant holds a shared gate before the next arrival can claim it.", 1f, 60f);
            BlackoutOnContention = ConfigBinder.BindSynced(config, configSync, sJit, "BlackoutOnContention", false,
                "If true, a JIT gate with two simultaneous claimants blacks out (safely blocks both) instead of honouring the first claimant only.");

            PingDebounceSeconds = ConfigBinder.BindSynced(config, configSync, sPlayer, "PingDebounceSeconds", 2f,
                "Player-Requested Routing (#36): minimum time between two map-ping destination picks from the same player, since a ping is public and otherwise trivially spammable.", 0.5f, 30f);
            ItemStandScanRadius = ConfigBinder.BindSynced(config, configSync, sPlayer, "ItemStandScanRadius", 4f,
                "Player-Requested Routing (#36): radius around a managed portal searched for an adjacent ItemStand ZDO whose s_item selects a destination.", 1f, 20f);
            EmotePollSecondsNote = ConfigBinder.BindLocal(config, sPlayer, "EmotePollSecondsNote", 0.25f,
                "Documentation only - the actual emote poll cadence is owned by Core/Data/EmoteSignals.cs, not this domain.");

            RerollIntervalSeconds = ConfigBinder.BindSynced(config, configSync, sAnchor, "RerollIntervalSeconds", 86400f,
                "Randomised Roguelike Re-Roll (#37): default real-time interval between re-rolls for a gate that doesn't override it.", 30f, 2592000f);
            RerollHeightMargin = ConfigBinder.BindSynced(config, configSync, sAnchor, "RerollHeightMargin", 2f,
                "Randomised Roguelike Re-Roll (#37): metres above WorldGenerator.GetHeight required for a candidate to be accepted (heights are only accurate to ~2m - terraforming/locations aren't modelled).", 0.5f, 20f);
            RerollMaxWorldRadius = ConfigBinder.BindSynced(config, configSync, sAnchor, "RerollMaxWorldRadius", 8000f,
                "Randomised Roguelike Re-Roll (#37): candidates are sampled uniformly inside this radius of world centre.", 100f, 20000f);
            RerollMaxAttemptsPerTick = ConfigBinder.BindSyncedInt(config, configSync, sAnchor, "RerollMaxAttemptsPerTick", 40,
                "Randomised Roguelike Re-Roll (#37): candidate points tested per tick before giving up until next tick (pure math, cheap).", 1, 2000);
            MovingAnchorRefreshSeconds = ConfigBinder.BindSynced(config, configSync, sAnchor, "MovingAnchorRefreshSeconds", 2f,
                "Moving Destination Anchor (#40): how often a moving anchor's target is re-checked for staleness and re-pushed to nearby peers.", 0.5f, 20f);

            PreStreamRadius = ConfigBinder.BindSynced(config, configSync, sDelivery, "PreStreamRadius", 20f,
                "Pre-Stream Engine (#222): radius at which an approaching player's client begins receiving the destination area's ZDOs. Only takes effect once ZDOMan.CreateSyncList has a postfix broker - see the NEEDS NEW HOOK BROKER note in RoutingPreStreamEngine.cs.", 5f, 100f);
            PreStreamMaxZdosPerCycle = ConfigBinder.BindSyncedInt(config, configSync, sDelivery, "PreStreamMaxZdosPerCycle", 400,
                "Pre-Stream Engine (#222): cap on appended ZDOs per send cycle per subscribed peer.", 10, 5000);
            JoinBroadcastPollSeconds = ConfigBinder.BindSynced(config, configSync, sDelivery, "JoinBroadcastPollSeconds", 1.0f,
                "Join-Time Portal Registry Broadcast (#223): how often newly-connected peers are detected (poll-based approximation of a ZNet.RPC_PeerInfo postfix - no broker exists for that method yet).", 0.25f, 5f);
            PrewarmRadius = ConfigBinder.BindSynced(config, configSync, sDelivery, "PrewarmRadius", 30f,
                "DestinationPrewarm (#224): radius around a source portal within which nearby peers are force-sent the destination ZDO the instant a managed connection write happens.", 5f, 100f);
            FastTransitTriggerRadius = ConfigBinder.BindSynced(config, configSync, sDelivery, "FastTransitTriggerRadius", 1.2f,
                "Fast-Transit Mode (#225) / Parked Terminal (#227): XZ radius of the position-based trigger substituting for the (server-invisible) TeleportWorldTrigger collider.", 0.5f, 5f);
            ZoneGhostBudgetPerTick = ConfigBinder.BindSyncedInt(config, configSync, sDelivery, "ZoneGhostBudgetPerTick", 1,
                "Destination Zone Ghost Pre-Generation (#228): zones ghost-generated per tick (ZoneSystem.SpawnZone already budgets internally; this caps how many calls this engine makes per tick).", 1, 25);
            PeerProbeIntervalSeconds = ConfigBinder.BindSynced(config, configSync, sDelivery, "PeerProbeIntervalSeconds", 10f,
                "Peer Link Probe (#229): seconds between RTT pings per peer.", 2f, 120f);
            RevisionTouchStride = ConfigBinder.BindSyncedInt(config, configSync, sDelivery, "RevisionTouchStride", 4096,
                "Revision-Bump Redelivery (#231): DataRevision increment applied by the 'touch' primitive so a re-push clears any peer's cached revision even when no field actually changed.", 1, 1000000);
        }
    }
}
