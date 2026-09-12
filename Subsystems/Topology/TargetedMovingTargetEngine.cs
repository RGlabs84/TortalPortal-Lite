using UnityEngine;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;

namespace TortalPortalLite.Subsystems.Topology
{
    /// <summary>
    /// #196 Moving Target Updated on a Tick (general engine) - the shared rules every option that tracks
    /// something in motion (#190 Ship, #191 Nearest Player, #194 Builder Trace's live variant) must
    /// follow, factored out here so each of those engines only supplies WHAT to track:
    ///
    ///  1. Only a non-portal Invisible Anchor (#179/TargetedAnchorFactory) may be repositioned in place -
    ///     ZDO.SetSector early-returns for any prefab in Game.PortalPrefabHash (SERVER decompile
    ///     :73752-73768), so a phantom PORTAL that is SetPosition'ed stays filed under its creation
    ///     sector forever (a leak on both server and client). TargetedAnchorFactory.MoveTo already
    ///     enforces this by construction (prefab hash 0).
    ///  2. ZDO.InternalSetPosition only bumps DataRevision when IsOwner() (:73734-73745), and
    ///     ZDOMan.ReleaseNearbyZDOS reassigns ownership of persistent ZDOs inside any peer's active area
    ///     every ~2 s (:76901-76924) - TargetedAnchorFactory.MoveTo re-claims ownership on every call
    ///     rather than assuming a prior claim still holds.
    ///  3. A client fetches a distant destination ZDO once (TeleportWorld.TargetFound -&gt; RequestZDO,
    ///     :143640-143658) and never re-requests it - THIS class's own job is exactly that missing
    ///     piece: force-sending the anchor's fresh ZDO to every peer near the SOURCE portal whenever it
    ///     actually moves, so a traveller standing at the hub sees an up-to-date destination rather than
    ///     whatever position their client first fetched.
    ///  4. SendZDOs has no delta encoding (~80-120 bytes per push, :77053-77062) - callers should only
    ///     invoke this after TargetedAnchorFactory.MoveTo reports a real move, never unconditionally
    ///     every tick.
    /// </summary>
    public static class TargetedMovingTargetEngine
    {
        public static float TickSeconds => TargetedConfig.MovingTargetTickSeconds?.Value ?? 1.0f;
        public static float MinDeltaMeters => TargetedConfig.MovingTargetMinDeltaMeters?.Value ?? 1f;

        /// <summary>
        /// Force-sends <paramref name="target"/>'s current ZDO state to every connected character within
        /// TargetedConfig.MovingTargetForceSendRadius of <paramref name="source"/>'s position. Call this
        /// only after a successful TargetedAnchorFactory.MoveTo/CreateOrRetarget for the same tick.
        /// </summary>
        public static void ForceSendToNearbySourcePeers(ZDO source, ZDO target)
        {
            if (source == null || !source.IsValid() || target == null || !target.IsValid() || ZDOMan.instance == null)
            {
                return;
            }

            float radius = TargetedConfig.MovingTargetForceSendRadius?.Value ?? 25f;
            float radiusSqr = radius * radius;
            Vector3 sourcePos = source.GetPosition();

            foreach (ConnectedCharacter cc in ConnectedCharacters.All())
            {
                if ((cc.Position - sourcePos).sqrMagnitude <= radiusSqr)
                {
                    ZDOMan.instance.ForceSendZDO(cc.Peer.m_uid, target.m_uid);
                }
            }
        }

        /// <summary>True if moving from <paramref name="lastPos"/> to <paramref name="newPos"/> clears the configured minimum-delta threshold - the "skip a tick's SetPosition/ForceSendZDO when nothing meaningful moved" guard #196 itself specifies.</summary>
        public static bool MovedEnough(Vector3 lastPos, Vector3 newPos)
        {
            float minDelta = MinDeltaMeters;
            return (newPos - lastPos).sqrMagnitude >= minDelta * minDelta;
        }
    }
}
