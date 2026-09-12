using BepInEx.Configuration;
using ServerSync;
using TortalPortalLite.Core;

namespace TortalPortalLite.Subsystems.Enforcement
{
    /// <summary>
    /// Config surface for the Access domain (Wave 2, catalog #s 46-62, 209-221 - access control /
    /// identity / locks). Follows the same per-subsystem-owns-its-own-config-class pattern as
    /// Subsystems/Foundations/FoundationsConfig.cs and Subsystems/Topology/TopologiesConfig.cs (see
    /// Core/ConfigBinder.cs's own doc comment for why there is no single central config class).
    /// </summary>
    public static class AccessConfig
    {
        // --- storage ---
        public static ConfigEntry<string>? AclFile;
        public static ConfigEntry<string>? RosterFile;
        public static ConfigEntry<float>? StoreFlushSeconds;

        // --- #49 Tag Watchdog / #47 Tag Author Rewrite / #218 Client-Edit Dirty Guard ---
        public static ConfigEntry<float>? WatchdogIntervalSeconds;
        public static ConfigEntry<bool>? WatchdogRewriteTagAuthor;

        // --- #51 Reserved Tag Namespace ---
        public static ConfigEntry<string>? ReservedPrefixes;
        public static ConfigEntry<string>? ReservedExactNames;

        // --- #52 Shadow Ward Index ---
        public static ConfigEntry<float>? WardResweepSeconds;
        public static ConfigEntry<int>? WardSectorBudgetPerTick;
        public static ConfigEntry<float>? WardDefaultRadius;

        // --- #53 Portal ACL / emote commands ---
        public static ConfigEntry<string>? EmoteClaim;
        public static ConfigEntry<string>? EmoteToggleLock;
        public static ConfigEntry<string>? EmoteCoOwnerAdd;
        public static ConfigEntry<float>? EmoteCoOwnerRange;

        // --- #55 Anti-Grief Tag Integrity ---
        public static ConfigEntry<bool>? SquattingEnabled;
        public static ConfigEntry<bool>? OrphanReportEnabled;

        // --- #56 Destroy Veto ---
        public static ConfigEntry<bool>? DestroyVetoEnabled;

        // --- #57/#210 RPC Hardening ---
        public static ConfigEntry<bool>? GlobalKeyGuardEnabled;
        public static ConfigEntry<string>? GuardedGlobalKeys;

        // --- #58 Progression Gate ---
        public static ConfigEntry<bool>? ProgressionGateEnabled;

        // --- #59 Key-Item Requirement ---
        public static ConfigEntry<float>? KeyItemScanSeconds;
        public static ConfigEntry<float>? KeyItemContainerSearchRadius;

        // --- #61 Retag Rate Limit ---
        public static ConfigEntry<int>? RateLimitTagChangesPerWindow;
        public static ConfigEntry<float>? RateLimitWindowSeconds;
        public static ConfigEntry<float>? RateLimitPortalCooldownSeconds;
        public static ConfigEntry<int>? RateLimitAbuseCountBeforeKick;

        // --- #48/#216 Ownership Pin ---
        public static ConfigEntry<float>? PinReconcileSeconds;
        public static ConfigEntry<int>? PinClaimsBeforeKick;
        public static ConfigEntry<float>? PinClaimWindowSeconds;

        // --- #211/#212 Per-Peer Withholding, #213 Destination Pre-Delivery ---
        public static ConfigEntry<bool>? PreDeliveryEnabled;
        public static ConfigEntry<float>? PreDeliveryRadius;
        public static ConfigEntry<float>? PreDeliveryIntervalSeconds;

        // --- #217 Arrival Bouncer ---
        public static ConfigEntry<bool>? ArrivalBouncerEnabled;
        public static ConfigEntry<float>? ArrivalSampleSeconds;
        public static ConfigEntry<float>? ArrivalReturnDelaySeconds;
        public static ConfigEntry<float>? ArrivalRadius;

        // --- #219 Boss Lockdown Correction ---
        public static ConfigEntry<bool>? BossLockdownActive;

