using System.Collections.Generic;
using UnityEngine;
using TortalPortalLite.Core.Data;
using TortalPortalLite.Subsystems.Foundations;

namespace TortalPortalLite.Subsystems.Topology
{
    /// <summary>
    /// Shared AFTER-THE-FACT transit detector behind #34 Queue Dispatch's confirmation step and #35
    /// Self-Disconnecting Portals' cooldown/one-shot trigger. There is no pre-transit event anywhere
    /// server-side - the only caller of TeleportWorld.Teleport is TeleportWorldTrigger.OnTriggerEnter,
    /// gated on Player.m_localPlayer == component (:143664-143682), which never runs on a dedicated
    /// server. What IS observable is a position sample straddling two known portals, adapted from
    /// Wonderland's production-tested Core/Security/PositionWatch.IsPortalTransit
    /// (/home/rohan/WubarrkCODING/SERVER SIDE ONLY/Wonderland/Subsystems/Security/PositionWatch.cs) -
    /// ported here against PortalCensus instead of a raw ZDOMan.GetPortals() walk (this mod already
    /// pays for that snapshot every second) and against the caller's own poll cadence instead of
    /// PositionWatch's 3s default, since #34 explicitly wants ~0.1s resolution.
    ///
    /// Deliberately a heuristic, not a signal: a player sprinting past two nearby portals without using
    /// either can false-positive. Callers that destroy state on a detected transit (#35's one-shot) are
    /// documented as accepting that risk; callers that only restart a cooldown (#35's cooldown variant)
    /// are unaffected by a false positive beyond an early recharge.
    /// </summary>
    public sealed class RoutingTransitDetector
    {
        public readonly struct Transit
        {
            public readonly ConnectedCharacter Character;
            public readonly PortalRecord From;
            public readonly PortalRecord To;
            public Transit(ConnectedCharacter character, PortalRecord from, PortalRecord to)
            {
                Character = character;
                From = from;
                To = to;
            }
        }

        private readonly Dictionary<long, Vector3> _lastPosition = new Dictionary<long, Vector3>();

        /// <summary>One poll pass over every connected character; returns every straddle-detected transit since the last call.</summary>
        public List<Transit> Poll(float straddleRadius)
        {
            var results = new List<Transit>();
            var seen = new HashSet<long>();

            foreach (ConnectedCharacter character in ConnectedCharacters.All())
            {
                long playerId = character.PlayerId;
                if (playerId == 0)
                {
                    continue;
                }
                seen.Add(playerId);

                Vector3 pos = character.Position;
                if (_lastPosition.TryGetValue(playerId, out Vector3 last) && !character.Zdo.GetBool(ZDOVars.s_dead))
                {
                    if (TryFindStraddle(last, pos, straddleRadius, out PortalRecord from, out PortalRecord to))
                    {
                        results.Add(new Transit(character, from, to));
                    }
                }
                _lastPosition[playerId] = pos;
            }

            if (_lastPosition.Count > seen.Count)
            {
                var stale = new List<long>();
                foreach (long id in _lastPosition.Keys)
                {
                    if (!seen.Contains(id))
                    {
                        stale.Add(id);
                    }
                }
                foreach (long id in stale)
                {
                    _lastPosition.Remove(id);
                }
            }

            return results;
        }

        private static bool TryFindStraddle(Vector3 fromPos, Vector3 toPos, float radius, out PortalRecord from, out PortalRecord to)
        {
            from = default;
            to = default;
            bool foundFrom = false;
            bool foundTo = false;
            float radiusSqr = radius * radius;

            foreach (PortalRecord record in PortalCensus.Latest)
            {
                if (!foundFrom && (record.Position - fromPos).sqrMagnitude <= radiusSqr)
                {
                    from = record;
                    foundFrom = true;
                }
                if (!foundTo && (record.Position - toPos).sqrMagnitude <= radiusSqr)
                {
                    to = record;
                    foundTo = true;
                }
                if (foundFrom && foundTo)
                {
                    break;
                }
            }
            return foundFrom && foundTo;
        }
    }
}
