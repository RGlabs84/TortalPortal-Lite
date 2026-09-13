using BepInEx.Configuration;
using HarmonyLib;
using ServerSync;
using TortalPortalLite.Core;

namespace TortalPortalLite.Subsystems.WorldOps
{
    /// <summary>
    /// The "wildcard cluster B" domain, trimmed to this build's wanted set: #148 Server-Enforced Portal
    /// Caps. Thin dispatcher, same shape as Subsystems/Foundations/PortalOpsSubsystem.cs.
    /// </summary>
    public class WildcardBSubsystem : IPortalSubsystem
    {
        public string Name => "WildcardB";
        public bool IsEnabled => GlobalConfig.Enabled?.Value != false && WildcardBConfig.Enabled?.Value != false;

        public void Initialize(ConfigFile config, ConfigSync configSync, Harmony harmony)
        {
            WildcardBConfig.Bind(config, configSync);
            WildcardBPortalCapEngine.Initialize();
        }

        public void OnWorldReady()
        {
        }

        public void OnUpdate()
        {
            float dt = UnityEngine.Time.deltaTime;
            WildcardBPortalCapEngine.OnUpdate(dt);
        }

        public void Shutdown()
        {
        }
    }
}
