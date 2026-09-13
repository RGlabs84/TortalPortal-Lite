using BepInEx.Configuration;
using ServerSync;
using TortalPortalLite.Core;

namespace TortalPortalLite.Subsystems.WorldOps
{
    /// <summary>
    /// Config surface for the `economy` domain, trimmed to this build's wanted set: #98 The Ore Gate.
    /// Follows Subsystems/Topology/RoutingConfig.cs's own precedent for section numbering.
    /// </summary>
    public static class EconomyConfig
    {
        public static ConfigEntry<bool>? Enabled;
        public static ConfigEntry<string>? RegistryFile;

        public static void Bind(ConfigFile config, ConfigSync configSync)
        {
            const string sCore = "90 - Economy: Core Kernel";

            Enabled = ConfigBinder.BindSynced(config, configSync, sCore, "Enabled", true,
                "Master switch for the whole economy domain. If off, every economy engine still registers but no-ops.");
            RegistryFile = ConfigBinder.BindLocal(config, sCore, "File", "economy.json",
                "Path (relative to the plugin folder) to the hot-reloaded economy declarations (Ore Gate upgrades, section \"oreGates\"). Local, not synced - a file path is server-machine-specific.");
        }
    }
}
