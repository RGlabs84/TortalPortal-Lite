using System.Collections.Generic;
using UnityEngine;
using TortalPortalLite.Core.Data;

namespace TortalPortalLite.Subsystems.Topology
{
    /// <summary>
    /// Shared "is a connected character closing on this gate" primitive behind catalog #32
    /// (Approach-Triggered Just-In-Time Routing) and every option built on it (#34 Queue Dispatch, #35
    /// Self-Disconnecting Portals' speculative blackout, #226 Character-State Predicates, #227 Parked
    /// Terminal). Each consumer owns its own instance (armed-state must not be shared between unrelated
    /// gate sets), keyed by an arbitrary caller-chosen string per gate so callers can use a stable
    /// identity (a rounded-position string, a declared name) rather than an index that shifts when
    /// routing.json reloads.
    ///
    /// Grounded in #32's own timing budget: character ZDO positions are fresh to ~50-150ms
    /// (ZSyncTransform.OwnerSync writes every physics tick; client-&gt;server delivery is
    /// distance-independent, drained every 0.05s) - polling at RoutingConfig.ApproachPollSeconds (default
    /// 0.1s) leaves ample margin at the default 12m arm radius even for a sprinting player.
    /// </summary>
    public sealed class RoutingApproachWatcher
    {
        public delegate void GateCharacterEvent(string gateKey, ConnectedCharacter character);

        private readonly Dictionary<string, HashSet<long>> _armed = new Dictionary<string, HashSet<long>>();
        private readonly Dictionary<string, HashSet<long>> _committed = new Dictionary<string, HashSet<long>>();

        /// <summary>
        /// One poll pass over every connected character against every supplied gate. Fires onArm the
        /// tick a player's distance first drops to/under armRadius, onDisarm the tick it rises back
        /// past armRadius+disarmHysteresis (dead/absent characters are treated as an implicit disarm),
        /// and onCommit (if given) once, the first time an already-armed player crosses inside
        /// innerCommitRadius (catalog #35's "committed" signal for a speculative blackout).
        /// </summary>
        public void Poll(
            IEnumerable<(string key, Vector3 pos)> gates,
            float armRadius,
            float disarmHysteresis,
            GateCharacterEvent? onArm = null,
            GateCharacterEvent? onDisarm = null,
            GateCharacterEvent? onCommit = null,
            float innerCommitRadius = 0f)
        {
            List<ConnectedCharacter> characters = ConnectedCharacters.All();
            float armSqr = armRadius * armRadius;
            float disarmRadius = armRadius + disarmHysteresis;
            float disarmSqr = disarmRadius * disarmRadius;
            float commitSqr = innerCommitRadius * innerCommitRadius;

            var seenPlayerIds = new HashSet<long>();
            var seenGateKeys = new HashSet<string>();

            foreach ((string key, Vector3 pos) in gates)
            {
                seenGateKeys.Add(key);
                if (!_armed.TryGetValue(key, out HashSet<long>? armedSet))
                {
                    armedSet = new HashSet<long>();
                    _armed[key] = armedSet;
                }
                HashSet<long>? committedSet = null;
                if (onCommit != null && !_committed.TryGetValue(key, out committedSet))
                {
                    committedSet = new HashSet<long>();
                    _committed[key] = committedSet;
                }

                foreach (ConnectedCharacter character in characters)
                {
                    long playerId = character.PlayerId;
                    if (playerId == 0 || character.Zdo.GetBool(ZDOVars.s_dead))
                    {
                        continue;
                    }
                    seenPlayerIds.Add(playerId);

                    float sqrDist = (character.Position - pos).sqrMagnitude;
                    bool wasArmed = armedSet.Contains(playerId);

                    if (!wasArmed && sqrDist <= armSqr)
                    {
                        armedSet.Add(playerId);
                        onArm?.Invoke(key, character);
                        wasArmed = true;
                    }
                    else if (wasArmed && sqrDist > disarmSqr)
                    {
                        armedSet.Remove(playerId);
                        committedSet?.Remove(playerId);
                        onDisarm?.Invoke(key, character);
                        wasArmed = false;
                    }

                    if (wasArmed && committedSet != null && !committedSet.Contains(playerId) && sqrDist <= commitSqr)
                    {
                        committedSet.Add(playerId);
                        onCommit!(key, character);
                    }
                }
            }

            PruneDisconnected(seenPlayerIds);
            PruneStaleGates(seenGateKeys);
        }

        /// <summary>True if the given player is currently armed at the given gate key (as of the last Poll).</summary>
        public bool IsArmed(string gateKey, long playerId) => _armed.TryGetValue(gateKey, out HashSet<long>? set) && set.Contains(playerId);

        /// <summary>Number of distinct players currently armed at the given gate key - the shared-portal contention signal (#32, #34).</summary>
        public int ArmedCount(string gateKey) => _armed.TryGetValue(gateKey, out HashSet<long>? set) ? set.Count : 0;

        private void PruneDisconnected(HashSet<long> stillConnected)
        {
            foreach (HashSet<long> set in _armed.Values)
            {
                set.RemoveWhere(id => !stillConnected.Contains(id));
            }
            foreach (HashSet<long> set in _committed.Values)
            {
                set.RemoveWhere(id => !stillConnected.Contains(id));
            }
        }

        private void PruneStaleGates(HashSet<string> stillDeclared)
        {
            PruneMissing(_armed, stillDeclared);
            PruneMissing(_committed, stillDeclared);
        }

        private static void PruneMissing(Dictionary<string, HashSet<long>> map, HashSet<string> stillDeclared)
        {
            List<string>? toRemove = null;
            foreach (string key in map.Keys)
            {
                if (!stillDeclared.Contains(key))
                {
                    (toRemove ??= new List<string>()).Add(key);
                }
            }
            if (toRemove == null)
            {
                return;
            }
            foreach (string key in toRemove)
            {
                map.Remove(key);
            }
        }
    }
}
