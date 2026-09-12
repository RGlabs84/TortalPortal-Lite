using UnityEngine;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;
using TortalPortalLite.Subsystems.Foundations;

namespace TortalPortalLite.Subsystems.Topology
{
    /// <summary>
    /// Shared low-level write/lookup helpers used by nearly every engine in this domain - the common
    /// plumbing behind catalog #23's write primitive (Game.SetConnection's own recipe, taken
    /// unconditionally: SetOwner -> write -> ForceSendZDO) so each engine's file only has to state ITS
    /// policy (what the tag/connection SHOULD be right now), never re-derive the write mechanics.
    /// Every actual s_tag/ConnectionType.Portal write still goes through
    /// Core/Data/PortalOwnership.ClaimAndWrite - this class never bypasses it.
    /// </summary>
    public static class RoutingWriteOps
    {
        /// <summary>
        /// Writes <paramref name="tag"/> (if non-null) and/or <paramref name="connection"/> (if given)
        /// on <paramref name="zdo"/> ONLY if at least one differs from its current value - the same
        /// idempotency NetworkReassertEngine relies on (ZDOExtraData.SetConnection early-returns on an
        /// unchanged value, so a blind re-write every tick is also safe, just wasteful to even attempt).
        /// Returns true if a write was actually issued.
        /// </summary>
        public static bool Reassert(ZDO zdo, string? tag, ZDOID? connection)
        {
            if (zdo == null || !zdo.IsValid())
            {
                return false;
            }

            string currentTag = zdo.GetString(ZDOVars.s_tag, "");
            ZDOID currentConnection = zdo.GetConnectionZDOID(ZDOExtraData.ConnectionType.Portal);

            bool tagWrong = tag != null && currentTag != tag;
            bool connectionWrong = connection.HasValue && currentConnection != connection.Value;
            if (!tagWrong && !connectionWrong)
            {
                return false;
            }

            PortalOwnership.ClaimAndWrite(zdo, z =>
            {
                if (tagWrong)
                {
                    z.Set(ZDOVars.s_tag, tag);
                }
                if (connectionWrong)
                {
                    z.SetConnection(ZDOExtraData.ConnectionType.Portal, connection!.Value);
                }
            });
            return true;
        }

        /// <summary>Blackout helper - explicit name at call sites reads better than Reassert(zdo, null, ZDOID.None).</summary>
        public static bool Disconnect(ZDO zdo, string? tag = null) => Reassert(zdo, tag, ZDOID.None);

        /// <summary>
        /// Resolves a declared routing.json position to its LIVE portal ZDO via PortalCensus (never a
        /// cached ZDO reference - ZDOPool recycles objects, catalog #23's own citation :78286-78311).
        /// Null if no portal currently sits at that rounded position.
        /// </summary>
        public static ZDO? ResolveLive(RoutingPosition pos)
        {
            if (ZDOMan.instance == null || !PortalCensus.TryGetByPosition(pos.ToVector3(), out PortalRecord record))
            {
                return null;
            }
            ZDO zdo = ZDOMan.instance.GetZDO(record.Uid);
            return zdo != null && zdo.IsValid() ? zdo : null;
        }

        /// <summary>
        /// DestinationPrewarm's core primitive (#224): force-resend an existing ZDO to one specific
        /// peer, guaranteeing TeleportWorld.TargetFound's "client must hold the destination ZDO" gate
        /// passes before that peer ever tries to use it. This does NOT touch s_tag/connection - it only
        /// triggers a resend of whatever the ZDO currently holds, so it is not restricted to
        /// PortalOwnership.
        /// </summary>
        public static void PrewarmToPeer(long peerUid, ZDOID targetZdoId)
        {
            ZDOMan.instance?.ForceSendZDO(peerUid, targetZdoId);
        }

        /// <summary>Prewarms every connected character within <paramref name="radius"/> of <paramref name="sourcePos"/> with <paramref name="targetZdoId"/>.</summary>
        public static void PrewarmToPeersNear(Vector3 sourcePos, float radius, ZDOID targetZdoId)
        {
            if (ZDOMan.instance == null)
            {
                return;
            }
            float radiusSqr = radius * radius;
            foreach (ConnectedCharacter character in ConnectedCharacters.All())
            {
                if ((character.Position - sourcePos).sqrMagnitude <= radiusSqr)
                {
                    PrewarmToPeer(character.Peer.m_uid, targetZdoId);
                }
            }
        }
    }

    /// <summary>Trivial per-tick write-budget counter (catalog #38's "budget writes per sweep" discipline) so a mass-rewrite engine never floods every peer's send queue in one tick.</summary>
    public struct RoutingWriteBudget
    {
        private int _remaining;
        public RoutingWriteBudget(int max) { _remaining = max; }
        public bool TryConsume()
        {
            if (_remaining <= 0)
            {
                return false;
            }
            _remaining--;
            return true;
        }
    }
}
