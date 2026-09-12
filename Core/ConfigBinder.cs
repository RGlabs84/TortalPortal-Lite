using BepInEx.Configuration;
using ServerSync;

namespace TortalPortalLite.Core
{
    /// <summary>
    /// Shared config-binding helpers, forked from Wonderland's WonderlandConfig.cs (its five private
    /// Bind* helpers only - not its ~130-field single-static-class pattern). Deliberately NOT a
    /// centralized config class: this mod is built by several concurrent subagents across waves, and
    /// Wonderland's own pattern of one 447-line static class every subsystem edits does not survive
    /// that. Instead each subsystem owns its own `<Group>Config.cs` (e.g.
    /// Subsystems/Topology/TopologyConfig.cs) with its own static ConfigEntry fields, calling these
    /// same five helpers from its own Initialize(). This file's signatures are frozen - a subsystem's
    /// config file should never need to touch this one.
    /// </summary>
    public static class ConfigBinder
    {
        public static ConfigEntry<float> BindSynced(ConfigFile cfg, ConfigSync sync, string section, string key, float def, string desc, float min = float.MinValue, float max = float.MaxValue)
        {
            ConfigDescription description = (min != float.MinValue && max != float.MaxValue)
                ? new ConfigDescription(desc, new AcceptableValueRange<float>(min, max))
                : new ConfigDescription(desc);

            var entry = cfg.Bind(section, key, def, description);
            sync.AddConfigEntry(entry);
            return entry;
        }

        public static ConfigEntry<int> BindSyncedInt(ConfigFile cfg, ConfigSync sync, string section, string key, int def, string desc, int min = int.MinValue, int max = int.MaxValue)
        {
            ConfigDescription description = (min != int.MinValue && max != int.MaxValue)
                ? new ConfigDescription(desc, new AcceptableValueRange<int>(min, max))
                : new ConfigDescription(desc);

            var entry = cfg.Bind(section, key, def, description);
            sync.AddConfigEntry(entry);
            return entry;
        }

        public static ConfigEntry<bool> BindSynced(ConfigFile cfg, ConfigSync sync, string section, string key, bool def, string desc)
        {
            var entry = cfg.Bind(section, key, def, new ConfigDescription(desc));
            sync.AddConfigEntry(entry);
            return entry;
        }

        public static ConfigEntry<string> BindSynced(ConfigFile cfg, ConfigSync sync, string section, string key, string def, string desc)
        {
            var entry = cfg.Bind(section, key, def, new ConfigDescription(desc));
            sync.AddConfigEntry(entry);
            return entry;
        }

        public static ConfigEntry<T> BindLocal<T>(ConfigFile cfg, string section, string key, T def, string desc)
        {
            return cfg.Bind(section, key, def, new ConfigDescription(desc));
        }
    }
}
