using System;
using System.Collections.Generic;
using UnityEngine;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;
using TortalPortalLite.Subsystems.Foundations;

namespace TortalPortalLite.Subsystems.WorldOps
{
    /// <summary>
    /// #242 Non-Portals As Portals - The Mandatory Migration Pass. Builds directly on the already-shipped
    /// Foundations/PrefabExtension.cs (#138, `Game.instance.PortalPrefabHash.Add(hash)`), which is
    /// deliberately conservative and only ADDS - it does NOT retroactively re-file already-instantiated
    /// ZDOs of the newly-enrolled prefab, and does not implement removal at all ("the catalog names
    /// removal as the dangerous half"). This engine supplies the two halves #242 says are MANDATORY
    /// around that add: a migration pass (or the enrolment silently orphans every pre-existing instance
    /// at the next save) and an uninstall reversal pass (or enrolled objects leak into m_portalObjects
    /// forever once this mod is removed).
    ///
    /// Save partitioning is a two-set split with no overlap (#242's own citation):
    /// AddObjectsPerChunk clones only `Persistent &amp;&amp; !PortalPrefabHash.Contains(prefab)` ZDOs
    /// (:77566-77576); the ChunkPortal file clones only `m_portalObjects` (:77619-77627). An instance
    /// that predates the hash being added is excluded from BOTH the moment the hash lands - hence the
    /// migration pass must run BEFORE the next save, moving every pre-existing instance from
    /// m_objectsBySector into m_portalObjects explicitly (RemoveFromSector + AddIfPortal, both public via
    /// this project's Publicize=true).
    ///
    /// Immobility restriction (#242's own citation): ZDO.SetSector early-returns for any enrolled hash
    /// (:73752-73755), so an enrolled MOBILE prefab (one with a ZSyncTransform) stops re-sectoring
    /// forever once moved and silently stops being delivered to clients at its new position. This engine
    /// refuses to enroll a prefab that carries a ZSyncTransform unless
    /// WildcardAConfig.MigrationAllowMobilePrefabs is explicitly set.
    /// </summary>
    public static class WildcardANonPortalMigrationEngine
    {
        private static readonly HashSet<int> _reversed = new HashSet<int>();

        /// <summary>
        /// Enrolls <paramref name="prefabName"/> into Game.PortalPrefabHash (via PrefabExtension) and
        /// immediately migrates every pre-existing instance so none are orphaned at the next save.
        /// Returns a human-readable result string suitable for an admin command response.
        /// </summary>
        public static string EnrollWithMigration(string prefabName)
        {
            if (Game.instance == null || ZNetScene.instance == null || ZDOMan.instance == null)
            {
                return "tpl-worldops: not ready yet (Game/ZNetScene/ZDOMan not initialised).";
            }
            GameObject prefab = ZNetScene.instance.GetPrefab(prefabName);
            if (prefab == null)
            {
                return $"tpl-worldops: no prefab named '{prefabName}'.";
            }
            int hash = prefabName.GetStableHashCode();

            if (prefab.GetComponent<ZSyncTransform>() != null && WildcardAConfig.MigrationAllowMobilePrefabs?.Value != true)
            {
                return $"tpl-worldops: refusing to enroll '{prefabName}' - it carries a ZSyncTransform (mobile). " +
                       "ZDO.SetSector's portal early-return means it would stop re-sectoring once moved (#242's own citation). " +
                       "Set MigrationAllowMobilePrefabs=true to override.";
            }

            bool alreadyExtended = PrefabExtension.IsExtended(hash);
            if (!PrefabExtension.TryExtend(hash))
            {
                return $"tpl-worldops: PrefabExtension.TryExtend failed for '{prefabName}' - Game.instance unavailable?";
            }
            _reversed.Remove(hash); // a re-enroll after a prior reversal is a fresh enrollment again.

            int migrated = alreadyExtended ? 0 : MigratePreExisting(prefabName, hash);
            ZDOMan.instance.SetDirtyPortals();
            return $"tpl-worldops: '{prefabName}' (hash {hash}) enrolled into Game.PortalPrefabHash; migrated {migrated} pre-existing instance(s) into the portal registry.";
        }

