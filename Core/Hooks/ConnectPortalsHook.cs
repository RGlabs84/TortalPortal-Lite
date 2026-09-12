using System;
using HarmonyLib;
using TortalPortalLite.Core;

namespace TortalPortalLite.Core.Hooks
{
    /// <summary>
    /// The single postfix on Game.ConnectPortals() (SERVER decompile :100589 - confirmed public,
    /// no-arg, on Game, not the private ZDOMan.ConnectPortals() at :77850, which is the different
    /// load-time hash-relinking pass). This is vanilla's own 5s reconciler; every engine that needs to
    /// re-assert a managed network's wiring after vanilla's self-heal pass runs registers here instead
    /// of patching Game.ConnectPortals itself - 64 buildable catalog options cite this method, and one
    /// independent Harmony patch per subsystem on the same hot method is exactly the "undefined prefix
    /// ordering, veto races" hazard this Core/Hooks/ folder exists to prevent (see the implementation
    /// plan). Catalog #69 NetworkReassertEngine is the intended sole writer of s_tag/ConnectionType.Portal
    /// among the handlers registered here - other handlers should only read/observe.
    ///
    /// Wave 1 integration note: three independent engines (Topologies #20 Displaced Routing, Routing
    /// #23 Pairing Authority Takeover, Targeted's nearest-player mechanism A) each asked for a
    /// prefix-cancel variant that could return false and skip vanilla's ENTIRE ConnectPortals() body
    /// for that tick. Deliberately not added: a prefix veto here has world-wide blast radius - it
    /// would skip reconciliation for every portal that tick, not just the ones the vetoing engine
    /// cares about, and two engines wanting to veto in the same tick for different reasons would
    /// silently fight (whichever registers first wins, the other's assumption breaks with no signal).
    /// All three requesters already ship a working alternative (a fast postfix correction, synchronous
    /// in the same frame vanilla's pass runs) - kept as the load-bearing mechanism. Revisit only if a
    /// future option genuinely cannot be built any other way, and if so, model it as a single
    /// first-veto-wins gate (like HandleDestroyedZdoHook) rather than letting arbitrary engines fight
    /// over it.
    /// </summary>
    public static class ConnectPortalsHook
    {
        public delegate void Handler();

        private static readonly PriorityList<Handler> _postfixHandlers = new PriorityList<Handler>();
        public static bool PatchOk { get; private set; }

        public static void RegisterPostfix(int priority, Handler handler) => _postfixHandlers.Add(priority, handler);

        internal static void Install(Harmony harmony)
        {
            try
            {
                var target = AccessTools.Method(typeof(Game), "ConnectPortals", Type.EmptyTypes);
                if (target == null)
                {
                    throw new MissingMethodException("Game.ConnectPortals() not found - vanilla method signature may have changed.");
                }
                harmony.Patch(target, postfix: new HarmonyMethod(typeof(ConnectPortalsHook), nameof(Postfix)));
                PatchOk = true;
            }
            catch (Exception ex)
            {
                PatchOk = false;
                PortalDebug.LogError($"[Hooks.ConnectPortals] failed to bind Game.ConnectPortals(): {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void Postfix()
        {
            foreach (Handler handler in _postfixHandlers.InOrder())
            {
                try
                {
                    handler();
                }
                catch (Exception ex)
                {
                    PortalDebug.LogError($"[Hooks.ConnectPortals] handler threw: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
                }
            }
        }
    }
}
