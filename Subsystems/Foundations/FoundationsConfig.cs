using BepInEx.Configuration;
using ServerSync;
using TortalPortalLite.Core;

namespace TortalPortalLite.Subsystems.Foundations
{
    /// <summary>
    /// Config surface for the Foundations subsystem, matching catalog #84's own architecture spec
    /// section-for-section ([Census], [Networks], [Health], [Repair], [Audit], [Metrics], [Export]).
    /// Own subsystem, own config file - see Core/ConfigBinder.cs for why this is not one central class.
    /// </summary>
    public static class FoundationsConfig
    {
        public static ConfigEntry<float>? CensusIntervalSeconds;

        public static ConfigEntry<string>? NetworksFile;
        public static ConfigEntry<float>? ReassertSeconds;
        public static ConfigEntry<bool>? AllowTagRewrite;
        public static ConfigEntry<int>? MaxWritesPerTick;

        public static ConfigEntry<float>? HealthIntervalSeconds;
        public static ConfigEntry<bool>? ReportUnmanagedOddGroups;
        public static ConfigEntry<bool>? ReportInteriorPortals;

        public static ConfigEntry<bool>? AutoRepair;
        public static ConfigEntry<bool>? RepairDryRunDefault;
        public static ConfigEntry<int>? RepairMaxActionsPerRun;

        public static ConfigEntry<bool>? AuditEnabled;
        public static ConfigEntry<int>? AuditRingSize;
        public static ConfigEntry<bool>? AuditToastOnBlockedRetag;

        public static ConfigEntry<float>? MetricsSampleSeconds;
        public static ConfigEntry<float>? MetricsAggregateSeconds;
        public static ConfigEntry<float>? MetricsTransitStraddleRadius;

        public static ConfigEntry<bool>? ExportJson;
        public static ConfigEntry<float>? ExportIntervalSeconds;

        public static void Bind(ConfigFile config, ConfigSync configSync)
        {
            const string sCensus = "2 - Foundations: Census";
            const string sNetworks = "3 - Foundations: Networks";
            const string sHealth = "4 - Foundations: Health";
            const string sRepair = "5 - Foundations: Repair";
            const string sAudit = "6 - Foundations: Audit";
            const string sMetrics = "7 - Foundations: Metrics";
            const string sExport = "8 - Foundations: Export";

            CensusIntervalSeconds = ConfigBinder.BindSynced(config, configSync, sCensus, "IntervalSeconds", 1.0f,
                "How often PortalCensus re-snapshots every portal ZDO in the world.", 0.25f, 10f);

            NetworksFile = ConfigBinder.BindLocal(config, sNetworks, "File", "networks.json",
                "Path (relative to the plugin folder) to the hot-reloaded network topology definition. Local, not synced - a file path is server-machine-specific.");
            ReassertSeconds = ConfigBinder.BindSynced(config, configSync, sNetworks, "ReassertSeconds", 2.0f,
                "How often NetworkReassertEngine re-checks and, if needed, re-writes each managed portal's tag/connection. Must stay under vanilla's own 5s reconciliation pass.", 0.5f, 4.5f);
            AllowTagRewrite = ConfigBinder.BindSynced(config, configSync, sNetworks, "AllowTagRewrite", true,
                "If off, NetworkReassertEngine only re-asserts connections, never tags (for admins who want to keep player-chosen tag text).");
            MaxWritesPerTick = ConfigBinder.BindSyncedInt(config, configSync, sNetworks, "MaxWritesPerTick", 50,
                "Caps how many portals NetworkReassertEngine will rewrite in a single tick, so a large drifted network doesn't spike a frame.", 1, 2000);

            HealthIntervalSeconds = ConfigBinder.BindSynced(config, configSync, sHealth, "IntervalSeconds", 30f,
                "How often HealthScanEngine classifies the census for orphans, strands and corruption.", 5f, 300f);
            ReportUnmanagedOddGroups = ConfigBinder.BindLocal(config, sHealth, "ReportUnmanagedOddGroups", true,
                "Report odd-count (3+) same-tag groups even for tags no declared network claims.");
            ReportInteriorPortals = ConfigBinder.BindLocal(config, sHealth, "ReportInteriorPortals", true,
                "Report portals whose position resolves inside a dungeon interior (likely a misplaced anchor).");

            AutoRepair = ConfigBinder.BindLocal(config, sRepair, "AutoRepair", false,
                "If on, RepairEngine actually applies its suggested fixes instead of only reporting them.");
            RepairDryRunDefault = ConfigBinder.BindLocal(config, sRepair, "DryRunDefault", true,
                "The tplite repair console/RemoteCommand verb defaults to a dry run (report only) unless the admin explicitly passes --apply.");
            RepairMaxActionsPerRun = ConfigBinder.BindSyncedInt(config, configSync, sRepair, "MaxActionsPerRun", 20,
                "Caps how many repair actions a single repair pass will apply.", 1, 500);

            AuditEnabled = ConfigBinder.BindLocal(config, sAudit, "Enabled", true,
                "Log every tag/connection change with its attributed cause.");
            AuditRingSize = ConfigBinder.BindLocal(config, sAudit, "RingSize", 2000,
                "How many audit entries to keep in memory (oldest dropped first).");
            AuditToastOnBlockedRetag = ConfigBinder.BindSynced(config, configSync, sAudit, "ToastOnBlockedRetag", true,
                "Send the retagging player a toast explaining why their change was reverted.");

            MetricsSampleSeconds = ConfigBinder.BindLocal(config, sMetrics, "SampleSeconds", 3f,
                "How often MetricsEngine samples for transit detection.");
            MetricsAggregateSeconds = ConfigBinder.BindLocal(config, sMetrics, "AggregateSeconds", 60f,
                "How often MetricsEngine rolls samples up into aggregate counters.");
            MetricsTransitStraddleRadius = ConfigBinder.BindSynced(config, configSync, sMetrics, "StraddleRadius", 40f,
                "Radius (metres) used to detect 'this player's last two position samples straddle a known portal pair' as a transit heuristic.", 5f, 200f);

            ExportJson = ConfigBinder.BindLocal(config, sExport, "Json", true,
                "Write portals.json (the current network model + census) to the plugin folder periodically.");
            ExportIntervalSeconds = ConfigBinder.BindLocal(config, sExport, "IntervalSeconds", 300f,
                "How often ExportEngine writes its snapshot files, in addition to writing on every detected change.");
        }
    }
}
