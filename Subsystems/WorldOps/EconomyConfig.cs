using BepInEx.Configuration;
using ServerSync;
using TortalPortalLite.Core;

namespace TortalPortalLite.Subsystems.WorldOps
{
    /// <summary>
    /// Config surface for the `economy` domain (Wave 3 catalog #88-#106, #273-#286 - portal-based tolls,
    /// fuel/charge systems, cooldowns, progression-gated networks, tiered portals, trade routes, server
    /// events built on portal networks). Follows Subsystems/Topology/RoutingConfig.cs's own precedent:
    /// grouped by mechanism family, section numbers 90-99 chosen to sit clear of every section already
    /// in use (see the numbered list from "1 - General" through "72 - Lockdown: Two-Hop Relay" plus
    /// Ux/Targeted's own 20-41 range) without needing to coordinate live with the concurrent Wave 3
    /// ops/wildcard agents editing this same shared ConfigFile.
    /// </summary>
    public static class EconomyConfig
    {
        // --- 90: core kernel / gate state (#88 Routing Kernel, #89 Park and Release) ---
        public static ConfigEntry<bool>? Enabled;
        public static ConfigEntry<float>? KernelReassertSeconds;
        public static ConfigEntry<string>? RegistryFile;
        public static ConfigEntry<int>? MaxWritesPerTick;
        public static ConfigEntry<float>? BindRadius;
        public static ConfigEntry<float>? PrewarmRadius;
        public static ConfigEntry<float>? NotifyRadius;

        // --- 91: customs house (#90) ---
        public static ConfigEntry<bool>? CustomsHouseEnabled;
        public static ConfigEntry<float>? CustomsHouseExitDistance;

        // --- 92: toll / turnstile / fuel (#91, #92, #93, #95) ---
        public static ConfigEntry<float>? EscrowPollSeconds;
        public static ConfigEntry<float>? TurnstilePollSeconds;
        public static ConfigEntry<float>? TransitStraddleRadius;
        public static ConfigEntry<float>? TransitPollSeconds;
        public static ConfigEntry<float>? ChargeCellPollSeconds;
        public static ConfigEntry<float>? ChargeIdleBurnSuppressRadius;

        // --- 93: debt / lien (#94) ---
        public static ConfigEntry<bool>? DebtLienEnabled;
        public static ConfigEntry<float>? DebtCheckSeconds;

        // --- 94: cooldowns and quotas (#96) ---
        public static ConfigEntry<float>? CooldownEvalSeconds;
        public static ConfigEntry<int>? DefaultCooldownSeconds;

        // --- 95: tiers and ore gate (#98, #99) ---
        public static ConfigEntry<float>? TierScanSeconds;
        public static ConfigEntry<float>? TierScanRadius;

        // --- 96: station credit (#273, #274) ---
        public static ConfigEntry<float>? StationCreditPollSeconds;
        public static ConfigEntry<float>? StationScanRadius;

        // --- 97: door / vault (#275, #276, #277) ---
        public static ConfigEntry<float>? DoorScanRadius;
        public static ConfigEntry<float>? DoorPollSeconds;

        // --- 98: presence / escort (#278, #279, #280) ---
        public static ConfigEntry<float>? PresenceEvalSeconds;
        public static ConfigEntry<float>? EscortRadius;
        public static ConfigEntry<float>? OfflineShieldGraceSeconds;

        // --- 99: reactive transit effects (#281, #282, #283) ---
        public static ConfigEntry<bool>? TransitEffectsEnabled;
        public static ConfigEntry<float>? TransitEffectsPollSeconds;
        public static ConfigEntry<float>? StatusRepingSeconds;

        // --- 100: trust / validation (#284, #285, #286) ---
        public static ConfigEntry<bool>? ValidatorEnabled;
        public static ConfigEntry<float>? ValidatorProximityRadius;
        public static ConfigEntry<int>? ValidatorKickThreshold;
        public static ConfigEntry<float>? ValidatorWindowSeconds;

        // --- 101: progression / treasury (#97, #100) ---
        public static ConfigEntry<float>? ProgressionEvalSeconds;
        public static ConfigEntry<bool>? TreasuryEnabled;
        public static ConfigEntry<float>? TreasuryEvalSeconds;

