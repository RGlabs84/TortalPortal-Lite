using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Hooks;
using TortalPortalLite.Subsystems.Foundations;

namespace TortalPortalLite.Subsystems.WorldOps
{
    /// <summary>
    /// #88 The Routing Kernel + #89 Park and Release, combined - #89's own text names itself "the atomic
    /// enforcement primitive" underneath #88's take-over-of-pairing idea, and in this codebase the actual
    /// L1/L2/L3 takeover machinery #88 describes already exists one layer down as Foundations'
    /// NetworkReassertEngine (admin-declared STATIC networks.json membership) and Topology's
    /// RoutingPairingAuthorityEngine (dynamic/conditional routes) - both already register a
    /// ConnectPortalsHook postfix and reassert tag/connection through PortalOwnership.ClaimAndWrite,
    /// which is precisely #88's "L1/L2 selection+pairing override" and "write primitive" made durable.
    /// Reinventing a THIRD copy of that same takeover in this domain would be exactly the "Harmony patch
    /// collisions" / duplicate-kernel hazard the orchestrator's Core/Hooks/ split exists to prevent (see
    /// Core/Hooks/ConnectPortalsHook.cs's own doc comment on why a second prefix-veto was declined
    /// twice). What THIS domain adds on top, and what #89 is actually about, is economy-specific:
    /// per-portal DYNAMIC open/closed conditions (a toll is unpaid, a lease lapsed, a cooldown active,
    /// an owner offline) that can each independently want a managed gate parked - #88's escalation
    /// levels are not re-implemented; this class IS the "one central place that composes several
    /// independent condition sources into a single write" #88's own architecture calls for, scoped to
    /// this domain exactly the way RoutingPairingAuthorityEngine is scoped to Topology's.
    ///
    /// Every economy engine that wants a managed portal parked/released or its tag decorated calls
    /// <see cref="Publish"/> every tick it re-evaluates (cheap - a dictionary upsert), never writing the
    /// ZDO itself. Aggregation rule: a portal is OPEN iff every currently-published condition for it says
    /// open (AND across sources - a toll AND a lease AND a cooldown must all pass); its tag is the
    /// concatenation of every source's non-empty fragment, ordered by <see cref="Publish"/>'s own
    /// <paramref name="fragmentOrder"/> parameter (lower first) so a "MINE" label consistently precedes a
    /// "43/50c" status instead of an arbitrary dictionary order. #89's own citation
    /// (TeleportWorld.RPC_SetTag/direct-ZDO-write have no length check) means this does not hard-clamp to
    /// TagCodec's 10-char sigil budget the way a parsed command tag must - but a long tag still risks the
    /// widget-truncation failure mode #89/#104 both document, so fragments are capped defensively rather
    /// than left unbounded.
    /// </summary>
    public static class EconomyRoutingKernel
    {
        private const int MaxTagLength = 24;

        private sealed class Condition
        {
            public bool Open;
            public string? Tag;
            public int Order;
        }

        private sealed class PortalState
        {
            public readonly Dictionary<string, Condition> Conditions = new Dictionary<string, Condition>();
            public ZDOID Destination = ZDOID.None;
            public bool HasDestination;
            public bool LastAppliedOpen = true;
            public bool Initialized;
        }

        private static readonly Dictionary<ZDOID, PortalState> _states = new Dictionary<ZDOID, PortalState>();
        private static float _timer;

        public static void Initialize()
        {
            // Lowest priority so vanilla's own pass, THEN Foundations'/Topology's own reassertion, all
            // run first, and this domain's economy gating is the final word for the portals it manages -
            // matching RoutingPairingAuthorityEngine's own "-1000" self-description of "lowest priority
            // in this domain's use of the broker".
            ConnectPortalsHook.RegisterPostfix(-2000, Apply);
        }

        public static void OnUpdate(float dt)
        {
            if (EconomyConfig.Enabled?.Value == false)
            {
                return;
            }
            _timer += dt;
            float interval = EconomyConfig.KernelReassertSeconds?.Value ?? 1.0f;
            if (_timer < interval)
            {
                return;
            }
            _timer = 0f;
            Apply();
        }

        /// <summary>
        /// A mechanism engine publishes what it currently wants this portal's open/closed state and tag
        /// fragment to be. <paramref name="fragmentOrder"/> controls left-to-right position when several
        /// sources contribute to one tag (conventionally: 0 = base label/naming, 10 = mechanism status,
        /// 20 = prestige/decoration). Call every tick the source re-evaluates, even if unchanged.
        /// </summary>
        public static void Publish(ZDOID portalUid, string conditionKey, bool open, string? tagFragment = null, int fragmentOrder = 10)
        {
            if (portalUid == ZDOID.None)
            {
                return;
            }
            if (!_states.TryGetValue(portalUid, out PortalState state))
            {
                state = new PortalState();
                _states[portalUid] = state;
            }
            state.Conditions[conditionKey] = new Condition { Open = open, Tag = tagFragment, Order = fragmentOrder };
        }

