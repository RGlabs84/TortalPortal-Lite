using BepInEx.Configuration;
using HarmonyLib;
using ServerSync;
using TortalPortalLite.Core;

namespace TortalPortalLite.Subsystems.WorldOps
{
    /// <summary>
    /// The "wildcard cluster A" domain subsystem (Wave 3, data-store/signage/puzzle/lore mechanisms -
    /// catalog #128/#132/#133/#134/#135/#139/#140/#141/#142/#146/#149/#151/#241/#242/#243, 15 catalog
    /// entries collapsing to 13 engines since #135/#241 and #134/#243 are each one corrected mechanism,
    /// not two). Thin dispatcher over sibling static engine classes, following PortalOpsSubsystem's
    /// (#84) own established shape exactly: real logic lives in the engines, this file only wires
    /// Initialize/OnWorldReady/OnUpdate order.
    ///
    /// Tick order rationale: hook-registering engines' Initialize() runs first (mirrors
    /// PortalOpsSubsystem calling CommandEngine.Install before anything else); boot-time scans run from
    /// OnWorldReady once ZNetScene/ObjectDB are populated; the per-tick loop puts the client-capability
    /// handshake retry first (cheap, and other engines may want to query IsEnhancedPeer), then the four
    /// tag/connection-writing engines (Void Anchor, Decoy Gate, Adamant Gate, Puzzle Gate - each owns a
    /// disjoint set of portals it created/claimed, so ordering between them is not semantically load-
    /// bearing the way Foundations' Census-before-Reassert order is), then the read-only/observational
    /// engines last (Self-Organising sampling, which only reads PortalCensus/ConnectedCharacters).
    /// </summary>
    public class WildcardASubsystem : IPortalSubsystem
    {
        public string Name => "WorldOps.WildcardA";
        public bool IsEnabled => GlobalConfig.Enabled?.Value != false && WildcardAConfig.Enabled?.Value != false;

        public void Initialize(ConfigFile config, ConfigSync configSync, Harmony harmony)
        {
            WildcardAConfig.Bind(config, configSync);

            // Engines that register against an existing Core/Hooks/ broker (or a plain ZRoutedRpc.Register
            // call, not a Harmony patch) do so once here, mirroring AuditEngine/PortalDirtyFlagGuardian's
            // own Initialize-time registration in PortalOpsSubsystem.
            WildcardAAdamantGateEngine.Initialize();
            WildcardAPuzzleGateEngine.Initialize();
            WildcardASelfOrganizingNetworkEngine.Initialize();
        }

        public void OnWorldReady()
        {
            // Boot-time rediscovery of pre-existing mod state (server restart) - same shape as
            // TargetedAnchorFactory/TargetedPhantomPortalFactory's own BootstrapScan in the Topology domain.
            WildcardAVoidAnchorEngine.OnWorldReady();
            WildcardADecoyGateEngine.OnWorldReady();
            WildcardAAdamantGateEngine.OnWorldReady();
            WildcardAPortalScalingEngine.OnWorldReady();
        }

        public void OnUpdate()
        {
            if (!IsEnabled)
            {
                return;
            }
            float dt = UnityEngine.Time.deltaTime;

            // ZRoutedRpc.instance may not exist yet at Initialize() time (Core boot runs before world
            // networking spins up) - retry here until it does; idempotent, cheap once registered.
            WildcardAClientCapabilityEngine.Initialize();
            WildcardAClientCapabilityEngine.PruneDisconnected();

            WildcardAVoidAnchorEngine.OnUpdate(dt);
            WildcardADecoyGateEngine.OnUpdate(dt);
            WildcardAAdamantGateEngine.OnUpdate(dt);
            WildcardAPuzzleGateEngine.OnUpdate(dt);
            WildcardASelfOrganizingNetworkEngine.OnUpdate(dt);
        }

        public void Shutdown()
        {
        }
    }
}
