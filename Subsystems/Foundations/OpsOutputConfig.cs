using System.IO;
using BepInEx.Configuration;
using ServerSync;
using TortalPortalLite.Core;

namespace TortalPortalLite.Subsystems.Foundations
{
    /// <summary>
    /// Config surface for the ops output engines this build actually keeps (#71 Repair, #78 Snapshot,
    /// #82 Compat). Everything else the original ops wave built (file-queue channel, network-declaration
    /// validation, JSON/CSV export, Discord, HTTP endpoint, realm map, join briefing) was cut - this
    /// product's final feature set does not use them.
    /// </summary>
    public static class OpsOutputConfig
    {
        // --- shared ---
        public static string PluginConfigDir { get; private set; } = ".";

        // --- #71 repair, extending FoundationsConfig's [5 - Foundations: Repair] section ---
        public static ConfigEntry<bool>? AllowRegistrySurgery;
        public static ConfigEntry<float>? RepairAutoIntervalSeconds;
        public static ConfigEntry<float>? RepairSkipRecentlyChangedSeconds;

        // --- #78 snapshot / rollback / migration ---
        public static ConfigEntry<string>? SnapshotDirName;
        public static ConfigEntry<int>? SnapshotRetain;
        public static ConfigEntry<bool>? SnapshotOnReassertApply;

        public static void Bind(ConfigFile config, ConfigSync configSync)
        {
            PluginConfigDir = Path.GetDirectoryName(config.ConfigFilePath) ?? ".";

            const string sRepair = "82 - Ops: Repair (extra)";
            const string sSnapshot = "83 - Ops: Snapshot";

            AllowRegistrySurgery = ConfigBinder.BindLocal(config, sRepair, "AllowRegistrySurgery", false,
                "Extremely dangerous: allows RepairEngine to mutate ZDOMan's live m_portalObjects dictionary directly to remove a duplicate-bucket entry (catalog #71's own warning: 'powerful and dangerous'). Off by default; every use is logged loudly.");
            RepairAutoIntervalSeconds = ConfigBinder.BindLocal(config, sRepair, "AutoIntervalSeconds", 300f,
                "How often RepairEngine runs an automatic pass when FoundationsConfig.AutoRepair is on (dry-run unless RepairDryRunDefault is false).");
            RepairSkipRecentlyChangedSeconds = ConfigBinder.BindLocal(config, sRepair, "SkipRecentlyChangedSeconds", 6f,
                "Skip repairing a portal whose connection changed within this many seconds - avoids fighting vanilla's own 5s ConnectPortals reconciliation pass (catalog #71 failure mode).");

            SnapshotDirName = ConfigBinder.BindLocal(config, sSnapshot, "DirName", "snapshots",
                "Subdirectory (under the plugin config directory) snapshots are written to.");
            SnapshotRetain = ConfigBinder.BindLocal(config, sSnapshot, "Retain", 30,
                "How many snapshot files to keep before pruning the oldest (auto-snapshots only - named/manual snapshots are never auto-pruned).");
            SnapshotOnReassertApply = ConfigBinder.BindLocal(config, sSnapshot, "OnRepairApply", true,
                "Take an automatic snapshot immediately before every --apply repair pass, so tplite restore can always undo the whole batch.");
        }
    }
}
