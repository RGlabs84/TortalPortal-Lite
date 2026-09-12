using System.Collections.Generic;
using TortalPortalLite.Core;

namespace TortalPortalLite.Subsystems.Topology
{
    /// <summary>
    /// #229 Peer Link Probe (RTT and Queue Budget). Two independent signals:
    ///
    ///  - SOCKET BACKLOG: `ISocket.GetSendQueueSize()` (:71836) is a plain PUBLIC interface method - the
    ///    exact number `ZDOMan.SendZDOs` itself subtracts from the 10240-byte cap (:77010-77015). Fully
    ///    implemented here with no patch needed at all.
    ///  - RTT: `Game.Start` registers `RPC_Ping`/`RPC_Pong` on EVERY peer, not IsServer-gated
    ///    (:100065-100066). Sending a ping is a normal outbound RPC call
    ///    (`ZRoutedRpc.instance.InvokeRoutedRPC(peerUid, "RPC_Ping", Time.time)`, exactly the pattern
    ///    Core/Data/PlayerNotify.cs already uses for "Message" - no patch needed to SEND). Capturing the
    ///    client's echoed reply requires observing the private `Game.RPC_Pong(long sender, float time)`
    ///    when the SERVER receives it back:
    ///
    /// // NEEDS NEW HOOK BROKER on Game.RPC_Pong(long, float): none of Core/Hooks/'s existing brokers
    /// // cover an arbitrary named routed RPC target - RTT stays at RoutingConfig-less hardcoded
    /// // fallback (see FallbackRttMs) until one exists.
    ///
    /// Budgets derived from whatever RTT/backlog data IS available: JitLeadMeters (how many extra metres
    /// of arm radius a laggy peer needs, per #32's own timing budget) and FastTransitAllowed (per #225's
    /// own "Rtt &lt; 250ms &amp;&amp; queue &lt; 4KB" rule).
    /// </summary>
    public static class RoutingPeerLinkProbeEngine
    {
        private const float FallbackRttMs = 150f;
        private const float SprintSpeedMetersPerSecond = 7f;

        private static float _timer;
        private static readonly Dictionary<long, float> _rttMsByPeer = new Dictionary<long, float>();

        public static void OnUpdate(float dt)
        {
            if (RoutingConfig.TakeoverEnabled?.Value == false || ZNet.instance == null || ZRoutedRpc.instance == null)
            {
                return;
            }
            _timer += dt;
            float interval = RoutingConfig.PeerProbeIntervalSeconds?.Value ?? 10f;
            if (_timer < interval)
            {
                return;
            }
            _timer = 0f;

            foreach (ZNetPeer peer in ZNet.instance.GetPeers())
            {
                if (peer == null || !peer.IsReady())
                {
                    continue;
                }
                try
                {
                    ZRoutedRpc.instance.InvokeRoutedRPC(peer.m_uid, "RPC_Ping", UnityEngine.Time.time);
                }
                catch (System.Exception ex)
                {
                    PortalDebug.LogWarning($"[RoutingPeerLinkProbeEngine] ping send failed for peer {peer.m_uid}: {ex.Message}");
                }
            }
        }

        /// <summary>Last-known RTT for a peer, in milliseconds - the documented fallback constant until Game.RPC_Pong has a hook broker to actually capture a reply.</summary>
        public static float RttMs(long peerUid) => _rttMsByPeer.TryGetValue(peerUid, out float rtt) ? rtt : FallbackRttMs;

        public static int SendQueueBytes(ZNetPeer peer)
        {
            try
            {
                return peer?.m_socket?.GetSendQueueSize() ?? 0;
            }
            catch
            {
                return 0;
            }
        }

        /// <summary>How many extra metres of JIT arm radius this peer's link needs beyond the base config value, per #32's own (poll + push-lag + RTT) x sprintSpeed budget.</summary>
        public static float JitLeadMeters(long peerUid)
        {
            float pollSeconds = RoutingConfig.ApproachPollSeconds?.Value ?? 0.1f;
            float rttSeconds = RttMs(peerUid) / 1000f;
            return (pollSeconds + 0.1f + rttSeconds) * SprintSpeedMetersPerSecond;
        }

        /// <summary>#225's own admission rule: only offer the fast (distant:false) hop to a peer whose link is healthy.</summary>
        public static bool FastTransitAllowed(ZNetPeer peer)
        {
            if (peer == null)
            {
                return false;
            }
            return RttMs(peer.m_uid) < 250f && SendQueueBytes(peer) < 4096;
        }
    }
}
