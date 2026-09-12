using System.Collections.Generic;
using UnityEngine;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;
using TortalPortalLite.Subsystems.Foundations;

namespace TortalPortalLite.Subsystems.Topology
{
    /// <summary>Admin declaration for one Self-Disconnecting portal (routing.json section "selfDisconnectPortals").</summary>
    public sealed class RoutingSelfDisconnectDefinition
    {
        public string Name = "";
        public RoutingPosition Position = new RoutingPosition();

        /// <summary>"cooldown" (reconnects after CooldownSeconds) or "oneshot" (stays disconnected, or is destroyed if Destroy is set).</summary>
        public string Mode = "cooldown";
        public float CooldownSeconds = 30f;
        public bool Destroy = false;

        /// <summary>Arm/inner-commit speculative blackout - shrinks the ~2s detection-latency hole the catalog quantifies (Player.UpdateTeleport doesn't move the transform until t&gt;2s) down to the much shorter time between committing and actually transiting.</summary>
        public bool SpeculativeBlackout = true;
    }

    /// <summary>
    /// #35 Self-Disconnecting Portals (One-Shot and Cooldown). Detection is unavoidably after-the-fact
    /// (RoutingTransitDetector's position-straddle heuristic) and has a quantified blind window: a
    /// transiting player's character ZDO position does not move until `Player.UpdateTeleport`'s
    /// `m_teleportTimer &gt; 2f` (:15337), so the earliest ANY position-based detector can confirm a
    /// transit is ~2.0-2.2s after it started - during which a second player can walk through the still
    /// live portal. This engine implements the catalog's own mitigation: pair after-the-fact confirmation
    /// with the shared RoutingApproachWatcher's inner-commit-radius signal (~3m default) to blacken the
    /// link SPECULATIVELY the instant a player is close enough to be considered "committed", cutting the
    /// exposed hole from ~2s to a few hundred milliseconds. A speculative blackout that turns out to be a
    /// false alarm (the player turned away) is restored after a short grace period with no confirmed
    /// transit.
    ///
    /// COOLDOWN: on confirmed transit (or immediately on speculative commit), SetConnection(Portal,
    /// None) + tag countdown; restores the ORIGINAL connection/tag once CooldownSeconds elapses. The
    /// departing player is unaffected either way - Player.UpdateTeleport already snapshotted their
    /// target the moment TeleportTo was accepted (:15337-15348).
    /// ONE-SHOT: on confirmed transit, either leaves the connection None permanently (default - a
    /// player's own built portal is never destroyed unless the admin opts in) or, if Destroy is set,
    /// physically removes the ZDO via the same owner-gated primitive #24 uses
    /// (ZDOMan.DestroyZDO is a silent no-op unless the server owns the ZDO first, :76929-76935).
    /// </summary>
    public static class RoutingSelfDisconnectEngine
    {
        private static float _timer;
        private static int _lastRegistryVersion = -1;
        private static List<RoutingSelfDisconnectDefinition> _defs = new List<RoutingSelfDisconnectDefinition>();
        private static readonly Dictionary<string, RoutingSelfDisconnectDefinition> _byName = new Dictionary<string, RoutingSelfDisconnectDefinition>();
        private static readonly RoutingApproachWatcher _watcher = new RoutingApproachWatcher();
        private static readonly RoutingTransitDetector _transitDetector = new RoutingTransitDetector();

        // NEEDS NEW KEY: tpl_routing_oneshotconsumed (bool) and tpl_routing_cooldownreconnectat
        // (long, DateTime.UtcNow.Ticks-style server clock, never a client-supplied timestamp), purpose:
        // GateState below is RAM-only, so a restart un-consumes a one-shot portal and resets any
        // in-progress cooldown early. Not persisting is a real limitation for "permanent" one-shot
        // semantics specifically (a restart brings a supposedly-spent gate back), flagged rather than
        // silently accepted.
        private sealed class GateState
        {
            public bool Blacked;
            public bool Confirmed;
            public double BlackedAt;
            public double ReconnectAt;
            public ZDOID SavedConnection = ZDOID.None;
            public string? SavedTag;
            public bool Consumed;
        }

        private static readonly Dictionary<string, GateState> _state = new Dictionary<string, GateState>();

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
            if (_defs.Count == 0)
            {
                return;
            }

            double now = ZNet.instance.GetTimeSeconds();

            var gatePositions = new List<(string, Vector3)>(_defs.Count);
            foreach (RoutingSelfDisconnectDefinition def in _defs)
            {
                if (RoutingPairingAuthorityEngine.TryClaim(def.Position, $"selfdisconnect:{def.Name}"))
                {
                    gatePositions.Add((def.Name, def.Position.ToVector3()));
                }
            }
            _watcher.Poll(gatePositions, RoutingConfig.ArmRadius?.Value ?? 12f, RoutingConfig.DisarmHysteresis?.Value ?? 3f,
                onCommit: HandleCommit, innerCommitRadius: RoutingConfig.InnerCommitRadius?.Value ?? 3f);

