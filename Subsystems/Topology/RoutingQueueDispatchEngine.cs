using System;
using System.Collections.Generic;
using UnityEngine;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;
using TortalPortalLite.Subsystems.Foundations;

namespace TortalPortalLite.Subsystems.Topology
{
    /// <summary>Admin declaration for one Queue Dispatch gate (routing.json section "queueDispatchers").</summary>
    public sealed class RoutingQueueDispatcherDefinition
    {
        public string Name = "";
        public RoutingPosition Position = new RoutingPosition();
        public List<RoutingPosition> Destinations = new List<RoutingPosition>();
        public List<string>? DestinationNames;

        /// <summary>0 = RoutingConfig.ContentionLockSeconds.</summary>
        public float LockSeconds = 0f;
    }

    /// <summary>
    /// #34 Queue Dispatch - "the next player to arrive gets the next target", honestly implemented as
    /// arrival-detection plus a lock rather than a true queue, because the catalog's own analysis holds:
    /// TeleportWorld.Teleport's only caller is TeleportWorldTrigger.OnTriggerEnter, gated on
    /// Player.m_localPlayer == component (:143664-143682) - the server never learns a transit is about
    /// to happen, or that it happened, as an event. What IS built: the shared RoutingApproachWatcher
    /// detects arming (the same primitive #32 uses); on arming, if the gate is unlocked, this engine
    /// atomically claims it for that player, writes the next destination in the declared list, advances
    /// the cursor, and starts a lock (RoutingConfig.ContentionLockSeconds default, or LockSeconds
    /// override) - the NEXT arrival after the lock expires gets the following destination. The lock
    /// tag counts down ("LOCKED 4s") using the same idempotent Reassert every consumers of this domain
    /// already share.
    ///
    /// Confirmation (did the dispatch actually get used) is deliberately NOT gated on - the catalog is
    /// explicit that any such check is a heuristic (RoutingTransitDetector's position-straddle, itself a
    /// port of Wonderland's production PositionWatch.IsPortalTransit) and this engine only logs it,
    /// never uses it to decide whether to advance the cursor (a player who arms and walks away without
    /// transiting still consumes a slot, exactly as the catalog's own failure-mode list requires: "a
    /// player who arms and walks away consumes a queue slot unless the lock is released on leaving the
    /// arming radius" - left AS a documented trade-off here since releasing on disarm would let a player
    /// repeatedly arm/disarm to peek at every destination in the list for free).
    /// </summary>
    public static class RoutingQueueDispatchEngine
    {
        private static float _timer;
        private static int _lastRegistryVersion = -1;
        private static List<RoutingQueueDispatcherDefinition> _dispatchers = new List<RoutingQueueDispatcherDefinition>();
        private static readonly Dictionary<string, RoutingQueueDispatcherDefinition> _byName = new Dictionary<string, RoutingQueueDispatcherDefinition>();
        private static readonly RoutingApproachWatcher _watcher = new RoutingApproachWatcher();
        private static readonly RoutingTransitDetector _transitDetector = new RoutingTransitDetector();

        // NEEDS NEW KEY: tpl_routing_dispatchcursor, purpose: DispatchState.Cursor is RAM-only - a
        // restart resets a dispatcher back to its first destination instead of resuming its turn-taking
        // sequence. Harmless (still a valid, fair rotation, just restarted), documented rather than silent.
        private sealed class DispatchState
        {
            public int Cursor;
            public long LockOwner;
            public double LockExpiresAt;
        }

        private static readonly Dictionary<string, DispatchState> _state = new Dictionary<string, DispatchState>();

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
            if (_dispatchers.Count == 0)
            {
                return;
            }

            double now = ZNet.instance.GetTimeSeconds();

            // Keep every currently-locked gate's countdown tag fresh even between arm events.
            foreach (RoutingQueueDispatcherDefinition dispatcher in _dispatchers)
            {
                if (!_state.TryGetValue(dispatcher.Name, out DispatchState? state) || now >= state.LockExpiresAt)
                {
                    continue;
                }
                ZDO? zdo = RoutingWriteOps.ResolveLive(dispatcher.Position);
                if (zdo != null)
                {
                    string tag = BuildLockedTag(now, state.LockExpiresAt);
                    RoutingWriteOps.Reassert(zdo, tag, null);
                    RoutingPairingAuthorityEngine.Publish(zdo.m_uid, tag, null);
                }
            }

