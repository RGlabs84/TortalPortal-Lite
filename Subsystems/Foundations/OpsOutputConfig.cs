using System.IO;
using BepInEx.Configuration;
using ServerSync;
using TortalPortalLite.Core;

namespace TortalPortalLite.Subsystems.Foundations
{
    /// <summary>
    /// Wave 3 "ops" domain - config surface for every OpsOutput* engine (catalog #65/#67/#68/#71/#73-78/
    /// #82/#201/#203). These are the remaining half of the SAME Foundations domain #84 laid out (its own
    /// doc comment names RepairEngine/MetricsEngine/ExportEngine as deferred to this wave); the reused
    /// entries already sitting in FoundationsConfig.cs ([Repair]/[Metrics]/[Export] sections, bound at
    /// Wave 0) are read directly by the corresponding engine below rather than re-declared here - this
    /// file only adds the config Wave 0 could not have anticipated (Discord toggles, HTTP bind/port,
    /// file-queue path, map cadence, etc).
    ///
    /// NEEDS INIT WIRING: PortalOpsSubsystem.Initialize (Subsystems/Foundations/PortalOpsSubsystem.cs) is
    /// an existing file this wave may not edit. The orchestrator should add, after
    /// `FoundationsConfig.Bind(config, configSync);`:
    ///     OpsOutputConfig.Bind(config, configSync);
    ///     OpsOutputSnapshotEngine.Initialize();     // subscribes ZNet.WorldSaveStarted (static field - safe at boot)
    ///     OpsOutputExportEngine.Initialize();       // subscribes ZNet.WorldSaveStarted (static field - safe at boot)
    ///     OpsOutputFileQueueEngine.Initialize();    // resolves/validates the .cmd file path against BepInEx.Paths.ConfigPath
    ///     OpsOutputDiscordEngine.Initialize();      // best-effort ZDOMan.m_onZDODestroyed subscribe + online lifecycle post
    ///     OpsOutputHttpEngine.Initialize();         // starts (or safely declines) the HttpListener per catalog #203
    /// (OpsOutputCompatEngine, OpsOutputJoinBriefingEngine, OpsOutputRepairEngine, OpsOutputMetricsEngine,
    /// OpsOutputMapEngine, OpsOutputNetworkValidationEngine and OpsOutputReportModel need no Initialize
    /// call - each self-initialises lazily from its own OnUpdate, the same tolerant "retry until the
    /// singleton exists" pattern CapabilityProbe already established, since several of their dependencies
    /// - ZDOMan.instance, ZRoutedRpc.instance - do not exist yet at this early Initialize point.)
    ///
    /// ...and, in PortalOpsSubsystem.OnUpdate(), after the existing CapabilityProbe.OnUpdate(dt) line
    /// (ReportModel first - Export/Http/Discord/Map/AdminCommands all read its Latest list, same "read
    /// what an earlier engine already computed" discipline #84 established for Census/Audit/Reassert):
    ///     OpsOutputReportModel.OnUpdate(dt);
    ///     OpsOutputNetworkValidationEngine.OnUpdate(dt);
    ///     OpsOutputRepairEngine.OnUpdate(dt);
    ///     OpsOutputMetricsEngine.OnUpdate(dt);
    ///     OpsOutputExportEngine.OnUpdate(dt);
    ///     OpsOutputDiscordEngine.OnUpdate(dt);
    ///     OpsOutputHttpEngine.OnUpdate(dt);
    ///     OpsOutputMapEngine.OnUpdate(dt);
    ///     OpsOutputCompatEngine.OnUpdate(dt);
    ///     OpsOutputFileQueueEngine.OnUpdate(dt);
    ///     OpsOutputJoinBriefingEngine.OnUpdate(dt);
    ///     OpsOutputSnapshotEngine.OnUpdate(dt);     // detects a NetworkModel.Networks reload via reference-change, for auto-snapshot
    ///
    /// ...and, in PortalOpsSubsystem.Shutdown() (currently an empty body):
    ///     OpsOutputHttpEngine.Shutdown();
    ///     OpsOutputExportEngine.Shutdown();
    ///     OpsOutputSnapshotEngine.Shutdown();
    ///     OpsOutputDiscordEngine.NotifyOffline();   // best-effort SendBlocking lifecycle post, mirrors Plugin.OnDestroy's own DiscordWebhook.SendBlocking call
    ///
    /// NEEDS DISPATCH WIRING: CommandEngine.Dispatch's `default:` case (Subsystems/Foundations/
    /// CommandEngine.cs) already forwards an unrecognised verb to
    /// TortalPortalLite.Subsystems.Enforcement.AccessAdminCommands.TryDispatch - the same pattern Wave 2's
    /// access agent used. This wave's verbs live in OpsOutputAdminCommands.TryDispatch (same signature);
    /// the orchestrator should chain it in, e.g. replace that single call with: first try
    /// AccessAdminCommands, then OpsOutputAdminCommands, falling through to the unknown-verb message only
    /// if both return false. See OpsOutputAdminCommands.cs for the verb list (export, map, repair
    /// [--apply], snapshot [name], snapshots, restore &lt;name&gt; [--recreate], networks, compat, metrics,
    /// http-status, report &lt;x&gt; &lt;y&gt; &lt;z&gt;).
    /// </summary>
    public static class OpsOutputConfig
    {
        // --- shared ---
        public static string PluginConfigDir { get; private set; } = ".";