        public static void Bind(ConfigFile config, ConfigSync configSync)
        {
            const string sStore = "9 - Access: Storage";
            const string sWatchdog = "10 - Access: Tag Watchdog";
            const string sReserved = "11 - Access: Reserved Namespace";
            const string sWard = "12 - Access: Shadow Ward Index";
            const string sAcl = "13 - Access: Portal ACL";
            const string sGrief = "14 - Access: Anti-Grief";
            const string sDestroy = "16 - Access: Destroy Veto";
            const string sRpc = "17 - Access: RPC Hardening";
            const string sProgression = "18 - Access: Progression Gate";
            const string sKeyItem = "19 - Access: Key-Item Requirement";
            const string sRate = "20 - Access: Retag Rate Limit";
            const string sPin = "21 - Access: Ownership Pin";
            const string sPreDelivery = "22 - Access: Per-Peer Delivery";
            const string sBouncer = "23 - Access: Arrival Bouncer";
            const string sBoss = "24 - Access: Boss Lockdown";

            AclFile = ConfigBinder.BindLocal(config, sStore, "AclFile", "access_acl.json",
                "Path (relative to the plugin folder) to the mod-owned per-portal ACL store (co-owners, lock level, approved tag/partner, retag counters). Local, not synced - machine-specific.");
            RosterFile = ConfigBinder.BindLocal(config, sStore, "RosterFile", "access_rosters.json",
                "Path (relative to the plugin folder) to the mod-owned team/guild roster store (#60).");
            StoreFlushSeconds = ConfigBinder.BindLocal(config, sStore, "FlushSeconds", 10f,
                "How often dirty in-memory ACL/roster state is flushed to disk.");

            WatchdogIntervalSeconds = ConfigBinder.BindSynced(config, configSync, sWatchdog, "IntervalSeconds", 1.5f,
                "How often the Tag Watchdog (#49) diffs the census against approved ACL state and reverts drift. Cheaper/faster than vanilla's own 5s ConnectPortals pass.", 0.5f, 5f);
            WatchdogRewriteTagAuthor = ConfigBinder.BindSynced(config, configSync, sWatchdog, "RewriteTagAuthor", true,
                "#47 Tag Author Rewrite - overwrite s_tagauthor with the verified account of whoever actually caused a tag change, instead of trusting the client-supplied value.");

            ReservedPrefixes = ConfigBinder.BindLocal(config, sReserved, "Prefixes", "⌁,!",
                "#51 Reserved Tag Namespace - comma-separated leading characters only the server/admins may write. Matches TagCodec's own '!' admin sigil plus an extra visible marker.");
            ReservedExactNames = ConfigBinder.BindLocal(config, sReserved, "ExactNames", "",
                "#51 - comma-separated exact reserved tag strings (not just prefixes), e.g. 'HUB,SPAWN'.");

            WardResweepSeconds = ConfigBinder.BindLocal(config, sWard, "ResweepSeconds", 45f,
                "#52 Shadow Ward Index - how often the ward index does a full resweep of PrivateArea ZDOs (incremental updates also arrive from RpcZdoDataHook between resweeps).");
            WardSectorBudgetPerTick = ConfigBinder.BindLocal(config, sWard, "SectorBudgetPerTick", 200,
                "#52 - non-empty sectors scanned per OnUpdate tick while resweeping, to avoid a frame spike on a large world.");
            WardDefaultRadius = ConfigBinder.BindLocal(config, sWard, "DefaultRadius", 10f,
                "#52 - fallback ward radius when the prefab's own PrivateArea.m_radius cannot be resolved.");

            EmoteClaim = ConfigBinder.BindLocal(config, sAcl, "ClaimEmote", "point",
                "#53 Portal ACL - emote that claims the nearest unclaimed portal (stand next to it, perform this emote).");
            EmoteToggleLock = ConfigBinder.BindLocal(config, sAcl, "ToggleLockEmote", "wave",
                "#53 - emote that toggles the nearest owned portal's lock on/off.");
            EmoteCoOwnerAdd = ConfigBinder.BindLocal(config, sAcl, "CoOwnerEmote", "challenge",
                "#53 - emote (performed by the owner, standing near both the portal and the candidate) that adds the nearest OTHER player as a co-owner.");
            EmoteCoOwnerRange = ConfigBinder.BindSynced(config, configSync, sAcl, "CoOwnerRange", 5f,
                "#53 - max metres between the acting player, the portal and the candidate for claim/lock/co-owner emotes to resolve unambiguously.", 1f, 30f);

            SquattingEnabled = ConfigBinder.BindSynced(config, configSync, sGrief, "SquattingDetection", true,
                "#55 Anti-Grief Tag Integrity - maintain a first-claim registry per normalised tag and revert a later account's collision.");
            OrphanReportEnabled = ConfigBinder.BindLocal(config, sGrief, "OrphanReport", true,
                "#55 - report odd-count same-tag groups (the '3 portals, 1 orphan' denial attack) in the heartbeat/health surface.");

            DestroyVetoEnabled = ConfigBinder.BindSynced(config, configSync, sDestroy, "Enabled", true,
                "#56 Destroy Veto - refuse an unauthorised HandleDestroyedZDO for a portal this mod protects (ACL-locked, pinned, or ward-covered).");

            GlobalKeyGuardEnabled = ConfigBinder.BindSynced(config, configSync, sRpc, "GlobalKeyGuard", true,
                "#57/#210 - reactively restore protected world global keys (see GuardedGlobalKeys) if a non-admin write changes them, since RPC_SetGlobalKey itself has no sender check and no hook broker exists yet to veto it directly (see NEEDS NEW HOOK BROKER in AccessRpcHardeningEngine.cs).");
            GuardedGlobalKeys = ConfigBinder.BindLocal(config, sRpc, "GuardedKeys", "nobossportals,noportals",
                "#57/#210 - comma-separated world global keys this mod actively guards against an unauthenticated client write.");

            ProgressionGateEnabled = ConfigBinder.BindSynced(config, configSync, sProgression, "Enabled", false,
                "#58 Progression Gate - refuse to wire a network's connection until its required global key (configured per-network in access_acl.json / networks.json) is present.");

            KeyItemScanSeconds = ConfigBinder.BindSynced(config, configSync, sKeyItem, "ScanSeconds", 2f,
                "#59 Key-Item Requirement - how often bound key-containers are re-read to decide whether a portal should be connected.", 0.5f, 10f);
            KeyItemContainerSearchRadius = ConfigBinder.BindSynced(config, configSync, sKeyItem, "ContainerSearchRadius", 4f,
                "#59 - radius around a portal searched for its bound key container.", 1f, 20f);

            RateLimitTagChangesPerWindow = ConfigBinder.BindSyncedInt(config, configSync, sRate, "TagChangesPerWindow", 5,
                "#61 Retag Rate Limit - max tag/connection changes a single verified account may make within RateLimitWindowSeconds before being refused.", 1, 100);
            RateLimitWindowSeconds = ConfigBinder.BindSynced(config, configSync, sRate, "WindowSeconds", 60f,
                "#61 - the token-bucket refill window.", 5f, 600f);
            RateLimitPortalCooldownSeconds = ConfigBinder.BindSynced(config, configSync, sRate, "PortalCooldownSeconds", 20f,
                "#61 - after any retag, further retags of the SAME portal by anyone but its owner/admins are refused for this long.", 0f, 300f);
            RateLimitAbuseCountBeforeKick = ConfigBinder.BindSyncedInt(config, configSync, sRate, "AbuseCountBeforeKick", 30,
                "#61 - sustained rate-limit violations (summed over many windows) from one account before an admin-configured kick escalation fires.", 5, 1000);

            PinReconcileSeconds = ConfigBinder.BindSynced(config, configSync, sPin, "ReconcileSeconds", 2f,
                "#48/#216 Ownership Pin - how often the slow sweep re-claims any pinned portal whose owner drifted away from the server session (covers ReleaseNearbyZDOS, which never goes through RPC_ZDOData).", 0.5f, 5f);
            PinClaimsBeforeKick = ConfigBinder.BindSyncedInt(config, configSync, sPin, "ClaimsBeforeKick", 8,
                "#216 - ping-pong bound: an account whose claims against a pinned portal are reverted this many times within PinClaimWindowSeconds gets kicked.", 1, 200);
            PinClaimWindowSeconds = ConfigBinder.BindSynced(config, configSync, sPin, "ClaimWindowSeconds", 30f,
                "#216 - the ping-pong escalation window.", 1f, 600f);

            PreDeliveryEnabled = ConfigBinder.BindSynced(config, configSync, sPreDelivery, "Enabled", true,
                "#213 Destination Pre-Delivery - proactively ForceSendZDO a managed portal's destination to any peer approaching it, so the first walk-through never silently fails.");
            PreDeliveryRadius = ConfigBinder.BindSynced(config, configSync, sPreDelivery, "Radius", 25f,
                "#213 - how close (metres) a peer must be to a managed portal's SOURCE before its destination ZDO is pre-pushed.", 5f, 100f);
            PreDeliveryIntervalSeconds = ConfigBinder.BindLocal(config, sPreDelivery, "IntervalSeconds", 2f,
                "#213 - poll cadence for connected-character proximity checks.");

            ArrivalBouncerEnabled = ConfigBinder.BindSynced(config, configSync, sBouncer, "Enabled", false,
                "#217 Arrival Bouncer - detect a non-permitted arrival at a managed destination and send the traveller back after the vanilla teleport's own floor has elapsed. Reactive only - cannot prevent the initial transit.");
            ArrivalSampleSeconds = ConfigBinder.BindLocal(config, sBouncer, "SampleSeconds", 3f,
                "#217 - position sampling cadence used to detect a portal transit.");
            ArrivalReturnDelaySeconds = ConfigBinder.BindSynced(config, configSync, sBouncer, "ReturnDelaySeconds", 10.5f,
                "#217 - delay after a detected transit before attempting the return teleport (must clear vanilla's 8s distant-teleport floor plus the 2s post-teleport cooldown).", 8.5f, 30f);
            ArrivalRadius = ConfigBinder.BindSynced(config, configSync, sBouncer, "Radius", 40f,
                "#217 - matching radius between a new position sample and a managed portal to call it 'arrived at that destination'.", 5f, 100f);

            BossLockdownActive = ConfigBinder.BindSynced(config, configSync, sBoss, "Active", false,
                "#219 Boss Lockdown Correction - when true, this mod holds 'nobossportals' + 'activebosses 1' set world-wide (the only genuinely world-wide half of vanilla's boss gate) regardless of any single client's own boss-aggro state.");
        }
    }
}