            foreach (RoutingTransitDetector.Transit transit in _transitDetector.Poll(40f))
            {
                foreach (RoutingSelfDisconnectDefinition def in _defs)
                {
                    if (PortalCensus.TryGetByPosition(def.Position.ToVector3(), out PortalRecord record) && record.Uid == transit.From.Uid)
                    {
                        HandleConfirmedTransit(def, now);
                    }
                }
            }

            // Steady-state per-gate bookkeeping: expire speculative blackouts with no confirmation, and reconnect cooldowns whose timer elapsed.
            foreach (RoutingSelfDisconnectDefinition def in _defs)
            {
                if (!_state.TryGetValue(def.Name, out GateState? state))
                {
                    continue;
                }
                ZDO? zdo = RoutingWriteOps.ResolveLive(def.Position);
                if (zdo == null)
                {
                    continue;
                }

                if (state.Blacked && !state.Confirmed && now - state.BlackedAt > 3f)
                {
                    // False alarm - restore what was there before the speculative blackout.
                    RoutingWriteOps.Reassert(zdo, state.SavedTag, state.SavedConnection);
                    RoutingPairingAuthorityEngine.Publish(zdo.m_uid, state.SavedTag, state.SavedConnection);
                    state.Blacked = false;
                }
                else if (state.Confirmed && def.Mode == "cooldown" && now >= state.ReconnectAt)
                {
                    RoutingWriteOps.Reassert(zdo, state.SavedTag, state.SavedConnection);
                    RoutingPairingAuthorityEngine.Publish(zdo.m_uid, state.SavedTag, state.SavedConnection);
                    state.Blacked = false;
                    state.Confirmed = false;
                }
                else if (state.Blacked && def.Mode == "cooldown")
                {
                    string tag = BuildCooldownTag(state.SavedTag, now, state.ReconnectAt);
                    RoutingWriteOps.Reassert(zdo, tag, ZDOID.None);
                    RoutingPairingAuthorityEngine.Publish(zdo.m_uid, tag, ZDOID.None);
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
            _defs = RoutingManagedPortalRegistry.Section<RoutingSelfDisconnectDefinition>("selfDisconnectPortals");
            _byName.Clear();
            foreach (RoutingSelfDisconnectDefinition def in _defs)
            {
                _byName[def.Name] = def;
            }
        }

        private static void HandleCommit(string gateName, ConnectedCharacter character)
        {
            if (!_byName.TryGetValue(gateName, out RoutingSelfDisconnectDefinition? def) || !def.SpeculativeBlackout || ZNet.instance == null)
            {
                return;
            }
            ZDO? zdo = RoutingWriteOps.ResolveLive(def.Position);
            if (zdo == null)
            {
                return;
            }
            if (!_state.TryGetValue(gateName, out GateState? state))
            {
                state = new GateState();
                _state[gateName] = state;
            }
            if (state.Blacked || state.Consumed)
            {
                return;
            }

            state.SavedConnection = zdo.GetConnectionZDOID(ZDOExtraData.ConnectionType.Portal);
            state.SavedTag = zdo.GetString(ZDOVars.s_tag, "");
            state.Blacked = true;
            state.Confirmed = false;
            state.BlackedAt = ZNet.instance.GetTimeSeconds();

            RoutingWriteOps.Disconnect(zdo);
            RoutingPairingAuthorityEngine.Publish(zdo.m_uid, state.SavedTag, ZDOID.None);
        }

        private static void HandleConfirmedTransit(RoutingSelfDisconnectDefinition def, double now)
        {
            ZDO? zdo = RoutingWriteOps.ResolveLive(def.Position);
            if (zdo == null)
            {
                return;
            }
            if (!_state.TryGetValue(def.Name, out GateState? state))
            {
                state = new GateState();
                _state[def.Name] = state;
            }
            if (!state.Blacked)
            {
                // No speculative blackout ran (disabled, or the ~2s detection floor caught up first) -
                // capture current state now, immediately before applying the real effect.
                state.SavedConnection = zdo.GetConnectionZDOID(ZDOExtraData.ConnectionType.Portal);
                state.SavedTag = zdo.GetString(ZDOVars.s_tag, "");
            }
            state.Blacked = true;
            state.Confirmed = true;

            if (def.Mode == "oneshot")
            {
                state.Consumed = true;
                if (def.Destroy)
                {
                    RoutingPhantomAnchorEngine.Destroy(zdo);
                }
                else
                {
                    RoutingWriteOps.Disconnect(zdo, state.SavedTag);
                    RoutingPairingAuthorityEngine.Publish(zdo.m_uid, state.SavedTag, ZDOID.None);
                }
            }
            else
            {
                state.ReconnectAt = now + def.CooldownSeconds;
            }
        }

        private static string BuildCooldownTag(string? baseTag, double now, double reconnectAt)
        {
            int secs = System.Math.Max(0, (int)System.Math.Ceiling(reconnectAt - now));
            string tag = $"Cool {secs}s";
            return tag.Length <= 10 ? tag : tag.Substring(0, 10);
        }
    }
}
