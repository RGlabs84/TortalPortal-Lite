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
    /// anything reads it; Audit must see the change stream before any corrective writes, or the mod
    /// attributes its own writes to players.
    ///
    /// Trimmed to this build's actual wanted set (#70/71/73/81/82/84 + #148's own PortalCensus read):
    /// VersionMigration (one-shot, the first tick Game.instance exists) -> Census -> Audit -> HealthScan
    /// -> ReportModel -> Repair -> Metrics -> BarrkBotExport -> Compat -> Snapshot. Command dispatch is
    /// patch-driven (Terminal.TryRunCommand prefix), not ticked. Everything
    /// else #84's own nine-engine spec named (declarative-network reassertion, generic export/Discord/
    /// HTTP/map/file-queue/join-briefing output surfaces, the boot-time capability probe) was deliberately
    /// cut - this product's final feature set does not use them, and they are not load-bearing for
    /// anything kept. BarrkBotExportEngine is the one exception re-added afterwards, purpose-built against
    /// BarrkBOT's own ingestion contract rather than reviving the generic export wave.
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
            VersionMigration.RunBootChecks();

            OpsOutputConfig.Bind(config, configSync);
            OpsOutputSnapshotEngine.Initialize();
        }

        public void OnWorldReady()
        {
            PortalRegistry.Discover();
        }

        public void OnUpdate()
        {
            float dt = UnityEngine.Time.deltaTime;
            VersionMigration.OnUpdate();
            PortalCensus.OnUpdate(dt);
            AuditEngine.OnUpdate(dt);
            HealthScanEngine.OnUpdate(dt);

            OpsOutputReportModel.OnUpdate(dt);
            OpsOutputRepairEngine.OnUpdate(dt);
            OpsOutputMetricsEngine.OnUpdate(dt);
            BarrkBotExportEngine.OnUpdate(dt);
            OpsOutputCompatEngine.OnUpdate(dt);
            OpsOutputSnapshotEngine.OnUpdate(dt);
        }

        public void Shutdown()
        {
            OpsOutputSnapshotEngine.Shutdown();
        }
    }
}
