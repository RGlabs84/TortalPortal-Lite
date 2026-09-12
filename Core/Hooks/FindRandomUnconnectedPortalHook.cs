using System;
using System.Collections.Generic;
using HarmonyLib;
using TortalPortalLite.Core;

namespace TortalPortalLite.Core.Hooks
{
    /// <summary>
    /// Override-capable prefix on the private Game.FindRandomUnconnectedPortal(List&lt;ZDO&gt; portals,
    /// ZDO skip, string tag) (SERVER decompile :100664 - confirmed private, on Game, part of
    /// ConnectPortals' pairing pass). Vanilla picks uniformly at random among same-tagged unconnected
    /// portals; catalog #31 Deterministic Assignment Replacement (nearest / oldest / round-robin / LRU)
    /// needs to intercept exactly this choice. First handler (priority order) that returns true wins
    /// and its result replaces vanilla's pick; if none do, vanilla's own random selection runs
    /// unmodified - so this hook is safe to install even before any handler is registered against it.
    /// </summary>
    public static class FindRandomUnconnectedPortalHook
    {
        public delegate bool Handler(List<ZDO> portals, ZDO skip, string tag, out ZDO result);

        private static readonly PriorityList<Handler> _handlers = new PriorityList<Handler>();
        public static bool PatchOk { get; private set; }

        public static void Register(int priority, Handler handler) => _handlers.Add(priority, handler);

        internal static void Install(Harmony harmony)
        {
            try
            {
                var target = AccessTools.Method(typeof(Game), "FindRandomUnconnectedPortal", new[] { typeof(List<ZDO>), typeof(ZDO), typeof(string) });
                if (target == null)
                {
                    throw new MissingMethodException("Game.FindRandomUnconnectedPortal(List<ZDO>, ZDO, string) not found - vanilla method signature may have changed.");
                }
                harmony.Patch(target, prefix: new HarmonyMethod(typeof(FindRandomUnconnectedPortalHook), nameof(Prefix)));
                PatchOk = true;
            }
            catch (Exception ex)
            {
                PatchOk = false;
                PortalDebug.LogError($"[Hooks.FindRandomUnconnectedPortal] failed to bind: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static bool Prefix(List<ZDO> portals, ZDO skip, string tag, ref ZDO __result)
        {
            foreach (Handler handler in _handlers.InOrder())
            {
                try
                {
                    if (handler(portals, skip, tag, out ZDO overridden))
                    {
                        __result = overridden;
                        return false;
                    }
                }
                catch (Exception ex)
                {
                    PortalDebug.LogError($"[Hooks.FindRandomUnconnectedPortal] handler threw: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
                }
            }
            return true;
        }
    }
}
