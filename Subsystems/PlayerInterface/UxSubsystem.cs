using BepInEx.Configuration;
using HarmonyLib;
using ServerSync;
using TortalPortalLite.Core;

namespace TortalPortalLite.Subsystems.PlayerInterface
{
    /// <summary>
    /// The `ux` domain, trimmed to this build's wanted set: #266 Live Portal Markers. Thin dispatcher,
    /// same shape as Subsystems/Foundations/PortalOpsSubsystem.cs.
    /// </summary>
    public class UxSubsystem : IPortalSubsystem
    {
        public string Name => "PlayerInterface";
        public bool IsEnabled => GlobalConfig.Enabled?.Value != false && UxConfig.Enabled?.Value != false;

        public void Initialize(ConfigFile config, ConfigSync configSync, Harmony harmony)
        {
            UxConfig.Bind(config, configSync);
        }

        public void OnWorldReady()
        {
        }

        public void OnUpdate()
        {
            float dt = UnityEngine.Time.deltaTime;
            UxLivePortalMarkersEngine.OnUpdate(dt);
        }

        public void Shutdown()
        {
        }
    }
}
