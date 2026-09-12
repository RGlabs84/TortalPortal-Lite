using BepInEx.Configuration;
using HarmonyLib;
using ServerSync;
using TortalPortalLite.Core;

namespace TortalPortalLite.Subsystems.Topology
{
    /// <summary>
    /// Wave 1 `routing` domain (catalog #23-#40, #222-#232 - dynamic/conditional portal routing).
    /// Thin dispatcher over ~25 sibling engine files, one (or a tightly-coupled pair) per catalog
    /// option, exactly the Foundations subsystem's own established shape
    /// (Subsystems/Foundations/PortalOpsSubsystem.cs): this class owns config binding, hook/emote
    /// registration and per-tick ordering; every engine's actual policy logic lives in its own file.
    ///
    /// Tick order: the shared registry poll and the pairing-authority safety net run first, then every
    /// mechanism-specific engine (grouped as topology/schedule/approach/player-input/anchor engines),
    /// then the delivery-assurance engines last - so a delivery engine reading "what does this portal
    /// currently point at" always sees this SAME tick's freshest decision, not last tick's.
    ///
    /// Does not itself write s_tag/ConnectionType.Portal - every engine here writes exclusively through
    /// RoutingWriteOps.Reassert, which itself always goes through
    /// Core/Data/PortalOwnership.ClaimAndWrite, per this mod's one ownership rule
    /// (Subsystems/Foundations/NetworkReassertEngine.cs's own doc comment).
    /// </summary>
    public class RoutingSubsystem : IPortalSubsystem
    {
        public string Name => "Routing";
        public bool IsEnabled => GlobalConfig.Enabled?.Value != false;

        public void Initialize(ConfigFile config, ConfigSync configSync, Harmony harmony)
        {
            RoutingConfig.Bind(config, configSync);

            RoutingPairingAuthorityEngine.Initialize();
            RoutingDeterministicAssignmentEngine.Initialize();
            RoutingPlayerRequestEngine.Initialize();
            RoutingPrivateTerminalEngine.Initialize();
        }

        public void OnWorldReady()
        {
            // Pre-peer window: m_peers is empty and every write below takes the immediate local path -
            // the cheapest point in the server's whole lifecycle to write a topology (catalog #27/#38's
            // own explicit guidance).
            RoutingRingRotationEngine.OnWorldReady();
            RoutingSeasonalSwapEngine.OnWorldReady();
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

            // Topology / condition engines - each computes and writes its own managed portals' desired state.
            RoutingRotatingHubEngine.OnUpdate(dt);
            RoutingRingRotationEngine.OnUpdate(dt);
            RoutingScheduledEngine.OnUpdate(dt);
            RoutingSealedGateEngine.OnUpdate(dt);
            RoutingEventRetargetEngine.OnUpdate(dt);
            RoutingSeasonalSwapEngine.OnUpdate(dt);
            RoutingWorldStateEngine.OnUpdate(dt);
            RoutingMovingAnchorEngine.OnUpdate(dt);
            RoutingRoguelikeRerollEngine.OnUpdate(dt);

            // Approach / player-input engines.
            RoutingApproachJitEngine.OnUpdate(dt);
            RoutingCharacterPredicateEngine.OnUpdate(dt);
            RoutingQueueDispatchEngine.OnUpdate(dt);
            RoutingSelfDisconnectEngine.OnUpdate(dt);
            RoutingPlayerRequestEngine.OnUpdate(dt);
            RoutingPrivateTerminalEngine.OnUpdate(dt);
            RoutingParkedTerminalEngine.OnUpdate(dt);

            // Delivery-assurance engines - run last so they observe this tick's freshest connections.
            RoutingPreStreamEngine.OnUpdate(dt);
            RoutingJoinBroadcastEngine.OnUpdate(dt);
            RoutingPrewarmEngine.OnUpdate(dt);
            RoutingZoneGhostPreGenEngine.OnUpdate(dt);
            RoutingPeerLinkProbeEngine.OnUpdate(dt);
        }

        public void Shutdown()
        {
        }
    }
}
