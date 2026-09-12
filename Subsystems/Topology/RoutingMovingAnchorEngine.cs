using System.Collections.Generic;
using UnityEngine;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;

namespace TortalPortalLite.Subsystems.Topology
{
    /// <summary>Admin declaration for one Moving Destination Anchor gate (routing.json section "movingAnchors").</summary>
    public sealed class RoutingMovingAnchorDefinition
    {
        public string Name = "";
        public RoutingPosition Position = new RoutingPosition();
        public RoutingPosition ShipSearchPosition = new RoutingPosition();
        public float ShipSearchRadius = 100f;
        public string MooredTag = "Moored";
        public string UnderwayTag = "Under way";
    }

    /// <summary>
    /// #40 Moving Destination Anchor. `TeleportWorld.Teleport` reads the destination ZDO's live
    /// GetPosition()/GetRotation() at the instant of transit with no prefab validation (:143539-143545),
    /// so pointing a portal at a Ship's ZDO genuinely resolves to that ship's current position on a
    /// vanilla client. The catalog's own three hard limits are why this engine deliberately targets
    /// ONLY stationary/moored ships, never a sailing one and never a player:
    ///  (1) A ZDO holds exactly ONE connection entry (ZDOExtraData's own single-target-per-type slot,
    ///      :74794/:75524-75534) - writing a Portal connection onto a player's character ZDO would
    ///      destroy the SyncTransform ship-attachment record a real passenger relies on. Ships have no
    ///      such parent record, so they alone are safe to reference this way.
    ///  (2) `TeleportWorld.TargetFound` requests a missing destination ZDO exactly ONCE
    ///      (:143646-143654) and `SendZDOs` consumes that request after one send (:77051) - a moving
    ///      target goes stale unless force-refreshed continuously, which this engine does via
    ///      RoutingRevisionTouch every MovingAnchorRefreshSeconds so nearby peers' copies never lapse.
    ///  (3) Arriving ON a moving ship is structurally impossible in vanilla - `Player.UpdateTeleport`
    ///      snapshots the target position once and holds the traveller pinned to it for at least 8s
    ///      (distantTeleport, which portals always pass, :15337-15349). So this engine gates on the ship
    ///      being CURRENTLY STATIONARY (no helmsman) rather than attempting to track one under way -
    ///      exactly the catalog's own "the gate to the longship only opens while she's at anchor".
    ///
    /// SHIP IDENTIFICATION: ships are not portals, so PortalCensus does not index them.
    /// ZdoSpatialQuery.FindNear locates nearby ZDOs; among those, a ZDO is treated as a candidate ship if
    /// its resolved prefab name contains "ship" or "raft" (case-insensitive) - a name-substring heuristic
    /// rather than a hardcoded exact prefab list, since no ship prefab name literal could be confirmed as
    /// a string constant directly in the decompile (Inspector-wired references only) and a substring
    /// match is more future-proof against new ship prefabs (e.g. an Ashlands cargo ship) than an exact
    /// list would be. "Moored" is read from `ZDOVars.s_user` (:78637, written by
    /// `ShipControlls.RPC_RequestControl`) - 0/absent means nobody currently holds the helm.
    /// </summary>
    public static class RoutingMovingAnchorEngine
    {
        private static float _timer;
        private static int _lastRegistryVersion = -1;
        private static List<RoutingMovingAnchorDefinition> _anchors = new List<RoutingMovingAnchorDefinition>();
        private static readonly List<ZDO> _scanBuffer = new List<ZDO>();

        public static void OnUpdate(float dt)
        {
            if (RoutingConfig.TakeoverEnabled?.Value == false || ZDOMan.instance == null || ZNetScene.instance == null)
            {
                return;
            }
            _timer += dt;
            float interval = RoutingConfig.MovingAnchorRefreshSeconds?.Value ?? 2f;
            if (_timer < interval)
            {
                return;
            }
            _timer = 0f;

            if (RoutingManagedPortalRegistry.Version != _lastRegistryVersion)
            {
                _lastRegistryVersion = RoutingManagedPortalRegistry.Version;
                _anchors = RoutingManagedPortalRegistry.Section<RoutingMovingAnchorDefinition>("movingAnchors");
            }

            foreach (RoutingMovingAnchorDefinition anchor in _anchors)
            {
                if (!RoutingPairingAuthorityEngine.TryClaim(anchor.Position, $"movinganchor:{anchor.Name}"))
                {
                    continue;
                }
                ZDO? gateZdo = RoutingWriteOps.ResolveLive(anchor.Position);
                if (gateZdo == null)
                {
                    continue;
                }

                ZDO? ship = FindNearestShip(anchor.ShipSearchPosition.ToVector3(), anchor.ShipSearchRadius);
                if (ship == null)
                {
                    RoutingWriteOps.Reassert(gateZdo, anchor.UnderwayTag, ZDOID.None);
                    RoutingPairingAuthorityEngine.Publish(gateZdo.m_uid, anchor.UnderwayTag, ZDOID.None);
                    continue;
                }

                bool moored = ship.GetLong(ZDOVars.s_user, 0L) == 0L;
                if (!moored)
                {
                    RoutingWriteOps.Reassert(gateZdo, anchor.UnderwayTag, ZDOID.None);
                    RoutingPairingAuthorityEngine.Publish(gateZdo.m_uid, anchor.UnderwayTag, ZDOID.None);
                    continue;
                }

                RoutingWriteOps.Reassert(gateZdo, anchor.MooredTag, ship.m_uid);
                RoutingPairingAuthorityEngine.Publish(gateZdo.m_uid, anchor.MooredTag, ship.m_uid);
                // Keep every nearby peer's copy of the ship ZDO fresh - TargetFound's RequestZDO only
                // fires once and SendZDOs consumes it after a single send (:143650, :77051).
                RoutingRevisionTouch.Touch(ship, claimOwnership: false);
                RoutingWriteOps.PrewarmToPeersNear(gateZdo.GetPosition(), RoutingConfig.PrewarmRadius?.Value ?? 30f, ship.m_uid);
            }
        }

        private static ZDO? FindNearestShip(Vector3 searchPos, float radius)
        {
            _scanBuffer.Clear();
            ZdoSpatialQuery.FindNear(searchPos, radius, _scanBuffer);
            ZDO? best = null;
            float bestSqr = float.MaxValue;
            foreach (ZDO candidate in _scanBuffer)
            {
                if (!LooksLikeShip(candidate))
                {
                    continue;
                }
                float sqr = (candidate.GetPosition() - searchPos).sqrMagnitude;
                if (sqr < bestSqr)
                {
                    bestSqr = sqr;
                    best = candidate;
                }
            }
            return best;
        }

        private static bool LooksLikeShip(ZDO zdo)
        {
            GameObject? prefab = ZNetScene.instance!.GetPrefab(zdo.GetPrefab());
            if (prefab == null)
            {
                return false;
            }
            string name = prefab.name;
            return name.IndexOf("ship", System.StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("raft", System.StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("karve", System.StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
