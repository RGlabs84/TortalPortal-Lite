using System;
using System.Collections.Generic;
using TortalPortalLite.Core;

namespace TortalPortalLite.Subsystems.Foundations
{
    /// <summary>
    /// #81 Version migration. Three independent versions logged at boot and recorded in every
    /// persisted file: this mod's own schema version (PortalKeys.SchemaVersion, bumped when a
    /// persisted format changes), the running Valheim build (Version.GetVersionString(), SERVER
    /// decompile :112112), and whether that build is one this mod has actually verified against.
    /// Structural self-check: Game.instance.PortalPrefabHash non-empty (:100021) - if this mod ever
    /// runs against a build where the Inspector-configured portal-prefab list is missing/empty, that
    /// alone should downgrade to read-only before anything else notices.
    /// </summary>
    public static class VersionMigration
    {
        public const int CurrentSchemaVersion = 1;

        /// <summary>Builds this mod has actually been checked against - grow this list as it's verified against newer patches, never assume forward compatibility silently.</summary>
        private static readonly HashSet<string> VerifiedBuilds = new HashSet<string> { "1.0.7", "1.0.12" };

        public static bool IsVerifiedBuild { get; private set; }
        public static bool PortalPrefabHashSane { get; private set; }
        public static string RunningVersion { get; private set; } = "(unknown)";

        /// <summary>True if a destructive pass (reassert/repair) is allowed to run. PortalPrefabHashSane is a hard gate - AcceptUnverifiedBuild only overrides the build-version check, never a genuinely broken portal registry.</summary>
        public static bool DestructivePassesAllowed => PortalPrefabHashSane && (IsVerifiedBuild || GlobalConfig.AcceptUnverifiedBuild?.Value == true);

        public static void RunBootChecks()
        {
            try
            {
                RunningVersion = Version.GetVersionString();
            }
            catch (Exception ex)
            {
                PortalDebug.LogWarning($"[VersionMigration] could not read Version.GetVersionString(): {ex.Message}");
            }

            IsVerifiedBuild = VerifiedBuilds.Contains(RunningVersion);
            PortalPrefabHashSane = Game.instance != null && Game.instance.PortalPrefabHash != null && Game.instance.PortalPrefabHash.Count > 0;

            if (!IsVerifiedBuild)
            {
                PortalDebug.LogWarning($"[VersionMigration] running Valheim '{RunningVersion}', which is outside this mod's verified build list ({string.Join(", ", VerifiedBuilds)}). Census/health/export still run; reassert/repair will refuse unless AcceptUnverifiedBuild=true.");
            }
            if (!PortalPrefabHashSane)
            {
                PortalDebug.LogError("[VersionMigration] Game.instance.PortalPrefabHash is null or empty - the game's own portal-prefab registry looks wrong on this build. Destructive passes refused regardless of AcceptUnverifiedBuild.");
            }
        }
    }
}
