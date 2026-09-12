using System;
using System.Collections.Generic;
using UnityEngine;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;

namespace TortalPortalLite.Subsystems.Topology
{
    /// <summary>
    /// #179 Invisible Anchor ZDO - a Persistent, non-portal destination ZDO (prefab hash 0). Unlike the
    /// phantom portal (#178), TeleportWorld.Teleport never checks the target's prefab (SERVER decompile
    /// :143539-143548: bare `ZDOMan.instance.GetZDO(connectionId)` then GetPosition()/GetRotation()), so
    /// any ZDO the client can resolve is a valid one-way destination - cheaper than a phantom and, unlike
    /// a portal prefab, safely MOVABLE in place (ZDO.SetSector's portal early-return, SERVER decompile
    /// :73752-73768, only applies to prefabs in Game.PortalPrefabHash). This is the primitive #196
    /// "Moving Target" and every option that tracks something in motion (#190 Ship, #191 Nearest Player,
    /// #194 Builder Trace live variant) is built on.
    ///
    /// Creation uses prefab hash 0 deliberately (not a custom mod-private hash): ZNetScene.CreateObject
    /// returns null SILENTLY for prefab 0 but LogWarning-spams "Missing prefab hash" up to 30 Hz for an
    /// unrecognised nonzero hash while the ZDO is in a client's near set (SERVER decompile citations in
    /// #179's own howItWorks, client-side ZNetScene.CreateObject). A vanilla client's CreateObjectsSorted
    /// only destroys "invalid prefab" ZDOs when `ZNet.instance.IsServer()`, which a client never is, so
    /// the anchor survives indefinitely unclaimed on every client that has it in range.
    ///
    /// Acknowledged edge case (not worked around, per this option's own verified spec): ZDOPool recycles
    /// ZDO instances and ZDO.Reset() (SERVER decompile :73536-73549) does not clear m_prefab, so the
    /// private ZDOMan.CreateNewZDO(ZDOID,Vector3,int) overload's `prefabHash = (prefabHashIn != 0) ?
    /// prefabHashIn : zDO.GetPrefab()` (:76684) could theoretically read a stale nonzero prefab hash left
    /// over on a reused pool slot and mis-file a brand new anchor into m_portalObjects for one tick. This
    /// mod does not special-case it (the catalog's own verified recipe does not flag it, and the resulting
    /// window - momentarily eligible for vanilla's random same-tag pairing before this factory's own
    /// reciprocal write lands - is the same class of race #178's FindRandomUnconnectedPortalHook handler
    /// already guards against for phantoms).
    /// </summary>
    public static class TargetedAnchorFactory
    {
        private static readonly HashSet<ZDOID> _knownAnchorIds = new HashSet<ZDOID>();
        private static bool _bootScanDone;

        public static void OnWorldReady()
        {
            BootstrapScan();
        }

        /// <summary>One-time full scan for anchors that already existed before this session (server restart) - see class remarks on why this isn't repeated every tick.</summary>
        private static void BootstrapScan()
        {
            if (_bootScanDone)
            {
                return;
            }
            _bootScanDone = true;
            try
            {
                int hash = TargetedZdoKeys.Anchor.GetStableHashCode();
                List<ZDOID> ids = ZDOExtraData.GetAllZDOIDsWithHash(ZDOExtraData.Type.Int, hash);
                foreach (ZDOID id in ids)
                {
                    ZDO zdo = ZDOMan.instance?.GetZDO(id);
                    if (zdo != null && zdo.IsValid() && zdo.GetInt(TargetedZdoKeys.Anchor) == 1)
                    {
                        _knownAnchorIds.Add(id);
                    }
                }
                PortalDebug.LogInfo($"[TargetedAnchorFactory] boot scan found {_knownAnchorIds.Count} pre-existing anchor(s).");
            }
            catch (Exception ex)
            {
                PortalDebug.LogWarning($"[TargetedAnchorFactory] boot scan failed: {ex.Message}");
            }
        }

