using BepInEx.Configuration;
using HarmonyLib;
using ServerSync;
using TortalPortalLite.Core;

namespace TortalPortalLite.Subsystems.Topology
{
    /// <summary>
    /// The `targeted` domain, trimmed to this build's wanted set: #207 Corpse-Run Gate, built on the
    /// shared phantom-portal factory (#178) and the bed/tombstone lookup substrate (#187/#189). Thin
    /// dispatcher, same pattern as Subsystems/Foundations/PortalOpsSubsystem.cs.
    ///
    /// Tick order: prefab discovery resolves once at OnWorldReady; the phantom factory's own maintenance
    /// pass runs first each tick so CorpseRun always sees a freshly-reaped, freshly-reasserted phantom
    /// set; the bed/tombstone sweeps run next so their lookup tables are current before CorpseRun reads
    /// them this same tick.
    ///
    /// CorpseRun also re-asserts its own one-way link from a Game.ConnectPortals postfix, which fires on
    /// vanilla's own 5 s cadence rather than this tick order - see TargetedCorpseRunEngine's header for
    /// why that has to happen inside vanilla's call rather than on the next tick.
    /// </summary>
    public class TargetedSubsystem : IPortalSubsystem
    {
        public string Name => "Targeted";
        public bool IsEnabled => GlobalConfig.Enabled?.Value != false;

        public void Initialize(ConfigFile config, ConfigSync configSync, Harmony harmony)
        {
            TargetedConfig.Bind(config, configSync);
            TargetedPhantomPortalFactory.Initialize();
            TargetedCorpseRunEngine.Initialize();
            // Not SafePatch: this set is installed patch-by-patch so one signature drift disables one
            // layer with a named warning instead of silently dropping the whole set.
            TargetedPortalProtection.Install(harmony);
        }

        public void OnWorldReady()
        {
            TargetedPrefabDiscovery.OnWorldReady();
        }

        public void OnUpdate()
        {
            float dt = UnityEngine.Time.deltaTime;

            TargetedPhantomPortalFactory.OnUpdate(dt);
            TargetedBedEngine.OnUpdate(dt);
            TargetedTombstoneEngine.OnUpdate(dt);
            TargetedCorpseRunEngine.OnUpdate(dt);
        }

        public void Shutdown()
        {
        }
    }
}
