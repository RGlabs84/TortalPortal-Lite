using System.Collections.Generic;
using UnityEngine;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Hooks;

namespace TortalPortalLite.Subsystems.Topology
{
    /// <summary>Selection policy for FindRandomUnconnectedPortal's replacement - see RoutingConfig.AssignmentPolicy.</summary>
    public enum RoutingAssignmentPolicy
    {
        /// <summary>Vanilla's own uniformly-random choice - effectively "policy off".</summary>
        VanillaRandom,
        /// <summary>Minimise distance from the portal being paired.</summary>
        Nearest,
        /// <summary>Sort by rounded world position (restart-stable, unlike sorting by ZDOID) and take the first - a deterministic, reproducible pick rather than "oldest" in any real-time sense.</summary>
        Stable,
        /// <summary>Per-tag cursor advanced on every successful pairing, over a position-sorted candidate list so "cursor N" names the same physical portal call to call.</summary>
        RoundRobin,
        /// <summary>Prefer whichever candidate this engine paired least recently.</summary>
        LeastRecentlyUsed,
        /// <summary>Prefer whichever candidate the fewest OTHER portals in the same list currently point at.</summary>
        LoadBalanced
    }

    /// <summary>
    /// #31 Deterministic Assignment Replacement - "the lowest-risk, highest-value intervention in the
    /// whole space" per the catalog's own assessment. `Game.FindRandomUnconnectedPortal` (:100664-100679)
    /// is private, builds a candidate list (reference-inequality with `skip`, exact `s_tag` equality,
    /// unconnected, `!IsCurrentlyConnectingPortal`) and returns `list[UnityEngine.Random.Range(0,
    /// list.Count)]`. `Core/Hooks/FindRandomUnconnectedPortalHook` exists specifically so this engine can
    /// override the PICK while leaving vanilla's own two-phase commit, ownership handling and logging
    /// completely untouched - registering a handler, never a Harmony patch of its own.
    ///
    /// The candidate FILTER is rebuilt here verbatim from the decompile (not re-derived) - dropping the
    /// `IsCurrentlyConnectingPortal` check would double-pair a portal owned by an online peer within the
    /// same tick, exactly as the catalog warns.
    ///
    /// This also fixes vanilla's permanent-orphan behaviour as a side effect: with N&gt;=3 same-tag
    /// portals vanilla always stunts exactly one member forever, and WHICH one depends on
    /// Dictionary&lt;SectorIndex, List&lt;ZDO&gt;&gt; bucket order (arbitrary, changes across restarts).
    /// Every policy here except VanillaRandom makes that choice reproducible instead of arbitrary.
    /// </summary>
    public static class RoutingDeterministicAssignmentEngine
    {
        private static readonly Dictionary<string, int> _roundRobinCursor = new Dictionary<string, int>();
        private static readonly Dictionary<ZDOID, float> _lastChosenAt = new Dictionary<ZDOID, float>();

        public static void Initialize()
        {
            FindRandomUnconnectedPortalHook.Register(0, Handler);
        }

        private static bool Handler(List<ZDO> portals, ZDO skip, string tag, out ZDO result)
        {
            result = null!;
            RoutingAssignmentPolicy policy = RoutingConfig.AssignmentPolicy?.Value ?? RoutingAssignmentPolicy.Nearest;
            if (policy == RoutingAssignmentPolicy.VanillaRandom || Game.instance == null)
            {
                return false;
            }

            var candidates = new List<ZDO>();
            foreach (ZDO portal in portals)
            {
                if (portal != skip
                    && portal.GetString(ZDOVars.s_tag) == tag
                    && portal.GetConnectionZDOID(ZDOExtraData.ConnectionType.Portal) == ZDOID.None
                    && !Game.instance.IsCurrentlyConnectingPortal(portal))
                {
                    candidates.Add(portal);
                }
            }
            if (candidates.Count == 0)
            {
                return false;
            }

            ZDO chosen = policy switch
            {
                RoutingAssignmentPolicy.Nearest => PickNearest(candidates, skip),
                RoutingAssignmentPolicy.Stable => PickStable(candidates),
                RoutingAssignmentPolicy.RoundRobin => PickRoundRobin(candidates, tag),
                RoutingAssignmentPolicy.LeastRecentlyUsed => PickLeastRecentlyUsed(candidates),
                RoutingAssignmentPolicy.LoadBalanced => PickLoadBalanced(candidates, portals),
                _ => candidates[0],
            };

            _lastChosenAt[chosen.m_uid] = Time.time;
            result = chosen;
            return true;
        }

