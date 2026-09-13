using BepInEx.Configuration;
using ServerSync;
using TortalPortalLite.Core;

namespace TortalPortalLite.Subsystems.PlayerInterface
{
    /// <summary>
    /// Config surface for the `ux` domain, trimmed to this build's wanted set: #266 Live Portal Markers.
    /// Own subsystem, own config file, same rule every other subsystem in this mod follows.
    /// </summary>
    public static class UxConfig
    {
        // --- General ---
        public static ConfigEntry<bool>? Enabled;

        // --- #266 Live portal markers ---
        public static ConfigEntry<bool>? LiveMarkersEnabled;
        public static ConfigEntry<float>? LiveMarkersRadius;
        public static ConfigEntry<float>? LiveMarkersReassertSeconds;

        public static void Bind(ConfigFile config, ConfigSync configSync)
        {
            const string sGeneral = "30 - Ux: General";
            const string sAdvanced = "40 - Ux: Advanced Schemes";

            Enabled = ConfigBinder.BindSynced(config, configSync, sGeneral, "Enabled", true,
                "Master switch for the whole ux domain. If off, every engine below still registers but no-ops.");

            LiveMarkersEnabled = ConfigBinder.BindLocal(config, sAdvanced, "LiveMarkersEnabled", false,
                "#266 removable map markers via PersistentEventSystem. Off by default - shares world state with vanilla's own event system; enable deliberately.");
            LiveMarkersRadius = ConfigBinder.BindSynced(config, configSync, sAdvanced, "LiveMarkersRadius", 6f,
                "Marker circle radius (metres) - kept small to minimise side effects on nearby spawners/pieces that react to persistent events.", 1f, 8f);
            LiveMarkersReassertSeconds = ConfigBinder.BindSynced(config, configSync, sAdvanced, "LiveMarkersReassertSeconds", 30f,
                "How often the marker list is re-broadcast, overwriting any client-requested (spoofed) removal.", 5f, 120f);
        }
    }
}
