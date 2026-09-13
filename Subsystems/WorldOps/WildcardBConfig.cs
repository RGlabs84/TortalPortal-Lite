using BepInEx.Configuration;
using ServerSync;
using TortalPortalLite.Core;

namespace TortalPortalLite.Subsystems.WorldOps
{
    /// <summary>
    /// Config surface for the "wildcard cluster B" domain, trimmed to this build's wanted set: #148
    /// Server-Enforced Portal Caps.
    /// </summary>
    public static class WildcardBConfig
    {
        public static ConfigEntry<bool>? Enabled;

        // --- #148 Server-Enforced Portal Caps ---
        public static ConfigEntry<bool>? PortalCapEnabled;
        public static ConfigEntry<int>? PortalCapPerCreator;
        public static ConfigEntry<float>? PortalCapSweepSeconds;

        public static void Bind(ConfigFile config, ConfigSync configSync)
        {
            const string sGeneral = "150 - WildcardB: General";
            const string sCap = "155 - WildcardB: Portal Caps";

            Enabled = ConfigBinder.BindSynced(config, configSync, sGeneral, "Enabled", true,
                "Master switch for the whole wildcard cluster B domain.");

            PortalCapEnabled = ConfigBinder.BindSynced(config, configSync, sCap, "Enabled", false,
                "Master switch for #148 Server-Enforced Portal Caps - destroys a creator's newest portal ZDOs past the configured per-creator cap on arrival, unlike a client-side placement cap this cannot be bypassed by an unmodified client.");
            PortalCapPerCreator = ConfigBinder.BindSyncedInt(config, configSync, sCap, "PerCreatorCap", 6,
                "Maximum live portals a single s_creator (player id) may own before the newest excess are destroyed.", 1, 999);
            PortalCapSweepSeconds = ConfigBinder.BindLocal(config, sCap, "SweepSeconds", 2f,
                "How often (seconds) the per-creator portal census tally is recomputed and enforced.");
        }
    }
}
