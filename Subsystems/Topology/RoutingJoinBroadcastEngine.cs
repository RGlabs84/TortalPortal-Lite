using System.Collections.Generic;
using TortalPortalLite.Core;

namespace TortalPortalLite.Subsystems.Topology
{
    /// <summary>
    /// #223 Join-Time Portal Registry Broadcast. Vanilla never sends a joining peer any portal outside
    /// their immediate near ring - portals are explicitly excluded from the "distant" ring
    /// (`ZDOMan.FindDistantObjects` reads only `m_objectsBySector`, portals live in the separate
    /// `m_portalObjects` map merged by `FindObjects`, :77376-77404), so a fresh joiner's first sync list
    /// holds only whatever portals sit within their spawn point's near sectors. Because a vanilla client
    /// never discards a received ZDO once held (only `HandleDestroyedZDO` removes one), a single push at
    /// join is permanent for that whole session - the catalog's own reasoning for doing this exactly once
    /// per connection rather than repeatedly.
    ///
    /// The catalog's own mechanism is a postfix on the private `ZNet.RPC_PeerInfo` (:79774), right after
    /// `ZDOMan.AddPeer` has run for that peer.
    ///
    /// // NEEDS NEW HOOK BROKER on ZNet.RPC_PeerInfo: none of Core/Hooks/'s existing brokers cover it.
    ///
    /// Implemented instead as a poll-based approximation that needs no new patch at all: every
    /// RoutingConfig.JoinBroadcastPollSeconds, this engine diffs `ZNet.instance.GetPeers()` (ready peers
    /// only) against a "already pushed" set and, for any peer seen for the first time, force-sends every
    /// portal ZDO to them via the same `ZDOMan.instance.ForceSendZDO(peerUid, id)` primitive the catalog
    /// itself specifies for the actual push. The only behavioural difference from a true RPC_PeerInfo
    /// postfix is timing - up to one poll interval (default 1s) later than the instant vanilla's own
    /// handshake completes, rather than in the same frame - which does not matter here since the push
    /// only needs to land before the player can physically reach a portal.
    /// </summary>
    public static class RoutingJoinBroadcastEngine
    {
        private static float _timer;
        private static readonly HashSet<long> _alreadyPushed = new HashSet<long>();

        public static void OnUpdate(float dt)
        {
            if (RoutingConfig.TakeoverEnabled?.Value == false || ZDOMan.instance == null || ZNet.instance == null)
            {
                return;
            }
            _timer += dt;
            float interval = RoutingConfig.JoinBroadcastPollSeconds?.Value ?? 1.0f;
            if (_timer < interval)
            {
                return;
            }
            _timer = 0f;

            var stillConnected = new HashSet<long>();
            foreach (ZNetPeer peer in ZNet.instance.GetPeers())
            {
                if (peer == null || !peer.IsReady())
                {
                    continue;
                }
                stillConnected.Add(peer.m_uid);
                if (_alreadyPushed.Add(peer.m_uid))
                {
                    PushEveryPortalTo(peer.m_uid);
                }
            }

            // Forget disconnected peers, so a reconnecting player (new ZNetPeer instance/session) gets a fresh push.
            _alreadyPushed.RemoveWhere(uid => !stillConnected.Contains(uid));
        }

        private static void PushEveryPortalTo(long peerUid)
        {
            List<ZDO> portals = ZDOMan.instance!.GetPortalList();
            foreach (ZDO portal in portals)
            {
                if (portal != null && portal.IsValid())
                {
                    ZDOMan.instance.ForceSendZDO(peerUid, portal.m_uid);
                }
            }
            PortalDebug.LogInfo($"[RoutingJoinBroadcastEngine] pushed {portals.Count} portal ZDO(s) to newly-joined peer {peerUid}.");
        }
    }
}
