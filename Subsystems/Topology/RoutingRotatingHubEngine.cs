using System.Collections.Generic;
using UnityEngine;
using TortalPortalLite.Core;

namespace TortalPortalLite.Subsystems.Topology
{
    /// <summary>Admin declaration for one Rotating Hub (routing.json section "hubs").</summary>
    public sealed class RoutingHubDefinition
    {
        public string Name = "";
        public RoutingPosition Position = new RoutingPosition();
        public List<RoutingPosition> Destinations = new List<RoutingPosition>();

        /// <summary>0 = use RoutingConfig.HubDefaultRotateSeconds.</summary>
        public float RotateSeconds = 0f;

        /// <summary>If set, the hub's tag is reasserted to this string every tick alongside its connection. If null, only the connection is managed (an admin who wants to keep a player-chosen tag can omit it).</summary>
        public string? Tag;

        /// <summary>If a destination position has no live portal there, fabricate an invisible void anchor (#24) rather than skip it. Cached per (hub, destination) - fabricated once, then resolved normally like any other declared position.</summary>
        public bool FabricateMissingDestinations = true;

        /// <summary>Route this hub's rotations through the Blackout Swap Protocol (#26) instead of a bare atomic swap.</summary>
        public bool Blackout = false;
    }

    /// <summary>
    /// #25 Rotating Hub. A hub portal's Portal connection cycles through an ordered ring of
    /// destinations on a fixed schedule, rewritten server-side well inside the ~50-200ms propagation
    /// window ForceSendZDO buys (AddForceSendZdos Insert(0)s at the head of a peer's sync list,
    /// :77263-77286; SendZDOToPeers2 services one peer per frame behind a 0.05s gate, :76837-76862).
    /// The transit-instant read is always atomic and race-free from a traveller's point of view
    /// (TeleportWorld.Teleport re-reads GetConnectionZDOID live, :143539; ZDOExtraData.SetConnection is
    /// one field write, :74951-74961) - the only real hazard is a player standing at the hub AT the
    /// rotation instant landing on the old destination, which is inherent to the design, not a bug.
    ///
    /// Ticks at a fixed 0.2s cadence (the catalog's own recommendation - cost is one dictionary lookup
    /// plus a ZNet.GetTimeSeconds() comparison per hub, trivial) independent of the domain's slower
    /// config-exposed intervals, since a hub's whole point is a crisp, on-schedule flip.
    /// </summary>
    public static class RoutingRotatingHubEngine
    {
        private const float TickInterval = 0.2f;
        private static float _timer;
        private static int _lastRegistryVersion = -1;
        private static List<RoutingHubDefinition> _hubs = new List<RoutingHubDefinition>();

        // NEEDS NEW KEY: tpl_routing_hubrotationindex, purpose: HubState.Index below is RAM-only, so a
        // server restart resets every hub back to destination 0 instead of resuming its rotation where
        // it left off. Not persisting is safe (no corruption, just a rotation-position reset) but a
        // future wave could add a per-portal ZDO key (written via PortalOwnership.ClaimAndWrite like
        // everything else) to survive restarts.
        private sealed class HubState
        {
            public int Index;
            public double NextRotateAt;
            public bool Initialized;
            public readonly Dictionary<int, ZDOID> FabricatedAnchors = new Dictionary<int, ZDOID>();
        }

        private static readonly Dictionary<string, HubState> _state = new Dictionary<string, HubState>();

