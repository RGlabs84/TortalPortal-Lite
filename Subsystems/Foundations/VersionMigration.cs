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
    /// alone should downgrade to read-only before anything else notices. The version half runs at
    /// plugin Awake; the registry half is deferred to the first tick Game.instance exists, since the
    /// game's own Game object has not been created yet when BepInEx runs a plugin's Awake.
    /// </summary>
    public static class VersionMigration
    {
        public const int CurrentSchemaVersion = 1;

        /// <summary>
        /// Builds this mod has actually been checked against - grow this list as it's verified against newer
        /// patches, never assume forward compatibility silently. 1.0.15 (2026-09-20): per-class diff of the
        /// ilspycmd decompiles of the 1.0.12 and 1.0.15 server assemblies - Game, ZDOMan, ZDO, ZDOExtraData,
        /// ZDOVars, ZRoutedRpc, ZRpc, ZNetScene, ZNetView, TeleportWorld, ZDOID and ZPackage are byte-identical,
        /// and the classes that did change (Terminal, ZNet, TerrainComp, Inventory, Piece, Player, Character)
        /// changed outside every member this mod patches or calls. 1.0.16 (2026-09-25): asmdiff of the 1.0.15
        /// and 1.0.16 server assemblies - Game, ZDOMan, ZDO, ZDOExtraData, ZDOVars, ZRoutedRpc, ZRpc, ZNetScene,
        /// ZNetView, TeleportWorld, ZDOID, ZPackage, ZNet and FejdStartup (every type this mod patches or calls)
        /// are unchanged; the 19 changed types changed outside every member this mod patches or calls -
        /// including TerrainComp, whose changes (Awake, ApplyOperation, PaintCleared, .cctor) miss
        /// Load/Save/ApplyToHeightmap, the only TerrainComp members TargetedGroundProbe's wire-format reader
        /// depends on.
        /// </summary>
        private static readonly HashSet<string> VerifiedBuilds = new HashSet<string> { "1.0.7", "1.0.12", "1.0.15", "1.0.16" };

        public static bool IsVerifiedBuild { get; private set; }
        public static bool PortalPrefabHashSane { get; private set; }
        public static string RunningVersion { get; private set; } = "(unknown)";

        private static bool _prefabCheckDone;

        /// <summary>True if a destructive pass (reassert/repair) is allowed to run. PortalPrefabHashSane is a hard gate - AcceptUnverifiedBuild only overrides the build-version check, never a genuinely broken portal registry.</summary>
        public static bool DestructivePassesAllowed => PortalPrefabHashSane && (IsVerifiedBuild || GlobalConfig.AcceptUnverifiedBuild?.Value == true);

        public static void RunBootChecks()
        {
            string bareVersion = "(unknown)";
            try
            {
                // GetVersionString() carries a platform prefix on some builds - "l-1.0.12" on Steam
                // Linux, "dw-"/"dl-" on Deck, "ms-" on Microsoft Store (Version.GetPlatformPrefix,
                // :112158) - so it is logged for humans but never compared. CurrentVersion.ToString()
                // is the bare major.minor.patch the verified list is keyed by.
                RunningVersion = Version.GetVersionString();
                bareVersion = Version.CurrentVersion.ToString();
            }
            catch (Exception ex)
            {
                PortalDebug.LogWarning($"[VersionMigration] could not read the game version: {ex.Message}");
            }

            IsVerifiedBuild = VerifiedBuilds.Contains(bareVersion);
            if (!IsVerifiedBuild)
            {
                PortalDebug.LogWarning($"[VersionMigration] running Valheim '{RunningVersion}' (build {bareVersion}), which is outside this mod's verified build list ({string.Join(", ", VerifiedBuilds)}). Census/health/export still run; reassert/repair will refuse unless AcceptUnverifiedBuild=true.");
            }
            // The portal-prefab registry check lives in OnUpdate: Game.instance does not exist yet
            // during a BepInEx plugin's own Awake (Game.Awake, :100026, runs when the scene loads).
        }

        /// <summary>
        /// Game.Awake (:100026-100032) sets Game.instance and fills PortalPrefabHash from m_portalPrefabs
        /// in the same synchronous pass, so "instance exists" is exactly "the registry is as populated as
        /// it will ever be" - evaluated once on the first tick that's true, never re-polled after.
        /// </summary>
        public static void OnUpdate()
        {
            if (_prefabCheckDone || Game.instance == null)
            {
                return;
            }
            _prefabCheckDone = true;

            int count = Game.instance.PortalPrefabHash?.Count ?? 0;
            PortalPrefabHashSane = count > 0;
            if (PortalPrefabHashSane)
            {
                PortalDebug.LogAlways($"[VersionMigration] portal-prefab registry verified ({count} prefab hash(es)). Destructive passes {(DestructivePassesAllowed ? "allowed" : "still gated by the unverified-build check")}.");
            }
            else
            {
                PortalDebug.LogError("[VersionMigration] Game.instance.PortalPrefabHash is empty - the game's own portal-prefab registry looks wrong on this build. Destructive passes refused regardless of AcceptUnverifiedBuild.");
            }
        }
    }
}