        public static void OnUpdate(float dt)
        {
            // Piggybacks the same cadence as the phantom factory's own maintenance tick; a second timer
            // would just double the config surface for no benefit, so this is driven directly from
            // TargetedSubsystem at the same point in the tick order (see TargetedSubsystem.OnUpdate).
            MaintenanceTick();
        }

        // ---------------------------------------------------------------- public API for other engines

        public static bool IsAnchor(ZDO zdo) => zdo != null && zdo.IsValid() && zdo.GetInt(TargetedZdoKeys.Anchor) == 1;

        public static string GetKind(ZDO anchor) => anchor != null && anchor.IsValid() ? anchor.GetString(TargetedZdoKeys.Kind, "") : "";

        public static ZDO ResolveExistingAnchor(ZDO source)
        {
            if (source == null || !source.IsValid() || ZDOMan.instance == null)
            {
                return null;
            }
            ZDOID targetId = source.GetConnectionZDOID(ZDOExtraData.ConnectionType.Portal);
            if (targetId == ZDOID.None)
            {
                return null;
            }
            ZDO target = ZDOMan.instance.GetZDO(targetId);
            return (target != null && target.IsValid() && target.GetInt(TargetedZdoKeys.Anchor) == 1) ? target : null;
        }

        /// <summary>Creates (or moves, if one already exists) the anchor paired to <paramref name="source"/>.</summary>
        public static ZDO CreateOrRetarget(ZDO source, Vector3 pos, Quaternion rot, string tag, string kind)
        {
            if (source == null || !source.IsValid() || ZDOMan.instance == null)
            {
                return null;
            }

            ZDO existing = ResolveExistingAnchor(source);
            if (existing != null)
            {
                if (GetKind(existing) == kind)
                {
                    MoveTo(existing, pos, rot);
                    if (existing.GetString(ZDOVars.s_tag, "") != tag)
                    {
                        PortalOwnership.ClaimAndWrite(existing, z => z.Set(ZDOVars.s_tag, tag));
                        PortalOwnership.ClaimAndWrite(source, z => z.Set(ZDOVars.s_tag, tag));
                    }
                    return existing;
                }
                DestroyAnchor(existing);
            }

            ZDO anchor = CreateNewAnchor(pos, rot, tag, kind);
            if (anchor == null)
            {
                return null;
            }

            PortalOwnership.ClaimAndWrite(source, z =>
            {
                z.Set(ZDOVars.s_tag, tag);
                z.SetConnection(ZDOExtraData.ConnectionType.Portal, anchor.m_uid);
            });
            PortalOwnership.ClaimAndWrite(anchor, z => z.SetConnection(ZDOExtraData.ConnectionType.Portal, source.m_uid));
            return anchor;
        }

        /// <summary>
        /// Repositions an existing anchor in place - safe only because it is NOT a portal prefab
        /// (ZDO.SetSector's early-return for portal prefabs, :73752-73768, does not apply). Re-claims
        /// ownership every call: ZDOMan.ReleaseNearbyZDOS hands ownership to any nearby peer every ~2 s
        /// (#178/#196's own citation), so a stale claim cannot be assumed to still hold. No-ops (returns
        /// false) when the move is smaller than 1 cm, so a caller can tick this every frame cheaply.
        /// </summary>
        public static bool MoveTo(ZDO anchor, Vector3 pos, Quaternion rot)
        {
            if (anchor == null || !anchor.IsValid())
            {
                return false;
            }
            if ((anchor.GetPosition() - pos).sqrMagnitude < 0.0001f)
            {
                return false; // idempotent - avoid a pointless DataRevision bump every tick when nothing moved
            }
            try
            {
                anchor.SetOwner(ZDOMan.GetSessionID());
                anchor.SetPosition(pos);
                anchor.SetRotation(rot);
                return true;
            }
            catch (Exception ex)
            {
                PortalDebug.LogWarning($"[TargetedAnchorFactory] MoveTo failed for {anchor.m_uid}: {ex.Message}");
                return false;
            }
        }