        public static void OnUpdate(float dt)
        {
            if (RoutingConfig.TakeoverEnabled?.Value == false || ZNet.instance == null || ZDOMan.instance == null)
            {
                return;
            }
            _timer += dt;
            if (_timer < TickInterval)
            {
                return;
            }
            _timer = 0f;

            if (RoutingManagedPortalRegistry.Version != _lastRegistryVersion)
            {
                _lastRegistryVersion = RoutingManagedPortalRegistry.Version;
                _hubs = RoutingManagedPortalRegistry.Section<RoutingHubDefinition>("hubs");
            }
            if (_hubs.Count == 0)
            {
                return;
            }

            double now = ZNet.instance.GetTimeSeconds();
            foreach (RoutingHubDefinition hub in _hubs)
            {
                if (hub.Destinations.Count == 0 || !RoutingPairingAuthorityEngine.TryClaim(hub.Position, $"hub:{hub.Name}"))
                {
                    continue;
                }
                ZDO? hubZdo = RoutingWriteOps.ResolveLive(hub.Position);
                if (hubZdo == null)
                {
                    continue;
                }

                HubState state = GetOrInitState(hub, now);

                if (hub.Blackout)
                {
                    RoutingBlackoutSwapEngine.Tick(hub, hubZdo, now,
                        advanceToNext: () => Advance(hub, state),
                        publish: (tag, dest) => RoutingPairingAuthorityEngine.Publish(hubZdo.m_uid, tag, dest));
                    continue;
                }

                if (now < state.NextRotateAt)
                {
                    // Keep republishing the CURRENT desired state every tick so RoutingPairingAuthorityEngine's
                    // postfix/safety-net correction can defend it against vanilla's reconciler between rotations.
                    RoutingPairingAuthorityEngine.Publish(hubZdo.m_uid, hub.Tag, CurrentDestinationId(hub, state));
                    continue;
                }

                ZDOID? destId = Advance(hub, state);
                state.NextRotateAt = now + EffectiveRotateSeconds(hub);
                if (!destId.HasValue)
                {
                    continue;
                }

                RoutingWriteOps.Reassert(hubZdo, hub.Tag, destId.Value);
                RoutingPairingAuthorityEngine.Publish(hubZdo.m_uid, hub.Tag, destId.Value);
                RoutingWriteOps.PrewarmToPeersNear(hubZdo.GetPosition(), RoutingConfig.HubPrewarmRadius?.Value ?? 200f, destId.Value);
            }
        }

        private static HubState GetOrInitState(RoutingHubDefinition hub, double now)
        {
            if (!_state.TryGetValue(hub.Name, out HubState? state))
            {
                state = new HubState();
                _state[hub.Name] = state;
            }
            if (!state.Initialized)
            {
                state.NextRotateAt = now + EffectiveRotateSeconds(hub);
                state.Initialized = true;
            }
            return state;
        }

        internal static float EffectiveRotateSeconds(RoutingHubDefinition hub) =>
            hub.RotateSeconds > 0f ? hub.RotateSeconds : (RoutingConfig.HubDefaultRotateSeconds?.Value ?? 60f);

        private static ZDOID? CurrentDestinationId(RoutingHubDefinition hub, HubState state)
        {
            int idx = ((state.Index - 1) % hub.Destinations.Count + hub.Destinations.Count) % hub.Destinations.Count;
            return ResolveOrFabricate(hub, state, idx)?.m_uid;
        }

        /// <summary>Advances to the next destination and returns its live ZDOID (fabricating a void anchor first if needed and permitted).</summary>
        private static ZDOID? Advance(RoutingHubDefinition hub, HubState stateObj)
        {
            int idx = stateObj.Index % hub.Destinations.Count;
            stateObj.Index = (stateObj.Index + 1) % hub.Destinations.Count;
            return ResolveOrFabricate(hub, stateObj, idx)?.m_uid;
        }

        private static ZDO? ResolveOrFabricate(RoutingHubDefinition hub, HubState state, int destIndex)
        {
            RoutingPosition pos = hub.Destinations[destIndex];
            ZDO? live = RoutingWriteOps.ResolveLive(pos);
            if (live != null)
            {
                return live;
            }
            if (!hub.FabricateMissingDestinations)
            {
                return null;
            }
            if (state.FabricatedAnchors.TryGetValue(destIndex, out ZDOID existingId))
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
                state.FabricatedAnchors[destIndex] = fabricated.m_uid;
                // #230: a void anchor that sits inside this hub's own zone (a short-hop stop, not a
                // far-flung one) benefits from Prioritized sync - harmless no-op cost otherwise.
                RoutingSyncOrderKnobs.MarkVoidAnchorPrioritized(fabricated);
            }
            return fabricated;
        }
    }
}
