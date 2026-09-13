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

        public static ConfigEntry<bool>? AllowTagRewrite;

        public static ConfigEntry<float>? HealthIntervalSeconds;
        public static ConfigEntry<bool>? ReportUnmanagedOddGroups;

        public static ConfigEntry<bool>? AutoRepair;
        public static ConfigEntry<bool>? RepairDryRunDefault;
        public static ConfigEntry<int>? RepairMaxActionsPerRun;

        public static ConfigEntry<bool>? AuditEnabled;
        public static ConfigEntry<int>? AuditRingSize;
        public static ConfigEntry<bool>? AuditToastOnBlockedRetag;

        public static ConfigEntry<float>? MetricsSampleSeconds;
        public static ConfigEntry<float>? MetricsAggregateSeconds;
        public static ConfigEntry<float>? MetricsTransitStraddleRadius;

        public static void Bind(ConfigFile config, ConfigSync configSync)
        {
            const string sCensus = "2 - Foundations: Census";
            const string sHealth = "4 - Foundations: Health";
            const string sRepair = "5 - Foundations: Repair";
            const string sAudit = "6 - Foundations: Audit";
            const string sMetrics = "7 - Foundations: Metrics";

            CensusIntervalSeconds = ConfigBinder.BindSynced(config, configSync, sCensus, "IntervalSeconds", 1.0f,
                "How often PortalCensus re-snapshots every portal ZDO in the world.", 0.25f, 10f);

            AllowTagRewrite = ConfigBinder.BindSynced(config, configSync, sCensus, "AllowTagRewrite", true,
                "If off, OpsOutputRepairEngine's OddCountStrand/whitespace-normalisation actions never retag a portal (for admins who want to keep player-chosen tag text untouched).");

            HealthIntervalSeconds = ConfigBinder.BindSynced(config, configSync, sHealth, "IntervalSeconds", 30f,
                "How often HealthScanEngine classifies the census for orphans, strands and corruption.", 5f, 300f);
            ReportUnmanagedOddGroups = ConfigBinder.BindLocal(config, sHealth, "ReportUnmanagedOddGroups", true,
                "Report odd-count (3+) same-tag groups even for tags no declared network claims.");

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
        }
    }
}
