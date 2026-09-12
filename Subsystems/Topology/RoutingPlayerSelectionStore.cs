using System.Collections.Generic;

namespace TortalPortalLite.Subsystems.Topology
{
    /// <summary>
    /// Shared per-(gate, player) destination-selection store - the hinge between #36 Player-Requested
    /// Routing (HOW a player picks a destination: map ping / emote cycle / item stand) and every
    /// just-in-time consumer that WRITES the shared portal ZDO right before that specific player uses
    /// it (#32 Approach-Triggered JIT, #33 Per-Player Terminals, #227 Parked Terminal). Runtime-only by
    /// design - a player re-picks in a few seconds after a restart, which is cheaper and less fragile
    /// than persisting per-player state into a new ZDO key this wave is not authorised to add
    /// (PortalKeys.cs is frozen mid-wave - see its own doc comment).
    /// </summary>
    public static class RoutingPlayerSelectionStore
    {
        private static readonly Dictionary<(string gate, long playerId), RoutingPosition> _selections = new Dictionary<(string, long), RoutingPosition>();
        private static readonly Dictionary<(string gate, long playerId), int> _cycleIndex = new Dictionary<(string, long), int>();

        public static void SetSelection(string gate, long playerId, RoutingPosition destination)
        {
            _selections[(gate, playerId)] = destination;
        }

        public static bool TryGetSelection(string gate, long playerId, out RoutingPosition destination)
        {
            return _selections.TryGetValue((gate, playerId), out destination!);
        }

        public static void ClearSelection(string gate, long playerId)
        {
            _selections.Remove((gate, playerId));
        }

        /// <summary>Advances (and returns) a 0-based cycle index for a gate/player pair, wrapping at <paramref name="count"/> - the "/wave again cycles to the next destination" gesture.</summary>
        public static int AdvanceCycle(string gate, long playerId, int count)
        {
            if (count <= 0)
            {
                return 0;
            }
            var key = (gate, playerId);
            _cycleIndex.TryGetValue(key, out int current);
            int next = (current + 1) % count;
            _cycleIndex[key] = next;
            return next;
        }

        public static int CurrentCycle(string gate, long playerId, int count)
        {
            if (count <= 0)
            {
                return 0;
            }
            return _cycleIndex.TryGetValue((gate, playerId), out int current) ? current % count : 0;
        }
    }
}
