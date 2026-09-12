using System.Collections.Generic;
using UnityEngine;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;
using TortalPortalLite.Subsystems.Foundations;

namespace TortalPortalLite.Subsystems.Enforcement
{
    /// <summary>
    /// #217 Arrival Bouncer. Prevention is impossible - `TeleportWorld.Teleport`'s gates all run on the
    /// TRAVELLER's own client - but the aftermath is fully observable server-side: `Player.UpdateTeleport`
    /// pins the character's position to the destination from t&gt;2s and the owning client's own
    /// ZSyncTransform pushes that into the character ZDO, so this server sees the jump roughly 2s after
    /// it happens, well before the "distant" 8s floor (`m_teleportCooldown` held at 0 for the whole
    /// teleport, per Player.TeleportTo's own citations) has elapsed - which is why the return delay
    /// defaults to 10.5s (8s floor + 2s post-teleport cooldown + one sample interval of slack).
    ///
    /// No Wonderland-style PositionWatch exists in this codebase, so this engine keeps its own minimal
    /// per-character position sample: if a character's position jumped between two samples from near a
    /// managed (ACL-locked) portal's SOURCE to near that portal's own DESTINATION, and the traveller is
    /// not permitted at the destination under the SAME #53 Portal ACL chain everything else in this
    /// domain shares (peeked, not mutated - arriving somewhere should never silently claim it), a return
    /// teleport is scheduled via the character's own `RPC_TeleportTo(Vector3,Quaternion,bool)` - an
    /// ordinary routed RPC call (owner-gated, exactly like a legitimate teleport), no patch required.
    ///
    /// Reactive only, by construction - the traveller is present at the destination for the whole delay
    /// window before being sent back (#217's own stated failure mode).
    /// </summary>
    public static class AccessArrivalBouncerEngine
    {
        private struct PendingReturn
        {
            public long PeerUid;
            public ZDOID CharacterUid;
            public Vector3 ReturnPos;
            public Quaternion ReturnRot;
            public float FireAtClock;
        }

        private static readonly Dictionary<ZDOID, Vector3> _lastPos = new Dictionary<ZDOID, Vector3>();
        private static readonly HashSet<ZDOID> _pendingCharacters = new HashSet<ZDOID>();
        private static readonly List<PendingReturn> _pending = new List<PendingReturn>();

        private static float _clock;
        private static float _timer;

        public static void OnUpdate(float dt)
        {
            _clock += dt;
            if (AccessConfig.ArrivalBouncerEnabled?.Value != true)
            {
                return;
            }
            ProcessPending();

            _timer += dt;
            float interval = AccessConfig.ArrivalSampleSeconds?.Value ?? 3f;
            if (_timer < interval)
            {
                return;
            }
            _timer = 0f;
            Sample();
        }

        private static void Sample()
        {
            float radius = AccessConfig.ArrivalRadius?.Value ?? 40f;
            float radiusSqr = radius * radius;

            foreach (ConnectedCharacter who in ConnectedCharacters.All())
            {
                Vector3 pos = who.Position;
                if (_lastPos.TryGetValue(who.Zdo.m_uid, out Vector3 prev) && (pos - prev).sqrMagnitude > 4f && !_pendingCharacters.Contains(who.Zdo.m_uid))
                {
                    DetectTransit(who, prev, pos, radiusSqr);
                }
                _lastPos[who.Zdo.m_uid] = pos;
            }
        }

        private static void DetectTransit(ConnectedCharacter who, Vector3 priorPos, Vector3 newPos, float radiusSqr)
        {
            foreach (PortalRecord portal in PortalCensus.Latest)
            {
                if (portal.Connection == ZDOID.None || (portal.Position - priorPos).sqrMagnitude > radiusSqr)
                {
                    continue;
                }
                if (!PortalCensus.TryGet(portal.Connection, out PortalRecord destination) || (destination.Position - newPos).sqrMagnitude > radiusSqr)
                {
                    continue;
                }

                // Only a LOCKED destination is this engine's concern - an open managed portal has nothing to bounce anyone out of.
                if (!AccessAclStore.TryGet(destination.Position, out AccessAclEntry entry) || entry.Lock == AccessLockLevel.None)
                {
                    return;
                }

                ZDO? destZdo = ZDOMan.instance?.GetZDO(destination.Uid);
                if (destZdo == null || !destZdo.IsValid())
                {
                    return;
                }

                string actorHost = SenderContext.HostNameOf(who.Peer) ?? "";
                AccessPortalAclEngine.Decision decision = AccessPortalAclEngine.Evaluate(destZdo, actorHost, 0L, false, false, out string reason, allowMutation: false);
                if (decision == AccessPortalAclEngine.Decision.Deny)
                {
                    SchedulePending(who, priorPos);
                    PlayerNotify.Toast(who, $"You are not permitted here ({reason}) - you will be returned shortly.");
                    PortalDebug.LogAlways($"[AccessArrivalBouncerEngine] {actorHost} arrived at restricted {destination.Uid} without permission - return scheduled.");
                }
                return;
            }
        }

        private static void SchedulePending(ConnectedCharacter who, Vector3 returnPos)
        {
            float delay = AccessConfig.ArrivalReturnDelaySeconds?.Value ?? 10.5f;
            _pendingCharacters.Add(who.Zdo.m_uid);
            _pending.Add(new PendingReturn
            {
                PeerUid = who.Peer.m_uid,
                CharacterUid = who.Zdo.m_uid,
                ReturnPos = returnPos,
                ReturnRot = who.Zdo.GetRotation(),
                FireAtClock = _clock + delay
            });
        }

        private static void ProcessPending()
        {
            if (_pending.Count == 0 || ZRoutedRpc.instance == null)
            {
                return;
            }
            for (int i = _pending.Count - 1; i >= 0; i--)
            {
                PendingReturn pending = _pending[i];
                if (_clock < pending.FireAtClock)
                {
                    continue;
                }
                _pending.RemoveAt(i);
                _pendingCharacters.Remove(pending.CharacterUid);

                if (ZNet.instance?.GetPeer(pending.PeerUid) == null)
                {
                    continue; // disconnected in the meantime - nothing to send back.
                }
                // distantTeleport:true never refuses on distance (Player.UpdateTeleport's own 15s force-land) - the one failure mode this can hit is the 2s post-teleport cooldown not having elapsed yet, in which case RPC_TeleportTo's TeleportTo(...) override on Player simply returns false silently; the delay default already budgets for that.
                ZRoutedRpc.instance.InvokeRoutedRPC(pending.PeerUid, pending.CharacterUid, "RPC_TeleportTo", pending.ReturnPos, pending.ReturnRot, true);
            }
        }
    }
}
