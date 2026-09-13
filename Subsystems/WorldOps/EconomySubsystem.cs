using BepInEx.Configuration;
using HarmonyLib;
using ServerSync;
using TortalPortalLite.Core;

namespace TortalPortalLite.Subsystems.WorldOps
{
    /// <summary>
    /// The `economy` domain, trimmed to this build's wanted set: #98 The Ore Gate. Thin dispatcher, same
    /// shape as Subsystems/Foundations/PortalOpsSubsystem.cs.
    /// </summary>
    public class EconomySubsystem : IPortalSubsystem
    {
        public string Name => "Economy";
        public bool IsEnabled => EconomyConfig.Enabled?.Value != false;

        public void Initialize(ConfigFile config, ConfigSync configSync, Harmony harmony)
        {
            EconomyConfig.Bind(config, configSync);
        }

        public void OnWorldReady()
        {
        }

        public void OnUpdate()
        {
            float dt = UnityEngine.Time.deltaTime;
            EconomyRegistry.OnUpdate(dt);
            EconomyOreGateEngine.OnUpdate(dt);
        }

        public void Shutdown()
        {
        }
    }
}
