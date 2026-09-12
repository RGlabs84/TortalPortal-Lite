using BepInEx.Configuration;
using HarmonyLib;
using ServerSync;
using TortalPortalLite.Core;

namespace TortalPortalLite.Subsystems.Enforcement
{
    /// <summary>
    /// Thin dispatcher for the Access domain (Wave 2 - catalog #s 46-62, 209-221: access control,
    /// identity, and locks). Follows the same pattern as Subsystems/Foundations/PortalOpsSubsystem.cs and
    /// Subsystems/Topology/TopologiesSubsystem.cs: real logic lives entirely in sibling `static` engine
    /// classes under this same folder, each prefixed `Access*` so this domain's files never collide with
    /// the concurrent lockdown/ux agents' own `Lockdown*`/`Ux*` files also living here; this class only
    /// sequences them.
    ///
    /// #44 Authentic Sender Context and #50 Portal Record Store (this domain's first two catalog
    /// entries) are Wave 0 infrastructure (Core/Data/SenderContext.cs,
    /// Subsystems/Foundations/PortalRecordStore.cs) and are consumed throughout this domain's engines,
    /// never rebuilt here.
    ///
    /// Tick order: storage flushes and the ward index refresh first (so the ACL policy chain and the Tag
    /// Watchdog read fresh ward/pin state this same tick); the Tag Watchdog and Ownership Pin reconcile
    /// next (the core reactive-enforcement loop); everything else (key-item gating, untagged-pair sweep,
    /// destination pre-delivery, the arrival bouncer, the RPC global-key guard, the boss-lockdown
    /// correction) follows and does not depend on running before any of the above. This class is NOT
    /// registered anywhere in this file - Plugin.cs's `Subsystems.Register(...)` wiring is the
    /// orchestrator's job, per this wave's own task boundaries.
    /// </summary>
    public class AccessSubsystem : IPortalSubsystem
    {
        public string Name => "Access";
        public bool IsEnabled => GlobalConfig.Enabled?.Value != false;

        public void Initialize(ConfigFile config, ConfigSync configSync, Harmony harmony)
        {
            AccessConfig.Bind(config, configSync);

            AccessAclStore.Initialize();
            AccessTeamRoster.Initialize();

            AccessCreatorAttestationEngine.Initialize();
            AccessTagWatchdogEngine.Initialize();
            AccessOwnershipPinEngine.Initialize();
            AccessPortalAclEngine.Initialize();
            AccessDestroyVetoEngine.Initialize();
            AccessManagedNetworkGovernorEngine.Initialize();
            AccessUntaggedAutoPairSuppressionEngine.Initialize();
        }

        public void OnWorldReady()
        {
            AccessShadowWardIndexEngine.OnWorldReady();
        }

        public void OnUpdate()
        {
            float dt = UnityEngine.Time.deltaTime;

            AccessAclStore.OnUpdate(dt);
            AccessTeamRoster.OnUpdate(dt);
            AccessShadowWardIndexEngine.OnUpdate(dt);

            AccessTagWatchdogEngine.OnUpdate(dt);
            AccessOwnershipPinEngine.OnUpdate(dt);

            AccessUntaggedAutoPairSuppressionEngine.OnUpdate(dt);
            AccessKeyItemRequirementEngine.OnUpdate(dt);
            AccessDestinationPreDeliveryEngine.OnUpdate(dt);
            AccessArrivalBouncerEngine.OnUpdate(dt);
            AccessRpcHardeningEngine.OnUpdate(dt);
            AccessBossLockdownCorrectionEngine.OnUpdate(dt);
        }

        /// <summary>Flush any dirty in-memory ACL/roster state before the process actually exits - the periodic flush timer alone would otherwise lose up to StoreFlushSeconds of the most recent changes.</summary>
        public void Shutdown()
        {
            AccessAclStore.Save();
            AccessTeamRoster.Save();
        }
    }
}
