using System.Collections.Generic;
using TortalPortalLite.Core.Data;
using TortalPortalLite.Subsystems.Foundations;

namespace TortalPortalLite.Subsystems.Topology
{
    /// <summary>
    /// #224 DestinationPrewarm (Event-Driven Per-Write Push) - the generic safety net underneath every
    /// mechanism-specific engine that already prewarms at its OWN moment of writing (Hub on rotation,
    /// JIT on arming, Player-Requested on selection, ...). Those cover "the player who is nearby WHEN the
    /// write happens"; this engine additionally covers the player who approaches or joins AFTER a write
    /// already landed and never got their own copy.
    ///
    /// Every tick, for every portal PortalCensus already knows about with a live connection, any
    /// connected character within RoutingConfig.PrewarmRadius is checked against
    /// RoutingReadinessGate.IsCurrent (#232's held-ZDO ledger read) and, if their client's copy of the
    /// destination is missing or stale, force-sent it via RoutingWriteOps.PrewarmToPeer - the same
    /// ForceSendZDO primitive every other engine already uses, just applied uniformly rather than
    /// per-mechanism. This is also exactly where #231's Revision-Bump Redelivery primitive matters: a
    /// destination whose FIELDS didn't change (only got re-approved by a re-run policy) would otherwise
    /// never re-satisfy ShouldSend, so a moving/rewritten destination that needs a guaranteed refresh
    /// calls RoutingRevisionTouch itself (see RoutingMovingAnchorEngine) - this engine only needs to ask
    /// "is it current", never to force a touch on a destination it does not own.
    ///
    /// Cost is bounded by construction: PortalCensus.Latest is already computed at 1Hz for other reasons,
    /// and the inner peer loop only runs for portals with a real connection, over whatever handful of
    /// characters are actually online - trivial for the server sizes this mod targets.
    /// </summary>
    public static class RoutingPrewarmEngine
    {
        private static float _timer;

        public static void OnUpdate(float dt)
        {
            if (RoutingConfig.TakeoverEnabled?.Value == false || ZDOMan.instance == null)
            {
                return;
            }
            _timer += dt;
            if (_timer < 1.0f)
            {
                return;
            }
            _timer = 0f;

            float radius = RoutingConfig.PrewarmRadius?.Value ?? 30f;
            float radiusSqr = radius * radius;
            List<ConnectedCharacter> characters = ConnectedCharacters.All();
            if (characters.Count == 0)
            {
                return;
            }

            foreach (PortalRecord record in PortalCensus.Latest)
            {
                if (record.Connection == ZDOID.None)
                {
                    continue;
                }
                ZDO? destZdo = ZDOMan.instance.GetZDO(record.Connection);
                if (destZdo == null || !destZdo.IsValid())
                {
                    continue;
                }

                foreach (ConnectedCharacter character in characters)
                {
                    if ((character.Position - record.Position).sqrMagnitude > radiusSqr)
                    {
                        continue;
                    }
                    long peerUid = character.Peer.m_uid;
                    if (!RoutingReadinessGate.IsCurrent(peerUid, destZdo))
                    {
                        RoutingWriteOps.PrewarmToPeer(peerUid, destZdo.m_uid);
                    }
                }
            }
        }
    }
}
