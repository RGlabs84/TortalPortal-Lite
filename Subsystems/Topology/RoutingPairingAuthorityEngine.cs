using System.Collections.Generic;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Hooks;

namespace TortalPortalLite.Subsystems.Topology
{
    /// <summary>
    /// #23 Pairing Authority Takeover - the architectural seam every other engine in this domain plugs
    /// into. Catalog's own "clean architecture" is a Harmony PREFIX on the public `Game.ConnectPortals`
    /// (:100589-100627) returning false, replacing both of vanilla's passes outright for the managed
    /// subset. That prefix-cancel shape does not exist in this codebase:
    /// `Core/Hooks/ConnectPortalsHook.cs` only exposes <c>RegisterPostfix</c> - every registered handler
    /// runs AFTER vanilla's own two passes have already executed, never instead of them.
    ///
    /// // NEEDS NEW HOOK BROKER on Game.ConnectPortals: a prefix-cancel variant (postfix-only today,
    /// // see Core/Hooks/ConnectPortalsHook.cs) would let this engine truly skip vanilla's pass 1/2
    /// // for managed portals instead of racing to correct them afterward. Not added here - three
    /// // agents are editing Core/Hooks/ concurrently this wave; flagged for the orchestrator.
    ///
    /// What IS implemented, and is a real substitute rather than a stub, is the two-part correction the
    /// catalog itself allows for as an equally valid alternative ("no-patch alternative: tag cloaking" -
    /// same-tag reciprocal wiring survives pass 1 unconditionally, since pass 1 never checks
    /// reciprocity, :100598-100605) plus closing the residual race window as tightly as this codebase's
    /// hook surface allows:
    ///
    ///  1. Every mechanism-specific engine (Hub, Ring, Schedule, Sealed Gate, ...) calls
    ///     <see cref="Publish"/> with the tag/connection it currently wants a managed portal to read.
    ///     This engine registers a `ConnectPortalsHook` postfix at the lowest priority in the domain, so
    ///     it runs immediately after vanilla's own pass within the SAME frame and re-applies every
    ///     published desired state right away - shrinking the "vanilla just tore this down" window from
    ///     up to 5s (vanilla's own cadence) to sub-frame.
    ///  2. A periodic OnUpdate safety net (RoutingConfig.ReassertSeconds, default well under vanilla's
    ///     5s) re-applies the same published states independent of the postfix ever firing - covering
    ///     drift from any OTHER cause (a player's own unauthenticated RPC_SetConnection, catalog #29's
    ///     own citation :100653-100662, or a mechanism engine that hasn't ticked yet).
    ///  3. A tiny claims table so two admin declarations (a hub AND a ring both naming the same
    ///     position in routing.json) fail loudly instead of silently fighting each other every tick.
    ///
    /// Must not run before OnWorldReady - the load-time relink pass (`ZDOMan.LoadChunks` :76474-76531,
    /// which itself invokes the private hash-based `ZDOMan.ConnectPortals` reciprocal relinker
    /// :77850-77893) needs to finish first, exactly as the catalog warns.
    /// </summary>
    public static class RoutingPairingAuthorityEngine
    {
        private static readonly Dictionary<ZDOID, (string? tag, ZDOID? connection)> _desired = new Dictionary<ZDOID, (string?, ZDOID?)>();
        private static readonly Dictionary<RoutingPositionKey, string> _claims = new Dictionary<RoutingPositionKey, string>();
        private static float _timer;

        public static void Initialize()
        {
            // Lowest priority in this domain's use of the broker: every other routing engine that also
            // registers a ConnectPortalsHook postfix (none currently do - they tick independently) would
            // still want vanilla's pass, then THIS correction, applied first.
            ConnectPortalsHook.RegisterPostfix(-1000, ReassertAfterVanillaPass);
        }

        public static void OnUpdate(float dt)
        {
            if (RoutingConfig.TakeoverEnabled?.Value == false)
            {
                return;
            }
            _timer += dt;
            float interval = RoutingConfig.ReassertSeconds?.Value ?? 1.0f;
            if (_timer < interval)
            {
                return;
            }
            _timer = 0f;
            ReassertAfterVanillaPass();
        }

        /// <summary>A mechanism engine publishes what it currently wants this portal's tag/connection to read. Call every time the desired state is (re)computed, even if unchanged - cheap, and keeps this table the single source of truth for the postfix/safety-net correction above.</summary>
        public static void Publish(ZDOID portalUid, string? tag, ZDOID? connection)
        {
            _desired[portalUid] = (tag, connection);
        }

        /// <summary>Stop reasserting a portal - call when a mechanism releases a portal (routing.json entry removed, hub disabled, ...).</summary>
        public static void Clear(ZDOID portalUid)
        {
            _desired.Remove(portalUid);
        }

        /// <summary>
        /// Registers that <paramref name="mechanism"/> owns the declared position <paramref name="pos"/>.
        /// Returns false (and logs once) if a DIFFERENT mechanism already claimed the same rounded
        /// position this reload cycle - routing.json authoring error, not a runtime race.
        /// </summary>
        public static bool TryClaim(RoutingPosition pos, string mechanism)
        {
            var key = new RoutingPositionKey(pos);
            if (_claims.TryGetValue(key, out string? owner))
            {
                if (owner == mechanism)
                {
                    return true;
                }
                PortalDebug.LogWarning($"[RoutingPairingAuthorityEngine] position {pos.X:F1},{pos.Y:F1},{pos.Z:F1} is declared by both '{owner}' and '{mechanism}' in routing.json - '{mechanism}' will not manage it.");
                return false;
            }
            _claims[key] = mechanism;
            return true;
        }

        /// <summary>Call once per reload before re-claiming, so a position dropped from routing.json stops blocking other mechanisms.</summary>
        public static void ResetClaims()
        {
            _claims.Clear();
        }

        private static void ReassertAfterVanillaPass()
        {
            if (ZDOMan.instance == null || _desired.Count == 0)
            {
                return;
            }
            int budget = RoutingConfig.MaxWritesPerTick?.Value ?? 64;
            int written = 0;
            foreach (KeyValuePair<ZDOID, (string? tag, ZDOID? connection)> kvp in _desired)
            {
                if (written >= budget)
                {
                    break;
                }
                ZDO zdo = ZDOMan.instance.GetZDO(kvp.Key);
                if (zdo == null || !zdo.IsValid())
                {
                    continue;
                }
                if (RoutingWriteOps.Reassert(zdo, kvp.Value.tag, kvp.Value.connection))
                {
                    written++;
                }
            }
        }

        /// <summary>Rounds to the same 0.5m grid PortalCensus uses for its own position index, so a claim lines up with what PortalCensus.TryGetByPosition resolves.</summary>
        private readonly struct RoutingPositionKey
        {
            private readonly int _x, _y, _z;
            public RoutingPositionKey(RoutingPosition p)
            {
                _x = UnityEngine.Mathf.RoundToInt(p.X * 2f);
                _y = UnityEngine.Mathf.RoundToInt(p.Y * 2f);
                _z = UnityEngine.Mathf.RoundToInt(p.Z * 2f);
            }
        }
    }
}
