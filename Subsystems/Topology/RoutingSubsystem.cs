using BepInEx.Configuration;
using HarmonyLib;
using ServerSync;
using TortalPortalLite.Core;

namespace TortalPortalLite.Subsystems.Topology
{
    /// <summary>
    /// The `routing` domain, trimmed to this build's wanted set: #24 Phantom Anchor Fabrication (as a
    /// standing, admin-declarable provisioner), #29 Progression-Gated Sealed Gate, #224
    /// DestinationPrewarm, #225 Fast-Transit Mode (#227 Parked Terminal is the same engine, its base
    /// mechanism). Thin dispatcher, same shape as Subsystems/Foundations/PortalOpsSubsystem.cs: this
    /// class owns config binding and per-tick ordering; every engine's actual policy logic lives in its
    /// own file.
    ///
    /// Tick order: the shared registry poll and the pairing-authority safety net run first (load-bearing
    /// substrate every engine below writes through), then phantom-anchor provisioning (so a
    /// freshly-declared anchor exists before anything tries to point at it this same tick), then the
    /// sealed gate and parked terminal, then the delivery-assurance engines last - so they observe this
    /// tick's freshest connections.
    ///
    /// Does not itself write s_tag/ConnectionType.Portal - every engine here writes exclusively through
    /// RoutingWriteOps.Reassert, which itself always goes through Core/Data/PortalOwnership.ClaimAndWrite.
    /// </summary>
    public class RoutingSubsystem : IPortalSubsystem
    {
        public string Name => "Routing";
        public bool IsEnabled => GlobalConfig.Enabled?.Value != false;

        public void Initialize(ConfigFile config, ConfigSync configSync, Harmony harmony)
        {
            RoutingConfig.Bind(config, configSync);
            RoutingPairingAuthorityEngine.Initialize();
        }

        public void OnWorldReady()
        {
        }

        public void OnUpdate()
        {
            float dt = UnityEngine.Time.deltaTime;

            int versionBefore = RoutingManagedPortalRegistry.Version;
            RoutingManagedPortalRegistry.OnUpdate(dt);
            if (RoutingManagedPortalRegistry.Version != versionBefore)
            {
                // routing.json changed - release every position claim so a mechanism that was removed
                // (or swapped for a different one at the same spot) cannot permanently block a fresh
                // declaration there; every engine below re-claims its own declared positions this same tick.
                RoutingPairingAuthorityEngine.ResetClaims();
            }
            RoutingPairingAuthorityEngine.OnUpdate(dt);

            RoutingPhantomAnchorEngine.OnUpdate(dt);
            RoutingSealedGateEngine.OnUpdate(dt);
            RoutingParkedTerminalEngine.OnUpdate(dt);

            // Delivery-assurance engines - run last so they observe this tick's freshest connections.
            RoutingPrewarmEngine.OnUpdate(dt);
            RoutingZoneGhostPreGenEngine.OnUpdate(dt);
        }

        public void Shutdown()
        {
        }
    }
}
