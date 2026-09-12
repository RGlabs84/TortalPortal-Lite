using System.Collections.Generic;
using UnityEngine;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;

namespace TortalPortalLite.Subsystems.Topology
{
    /// <summary>Admin declaration for one Just-In-Time gate (routing.json section "jitGates").</summary>
    public sealed class RoutingJitGateDefinition
    {
        public string Name = "";
        public RoutingPosition Position = new RoutingPosition();

        /// <summary>Tag shown while the gate has no armed destination for anyone nearby.</summary>
        public string? IdleTag;
    }

    /// <summary>
    /// #32 Approach-Triggered Just-In-Time Routing. The transit itself is unhookable (no server-side
    /// pre-transit event exists anywhere), but the APPROACH is fully observable: the owning client's
    /// ZSyncTransform writes the character ZDO's position every physics tick, delivered to the server
    /// distance-independently on the same ~0.05s cadence every other client ZDO write uses
    /// (:87527-87540, :77241-77258). RoutingApproachWatcher polls that at RoutingConfig.ApproachPollSeconds
    /// (default 0.1s) against every declared gate position; on arming (default 12m), this engine looks up
    /// that PLAYER's own selected destination (RoutingPlayerSelectionStore, fed by #36) and writes it
    /// into the shared gate ZDO before the player physically reaches the trigger collider.
    ///
    /// Pre-warm is mandatory, not optional: TeleportWorld.TargetFound requires the traveller's client to
    /// already HOLD the destination ZDO or the first walk-through silently no-ops while RequestZDO
    /// round-trips (:143640-143656) - handled here via RoutingWriteOps.PrewarmToPeer the instant a gate
    /// arms for someone.
    ///
    /// CONTENTION is the fatal flaw the catalog names explicitly: two players inside the arm radius
    /// share one ZDOID field. First-claim-lock is implemented (RoutingConfig.ContentionLockSeconds) -
    /// the first arriver's selection wins for the lock duration; a second arriver is toasted or (if
    /// RoutingConfig.BlackoutOnContention) the gate is safely blacked out for both rather than
    /// mis-routing either. A player who arms and then turns away releases the claim on disarm
    /// (hysteresis-gated, see RoutingApproachWatcher) rather than leaving the gate mis-set.
    /// </summary>
    public static class RoutingApproachJitEngine
    {
        private static float _timer;
        private static int _lastRegistryVersion = -1;
        private static List<RoutingJitGateDefinition> _gates = new List<RoutingJitGateDefinition>();
        private static readonly Dictionary<string, RoutingJitGateDefinition> _byName = new Dictionary<string, RoutingJitGateDefinition>();
        private static readonly RoutingApproachWatcher _watcher = new RoutingApproachWatcher();

        private static readonly Dictionary<string, long> _lockOwner = new Dictionary<string, long>();
        private static readonly Dictionary<string, double> _lockExpiresAt = new Dictionary<string, double>();

        public static void OnUpdate(float dt)
        {
            if (RoutingConfig.TakeoverEnabled?.Value == false || ZDOMan.instance == null || ZNet.instance == null)
            {
                return;
            }
            _timer += dt;
            float interval = RoutingConfig.ApproachPollSeconds?.Value ?? 0.1f;
            if (_timer < interval)
            {
                return;
            }
            _timer = 0f;

            RefreshDeclarationsIfNeeded();
            if (_gates.Count == 0)
            {
                return;
            }

            var gatePositions = new List<(string, Vector3)>(_gates.Count);
            foreach (RoutingJitGateDefinition gate in _gates)
            {
                if (RoutingPairingAuthorityEngine.TryClaim(gate.Position, $"jit:{gate.Name}"))
                {
                    gatePositions.Add((gate.Name, gate.Position.ToVector3()));
                }
            }

            _watcher.Poll(
                gatePositions,
                RoutingConfig.ArmRadius?.Value ?? 12f,
                RoutingConfig.DisarmHysteresis?.Value ?? 3f,
                onArm: HandleArm,
                onDisarm: HandleDisarm);
        }

        private static void RefreshDeclarationsIfNeeded()
        {
            if (RoutingManagedPortalRegistry.Version == _lastRegistryVersion)
            {
                return;
            }
            _lastRegistryVersion = RoutingManagedPortalRegistry.Version;
            _gates = RoutingManagedPortalRegistry.Section<RoutingJitGateDefinition>("jitGates");
            _byName.Clear();
            foreach (RoutingJitGateDefinition gate in _gates)
            {
                _byName[gate.Name] = gate;
            }
        }

        private static void HandleArm(string gateName, ConnectedCharacter character)
        {
            if (!_byName.TryGetValue(gateName, out RoutingJitGateDefinition? gate))
            {
                return;
            }
            ZDO? gateZdo = RoutingWriteOps.ResolveLive(gate.Position);
            if (gateZdo == null || ZNet.instance == null)
            {
                return;
            }

            long playerId = character.PlayerId;
            double now = ZNet.instance.GetTimeSeconds();
            float lockSeconds = RoutingConfig.ContentionLockSeconds?.Value ?? 6f;

            bool locked = _lockExpiresAt.TryGetValue(gateName, out double expiresAt) && now < expiresAt;
            bool lockedByOther = locked && _lockOwner.TryGetValue(gateName, out long owner) && owner != playerId;
            if (lockedByOther)
            {
                if (RoutingConfig.BlackoutOnContention?.Value == true)
                {
                    RoutingWriteOps.Disconnect(gateZdo, gate.IdleTag);
                    RoutingPairingAuthorityEngine.Publish(gateZdo.m_uid, gate.IdleTag, ZDOID.None);
                }
                else
                {
                    PlayerNotify.Toast(character, "Gate is claimed - wait a moment");
                }
                return;
            }

            if (!RoutingPlayerSelectionStore.TryGetSelection(gateName, playerId, out RoutingPosition destPos))
            {
                // Nobody has picked a destination for this gate yet - leave it reading whatever it
                // currently does (idle/closed) rather than guess.
                return;
            }
            ZDO? destZdo = RoutingWriteOps.ResolveLive(destPos);
            if (destZdo == null)
            {
                return;
            }

            RoutingWriteOps.PrewarmToPeer(character.Peer.m_uid, destZdo.m_uid);
            RoutingWriteOps.Reassert(gateZdo, gate.IdleTag == null ? null : gate.IdleTag, destZdo.m_uid);
            RoutingPairingAuthorityEngine.Publish(gateZdo.m_uid, gate.IdleTag, destZdo.m_uid);

            _lockOwner[gateName] = playerId;
            _lockExpiresAt[gateName] = now + lockSeconds;
        }

        private static void HandleDisarm(string gateName, ConnectedCharacter character)
        {
            if (_lockOwner.TryGetValue(gateName, out long owner) && owner == character.PlayerId)
            {
                _lockOwner.Remove(gateName);
                _lockExpiresAt.Remove(gateName);
            }
        }
    }
}
