using System.Collections.Generic;
using UnityEngine;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;
using TortalPortalLite.Core.Hooks;
using TortalPortalLite.Subsystems.Foundations;
using TortalPortalLite.Subsystems.Topology;

namespace TortalPortalLite.Subsystems.PlayerInterface
{
    /// <summary>
    /// #161 Item stand token board - POSITIONAL encoding (the catalog's own (a) option): a row of
    /// ItemStands beside a named portal, stand index N (by bearing around the portal) selects the N-th
    /// address-book destination. `ItemStand.UpdateAttach` writes `ZDOVars.s_item` (the mounted item's
    /// prefab hash, 0 when empty) with no RPC involved for the STATE itself - reaches the server as
    /// ordinary `RPC_ZDOData`, the same `RpcZdoDataHook` choke point every other ZDO-write channel in
    /// this domain uses.
    ///
    /// HARD LIMIT the catalog is explicit about: the server can clear a stand's `s_item` field, but the
    /// physical item is then GONE - spawning the drop needs a live GameObject, which never exists
    /// server-side. So this engine only ever READS `s_item`; it never writes to a stand.
    /// </summary>
    public static class UxItemStandBoardEngine
    {
        public static void Initialize()
        {
            RpcZdoDataHook.RegisterPostfix(100, OnZdoDataFromClient);
        }

        private static void OnZdoDataFromClient(ZNetPeer? sender, ZDOID zdoid)
        {
            if (UxConfig.Enabled?.Value == false || UxConfig.ItemStandBoardEnabled?.Value == false)
            {
                return;
            }
            if (ZDOMan.instance == null)
            {
                return;
            }
            ZDO stand = ZDOMan.instance.GetZDO(zdoid);
            if (stand == null || !stand.IsValid() || !TargetedPrefabDiscovery.IsItemStand(stand.GetPrefab()))
            {
                return;
            }
            int itemHash = stand.GetInt(ZDOVars.s_item, 0);
            if (itemHash == 0)
            {
                return; // detach - nothing to dial to (never auto-clear a stand, so there is nothing useful to do here either)
            }

            float radius = UxConfig.ItemStandDiscoveryRadius?.Value ?? 8f;
            if (!UxAddressBook.TryNearestAnyPortal(stand.GetPosition(), radius, out PortalRecord portal))
            {
                return;
            }

            List<ZDO> stands = DiscoverStands(portal.Position, radius);
            int index = stands.FindIndex(z => z.m_uid == zdoid);
            if (index < 0 || !UxAddressBook.TryGetByIndex(index + 1, out UxAddressBook.AddressEntry entry))
            {
                return;
            }

            ZDO portalZdo = ZDOMan.instance.GetZDO(portal.Uid);
            if (portalZdo == null || !portalZdo.IsValid())
            {
                return;
            }
            ConnectedCharacter? requester = sender != null ? ResolveByPeer(sender) : null;
            UxDialAction.TryDialToRecord(portalZdo, entry.Record, requester, out _);
        }

        /// <summary>Bearing-ordered, never ZDOID-ordered (ZDO.Load renumbers uids on every world reload) - the same rule UxLedgerEngine's sign discovery follows.</summary>
        private static List<ZDO> DiscoverStands(Vector3 portalPos, float radius)
        {
            List<ZDO> nearby = ZdoSpatialQuery.FindNear(portalPos, radius);
            var stands = new List<ZDO>();
            foreach (ZDO z in nearby)
            {
                if (TargetedPrefabDiscovery.IsItemStand(z.GetPrefab()))
                {
                    stands.Add(z);
                }
            }
            stands.Sort((a, b) => Bearing(portalPos, a.GetPosition()).CompareTo(Bearing(portalPos, b.GetPosition())));
            return stands;
        }

        private static float Bearing(Vector3 origin, Vector3 point)
        {
            Vector3 d = point - origin;
            return Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg;
        }

        private static ConnectedCharacter? ResolveByPeer(ZNetPeer peer)
        {
            foreach (ConnectedCharacter cc in ConnectedCharacters.All())
            {
                if (cc.Peer == peer)
                {
                    return cc;
                }
            }
            return null;
        }
    }
}