        private static ZDO PickNearest(List<ZDO> candidates, ZDO skip)
        {
            Vector3 origin = skip.GetPosition();
            ZDO best = candidates[0];
            float bestSqr = (best.GetPosition() - origin).sqrMagnitude;
            for (int i = 1; i < candidates.Count; i++)
            {
                float sqr = (candidates[i].GetPosition() - origin).sqrMagnitude;
                if (sqr < bestSqr)
                {
                    bestSqr = sqr;
                    best = candidates[i];
                }
            }
            return best;
        }

        private static ZDO PickStable(List<ZDO> candidates)
        {
            List<ZDO> sorted = SortedByPosition(candidates);
            return sorted[0];
        }

        private static ZDO PickRoundRobin(List<ZDO> candidates, string tag)
        {
            List<ZDO> sorted = SortedByPosition(candidates);
            _roundRobinCursor.TryGetValue(tag, out int cursor);
            ZDO chosen = sorted[cursor % sorted.Count];
            _roundRobinCursor[tag] = (cursor + 1) % sorted.Count;
            return chosen;
        }

        /// <summary>
        /// "Least recently used" here means least recently CHOSEN BY THIS POLICY, not the catalog's
        /// fuller vision of a real per-portal last-TRANSIT timestamp fed by a straddle detector watching
        /// every portal in the world - that would need an always-on global transit sweep over every
        /// managed AND unmanaged portal, a materially bigger always-on cost than this domain's other
        /// transit-detection uses (Queue Dispatch / Self-Disconnect only watch their OWN declared gates).
        /// Load-spreading intent is preserved; the exact recency signal is a documented simplification.
        /// </summary>
        private static ZDO PickLeastRecentlyUsed(List<ZDO> candidates)
        {
            ZDO best = candidates[0];
            float bestTime = _lastChosenAt.TryGetValue(best.m_uid, out float t0) ? t0 : float.MinValue;
            for (int i = 1; i < candidates.Count; i++)
            {
                float time = _lastChosenAt.TryGetValue(candidates[i].m_uid, out float t) ? t : float.MinValue;
                if (time < bestTime)
                {
                    bestTime = time;
                    best = candidates[i];
                }
            }
            return best;
        }

        private static ZDO PickLoadBalanced(List<ZDO> candidates, List<ZDO> allPortalsInPass)
        {
            ZDO best = candidates[0];
            int bestCount = CountPointingAt(best.m_uid, allPortalsInPass);
            for (int i = 1; i < candidates.Count; i++)
            {
                int count = CountPointingAt(candidates[i].m_uid, allPortalsInPass);
                if (count < bestCount)
                {
                    bestCount = count;
                    best = candidates[i];
                }
            }
            return best;
        }

        private static int CountPointingAt(ZDOID target, List<ZDO> allPortalsInPass)
        {
            int count = 0;
            foreach (ZDO portal in allPortalsInPass)
            {
                if (portal.GetConnectionZDOID(ZDOExtraData.ConnectionType.Portal) == target)
                {
                    count++;
                }
            }
            return count;
        }

        private static List<ZDO> SortedByPosition(List<ZDO> candidates)
        {
            var sorted = new List<ZDO>(candidates);
            sorted.Sort((a, b) =>
            {
                Vector3 pa = a.GetPosition();
                Vector3 pb = b.GetPosition();
                int cmp = pa.x.CompareTo(pb.x);
                if (cmp != 0) return cmp;
                cmp = pa.y.CompareTo(pb.y);
                if (cmp != 0) return cmp;
                return pa.z.CompareTo(pb.z);
            });
            return sorted;
        }
    }
}