        // --- #67 file-queue command channel ---
        public static ConfigEntry<bool>? FileQueueEnabled;
        public static ConfigEntry<string>? FileQueueFileName;
        public static ConfigEntry<float>? FileQueuePollSeconds;

        // --- #68 network validation (layered on top of the existing NetworkModel hot-reload) ---
        public static ConfigEntry<bool>? NetworkValidationEnabled;
        public static ConfigEntry<bool>? NetworkAllowInteriorMembers;

        // --- #71 repair, extending FoundationsConfig's [5 - Foundations: Repair] section ---
        public static ConfigEntry<bool>? AllowRegistrySurgery;
        public static ConfigEntry<float>? RepairAutoIntervalSeconds;
        public static ConfigEntry<float>? RepairSkipRecentlyChangedSeconds;

        // --- #78 snapshot / rollback / migration ---
        public static ConfigEntry<string>? SnapshotDirName;
        public static ConfigEntry<int>? SnapshotRetain;
        public static ConfigEntry<bool>? SnapshotOnReassertApply;

        // --- #74 export, extending FoundationsConfig's [8 - Foundations: Export] section ---
        public static ConfigEntry<bool>? ExportCsv;

        // --- #75 Discord webhook reporting ---
        public static ConfigEntry<bool>? DiscordHealthDigest;
        public static ConfigEntry<bool>? DiscordAuditStream;
        public static ConfigEntry<bool>? DiscordDestructionNotices;
        public static ConfigEntry<bool>? DiscordNetworkReload;
        public static ConfigEntry<bool>? DiscordPeriodicSummary;
        public static ConfigEntry<bool>? DiscordServerLifecycle;
        public static ConfigEntry<float>? DiscordSummaryIntervalMinutes;
        public static ConfigEntry<int>? DiscordMaxMessagesPerMinute;

        // --- #76/#203 in-process HTTP endpoint ---
        public static ConfigEntry<bool>? HttpEnabled;
        public static ConfigEntry<string>? HttpBindAddress;
        public static ConfigEntry<int>? HttpPort;
        public static ConfigEntry<string>? HttpBearerToken;

        // --- #77 generated realm map ---
        public static ConfigEntry<bool>? MapEnabled;
        public static ConfigEntry<float>? MapIntervalSeconds;
        public static ConfigEntry<int>? MapBiomeGridResolution;

        // --- #82 compatibility ---
        public static ConfigEntry<bool>? CompatForeignWriteWarnings;

        // --- #201 OnJoin briefing ---
        public static ConfigEntry<bool>? JoinBriefingEnabled;
        public static ConfigEntry<float>? JoinBriefingSettleSeconds;
        public static ConfigEntry<int>? JoinBriefingMaxForceSendPerJoin;
        public static ConfigEntry<bool>? JoinBriefingAdminHealthSummary;

