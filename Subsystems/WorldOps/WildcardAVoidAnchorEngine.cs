using System;
using System.Collections.Generic;
using UnityEngine;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;

namespace TortalPortalLite.Subsystems.WorldOps
{
    /// <summary>
    /// #128 The Void Anchor. TeleportWorld.Teleport (SERVER decompile :143539-143546) resolves its
    /// destination with a bare ZDOMan.instance.GetZDO(connectionZDOID) and reads only GetPosition()/
    /// GetRotation() - it never checks the target's prefab, tag, type, persistence or ownership. A ZDO
    /// with prefab hash exactly 0 is therefore a legal, invisible, never-logged teleport destination:
    /// ZNetScene.CreateObject opens with `if (prefab == 0) return null;` BEFORE the missing-prefab
    /// warning (:81999-82002), and ZNetScene.IsPrefabZDOValid returns false for hash 0 so IsAreaReady
    /// (:82064-82082) never blocks an arriving player waiting for it.
    ///
    /// Two failure modes this engine exists specifically to defeat (both from #128's own howItWorks):
    ///  1. Game.ConnectPortals phase 1 (:100601) nulls the portal's connection within 5s unless the
    ///     anchor ZDO ALSO carries the identical s_tag string AND a non-None Portal connection of its
    ///     own - a self-loop (anchor.SetConnection(Portal, anchor.m_uid)) satisfies the second clause.
    ///  2. That self-loop is erased at world save by ZDOExtraData.RegenerateConnectionHashData
    ///     (:75472-75475), which overwrites the source slot with the Target slot - so the link must be
    ///     re-asserted after every world load/save, not written once and forgotten.
    ///
    /// Anchors are tracked by a marker key (never by ZDOID - #128's own citation: ZDOIDs are renumbered
    /// on every world load) and rediscovered at boot via ZDOExtraData.GetAllZDOIDsWithHash, the same
    /// pattern TargetedAnchorFactory already uses for its own anchors.
    /// </summary>
    public static class WildcardAVoidAnchorEngine
    {
        private static readonly HashSet<ZDOID> _knownAnchors = new HashSet<ZDOID>();
        private static float _timer;
        private static bool _bootScanDone;

        public static void OnWorldReady()
        {
            BootScan();
        }

        private static void BootScan()
        {
            if (_bootScanDone || ZDOMan.instance == null)
            {
                return;
            }
            _bootScanDone = true;
            try
            {
                List<ZDOID> ids = ZDOExtraData.GetAllZDOIDsWithHash(ZDOExtraData.Type.Int, WildcardAZdoKeys.VoidAnchorMarker);
                foreach (ZDOID id in ids)
                {
                    ZDO zdo = ZDOMan.instance.GetZDO(id);
                    if (zdo != null && zdo.IsValid() && zdo.GetInt(WildcardAZdoKeys.VoidAnchorMarker) == 1)
                    {
                        _knownAnchors.Add(id);
                    }
                }
                PortalDebug.LogInfo($"[WildcardAVoidAnchorEngine] boot scan found {_knownAnchors.Count} pre-existing void anchor(s).");
            }
            catch (Exception ex)
            {
                PortalDebug.LogWarning($"[WildcardAVoidAnchorEngine] boot scan failed: {ex.Message}");
            }
        }

        public static void OnUpdate(float dt)
        {
            if (WildcardAConfig.Enabled?.Value == false)
            {
                return;
            }
            _timer += dt;
            float interval = WildcardAConfig.VoidAnchorReassertSeconds?.Value ?? 2.0f;
            if (_timer < interval)
            {
                return;
            }
            _timer = 0f;
            ReassertSelfLoops();
        }

        public static bool IsVoidAnchor(ZDO zdo) => zdo != null && zdo.IsValid() && zdo.GetInt(WildcardAZdoKeys.VoidAnchorMarker) == 1;

