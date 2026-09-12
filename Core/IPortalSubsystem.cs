using BepInEx.Configuration;
using HarmonyLib;
using ServerSync;

namespace TortalPortalLite.Core
{
    public interface IPortalSubsystem
    {
        string Name { get; }
        bool IsEnabled { get; }

        void Initialize(ConfigFile config, ConfigSync configSync, Harmony harmony);

        /// <summary>
        /// Fires once, from a Harmony postfix on ZNetScene.Awake - the first point at which
        /// ZNetScene.instance.m_prefabs/ObjectDB.instance are actually populated. BepInEx's own
        /// Awake() (and therefore Initialize above) runs far earlier, before any world data exists,
        /// so prefab-dependent setup belongs here instead - never in Initialize.
        /// </summary>
        void OnWorldReady();

        void OnUpdate();
        void Shutdown();
    }
}
