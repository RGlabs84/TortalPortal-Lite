using System;
using System.Collections.Generic;
using UnityEngine;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;

namespace TortalPortalLite.Subsystems.WorldOps
{
    /// <summary>
    /// #132 The Decoy Gate. TeleportWorld.HaveTarget (:143631-143638) is a pure field test -
    /// `GetConnectionZDOID(Portal) != ZDOID.None` - so a portal whose connection points at a ZDOID that
    /// simply does not resolve reads as "connected" (hover text, blue glow) forever, while
    /// TeleportWorld.TargetFound (:143640-143656), which ADDITIONALLY requires
    /// `ZDOMan.instance.GetZDO(connectionZDOID) != null`, is permanently false - Teleport() gate 1
    /// (:143519-143522) is a bare `return` with NO player.Message. Walking through does nothing, with
    /// zero feedback - the best puzzle "sealed gate" primitive available: no error to search for.
    ///
    /// The fake target is minted from a REAL ZDOMan.CreateNewZDO(...) that is then immediately
    /// destroyed while server-owned, so its id lands in ZDOMan's own m_deadZDOs set (SERVER decompile
    /// :76997) and can never collide with a future real allocation - #132's own citation, and the exact
    /// recipe RoutingPhantomAnchorEngine.Destroy already proves safe in this codebase.
    ///
    /// Game.ConnectPortals phase 1 (:100600-100604) tears this down within 5s because the resolved
    /// zDO is null, so the fake connection is re-asserted on a fast tick here - same idempotent-write
    /// discipline as NetworkReassertEngine (a no-op when the value is already correct).
    /// </summary>
    public static class WildcardADecoyGateEngine
    {
        private static readonly Dictionary<ZDOID, ZDOID> _decoys = new Dictionary<ZDOID, ZDOID>(); // portal uid -> fake target uid
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
                List<ZDOID> ids = ZDOExtraData.GetAllZDOIDsWithHash(ZDOExtraData.Type.Int, WildcardAZdoKeys.DecoyMarker);
                foreach (ZDOID id in ids)
                {
                    ZDO zdo = ZDOMan.instance.GetZDO(id);
                    if (zdo == null || !zdo.IsValid() || zdo.GetInt(WildcardAZdoKeys.DecoyMarker) != 1)
                    {
                        continue;
                    }
                    ZDOID fakeTarget = zdo.GetZDOID(WildcardAZdoKeys.DecoyFakeTarget);
                    if (fakeTarget != ZDOID.None)
                    {
                        _decoys[id] = fakeTarget;
                    }
                }
                PortalDebug.LogInfo($"[WildcardADecoyGateEngine] boot scan found {_decoys.Count} pre-existing decoy gate(s).");
            }
            catch (Exception ex)
            {
                PortalDebug.LogWarning($"[WildcardADecoyGateEngine] boot scan failed: {ex.Message}");
            }
        }

        public static void OnUpdate(float dt)
        {
            if (WildcardAConfig.Enabled?.Value == false)
            {
                return;
            }
            _timer += dt;
            float interval = WildcardAConfig.DecoyReassertSeconds?.Value ?? 2.0f;
            if (_timer < interval)
            {
                return;
            }
            _timer = 0f;
            Reassert();
        }

        public static bool IsDecoy(ZDO zdo) => zdo != null && zdo.IsValid() && zdo.GetInt(WildcardAZdoKeys.DecoyMarker) == 1;

        /// <summary>
        /// Mints an unresolvable ZDOID and points <paramref name="portal"/> at it. Refuses (logs, no-op)
        /// on an already NetworkReassertEngine-managed portal - task rule #5. Idempotent: re-calling on
        /// an already-decoyed portal just re-mints a fresh dead id (harmless, but Release() is the
        /// normal way to undo this).
        /// </summary>
        public static bool MakeDecoy(ZDO portal)
        {
            if (portal == null || !portal.IsValid() || ZDOMan.instance == null)
            {
                return false;
            }
            if (WildcardAWriteOps.IsNetworkManaged(portal))
            {
                PortalDebug.LogWarning($"[WildcardADecoyGateEngine] refusing to decoy {portal.m_uid} - it is already NetworkReassertEngine-managed.");
                return false;
            }

            ZDOID fakeId = MintDeadId(portal.GetPosition());
            if (fakeId == ZDOID.None)
            {
                return false;
            }

            PortalOwnership.ClaimAndWrite(portal, z =>
            {
                z.SetConnection(ZDOExtraData.ConnectionType.Portal, fakeId);
                z.Set(WildcardAZdoKeys.DecoyMarker, 1);
                z.Set(WildcardAZdoKeys.DecoyFakeTarget, fakeId);
            });
            _decoys[portal.m_uid] = fakeId;
            return true;
        }

        /// <summary>
        /// The "softer" decoy variant #132 also documents: link to a real Void Anchor that is
        /// deliberately never force-sent to any peer. The link is legitimate (survives reconciliation
        /// given a matching tag and the anchor's own self-loop) but TargetFound fails on every client
        /// until the server chooses to deliver the anchor via WildcardAVoidAnchorEngine.PreWarm - a
        /// switchable gate with no visible state change when flipped.
        /// </summary>
        public static bool MakeSoftDecoy(ZDO portal, string tag, out ZDO? undeliveredAnchor)
        {
            undeliveredAnchor = null;
            if (portal == null || !portal.IsValid())
            {
                return false;
            }
            if (WildcardAWriteOps.IsNetworkManaged(portal))
            {
                PortalDebug.LogWarning($"[WildcardADecoyGateEngine] refusing to soft-decoy {portal.m_uid} - it is already NetworkReassertEngine-managed.");
                return false;
            }
            ZDO? anchor = WildcardAVoidAnchorEngine.CreateAnchor(portal.GetPosition() + Vector3.up * 2f, portal.GetRotation(), tag);
            if (anchor == null)
            {
                return false;
            }
            if (!WildcardAVoidAnchorEngine.LinkPortalToAnchor(portal, anchor, tag))
            {
                return false;
            }
            undeliveredAnchor = anchor;
            return true;
        }

        /// <summary>Removes the decoy marker and leaves the portal's connection for vanilla's own 5s reconciler to clear/re-pair.</summary>
        public static void Release(ZDO portal)
        {
            if (portal == null || !portal.IsValid())
            {
                return;
            }
            _decoys.Remove(portal.m_uid);
            PortalOwnership.ClaimAndWrite(portal, z =>
            {
                z.RemoveInt(WildcardAZdoKeys.DecoyMarker);
                z.RemoveZDOID(WildcardAZdoKeys.DecoyFakeTarget);
            });
        }

        private static void Reassert()
        {
            if (ZDOMan.instance == null || _decoys.Count == 0)
            {
                return;
            }
            List<ZDOID> stale = null;
            var snapshot = new List<KeyValuePair<ZDOID, ZDOID>>(_decoys);
            foreach (var kvp in snapshot)
            {
                ZDO portal = ZDOMan.instance.GetZDO(kvp.Key);
                if (portal == null || !portal.IsValid())
                {
                    (stale ??= new List<ZDOID>()).Add(kvp.Key);
                    continue;
                }
                if (portal.GetConnectionZDOID(ZDOExtraData.ConnectionType.Portal) != kvp.Value)
                {
                    // Game.ConnectPortals phase 1 nulled it (or re-paired it) - re-assert our fake target.
                    PortalOwnership.ClaimAndWrite(portal, z => z.SetConnection(ZDOExtraData.ConnectionType.Portal, kvp.Value));
                }
            }
            if (stale != null)
            {
                foreach (ZDOID id in stale)
                {
                    _decoys.Remove(id);
                }
            }
        }

        /// <summary>
        /// Mints a ZDOID that can never resolve: create a real, server-owned ZDO, then destroy it
        /// immediately so it lands in ZDOMan's own dead-id set (#132's own citation, :76997) - never
        /// hand-build a ZDOID, since ZDOID.UserID values renumber on every world load.
        /// </summary>
        private static ZDOID MintDeadId(Vector3 nearPos)
        {
            try
            {
                ZDO throwaway = ZDOMan.instance.CreateNewZDO(nearPos, 0);
                if (throwaway == null)
                {
                    return ZDOID.None;
                }
                ZDOID id = throwaway.m_uid;
                WildcardAWriteOps.OwnerDestroy(throwaway);
                return id;
            }
            catch (Exception ex)
            {
                PortalDebug.LogError($"[WildcardADecoyGateEngine] MintDeadId failed: {ex.GetType().Name}: {ex.Message}");
                return ZDOID.None;
            }
        }
    }
}
