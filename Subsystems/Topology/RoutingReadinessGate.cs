using TortalPortalLite.Core;

namespace TortalPortalLite.Subsystems.Topology
{
    /// <summary>
    /// #232 Held-ZDO Ledger Readiness Gate. ZDOMan keeps, per connected peer, a
    /// Dictionary&lt;ZDOID, PeerZDOInfo&gt; recording exactly what that peer's client has actually been
    /// sent (ZDOMan.ZDOPeer.m_zdos, written on every real send and consulted by ShouldSend) - the same
    /// ground truth TeleportWorld.TargetFound implicitly depends on ("does the traveller's client
    /// already hold the destination ZDO"). Reading it directly here turns that into an explicit
    /// before-the-fact check instead of an after-the-fact "walked through, nothing happened" bug
    /// report - every JIT/fast-transit/pre-stream engine in this domain should check Holds/IsCurrent
    /// before opening a gate or announcing "ready", and fall back to DestinationPrewarm when it says no.
    ///
    /// ZDOMan.m_peers and the nested ZDOMan.ZDOPeer class are private in the decompile
    /// (:76041, :75961) - reachable here only because TortalPortalLite.csproj sets
    /// &lt;Publicize&gt;true&lt;/Publicize&gt; on the assembly_valheim reference (same reasoning as
    /// ZdoSpatialQuery's direct use of ZDOMan.instance.m_objectsBySector). If a future game build
    /// renames or removes this nested type, every method below fails closed (returns "not held"/"not
    /// current"), which only ever causes an extra harmless DestinationPrewarm - never a false "ready".
    /// </summary>
    public static class RoutingReadinessGate
    {
        /// <summary>True if the given peer's client has ever been sent this ZDO at all.</summary>
        public static bool Holds(long peerUid, ZDOID target)
        {
            try
            {
                ZDOMan.ZDOPeer? peer = FindPeer(peerUid);
                return peer != null && peer.m_zdos.ContainsKey(target);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>True if the given peer's held copy of <paramref name="target"/> is at least as new as the ZDO's current DataRevision.</summary>
        public static bool IsCurrent(long peerUid, ZDO target)
        {
            if (target == null || !target.IsValid())
            {
                return false;
            }
            try
            {
                ZDOMan.ZDOPeer? peer = FindPeer(peerUid);
                if (peer == null || !peer.m_zdos.TryGetValue(target.m_uid, out ZDOMan.ZDOPeer.PeerZDOInfo info))
                {
                    return false;
                }
                return info.m_dataRevision >= target.DataRevision;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>How long (seconds, Time.time-based) since this peer's client was last actually sent this ZDO, or -1 if never.</summary>
        public static float HeldForSeconds(long peerUid, ZDOID target)
        {
            try
            {
                ZDOMan.ZDOPeer? peer = FindPeer(peerUid);
                if (peer != null && peer.m_zdos.TryGetValue(target, out ZDOMan.ZDOPeer.PeerZDOInfo info))
                {
                    return UnityEngine.Time.time - info.m_syncTime;
                }
            }
            catch
            {
                // fall through
            }
            return -1f;
        }

        private static ZDOMan.ZDOPeer? FindPeer(long peerUid)
        {
            if (ZDOMan.instance == null)
            {
                return null;
            }
            foreach (ZDOMan.ZDOPeer peer in ZDOMan.instance.m_peers)
            {
                if (peer?.m_peer != null && peer.m_peer.m_uid == peerUid)
                {
                    return peer;
                }
            }
            return null;
        }
    }
}
