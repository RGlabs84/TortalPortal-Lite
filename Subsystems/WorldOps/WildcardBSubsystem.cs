using BepInEx.Configuration;
using HarmonyLib;
using ServerSync;
using TortalPortalLite.Core;

namespace TortalPortalLite.Subsystems.WorldOps
{
    /// <summary>
    /// Wave 3 "wildcard cluster B" subsystem - motion/companion/world-event/Sector-Zero mechanisms:
    ///
    ///  #129 Skyfall/Seabed/Underworld Exits        -> WildcardBArrivalLadderEngine (pure API, no tick)
    ///  #130 Crypt Ingress                          -> WildcardBCryptIngressEngine
    ///  #131 Ephemeral Event Gates                  -> WildcardBEventGateEngine
    ///  #136 Portal As Trigger, RPC As Transport     -> WildcardBTriggerTransportEngine
    ///  #143 Sector Zero + #244 Sector Zero, Named   -> WildcardBSectorZeroEngine (ONE merged engine)
    ///  #148 Server-Enforced Portal Caps            -> WildcardBPortalCapEngine
    ///  #233 The Event Beacon                       -> WildcardBEventBeaconEngine (pure API, no tick)
    ///  #234 The Landing Pad Engine                 -> WildcardBLandingPadEngine (pure API, no tick)
    ///  #235 Phantom Survival                       -> WildcardBPhantomSurvivalEngine (pure API, no tick)
    ///  #236 The Ferry Route                        -> WildcardBFerryRouteEngine
    ///  #237 Player's Ship, Corrected                -> WildcardBKinematicShipEngine (pure API, no tick)
    ///  #238 Tame And Cart Follow-Through            -> WildcardBCompanionTransitEngine
    ///  #239 Per-Peer Location Icons                -> WildcardBFactionSpawnEngine
    ///  #240 Ambush Gates                            -> WildcardBAmbushGateEngine
    ///  #246 Boss Blackout, Corrected                -> NOT implemented here. Read against
    ///       Subsystems/Enforcement/LockdownBossWatchdogEngine.cs (Wave 2) first, per this wave's own
    ///       instructions: that engine's own header already documents both halves #246 itself names
    ///       (the per-client HUD half, server-unobservable; the world-wide activeBosses key half,
    ///       server-observable/drivable) and already implements the corresponding watchdog for the
    ///       leak case ("a boss that alerts and then has its owning client disconnect leaks the counter
    ///       forever"). This is a straight duplicate, not a missing "other half" - see this wave's final
    ///       report for the full comparison.
    ///
    /// 15 distinct engines for 16 catalog entries (#143/#244 merge into one; #246 is a documented
    /// duplicate and is skipped). Follows Subsystems/Foundations/PortalOpsSubsystem.cs's own established
    /// pattern exactly: a thin dispatcher, real logic in sibling static engine classes.
    /// </summary>
    public class WildcardBSubsystem : IPortalSubsystem
    {
        public string Name => "WildcardB";
        public bool IsEnabled => GlobalConfig.Enabled?.Value != false && WildcardBConfig.Enabled?.Value != false;

        public void Initialize(ConfigFile config, ConfigSync configSync, Harmony harmony)
        {
            WildcardBConfig.Bind(config, configSync);
            WildcardBPortalCapEngine.Initialize();
            WildcardBTriggerTransportEngine.Initialize();
        }

        public void OnWorldReady()
        {
        }

        public void OnUpdate()
        {
            float dt = UnityEngine.Time.deltaTime;

            WildcardBSectorZeroEngine.OnUpdate(dt);
            WildcardBCryptIngressEngine.OnUpdate(dt);
            WildcardBEventGateEngine.OnUpdate(dt);
            WildcardBTriggerTransportEngine.OnUpdate(dt);
            WildcardBPortalCapEngine.OnUpdate(dt);
            WildcardBFerryRouteEngine.OnUpdate(dt);
            WildcardBCompanionTransitEngine.OnUpdate(dt);
            WildcardBFactionSpawnEngine.OnUpdate(dt);
            WildcardBAmbushGateEngine.OnUpdate(dt);
        }

        public void Shutdown()
        {
        }
    }
}
