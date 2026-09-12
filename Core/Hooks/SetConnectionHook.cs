using System;
using HarmonyLib;
using TortalPortalLite.Core;

namespace TortalPortalLite.Core.Hooks
{
    /// <summary>
    /// Postfix on the private Game.SetConnection(ZDO portal, ZDOID connection, bool
    /// forceImmediateConnection = false) (SERVER decompile :100637 - confirmed private, on Game).
    /// Observability only: this is vanilla's own write path (called from within ConnectPortals'
    /// pairing pass, and by RPC_SetConnection when an owning client's routed request lands), so a
    /// handler here sees every connection change regardless of who caused it - AuditEngine's
    /// "attribute the write, don't just log it" requirement (catalog #72) needs exactly this.
    /// </summary>
    public static class SetConnectionHook
    {
        public delegate void Handler(ZDO portal, ZDOID connection, bool forceImmediateConnection);

        private static readonly PriorityList<Handler> _postfixHandlers = new PriorityList<Handler>();
        public static bool PatchOk { get; private set; }

        public static void RegisterPostfix(int priority, Handler handler) => _postfixHandlers.Add(priority, handler);

        internal static void Install(Harmony harmony)
        {
            try
            {
                var target = AccessTools.Method(typeof(Game), "SetConnection", new[] { typeof(ZDO), typeof(ZDOID), typeof(bool) });
                if (target == null)
                {
                    throw new MissingMethodException("Game.SetConnection(ZDO, ZDOID, bool) not found - vanilla method signature may have changed.");
                }
                harmony.Patch(target, postfix: new HarmonyMethod(typeof(SetConnectionHook), nameof(Postfix)));
                PatchOk = true;
            }
            catch (Exception ex)
            {
                PatchOk = false;
                PortalDebug.LogError($"[Hooks.SetConnection] failed to bind Game.SetConnection(...): {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void Postfix(ZDO portal, ZDOID connection, bool forceImmediateConnection)
        {
            foreach (Handler handler in _postfixHandlers.InOrder())
            {
                try
                {
                    handler(portal, connection, forceImmediateConnection);
                }
                catch (Exception ex)
                {
                    PortalDebug.LogError($"[Hooks.SetConnection] handler threw: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
                }
            }
        }
    }
}
