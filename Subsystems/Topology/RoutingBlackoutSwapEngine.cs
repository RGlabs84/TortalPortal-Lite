using System;
using System.Collections.Generic;
using TortalPortalLite.Core;

namespace TortalPortalLite.Subsystems.Topology
{
    /// <summary>
    /// #26 Blackout Swap Protocol - a three-phase rotation (announce -&gt; disconnect -&gt; re-point)
    /// layered on top of a Rotating Hub (#25) so a swap becomes a legible, signposted event on a 100%
    /// vanilla client instead of an invisible instant flip. Driven entirely by
    /// <see cref="RoutingRotatingHubEngine"/> (one call to <see cref="Tick"/> per hub per its 0.2s
    /// cadence) rather than owning its own timer loop, so a hub is managed by exactly one write path
    /// regardless of whether Blackout is enabled for it.
    ///
    /// Phase 1 ANNOUNCE (default 30s): writes an admin-visible countdown into s_tag - `GetHoverText`
    /// renders it live every frame a player hovers (:143453-143463) with no server-side length cap (the
    /// 10-char limit is client-UI-only, TextInput.RequestText(..., 10) at :143482). Connection is left
    /// untouched.
    /// Phase 2 BLACKOUT (default 3s): connection set to ZDOID.None. Within one client UpdatePortal poll
    /// (&lt;=0.5s, :143491-143509) the glow dies and hover flips to "$piece_portal_unconnected"
    /// (:143461); a walk-through during this window hits TeleportWorld.Teleport's gate 1 and returns
    /// silently (:143519-143522) - safely inert, never mis-routed.
    /// Phase 3 REPOINT: writes the new connection (+ base tag). The client's next UpdatePortal sees
    /// HaveTarget() flip true and fires the vanilla m_connected VFX/SFX (:143496-143507) - the normal
    /// "portal just connected" cue, for free. This phase then holds (re-asserting every tick, so
    /// RoutingPairingAuthorityEngine's correction has a stable target to defend) for the hub's own
    /// configured RotateSeconds before the next announce cycle begins.
    ///
    /// Every phase calls the supplied <c>publish</c> delegate with its OWN current desired state so
    /// RoutingPairingAuthorityEngine's postfix/safety-net keeps defending exactly what this protocol
    /// intends RIGHT NOW (e.g. "None" during blackout is a real desired state, not an absence of one).
    /// </summary>
    public static class RoutingBlackoutSwapEngine
    {
        private enum Phase { Idle, Announce, Blackout, Repointed }

        private sealed class State
        {
            public Phase CurrentPhase = Phase.Idle;
            public double PhaseEndsAt;
            public ZDOID CurrentDestination = ZDOID.None;
            public bool HasDestination;
        }

        private static readonly Dictionary<string, State> _state = new Dictionary<string, State>();

        /// <summary>
        /// Advances one hub's blackout state machine by one tick. <paramref name="advanceToNext"/> is
        /// called exactly once per cycle, at the blackout-&gt;repoint transition, and must return the
        /// next destination's live ZDOID (fabricating one if necessary) or null if none could be
        /// resolved (in which case the hub simply stays dark one more cycle).
        /// </summary>
        public static void Tick(RoutingHubDefinition hub, ZDO hubZdo, double now, Func<ZDOID?> advanceToNext, Action<string?, ZDOID?> publish)
        {
            if (!_state.TryGetValue(hub.Name, out State? state))
            {
                state = new State();
                _state[hub.Name] = state;
            }

            switch (state.CurrentPhase)
            {
                case Phase.Idle:
                    StartAnnounce(hub, state, now);
                    break;

                case Phase.Announce:
                {
                    float remaining = (float)(state.PhaseEndsAt - now);
                    string announceTag = BuildAnnounceTag(hub, remaining);
                    RoutingWriteOps.Reassert(hubZdo, announceTag, null);
                    publish(announceTag, null);
                    if (now >= state.PhaseEndsAt)
                    {
                        state.CurrentPhase = Phase.Blackout;
                        state.PhaseEndsAt = now + (RoutingConfig.BlackoutDwellSeconds?.Value ?? 3f);
                    }
                    break;
                }

                case Phase.Blackout:
                    RoutingWriteOps.Reassert(hubZdo, hub.Tag, ZDOID.None);
                    publish(hub.Tag, ZDOID.None);
                    if (now >= state.PhaseEndsAt)
                    {
                        ZDOID? next = advanceToNext();
                        if (next.HasValue)
                        {
                            state.CurrentDestination = next.Value;
                            state.HasDestination = true;
                        }
                        state.CurrentPhase = Phase.Repointed;
                        state.PhaseEndsAt = now + RoutingRotatingHubEngine.EffectiveRotateSeconds(hub);
                    }
                    break;

                case Phase.Repointed:
                    if (state.HasDestination)
                    {
                        RoutingWriteOps.Reassert(hubZdo, hub.Tag, state.CurrentDestination);
                        publish(hub.Tag, state.CurrentDestination);
                    }
                    if (now >= state.PhaseEndsAt)
                    {
                        state.CurrentPhase = Phase.Idle;
                    }
                    break;
            }
        }

        private static void StartAnnounce(RoutingHubDefinition hub, State state, double now)
        {
            float announceSeconds = RoutingConfig.BlackoutAnnounceSeconds?.Value ?? 30f;
            if (announceSeconds <= 0f)
            {
                state.CurrentPhase = Phase.Blackout;
                state.PhaseEndsAt = now + (RoutingConfig.BlackoutDwellSeconds?.Value ?? 3f);
                return;
            }
            state.CurrentPhase = Phase.Announce;
            state.PhaseEndsAt = now + announceSeconds;
        }

        /// <summary>
        /// Built to stay well inside the 10-char CLIENT UI cap even though the server itself has no
        /// length check (TextInput.RequestText's characterLimit, :143482, is what actually truncates a
        /// player-reopened tag dialog - the catalog's own documented failure mode). "12s" is 3 chars,
        /// leaving 7 for a short name.
        /// </summary>
        private static string BuildAnnounceTag(RoutingHubDefinition hub, float remainingSeconds)
        {
            int secs = Math.Max(0, (int)Math.Ceiling(remainingSeconds));
            string baseName = string.IsNullOrEmpty(hub.Tag) ? hub.Name : hub.Tag!;
            if (baseName.Length > 6)
            {
                baseName = baseName.Substring(0, 6);
            }
            string tag = $"{baseName} {secs}s";
            return tag.Length <= 10 ? tag : tag.Substring(0, 10);
        }
    }
}