        /// <summary>Declares (or updates) the destination this portal should point at while open. Last writer this tick wins - well-formed economy.json declarations name the same destination from every mechanism stacked on one portal, so this is not a real race in practice.</summary>
        public static void SetDestination(ZDOID portalUid, ZDOID destination)
        {
            if (portalUid == ZDOID.None)
            {
                return;
            }
            if (!_states.TryGetValue(portalUid, out PortalState state))
            {
                state = new PortalState();
                _states[portalUid] = state;
            }
            state.Destination = destination;
            state.HasDestination = true;
        }

        /// <summary>Stops one source's participation for this portal - call when a mechanism releases a portal (declaration removed, binding lost, ...). The portal stops being kernel-managed entirely once no conditions and no destination remain.</summary>
        public static void Clear(ZDOID portalUid, string conditionKey)
        {
            if (!_states.TryGetValue(portalUid, out PortalState state))
            {
                return;
            }
            state.Conditions.Remove(conditionKey);
            if (state.Conditions.Count == 0 && !state.HasDestination)
            {
                _states.Remove(portalUid);
            }
        }

        /// <summary>True if any economy mechanism currently manages this portal (used by engines that must not double-manage a portal another mechanism already owns).</summary>
        public static bool IsManaged(ZDOID portalUid) => _states.ContainsKey(portalUid);

        /// <summary>The last-applied open/closed verdict for this portal (true if unmanaged - an economy mechanism that has never seen this portal has no opinion, so it reads as "not blocked by economy"). Used by output-only engines (e.g. #276 Server Portcullis) that need to mirror the kernel's decision onto a physical prop rather than the connection itself.</summary>
        public static bool IsCurrentlyOpen(ZDOID portalUid) => !_states.TryGetValue(portalUid, out PortalState state) || state.LastAppliedOpen;

        private static void Apply()
        {
            if (ZDOMan.instance == null || _states.Count == 0)
            {
                return;
            }
            int budget = EconomyConfig.MaxWritesPerTick?.Value ?? 64;
            int written = 0;

            // Snapshot keys - a handler below may itself call Clear/Publish (re-entrancy is expected:
            // e.g. a toll engine re-evaluates and republishes inside the same Apply pass is NOT how this
            // is used - engines publish on their own OnUpdate, this only ever reads/writes ZDOs).
            foreach (KeyValuePair<ZDOID, PortalState> kvp in _states.ToList())
            {
                if (written >= budget)
                {
                    break;
                }
                ZDOID uid = kvp.Key;
                PortalState state = kvp.Value;
                ZDO zdo = ZDOMan.instance.GetZDO(uid);
                if (zdo == null || !zdo.IsValid())
                {
                    continue;
                }

                bool open = true;
                foreach (Condition c in state.Conditions.Values)
                {
                    if (!c.Open)
                    {
                        open = false;
                        break;
                    }
                }

                string? tag = ComposeTag(state);
                ZDOID? desiredConnection = state.HasDestination ? (open ? state.Destination : ZDOID.None) : (ZDOID?)null;

                bool wrote = EconomyWriteOps.Reassert(zdo, tag, desiredConnection);
                if (wrote)
                {
                    written++;
                }

                if (state.Initialized && open != state.LastAppliedOpen)
                {
                    OnTransition(zdo, state, open);
                }
                state.LastAppliedOpen = open;
                state.Initialized = true;
            }
        }

        private static string? ComposeTag(PortalState state)
        {
            var fragments = state.Conditions.Values
                .Where(c => !string.IsNullOrEmpty(c.Tag))
                .OrderBy(c => c.Order)
                .Select(c => c.Tag!)
                .ToList();
            if (fragments.Count == 0)
            {
                return null;
            }
            string joined = string.Join(" ", fragments);
            return joined.Length > MaxTagLength ? joined.Substring(0, MaxTagLength) : joined;
        }

        private static void OnTransition(ZDO zdo, PortalState state, bool nowOpen)
        {
            float notifyRadius = EconomyConfig.NotifyRadius?.Value ?? 20f;
            float prewarmRadius = EconomyConfig.PrewarmRadius?.Value ?? 40f;
            Vector3 pos = zdo.GetPosition();
            string label = ComposeTag(state) ?? "Gate";

            if (nowOpen)
            {
                if (state.HasDestination && state.Destination != ZDOID.None)
                {
                    EconomyWriteOps.PrewarmToPeersNear(pos, prewarmRadius, state.Destination);
                }
                EconomyWriteOps.NotifyNear(pos, notifyRadius, $"{label}: open.");
            }
            else
            {
                EconomyWriteOps.NotifyNear(pos, notifyRadius, $"{label}: closed.");
            }
        }
    }
}
