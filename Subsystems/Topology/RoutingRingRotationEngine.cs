using System.Collections.Generic;
using UnityEngine;
using TortalPortalLite.Core;

namespace TortalPortalLite.Subsystems.Topology
{
    /// <summary>Admin declaration for one One-Way Ring (routing.json section "rings").</summary>
    public sealed class RoutingRingDefinition
    {
        public string Name = "";
        public List<RoutingPosition> Members = new List<RoutingPosition>();
        public string Tag = "ring";

        /// <summary>0 = a static cyclic wiring (member[i] -&gt; member[i+1]) that never reshuffles. &gt;0 = periodically advance an offset so which physical gate sits at which ring slot changes over time.</summary>
        public float RotateSeconds = 0f;

        /// <summary>Rings are normally declared over real player-built portals; set true to fabricate a void anchor for a missing member position instead of skipping it.</summary>
        public bool FabricateMissing = false;
    }

    /// <summary>
    /// #27 One-Way Ring Rotation. Exploits a real gap in vanilla's own reconciler: `Game.ConnectPortals`
    /// pass 1's teardown predicate is exactly "partner missing OR partner's tag differs OR partner's
    /// OWN connection is None" (:100598-100605) - it never checks that the partner points BACK. So an
    /// N-member cycle (A-&gt;B-&gt;C-&gt;A) sharing one tag survives vanilla's reconciler indefinitely, even
    /// though every link is one-way.
    ///
    /// THE CAVEAT THIS ENGINE EXISTS TO PAY FOR: the on-disk format cannot express this topology at all.
    /// `ZDOExtraData.RegenerateConnectionHashData` (:75461-75478) collapses each ZDO to ONE
    /// `ZDOConnectionHashData` slot - writing C's outbound slot silently overwrites A's inbound slot, so
    /// a save/reload collapses an N-member ring to one surviving pair plus orphans, and
    /// `ZDOMan.ConnectPortals` (the load-time hash relinker, :77850-77893) then writes that surviving
    /// pair back as RECIPROCAL, undoing the one-way property too. The mitigation the catalog itself
    /// names is exactly what this engine does: re-assert the whole ring from routing.json (a) once, at
    /// OnWorldReady, before any peer connects - `m_peers` is empty and every write takes the immediate
    /// local path, the cheapest point in the server's whole lifecycle to write a topology - and (b)
    /// again on a slow heartbeat thereafter. Both are cheap: `ZDOExtraData.SetConnection` early-returns
    /// on an unchanged value (:74954-74957), so idempotent re-assertion of an already-correct ring costs
    /// zero DataRevision bumps and zero network traffic.
    /// </summary>
    public static class RoutingRingRotationEngine
    {
        private static float _timer;
        private static int _lastRegistryVersion = -1;
        private static List<RoutingRingDefinition> _rings = new List<RoutingRingDefinition>();

        // NEEDS NEW KEY: tpl_routing_ringrotationoffset, purpose: RingState.Offset is RAM-only - a
        // restart resets a rotating ring back to its declared base wiring instead of resuming mid-cycle.
        // Harmless (the ring is still internally consistent, just reset), but a future wave could persist
        // it per-network-name on a well-known world ZDO the way CapabilityProbe caches its own results.
        private sealed class RingState
        {
            public int Offset;
            public double NextRotateAt;
            public bool Initialized;
            public readonly Dictionary<int, ZDOID> FabricatedAnchors = new Dictionary<int, ZDOID>();
        }

        private static readonly Dictionary<string, RingState> _state = new Dictionary<string, RingState>();

        /// <summary>Called once from RoutingSubsystem.OnWorldReady - the pre-peer window the catalog specifically calls out as the cheapest place to write a whole topology.</summary>
        public static void OnWorldReady()
        {
            RefreshDeclarationsIfNeeded();
            ReassertAll(force: true);
        }

        public static void OnUpdate(float dt)
        {
            if (RoutingConfig.TakeoverEnabled?.Value == false || ZDOMan.instance == null)
            {
                return;
            }
            RefreshDeclarationsIfNeeded();
            if (_rings.Count == 0)
            {
                return;
            }

            _timer += dt;
            float interval = RoutingConfig.RingReassertSeconds?.Value ?? 30f;
            if (_timer < interval)
            {
                return;
            }
            _timer = 0f;
            ReassertAll(force: false);
        }

        private static void RefreshDeclarationsIfNeeded()
        {
            if (RoutingManagedPortalRegistry.Version == _lastRegistryVersion)
            {
                return;
            }
            _lastRegistryVersion = RoutingManagedPortalRegistry.Version;
            _rings = RoutingManagedPortalRegistry.Section<RoutingRingDefinition>("rings");
        }

        private static void ReassertAll(bool force)
        {
            double now = ZNet.instance?.GetTimeSeconds() ?? 0.0;
            foreach (RoutingRingDefinition ring in _rings)
            {
                if (ring.Members.Count < 2)
                {
                    continue;
                }
                bool claimed = true;
                foreach (RoutingPosition memberPos in ring.Members)
                {
                    claimed &= RoutingPairingAuthorityEngine.TryClaim(memberPos, $"ring:{ring.Name}");
                }
                if (!claimed)
                {
                    continue;
                }

                if (!_state.TryGetValue(ring.Name, out RingState? state))
                {
                    state = new RingState();
                    _state[ring.Name] = state;
                }
                if (!state.Initialized)
                {
                    state.NextRotateAt = now + (ring.RotateSeconds > 0f ? ring.RotateSeconds : double.MaxValue);
                    state.Initialized = true;
                }
                if (ring.RotateSeconds > 0f && now >= state.NextRotateAt)
                {
                    state.Offset = (state.Offset + 1) % ring.Members.Count;
                    state.NextRotateAt = now + ring.RotateSeconds;
                }

                ReassertRing(ring, state);
            }
        }

        private static void ReassertRing(RoutingRingDefinition ring, RingState state)
        {
            int n = ring.Members.Count;
            var members = new ZDO?[n];
            for (int i = 0; i < n; i++)
            {
                members[i] = ResolveOrFabricate(ring, state, i);
            }

            for (int i = 0; i < n; i++)
            {
                ZDO? me = members[i];
                if (me == null)
                {
                    continue;
                }
                int nextSlot = (i + 1 + state.Offset) % n;
                ZDO? next = members[nextSlot];
                if (next == null)
                {
                    continue;
                }
                RoutingWriteOps.Reassert(me, ring.Tag, next.m_uid);
                RoutingPairingAuthorityEngine.Publish(me.m_uid, ring.Tag, next.m_uid);
            }
        }

        private static ZDO? ResolveOrFabricate(RoutingRingDefinition ring, RingState state, int memberIndex)
        {
            RoutingPosition pos = ring.Members[memberIndex];
            ZDO? live = RoutingWriteOps.ResolveLive(pos);
            if (live != null)
            {
                return live;
            }
            if (!ring.FabricateMissing)
            {
                return null;
            }
            if (state.FabricatedAnchors.TryGetValue(memberIndex, out ZDOID existingId))
            {
                ZDO? existing = ZDOMan.instance?.GetZDO(existingId);
                if (existing != null && existing.IsValid())
                {
                    return existing;
                }
            }
            ZDO? fabricated = RoutingPhantomAnchorEngine.FabricateVoidAnchor(pos.ToVector3(), Quaternion.identity);
            if (fabricated != null)
            {
                state.FabricatedAnchors[memberIndex] = fabricated.m_uid;
            }
            return fabricated;
        }
    }
}