        public static void Bind(ConfigFile config, ConfigSync configSync)
        {
            PluginConfigDir = Path.GetDirectoryName(config.ConfigFilePath) ?? ".";

            const string sQueue = "80 - Ops: File Queue";
            const string sNetworkValidation = "81 - Ops: Network Validation";
            const string sRepair = "82 - Ops: Repair (extra)";
            const string sSnapshot = "83 - Ops: Snapshot";
            const string sExport = "84 - Ops: Export (extra)";
            const string sDiscord = "85 - Ops: Discord";
            const string sHttp = "86 - Ops: HTTP Endpoint";
            const string sMap = "87 - Ops: Realm Map";
            const string sCompat = "88 - Ops: Compatibility";
            const string sJoin = "89 - Ops: Join Briefing";

            FileQueueEnabled = ConfigBinder.BindLocal(config, sQueue, "Enabled", true,
                "Poll <ConfigDir>/TortalPortalLite.cmd for admin command lines (cron/ssh-friendly), executing them on the main thread and appending results to TortalPortalLite.cmd.out.");
            FileQueueFileName = ConfigBinder.BindLocal(config, sQueue, "FileName", "TortalPortalLite.cmd",
                "File name (inside the plugin's config directory only - never an absolute/external path) polled for queued command lines.");
            FileQueuePollSeconds = ConfigBinder.BindLocal(config, sQueue, "PollSeconds", 1f,
                "How often to check the command file's mtime for changes.");

            NetworkValidationEnabled = ConfigBinder.BindLocal(config, sNetworkValidation, "Enabled", true,
                "Validate the hot-reloaded networks.json against catalog #68's hazard list (duplicate tag/id, too-few members, out-of-range coordinates, ambiguous same-portal membership) and surface errors instead of only DeclaredNotFound.");
            NetworkAllowInteriorMembers = ConfigBinder.BindLocal(config, sNetworkValidation, "AllowInteriorMembers", false,
                "If off, a declared member resolving to a portal at y>3000 (a dungeon interior) is flagged as a validation warning.");

            AllowRegistrySurgery = ConfigBinder.BindLocal(config, sRepair, "AllowRegistrySurgery", false,
                "Extremely dangerous: allows RepairEngine to mutate ZDOMan's live m_portalObjects dictionary directly to remove a duplicate-bucket entry (catalog #71's own warning: 'powerful and dangerous'). Off by default; every use is logged loudly.");
            RepairAutoIntervalSeconds = ConfigBinder.BindLocal(config, sRepair, "AutoIntervalSeconds", 300f,
                "How often RepairEngine runs an automatic pass when FoundationsConfig.AutoRepair is on (dry-run unless RepairDryRunDefault is false).");
            RepairSkipRecentlyChangedSeconds = ConfigBinder.BindLocal(config, sRepair, "SkipRecentlyChangedSeconds", 6f,
                "Skip repairing a portal whose connection changed within this many seconds - avoids fighting vanilla's own 5s ConnectPortals reconciliation pass (catalog #71 failure mode).");

            SnapshotDirName = ConfigBinder.BindLocal(config, sSnapshot, "DirName", "snapshots",
                "Subdirectory (under the plugin config directory) snapshots are written to.");
            SnapshotRetain = ConfigBinder.BindLocal(config, sSnapshot, "Retain", 30,
                "How many snapshot files to keep before pruning the oldest (auto-snapshots only - named/manual snapshots are never auto-pruned).");
            SnapshotOnReassertApply = ConfigBinder.BindLocal(config, sSnapshot, "OnRepairApply", true,
                "Take an automatic snapshot immediately before every --apply repair pass, so tplite restore can always undo the whole batch.");

            ExportCsv = ConfigBinder.BindLocal(config, sExport, "Csv", true,
                "Also write portals.csv (flat, one row per portal) alongside portals.json.");

            DiscordHealthDigest = ConfigBinder.BindLocal(config, sDiscord, "HealthDigest", true,
                "Post a health-findings digest to Discord when HealthScanEngine's findings change.");
            DiscordAuditStream = ConfigBinder.BindLocal(config, sDiscord, "AuditStream", false,
                "Post every audit entry to Discord. Default off - catalog #75's own warning: a busy server makes this unreadable.");
            DiscordDestructionNotices = ConfigBinder.BindLocal(config, sDiscord, "DestructionNotices", false,
                "Post a notice to Discord whenever a managed portal ZDO is destroyed.");
            DiscordNetworkReload = ConfigBinder.BindLocal(config, sDiscord, "NetworkReload", true,
                "Post to Discord whenever networks.json is reloaded (success or parse error).");
            DiscordPeriodicSummary = ConfigBinder.BindLocal(config, sDiscord, "PeriodicSummary", false,
                "Post a periodic portal-count/metrics summary to Discord, on the same timer as the mod-wide Heartbeat.");
            DiscordServerLifecycle = ConfigBinder.BindLocal(config, sDiscord, "ServerLifecycle", false,
                "Post 'portal engine online/offline' lifecycle notices to Discord.");
            DiscordSummaryIntervalMinutes = ConfigBinder.BindLocal(config, sDiscord, "SummaryIntervalMinutes", 60f,
                "Minutes between periodic summary posts (only used if PeriodicSummary is on and no shared Heartbeat interval is available).");
            DiscordMaxMessagesPerMinute = ConfigBinder.BindLocal(config, sDiscord, "MaxMessagesPerMinute", 6,
                "Hard cap on Discord posts per minute from this mod - coalesce/drop rather than spam.");

            HttpEnabled = ConfigBinder.BindLocal(config, sHttp, "Enabled", false,
                "Try to start an in-process HttpListener serving /health, /portals.json, /portals.csv, /metrics. Downgraded to needs-ingame-check per catalog #203 - self-probes at boot and disables itself on failure, falling back to the file export.");
            HttpBindAddress = ConfigBinder.BindLocal(config, sHttp, "BindAddress", "127.0.0.1",
                "Address HttpListener binds. Anything other than 127.0.0.1/localhost REQUIRES HttpBearerToken to be set or the listener refuses to start.");
            HttpPort = ConfigBinder.BindLocal(config, sHttp, "Port", 7770,
                "TCP port for the HTTP endpoint.");
            HttpBearerToken = ConfigBinder.BindLocal(config, sHttp, "BearerToken", "",
                "If set, every request must carry 'Authorization: Bearer <token>'. Required (non-empty) to bind any non-loopback address.");

            MapEnabled = ConfigBinder.BindLocal(config, sMap, "Enabled", true,
                "Render portals.svg (top-down realm map, biome-tinted, coloured by network) periodically and on demand.");
            MapIntervalSeconds = ConfigBinder.BindLocal(config, sMap, "IntervalSeconds", 300f,
                "How often to re-render the SVG map, in addition to on-demand (tplite map) renders.");
            MapBiomeGridResolution = ConfigBinder.BindLocal(config, sMap, "BiomeGridResolution", 128,
                "Grid resolution (N x N cells across the playable disc) for the cached biome-tint background. Sampled once (time-sliced across ticks) and cached - catalog #77's own warning against resampling per export.");

            CompatForeignWriteWarnings = ConfigBinder.BindLocal(config, sCompat, "ForeignWriteWarnings", true,
                "Log a warning when a managed portal's tag changes to a value this mod did not write and no matching RPC_SetTag crossed the wire (catalog #82's 'foreign writer' detection).");

            JoinBriefingEnabled = ConfigBinder.BindSynced(config, configSync, sJoin, "Enabled", true,
                "Toast a newly-joined player a summary of their own portals (and force-send managed anchors) a few seconds after their character ZDO is identified.");
            JoinBriefingSettleSeconds = ConfigBinder.BindSynced(config, configSync, sJoin, "SettleSeconds", 8f,
                "Delay after a player's character identity is known before sending the briefing toast - covers the post-spawn window before Player.Start has registered the 'Message' RPC.", 1f, 60f);
            JoinBriefingMaxForceSendPerJoin = ConfigBinder.BindSyncedInt(config, configSync, sJoin, "MaxForceSendPerJoin", 64,
                "Caps how many managed-anchor ZDOs are force-sent to a single newly-joined peer.", 1, 2000);
            JoinBriefingAdminHealthSummary = ConfigBinder.BindSynced(config, configSync, sJoin, "AdminHealthSummary", true,
                "Additionally remote-print the current HealthScanEngine summary to an admin's console on join.");
        }
    }
}