        // --- 102: one-way / distance / lease / naming / events / auction (#101-#106) ---
        public static ConfigEntry<float>? OneWayReassertSeconds;
        public static ConfigEntry<float>? DistancePricePerMeter;
        public static ConfigEntry<float>? LeaseEvalSeconds;
        public static ConfigEntry<int>? BuildCapDefault;
        public static ConfigEntry<float>? EventTopologyEvalSeconds;
        public static ConfigEntry<float>? AuctionCycleSeconds;

        public static void Bind(ConfigFile config, ConfigSync configSync)
        {
            const string sCore = "90 - Economy: Core Kernel";
            const string sCustoms = "91 - Economy: Customs House";
            const string sToll = "92 - Economy: Toll, Turnstile, Fuel";
            const string sDebt = "93 - Economy: Debt and Liens";
            const string sCooldown = "94 - Economy: Cooldowns and Quotas";
            const string sTier = "95 - Economy: Tiers and Ore Gate";
            const string sStation = "96 - Economy: Station Credit";
            const string sDoor = "97 - Economy: Door and Vault";
            const string sPresence = "98 - Economy: Presence and Escort";
            const string sEffects = "99 - Economy: Reactive Transit Effects";
            const string sTrust = "100 - Economy: Trust and Validation";
            const string sProg = "101 - Economy: Progression and Treasury";
            const string sTrade = "102 - Economy: Trade Routes, Leases, Events";

            Enabled = ConfigBinder.BindSynced(config, configSync, sCore, "Enabled", true,
                "Master switch for the whole economy domain (catalog #88-#106, #273-#286). If off, every economy engine still registers but no-ops.");
            KernelReassertSeconds = ConfigBinder.BindSynced(config, configSync, sCore, "KernelReassertSeconds", 1.0f,
                "How often EconomyRoutingKernel (#88/#89) re-checks every published condition and, if needed, re-writes a managed portal's tag/connection. Must stay well under vanilla's own 5s Game.ConnectPortals pass.", 0.2f, 4.5f);
            RegistryFile = ConfigBinder.BindLocal(config, sCore, "File", "economy.json",
                "Path (relative to the plugin folder) to the hot-reloaded economy declarations (tolls, leases, one-way routes, auctions, event chains, ...). Local, not synced - a file path is server-machine-specific.");
            MaxWritesPerTick = ConfigBinder.BindSyncedInt(config, configSync, sCore, "MaxWritesPerTick", 64,
                "Caps how many portal ZDOs the kernel will rewrite in one tick, so a large managed set never spikes a frame (ZDOMan.SendZDOs re-serializes whole ZDOs with no delta encoding).", 1, 4000);
            BindRadius = ConfigBinder.BindSynced(config, configSync, sCore, "FixtureBindRadius", 4f,
                "Radius around a managed portal searched for its bound fixture (toll chest, item stand, fireplace, door) - catalog #91's own default.", 1f, 20f);
            PrewarmRadius = ConfigBinder.BindSynced(config, configSync, sCore, "PrewarmRadius", 40f,
                "On a release (park -> open transition), every connected peer within this radius of the gate is ForceSendZDO'd the destination ZDO so the first walk-through does not silently fail (catalog #89's own citation of TeleportWorld.TargetFound's RequestZDO gate).", 5f, 200f);
            NotifyRadius = ConfigBinder.BindSynced(config, configSync, sCore, "NotifyRadius", 20f,
                "Radius within which a park/release toast is sent to standing players (catalog #89's own playerExperience guidance).", 5f, 100f);

            CustomsHouseEnabled = ConfigBinder.BindSynced(config, configSync, sCustoms, "Enabled", true,
                "Master switch for Customs House anchors (#90) - decoy-sink parking instead of a bare disconnect.");
            CustomsHouseExitDistance = ConfigBinder.BindSynced(config, configSync, sCustoms, "AnchorOffset", 2f,
                "Metres in front of a parked gate (along its facing) where a cheap 'bounce back' anchor is placed.", 0.5f, 20f);

            EscrowPollSeconds = ConfigBinder.BindSynced(config, configSync, sToll, "EscrowPollSeconds", 1.0f,
                "Prepaid Toll Escrow (#91) / Route Auctions (#106) standing-balance read cadence.", 0.25f, 10f);
            TurnstilePollSeconds = ConfigBinder.BindSynced(config, configSync, sToll, "TurnstilePollSeconds", 0.5f,
                "Token Turnstile (#92) item-stand s_item read cadence - cheap enough to run fast (catalog: 'no Inventory deserialisation anywhere').", 0.1f, 5f);
            TransitStraddleRadius = ConfigBinder.BindSynced(config, configSync, sToll, "TransitStraddleRadius", 8f,
                "Reactive Transit Toll (#93) detector radius - deliberately tighter than the 40m Wonderland default per the catalog's own false-positive-reduction guidance.", 2f, 40f);
            TransitPollSeconds = ConfigBinder.BindSynced(config, configSync, sToll, "TransitPollSeconds", 0.25f,
                "Reactive Transit Toll (#93) position-sample cadence.", 0.1f, 2f);
            ChargeCellPollSeconds = ConfigBinder.BindSynced(config, configSync, sToll, "ChargeCellPollSeconds", 1.0f,
                "Charge Cells (#95) fuel burn-down tick.", 0.25f, 10f);
            ChargeIdleBurnSuppressRadius = ConfigBinder.BindSynced(config, configSync, sToll, "IdleBurnSuppressRadius", 0f,
                "If > 0, idle (non-transit) fuel burn is suppressed while no connected peer is within this radius of the gate - a mothballed outpost does not silently drain (catalog #95's own play-quality guidance). 0 disables suppression (always burns).", 0f, 20000f);

            DebtLienEnabled = ConfigBinder.BindSynced(config, configSync, sDebt, "Enabled", true,
                "Debtor's Lien and Personal Gates (#94) master switch.");
            DebtCheckSeconds = ConfigBinder.BindSynced(config, configSync, sDebt, "CheckSeconds", 2.0f,
                "How often every managed portal's s_creator is checked against the debt ledger.", 0.5f, 30f);

            CooldownEvalSeconds = ConfigBinder.BindSynced(config, configSync, sCooldown, "EvalSeconds", 1.0f,
                "Cooldowns and Throughput Quotas (#96) evaluation tick - per-portal cooldown, per-network quota and scheduled-service checks all share this cadence.", 0.2f, 10f);
            DefaultCooldownSeconds = ConfigBinder.BindSyncedInt(config, configSync, sCooldown, "DefaultCooldownSeconds", 60,
                "Fallback per-portal cooldown length for a declaration that doesn't override it.", 1, 86400);

            TierScanSeconds = ConfigBinder.BindSynced(config, configSync, sTier, "ScanSeconds", 10f,
                "Physical Tiers (#99) neighbourhood rescan cadence - deliberately slow (a spatial sweep per portal), never every tick per the catalog's own perf warning.", 1f, 300f);
            TierScanRadius = ConfigBinder.BindSynced(config, configSync, sTier, "ScanRadius", 12f,
                "Physical Tiers (#99) radius searched for adjacent structures/wards - catalog's own default.", 2f, 50f);

            StationCreditPollSeconds = ConfigBinder.BindSynced(config, configSync, sStation, "PollSeconds", 1.0f,
                "Station Credit Primitives (#273) / Smelter Output Queue Credit (#274) processing cadence for queued credits.", 0.25f, 10f);
            StationScanRadius = ConfigBinder.BindSynced(config, configSync, sStation, "ScanRadius", 16f,
                "Radius around a payer's last known portal use searched for their fuel/ore stations.", 2f, 64f);

            DoorScanRadius = ConfigBinder.BindSynced(config, configSync, sDoor, "ScanRadius", 6f,
                "Door Lever (#275) / Server Portcullis (#276) / Vault Seal (#277): radius around a managed portal searched for its bound door/chest.", 1f, 20f);
            DoorPollSeconds = ConfigBinder.BindSynced(config, configSync, sDoor, "PollSeconds", 0.5f,
                "Door Lever / Server Portcullis / Vault Seal poll cadence.", 0.1f, 5f);

            PresenceEvalSeconds = ConfigBinder.BindSynced(config, configSync, sPresence, "EvalSeconds", 1.0f,
                "Presence-Wired Routes (#278) / Offline-Raid Shield (#279) reconcile tick.", 0.25f, 10f);
            EscortRadius = ConfigBinder.BindSynced(config, configSync, sPresence, "EscortRadius", 15f,
                "Escort Gate (#280) default radius an owner must stand within to keep the route wired.", 2f, 100f);
            OfflineShieldGraceSeconds = ConfigBinder.BindSynced(config, configSync, sPresence, "OfflineShieldGraceSeconds", 60f,
                "Offline-Raid Shield (#279) grace window after the last network member leaves before inbound routes are actually cut, so a crash-and-rejoin doesn't strand the owner outside their own base.", 0f, 3600f);

            TransitEffectsEnabled = ConfigBinder.BindSynced(config, configSync, sEffects, "Enabled", true,
                "Portal Sickness (#281) / Arrival Blessing (#282) / Per-Player Cooldown Made Visible (#283) master switch.");
            TransitEffectsPollSeconds = ConfigBinder.BindSynced(config, configSync, sEffects, "PollSeconds", 0.5f,
                "Transit-effects detector poll cadence - tighter than the 3s Wonderland default since these grants are time-sensitive feedback.", 0.1f, 3f);
            StatusRepingSeconds = ConfigBinder.BindSynced(config, configSync, sEffects, "RepingSeconds", 8f,
                "How often an active status-effect grant is re-pinged to keep it alive for a configured window (there is no removal RPC - only re-ping or let expire).", 1f, 60f);

            ValidatorEnabled = ConfigBinder.BindSynced(config, configSync, sTrust, "Enabled", true,
                "Inline ZDOData Validator (#285) / Sender-Owner Binding Rule (#286) master switch.");
            ValidatorProximityRadius = ConfigBinder.BindSynced(config, configSync, sTrust, "ProximityRadius", 6f,
                "A watched-ZDO write is only plausible if the sender's own character is within this radius of it.", 1f, 30f);
            ValidatorKickThreshold = ConfigBinder.BindSyncedInt(config, configSync, sTrust, "KickThreshold", 8,
                "Rejections from the same sender within ValidatorWindowSeconds before an automatic kick.", 1, 1000);
            ValidatorWindowSeconds = ConfigBinder.BindSynced(config, configSync, sTrust, "WindowSeconds", 30f,
                "Rolling window for the kick-threshold rejection count.", 1f, 600f);

            ProgressionEvalSeconds = ConfigBinder.BindSynced(config, configSync, sProg, "ProgressionEvalSeconds", 1.0f,
                "Progression-Gated Route Tiers (#97) global-key evaluation tick.", 0.2f, 10f);
            TreasuryEnabled = ConfigBinder.BindSynced(config, configSync, sProg, "TreasuryEnabled", true,
                "Server Treasury, Tax Skim and Collective Unlocks (#100) master switch.");
            TreasuryEvalSeconds = ConfigBinder.BindSynced(config, configSync, sProg, "TreasuryEvalSeconds", 2.0f,
                "Treasury skim-and-spend evaluation tick.", 0.5f, 30f);

            OneWayReassertSeconds = ConfigBinder.BindSynced(config, configSync, sTrade, "OneWayReassertSeconds", 2.0f,
                "One-Way Trade Routes (#101) reassertion tick, closing the window vanilla's own load-time relink (ZDOMan.ConnectPortals) opens by making a one-way link bidirectional again.", 0.5f, 30f);
            DistancePricePerMeter = ConfigBinder.BindSynced(config, configSync, sTrade, "DistancePricePerMeter", 0.01f,
                "Distance and Biome Pricing (#102) default coins-per-metre rate for a declaration that doesn't override it.", 0f, 10f);
            LeaseEvalSeconds = ConfigBinder.BindSynced(config, configSync, sTrade, "LeaseEvalSeconds", 5.0f,
                "Leases, Rent, Repossession and Caps (#103) expiry/repossession evaluation tick.", 0.5f, 60f);
            BuildCapDefault = ConfigBinder.BindSyncedInt(config, configSync, sTrade, "BuildCapDefault", 0,
                "Default per-player portal build cap (#103) - 0 disables the cap entirely (opt-in feature).", 0, 10000);
            EventTopologyEvalSeconds = ConfigBinder.BindSynced(config, configSync, sTrade, "EventTopologyEvalSeconds", 5.0f,
                "Event Topologies (#105) - rotating gate / treasure chain / world tour ring evaluation tick.", 0.5f, 60f);
            AuctionCycleSeconds = ConfigBinder.BindSynced(config, configSync, sTrade, "AuctionCycleSeconds", 86400f,
                "Route Auctions (#106) default cycle length (real seconds) for a declaration that doesn't override it.", 60f, 2592000f);
        }
    }
}
