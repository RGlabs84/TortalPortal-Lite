using BepInEx.Configuration;
using HarmonyLib;
using ServerSync;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;

namespace TortalPortalLite.Subsystems.Foundations
{
    /// <summary>
    /// #84's architecture spec: one subsystem owning several engines with disjoint write ownership and
    /// a fixed intra-tick order, because ordering here is semantics, not taste - Census must run before
    /// anything reads it; Audit must see the change stream before Reassert issues new writes, or the
    /// mod attributes its own writes to players.
    ///
    /// Tick order this build implements: Census -> NetworkModel -> Audit -> Reassert -> HealthScan ->
    /// (Wave 3) ReportModel -> NetworkValidation -> Repair -> Metrics -> Export -> Discord -> Http -> Map
    /// -> Compat -> FileQueue -> JoinBriefing -> Snapshot. Command dispatch is patch-driven
    /// (Terminal.TryRunCommand prefix), not ticked. RepairEngine, MetricsEngine and ExportEngine from
    /// #84's full nine-engine spec were deferred out of Wave 0 (on-demand/diagnostic, not load-bearing
    /// for anything Waves 1-2 needed) and built in Wave 3 as OpsOutput* - see
    /// Subsystems/Foundations/OpsOutputConfig.cs for their own doc comment, which specified this exact
    /// wiring.
    /// </summary>
    public class PortalOpsSubsystem : IPortalSubsystem
    {
        public string Name => "PortalOps";
        public bool IsEnabled => GlobalConfig.Enabled?.Value != false;

        public void Initialize(ConfigFile config, ConfigSync configSync, Harmony harmony)
        {
            FoundationsConfig.Bind(config, configSync);
            CommandEngine.Install(harmony);
            AuditEngine.Initialize();
            PortalDirtyFlagGuardian.Initialize();
            VersionMigration.RunBootChecks();

            OpsOutputConfig.Bind(config, configSync);
            OpsOutputSnapshotEngine.Initialize();
            OpsOutputExportEngine.Initialize();
            OpsOutputFileQueueEngine.Initialize();
            OpsOutputDiscordEngine.Initialize();
            OpsOutputHttpEngine.Initialize();
        }

        public void OnWorldReady()
        {
            PortalRegistry.Discover();
        }

        public void OnUpdate()
        {
            float dt = UnityEngine.Time.deltaTime;
            PortalCensus.OnUpdate(dt);
            NetworkModel.OnUpdate(dt);
            AuditEngine.OnUpdate(dt);
            NetworkReassertEngine.OnUpdate(dt);
            HealthScanEngine.OnUpdate(dt);
            CapabilityProbe.OnUpdate(dt);

            OpsOutputReportModel.OnUpdate(dt);
            OpsOutputNetworkValidationEngine.OnUpdate(dt);
            OpsOutputRepairEngine.OnUpdate(dt);
            OpsOutputMetricsEngine.OnUpdate(dt);
            OpsOutputExportEngine.OnUpdate(dt);
            OpsOutputDiscordEngine.OnUpdate(dt);
            OpsOutputHttpEngine.OnUpdate(dt);
            OpsOutputMapEngine.OnUpdate(dt);
            OpsOutputCompatEngine.OnUpdate(dt);
            OpsOutputFileQueueEngine.OnUpdate(dt);
            OpsOutputJoinBriefingEngine.OnUpdate(dt);
            OpsOutputSnapshotEngine.OnUpdate(dt);
        }

        public void Shutdown()
        {
            OpsOutputHttpEngine.Shutdown();
            OpsOutputExportEngine.Shutdown();
            OpsOutputSnapshotEngine.Shutdown();
            OpsOutputDiscordEngine.NotifyOffline();
        }
    }
}