            var gatePositions = new List<(string, Vector3)>(_dispatchers.Count);
            foreach (RoutingQueueDispatcherDefinition dispatcher in _dispatchers)
            {
                if (dispatcher.Destinations.Count > 0 && RoutingPairingAuthorityEngine.TryClaim(dispatcher.Position, $"queue:{dispatcher.Name}"))
                {
                    gatePositions.Add((dispatcher.Name, dispatcher.Position.ToVector3()));
                }
            }
            _watcher.Poll(gatePositions, RoutingConfig.ArmRadius?.Value ?? 12f, RoutingConfig.DisarmHysteresis?.Value ?? 3f, onArm: HandleArm);

            foreach (RoutingTransitDetector.Transit transit in _transitDetector.Poll(40f))
            {
                foreach (RoutingQueueDispatcherDefinition dispatcher in _dispatchers)
                {
                    if (PortalCensus.TryGetByPosition(dispatcher.Position.ToVector3(), out PortalRecord record) && record.Uid == transit.From.Uid)
                    {
                        PortalDebug.LogInfo($"[RoutingQueueDispatchEngine] '{dispatcher.Name}' confirmed used by {transit.Character.Name}.");
                    }
                }
            }
        }

        private static void RefreshDeclarationsIfNeeded()
        {
            if (RoutingManagedPortalRegistry.Version == _lastRegistryVersion)
            {
                return;
            }
            _lastRegistryVersion = RoutingManagedPortalRegistry.Version;
            _dispatchers = RoutingManagedPortalRegistry.Section<RoutingQueueDispatcherDefinition>("queueDispatchers");
            _byName.Clear();
            foreach (RoutingQueueDispatcherDefinition dispatcher in _dispatchers)
            {
                _byName[dispatcher.Name] = dispatcher;
            }
        }

        private static void HandleArm(string gateName, ConnectedCharacter character)
        {
            if (!_byName.TryGetValue(gateName, out RoutingQueueDispatcherDefinition? dispatcher) || ZNet.instance == null)
            {
                return;
            }
            ZDO? zdo = RoutingWriteOps.ResolveLive(dispatcher.Position);
            if (zdo == null)
            {
                return;
            }

            double now = ZNet.instance.GetTimeSeconds();
            if (!_state.TryGetValue(dispatcher.Name, out DispatchState? state))
            {
                state = new DispatchState();
                _state[dispatcher.Name] = state;
            }

            bool locked = now < state.LockExpiresAt;
            if (locked && state.LockOwner != character.PlayerId)
            {
                // Busy - leave whatever the countdown tag already shows; nothing to write for this arrival.
                return;
            }
            if (locked && state.LockOwner == character.PlayerId)
            {
                // Same player still standing in the arm radius from their own dispatch - do not re-advance.
                return;
            }

            int index = state.Cursor % dispatcher.Destinations.Count;
            state.Cursor = (state.Cursor + 1) % dispatcher.Destinations.Count;
            RoutingPosition destPos = dispatcher.Destinations[index];
            ZDO? destZdo = RoutingWriteOps.ResolveLive(destPos);
            if (destZdo == null)
            {
                return;
            }

            float lockSeconds = dispatcher.LockSeconds > 0f ? dispatcher.LockSeconds : (RoutingConfig.ContentionLockSeconds?.Value ?? 6f);
            state.LockOwner = character.PlayerId;
            state.LockExpiresAt = now + lockSeconds;

            string? name = dispatcher.DestinationNames != null && index < dispatcher.DestinationNames.Count ? dispatcher.DestinationNames[index] : null;
            string tag = string.IsNullOrEmpty(name) ? BuildLockedTag(now, state.LockExpiresAt) : Truncate($"-> {name}");

            RoutingWriteOps.PrewarmToPeer(character.Peer.m_uid, destZdo.m_uid);
            RoutingWriteOps.Reassert(zdo, tag, destZdo.m_uid);
            RoutingPairingAuthorityEngine.Publish(zdo.m_uid, tag, destZdo.m_uid);
        }

        private static string BuildLockedTag(double now, double lockExpiresAt)
        {
            int secs = Math.Max(0, (int)Math.Ceiling(lockExpiresAt - now));
            return Truncate($"LOCKED {secs}s");
        }

        private static string Truncate(string s) => s.Length <= 10 ? s : s.Substring(0, 10);
    }
}