        public static void ReleaseSource(ZDO source)
        {
            ZDO existing = ResolveExistingAnchor(source);
            if (existing != null)
            {
                DestroyAnchor(existing);
            }
        }

        // ---------------------------------------------------------------------------- creation/destroy

        private static ZDO CreateNewAnchor(Vector3 pos, Quaternion rot, string tag, string kind)
        {
            ZDO zdo;
            try
            {
                zdo = ZDOMan.instance.CreateNewZDO(pos, 0);
            }
            catch (Exception ex)
            {
                PortalDebug.LogError($"[TargetedAnchorFactory] CreateNewZDO failed: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
            if (zdo == null)
            {
                return null;
            }

            PortalOwnership.ClaimAndWrite(zdo, z =>
            {
                z.Persistent = true;
                z.SetPrefab(0);
                z.SetRotation(rot);
                z.Set(ZDOVars.s_tag, tag);
                z.Set(ZDOVars.s_tagauthor, "server");
                z.Set(TargetedZdoKeys.Anchor, 1);
                z.Set(TargetedZdoKeys.Kind, kind ?? "");
            });

            _knownAnchorIds.Add(zdo.m_uid);
            return zdo;
        }

        private static void DestroyAnchor(ZDO zdo)
        {
            if (zdo == null || !zdo.IsValid() || ZDOMan.instance == null)
            {
                return;
            }
            try
            {
                zdo.SetOwner(ZDOMan.GetSessionID());
                ZDOMan.instance.DestroyZDO(zdo);
            }
            catch (Exception ex)
            {
                PortalDebug.LogWarning($"[TargetedAnchorFactory] destroy failed for {zdo.m_uid}: {ex.Message}");
            }
            finally
            {
                _knownAnchorIds.Remove(zdo.m_uid);
            }
        }

        private static void MaintenanceTick()
        {
            if (ZDOMan.instance == null || _knownAnchorIds.Count == 0)
            {
                return;
            }

            List<ZDOID> toForget = null;
            // Copy - the loop body can mutate _knownAnchorIds via DestroyAnchor.
            var snapshot = new List<ZDOID>(_knownAnchorIds);
            foreach (ZDOID id in snapshot)
            {
                ZDO zdo = ZDOMan.instance.GetZDO(id);
                if (zdo == null || !zdo.IsValid())
                {
                    (toForget ??= new List<ZDOID>()).Add(id);
                    continue;
                }

                ZDOID sourceId = zdo.GetConnectionZDOID(ZDOExtraData.ConnectionType.Portal);
                ZDO source = sourceId != ZDOID.None ? ZDOMan.instance.GetZDO(sourceId) : null;
                if (source == null || !source.IsValid())
                {
                    DestroyAnchor(zdo);
                    continue;
                }

                string myTag = zdo.GetString(ZDOVars.s_tag, "");
                string sourceTag = source.GetString(ZDOVars.s_tag, "");
                if (myTag != sourceTag)
                {
                    // Same "accept the player's retag choice" rule as the phantom factory (#178).
                    DestroyAnchor(zdo);
                    continue;
                }

                if (source.GetConnectionZDOID(ZDOExtraData.ConnectionType.Portal) != zdo.m_uid)
                {
                    PortalOwnership.ClaimAndWrite(source, z => z.SetConnection(ZDOExtraData.ConnectionType.Portal, zdo.m_uid));
                }
                if (zdo.GetConnectionZDOID(ZDOExtraData.ConnectionType.Portal) != source.m_uid)
                {
                    PortalOwnership.ClaimAndWrite(zdo, z => z.SetConnection(ZDOExtraData.ConnectionType.Portal, source.m_uid));
                }
            }

            if (toForget != null)
            {
                foreach (ZDOID id in toForget)
                {
                    _knownAnchorIds.Remove(id);
                }
            }
        }
    }
}
