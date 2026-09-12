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
    /// Tick order this build implements: Census -> NetworkModel -> Audit -> Reassert -> HealthScan.
    /// Command dispatch is patch-driven (Terminal.TryRunCommand prefix), not ticked. RepairEngine,
    /// MetricsEngine and ExportEngine from #84's full nine-engine spec are NOT implemented in this
    /// wave - they are on-demand/diagnostic (repair, transit metrics, JSON/CSV/SVG export) rather than
    /// load-bearing for anything a later wave needs, and are deferred rather than stubbed silently: see
    /// the Wave 0 completion notes.
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
        }

        public void Shutdown()
        {
        }
    }
}
