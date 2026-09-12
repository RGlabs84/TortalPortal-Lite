using BepInEx.Configuration;
using HarmonyLib;
using ServerSync;
using TortalPortalLite.Core;

namespace TortalPortalLite.Subsystems.Topology
{
    /// <summary>
    /// The `targeted` domain's dispatcher - fixed/computed-destination portals (a specific coordinate,
    /// boss altar, trader, biome, player's bed, player-placed anchor, etc.), implementing wave1_targeted.json
    /// (23 catalog options: #178-196, #204-207). Thin by design, same pattern as
    /// Subsystems/Foundations/PortalOpsSubsystem.cs: real logic lives in sibling static engine classes
    /// with their own Initialize()/OnUpdate(float dt)/Shutdown(), this class only owns registration order
    /// and the master enable gate.
    ///
    /// Tick order below is semantics, not taste:
    ///  - TargetedRouteStore/TargetedPrefabDiscovery/TargetedZoneGovernorEngine are pure infrastructure
    ///    and run first.
    ///  - TargetedPhantomPortalFactory/TargetedAnchorFactory's own maintenance passes run next so every
    ///    option-specific engine below observes a freshly-reaped, freshly-reasserted set of phantoms
    ///    before deciding whether to (re)create one of its own.
    ///  - TargetedSpawnPointEngine runs before TargetedBedEngine because Bed's own placement logic reads
    ///    SpawnPointEngine's inferred-home override (#188 is an enhancement layered onto #187, not a
    ///    separate hub kind - see TargetedBedEngine.PlaceFor).
    ///  - TargetedTombstoneEngine runs before TargetedCorpseRunEngine, which reuses its tombstone
    ///    tracking as its own death-detection signal (see TargetedCorpseRunEngine's own remarks).
    ///  - TargetedMapPingEngine runs before TargetedCorpseRunEngine only incidentally (CorpseRun calls
    ///    MapPing's PushSavedPin helper, a pure function with no ordering dependency).
    /// </summary>
    public class TargetedSubsystem : IPortalSubsystem
    {
        public string Name => "Targeted";
        public bool IsEnabled => GlobalConfig.Enabled?.Value != false;

        public void Initialize(ConfigFile config, ConfigSync configSync, Harmony harmony)
        {
            TargetedConfig.Bind(config, configSync);

            TargetedPhantomPortalFactory.Initialize();
            TargetedBedEngine.Initialize();
            TargetedTombstoneEngine.Initialize();
            TargetedShipEngine.Initialize();
            TargetedNearestPlayerEngine.Initialize();
            TargetedPlayerAnchorEngine.Initialize();
            TargetedBaseCentroidEngine.Initialize();
        }

        public void OnWorldReady()
        {
            TargetedPrefabDiscovery.OnWorldReady();
            TargetedAnchorFactory.OnWorldReady();
        }

        public void OnUpdate()
        {
            float dt = UnityEngine.Time.deltaTime;

            // Infrastructure
            TargetedRouteStore.OnUpdate(dt);
            TargetedZoneGovernorEngine.OnUpdate(dt);
            TargetedPhantomPortalFactory.OnUpdate(dt);
            TargetedAnchorFactory.OnUpdate(dt);

            // Dynamic / per-player claimed destinations
            TargetedSpawnPointEngine.OnUpdate(dt);
            TargetedBedEngine.OnUpdate(dt);
            TargetedTombstoneEngine.OnUpdate(dt);
            TargetedShipEngine.OnUpdate(dt);
            TargetedNearestPlayerEngine.OnUpdate(dt);
            TargetedPlayerAnchorEngine.OnUpdate(dt);
            TargetedMapPingEngine.OnUpdate(dt);
            TargetedBuilderTraceEngine.OnUpdate(dt);
            TargetedBaseCentroidEngine.OnUpdate(dt);

            // Static / admin-declared destinations
            TargetedAdminCoordinatesEngine.OnUpdate(dt);
            TargetedWorldSpawnEngine.OnUpdate(dt);
            TargetedBossAltarEngine.OnUpdate(dt);
            TargetedTraderEngine.OnUpdate(dt);
            TargetedDungeonEngine.OnUpdate(dt);
            TargetedBiomeEngine.OnUpdate(dt);
            TargetedLocationEngine.OnUpdate(dt);
            TargetedTerminalArchitectureEngine.OnUpdate(dt);

            // Event-driven
            TargetedCorpseRunEngine.OnUpdate(dt);
        }

        public void Shutdown()
        {
        }
    }
}