        /// <summary>
        /// Creates a new invisible, persistent, self-looped anchor at <paramref name="pos"/>/<paramref name="rot"/>
        /// carrying <paramref name="tag"/> (the same literal tag the linked portal will carry - Game.ConnectPortals'
        /// own reconcile predicate requires it). Returns null on failure (ZDOMan not ready, etc.).
        /// </summary>
        public static ZDO? CreateAnchor(Vector3 pos, Quaternion rot, string tag)
        {
            ZDO? zdo = WildcardAWriteOps.FabricateVoidZdo(pos, rot);
            if (zdo == null)
            {
                return null;
            }
            PortalOwnership.ClaimAndWrite(zdo, z =>
            {
                z.Set(WildcardAZdoKeys.VoidAnchorMarker, 1);
                z.Set(ZDOVars.s_tag, tag ?? "");
                z.Set(ZDOVars.s_tagauthor, "server");
                // Self-loop: the anchor is its own Portal connection partner, satisfying
                // Game.ConnectPortals phase 1's "partner connection is non-None" clause (#128 failure
                // mode 2) without needing a second real ZDO on the other end.
                z.SetConnection(ZDOExtraData.ConnectionType.Portal, z.m_uid);
            });
            _knownAnchors.Add(zdo.m_uid);
            return zdo;
        }

        /// <summary>
        /// Links an existing (standalone, unmanaged) portal to <paramref name="anchor"/>: same tag on
        /// both ends, portal's connection set to the anchor. Refuses (logs, no-op) if the portal is
        /// already NetworkReassertEngine-managed - task rule #5.
        /// </summary>
        public static bool LinkPortalToAnchor(ZDO portal, ZDO anchor, string tag)
        {
            if (portal == null || !portal.IsValid() || anchor == null || !anchor.IsValid())
            {
                return false;
            }
            if (WildcardAWriteOps.IsNetworkManaged(portal))
            {
                PortalDebug.LogWarning($"[WildcardAVoidAnchorEngine] refusing to link {portal.m_uid} to a void anchor - it is already NetworkReassertEngine-managed.");
                return false;
            }
            WildcardAWriteOps.ReassertTagAndConnection(portal, tag, anchor.m_uid);
            if (anchor.GetString(ZDOVars.s_tag, "") != tag)
            {
                PortalOwnership.ClaimAndWrite(anchor, z => z.Set(ZDOVars.s_tag, tag ?? ""));
            }
            return true;
        }

        /// <summary>
        /// #128's own citation: first walk-through after linking silently no-ops while the client's
        /// TeleportWorld.TargetFound issues ZDOMan.RequestZDO for an anchor it does not yet hold.
        /// Pre-warming with ForceSendZDO to a peer on join/near-arrival removes that warm-up cost.
        /// </summary>
        public static void PreWarm(long peerUid, ZDOID anchorId)
        {
            ZDOMan.instance?.ForceSendZDO(peerUid, anchorId);
        }

        private static void ReassertSelfLoops()
        {
            if (ZDOMan.instance == null || _knownAnchors.Count == 0)
            {
                return;
            }
            List<ZDOID> stale = null;
            // Copy - ClaimAndWrite never destroys, but keep the same defensive shape as sibling engines.
            var snapshot = new List<ZDOID>(_knownAnchors);
            foreach (ZDOID id in snapshot)
            {
                ZDO zdo = ZDOMan.instance.GetZDO(id);
                if (zdo == null || !zdo.IsValid())
                {
                    (stale ??= new List<ZDOID>()).Add(id);
                    continue;
                }
                if (zdo.GetConnectionZDOID(ZDOExtraData.ConnectionType.Portal) != zdo.m_uid)
                {
                    // RegenerateConnectionHashData wiped the self-loop at the last save/load - #128 failure mode 3.
                    PortalOwnership.ClaimAndWrite(zdo, z => z.SetConnection(ZDOExtraData.ConnectionType.Portal, z.m_uid));
                }
            }
            if (stale != null)
            {
                foreach (ZDOID id in stale)
                {
                    _knownAnchors.Remove(id);
                }
            }
        }
    }
}
