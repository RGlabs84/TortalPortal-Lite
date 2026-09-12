using BepInEx.Configuration;
using HarmonyLib;
using ServerSync;
using TortalPortalLite.Core;

namespace TortalPortalLite.Subsystems.Topology
{
    /// <summary>
    /// Thin dispatcher for the Topologies domain (catalog #s 1, 2, 4, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15,
    /// 16, 17, 18, 19, 20, 21 - "Wave 1: the topology core"), following the same pattern as
    /// Subsystems/Foundations/PortalOpsSubsystem.cs: real logic lives entirely in sibling `static` engine
    /// classes, each with its own Initialize()/OnUpdate(float dt); this class only sequences them.
    ///
    /// Tick order matters here for the same reason PortalOpsSubsystem's own doc comment gives:
    /// TopologiesDefinitions (the hot-reloaded topologies.json) must refresh before anything reads it
    /// this tick, and the plain declarative shape/vestibule/sharding passes run before the
    /// player/timer-driven ones so a just-reloaded declaration is visible to Switchboard/Carousel's own
    /// star-leg maintenance in the same tick it changes.
    ///
    /// This class is intentionally NOT registered anywhere in this file - Plugin.cs's
    /// `Subsystems.Register(...)` wiring is the orchestrator's job for this wave, per this wave's own
    /// task boundaries.
    /// </summary>
    public class TopologiesSubsystem : IPortalSubsystem
    {
        public string Name => "Topologies";
        public bool IsEnabled => GlobalConfig.Enabled?.Value != false;

        public void Initialize(ConfigFile config, ConfigSync configSync, Harmony harmony)
        {
            TopologiesConfig.Bind(config, configSync);
            TopologiesSwitchboardEngine.Initialize();
            TopologiesRouletteEngine.Initialize();
            TopologiesDisplacedRoutingEngine.Initialize();
        }

        public void OnWorldReady()
        {
        }

        public void OnUpdate()
        {
            float dt = UnityEngine.Time.deltaTime;

            TopologiesDefinitions.OnUpdate(dt);

            TopologiesShapeEngine.OnUpdate(dt);
            TopologiesVestibuleEngine.OnUpdate(dt);
            TopologiesPrivateNetworksEngine.OnUpdate(dt);
            TopologiesFactionEngine.OnUpdate(dt);
            TopologiesLockdownEngine.OnUpdate(dt);
            TopologiesRouletteEngine.OnUpdate(dt);
            TopologiesDisplacedRoutingEngine.OnUpdate(dt);

            TopologiesSwitchboardEngine.OnUpdate(dt);
            TopologiesCarouselEngine.OnUpdate(dt);
        }

        /// <summary>Catalog #17's own explicit warning: a mid-lockdown uninstall/shutdown must restore first, or every portal is left a permanently-scrambled singleton tag group.</summary>
        public void Shutdown()
        {
            TopologiesLockdownEngine.ForceRestoreAll();
        }
    }
}
