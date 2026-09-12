using System.Collections.Generic;
using UnityEngine;
using TortalPortalLite.Core.Data;

namespace TortalPortalLite.Subsystems.Enforcement
{
    /// <summary>
    /// #124 Mass-Rewrite Spike Control and Save-Bloat Budget. A shared per-tick write budget every
    /// mass-rewriting lockdown engine (Force-Disconnect, Quarantine-Tag, Region) consumes from, plus a
    /// smarter ForceSendZDO wrapper: peers within LockdownConfig.ForceSendTargetedRadius of the write get
    /// an immediate targeted push (ZDOMan.ForceSendZDO(peerID, id), the single-peer overload - internally
    /// server-guarded and far cheaper than the broadcast form), everyone else rides the ordinary 0.05s/peer
    /// sync sweep. Exploits SetConnection/tag-write idempotence for free: PortalOwnership.ClaimAndWrite's
    /// own write delegate is a no-op when nothing actually changed (ZDOExtraData.SetConnection's own early
    /// return, catalog #124's own citation), so a steady-state re-assert tick over an already-consistent
    /// world costs one dictionary read per portal, not a write.
    /// </summary>
    public static class LockdownWriteBudget
    {
        private static int _remaining;
        private static int _tickToken = -1;

        /// <summary>Call once per engine per tick before consuming - resets the shared counter the first time any engine touches it this frame (Unity's own frameCount is the cheapest available tick token).</summary>
        private static void EnsureFreshTick()
        {
            int token = Time.frameCount;
            if (token != _tickToken)
            {
                _tickToken = token;
                _remaining = LockdownConfig.MaxWritesPerTick?.Value ?? 40;
            }
        }

        /// <summary>True and decrements if budget remains this tick; false if the shared per-tick cap has been reached (caller should stop and pick up next tick).</summary>
        public static bool TryConsume()
        {
            EnsureFreshTick();
            if (_remaining <= 0)
            {
                return false;
            }
            _remaining--;
            return true;
        }

        /// <summary>Pushes a write immediately to nearby connected characters, lets everyone else ride the ordinary sync sweep - the mitigation for ForceSendZDO's broadcast form flooding every peer's queue.</summary>
        public static void ForceSendNearbyOnly(ZDOID id, Vector3 portalPos)
        {
            if (ZDOMan.instance == null)
            {
                return;
            }
            float radius = LockdownConfig.ForceSendTargetedRadius?.Value ?? 60f;
            float radiusSqr = radius * radius;
            List<ConnectedCharacter> chars = ConnectedCharacters.All();
            bool anyNearby = false;
            foreach (ConnectedCharacter c in chars)
            {
                if ((c.Position - portalPos).sqrMagnitude <= radiusSqr)
                {
                    ZDOMan.instance.ForceSendZDO(c.Peer.m_uid, id);
                    anyNearby = true;
                }
            }
            if (!anyNearby)
            {
                // Nobody is close enough to care about latency - a targeted push to nobody is a no-op,
                // so fall back to nothing extra: the ordinary sweep will carry it within its own cadence.
            }
        }
    }
}
