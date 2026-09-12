using System.IO;
using BepInEx.Configuration;

namespace TortalPortalLite.Subsystems.PlayerInterface
{
    /// <summary>
    /// Shared "resolve a configured file name against the plugin's own config folder" helper - the same
    /// anchor Foundations' `NetworkModel.FilePath` already uses
    /// (`Path.GetDirectoryName(entry.ConfigFile.ConfigFilePath)`), so every admin-declared JSON file this
    /// mod reads (networks.json, and this domain's own equip/chest/plate/socket declarations) lives
    /// next to the same .cfg an admin is already editing, not scattered across the BepInEx tree.
    /// </summary>
    internal static class UxFilePaths
    {
        public static string Resolve(ConfigEntry<string>? fileNameEntry, string fallbackFileName)
        {
            string fileName = fileNameEntry?.Value ?? fallbackFileName;
            string dir = Path.GetDirectoryName(fileNameEntry?.ConfigFile?.ConfigFilePath ?? "") ?? ".";
            return Path.Combine(dir, fileName);
        }
    }
}