        /// <summary>
        /// Walks EVERY existing ZDO of <paramref name="prefabName"/> to completion
        /// (ZDOMan.GetAllZDOsWithPrefabIterative signals "done" via its own return value; a partial walk
        /// migrates nothing per #242's own failure-mode warning) and re-files each one: out of the
        /// ordinary sector list, into m_portalObjects via the (Publicizer-exposed) private AddIfPortal.
        /// </summary>
        private static int MigratePreExisting(string prefabName, int hash)
        {
            int migrated = 0;
            try
            {
                var found = new List<ZDO>();
                int cursor = 0;
                bool done;
                do
                {
                    found.Clear();
                    done = ZDOMan.instance.GetAllZDOsWithPrefabIterative(prefabName, found, ref cursor);
                    foreach (ZDO zdo in found)
                    {
                        if (zdo == null || !zdo.IsValid())
                        {
                            continue;
                        }
                        try
                        {
                            ZoneSystem.SectorIndex sector = zdo.GetSectorIndex();
                            ZDOMan.instance.RemoveFromSector(zdo, sector);
                            ZDOMan.instance.AddIfPortal(zdo, hash);
                            PortalOwnership.ClaimAndWrite(zdo, z => z.Set(WildcardAZdoKeys.MigratedByThisMod, 1));
                            migrated++;
                        }
                        catch (Exception ex)
                        {
                            PortalDebug.LogWarning($"[WildcardANonPortalMigrationEngine] failed to migrate {zdo.m_uid}: {ex.Message}");
                        }
                    }
                } while (!done);
            }
            catch (Exception ex)
            {
                PortalDebug.LogError($"[WildcardANonPortalMigrationEngine] migration walk for '{prefabName}' failed: {ex.GetType().Name}: {ex.Message}");
            }
            return migrated;
        }

        /// <summary>
        /// Uninstall reversal pass (#242's own MANDATORY second half). Must be run WHILE this mod is
        /// still installed, before removal - a later boot without the mod would otherwise have
        /// ZDOMan.LoadChunks insert every ChunkPortal ZDO straight into m_portalObjects regardless of
        /// prefab (:76474-76482), get paired by tag by Game.ConnectPortals (empty tag = the global
        /// untagged group), and leak in m_portalObjects on destruction (HandleDestroyedZDO only removes
        /// from m_portalObjects when the prefab IS in PortalPrefabHash, :76976-76986).
        ///
        /// Known limitation, documented rather than worked around: Foundations/PrefabExtension.cs's own
        /// `_extendedByThisMod` bookkeeping is a private set this engine cannot clear (that file is
        /// explicitly out of scope this wave and is Add-only by design). After this reversal,
        /// `PrefabExtension.IsExtended(hash)` may still report true even though the prefab has actually
        /// been removed from `Game.instance.PortalPrefabHash` by this method - a stale INFORMATIONAL flag
        /// only, not a correctness issue: the actual game list and every ZDO's actual sector/connection
        /// state are fully reverted below, which is what save/pairing/destruction behaviour depends on.
        /// </summary>
        public static string ReverseEnrollment(string prefabName)
        {
            if (Game.instance == null || ZNetScene.instance == null || ZDOMan.instance == null)
            {
                return "tpl-worldops: not ready yet (Game/ZNetScene/ZDOMan not initialised).";
            }
            int hash = prefabName.GetStableHashCode();
            if (!Game.instance.PortalPrefabHash.Contains(hash))
            {
                return $"tpl-worldops: '{prefabName}' is not currently enrolled.";
            }

            int reverted = 0;
            try
            {
                List<ZDO> portals = ZDOMan.instance.GetPortalList();
                foreach (ZDO zdo in portals)
                {
                    if (zdo == null || !zdo.IsValid() || zdo.GetPrefab() != hash)
                    {
                        continue;
                    }
                    try
                    {
                        ZoneSystem.SectorIndex sector = zdo.GetSectorIndex();
                        ZDOMan.instance.m_portalObjects.TryGetValue(sector, out List<ZDO> bucket);
                        bucket?.Remove(zdo);
                        ZDOMan.instance.AddToSector(zdo, sector);
                        PortalOwnership.ClaimAndWrite(zdo, z =>
                        {
                            z.SetConnection(ZDOExtraData.ConnectionType.Portal, ZDOID.None);
                            z.RemoveInt(WildcardAZdoKeys.MigratedByThisMod);
                        });
                        reverted++;
                    }
                    catch (Exception ex)
                    {
                        PortalDebug.LogWarning($"[WildcardANonPortalMigrationEngine] failed to revert {zdo.m_uid}: {ex.Message}");
                    }
                }

                Game.instance.PortalPrefabHash.Remove(hash);
                _reversed.Add(hash);
                ZDOMan.instance.SetDirtyPortals();
            }
            catch (Exception ex)
            {
                PortalDebug.LogError($"[WildcardANonPortalMigrationEngine] reversal for '{prefabName}' failed: {ex.GetType().Name}: {ex.Message}");
                return $"tpl-worldops: reversal for '{prefabName}' failed: {ex.Message}";
            }
            return $"tpl-worldops: '{prefabName}' (hash {hash}) reverted - {reverted} instance(s) moved back to the ordinary chunk registry and un-enrolled from Game.PortalPrefabHash.";
        }

        public static bool WasReversed(int prefabHash) => _reversed.Contains(prefabHash);
    }
}
