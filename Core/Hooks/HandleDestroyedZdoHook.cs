using System;
using HarmonyLib;
using TortalPortalLite.Core;

namespace TortalPortalLite.Core.Hooks
{
    /// <summary>
    /// The real "a ZDO is being permanently removed" choke point - and NOT ZDOMan.DestroyZDO(ZDO),
    /// which a first pass at this file wrongly assumed was it. Confirmed by reading the SERVER decompile
    /// directly (:76929-76935): DestroyZDO only adds to m_destroySendList for later broadcast if the
    /// caller owns the ZDO - it never actually removes anything locally. The private
    /// HandleDestroyedZDO(ZDOID uid) (:76963) is what does: GetZDO/null-check, fires m_onZDODestroyed,
    /// RemoveFromSector, and vanilla's own portal-specific bookkeeping
    /// (Game.instance.PortalPrefabHash.Contains(zDO.GetPrefab()) at :76980). It is reached from
    /// RPC_DestroyZDO's loop (:76953 - "no sender check" per the catalog's own verified findings) and
    /// is presumably the same path any other internal removal takes, so a veto here is the actual
    /// "unbypassable force-disconnect" / "destroy veto" primitive the catalog's access/lockdown domains
    /// need - patching DestroyZDO instead would have shipped a veto that did nothing.
    ///
    /// HandleDestroyedZDO itself takes only a ZDOID, no sender - so a small prefix on RPC_DestroyZDO
    /// captures the current request's sender into _currentRpcSender for the single-threaded duration of
    /// that call (game-loop code is not reentrant/concurrent here), and the HandleDestroyedZDO veto
    /// reads it. Falls back to sender 0 ("server-internal") outside of an RPC_DestroyZDO call, e.g. if
    /// vanilla or another mod calls HandleDestroyedZDO directly.
    /// </summary>
    public static class HandleDestroyedZdoHook
    {
        /// <summary>Return false to block the removal. Handlers see the still-valid ZDO (looked up before removal) and the sender.</summary>
        public delegate bool Handler(ZDO zdo, long sender);

        private static readonly PriorityList<Handler> _handlers = new PriorityList<Handler>();
        private static long _currentRpcSender;
        private static bool _insideRpcDestroy;

        public static bool RpcSenderCapturePatchOk { get; private set; }
        public static bool VetoPatchOk { get; private set; }
        public static bool PatchOk => RpcSenderCapturePatchOk && VetoPatchOk;

        public static void Register(int priority, Handler handler) => _handlers.Add(priority, handler);

        internal static void Install(Harmony harmony)
        {
            InstallSenderCapture(harmony);
            InstallVeto(harmony);
        }

        private static void InstallSenderCapture(Harmony harmony)
        {
            try
            {
                var target = AccessTools.Method(typeof(ZDOMan), "RPC_DestroyZDO", new[] { typeof(long), typeof(ZPackage) });
                if (target == null)
                {
                    throw new MissingMethodException("ZDOMan.RPC_DestroyZDO(long, ZPackage) not found - vanilla method signature may have changed.");
                }
                harmony.Patch(target,
                    prefix: new HarmonyMethod(typeof(HandleDestroyedZdoHook), nameof(RpcDestroyPrefix)),
                    postfix: new HarmonyMethod(typeof(HandleDestroyedZdoHook), nameof(RpcDestroyPostfix)));
                RpcSenderCapturePatchOk = true;
            }
            catch (Exception ex)
            {
                RpcSenderCapturePatchOk = false;
                PortalDebug.LogError($"[Hooks.HandleDestroyedZdo] failed to bind ZDOMan.RPC_DestroyZDO(...) for sender capture: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void InstallVeto(Harmony harmony)
        {
            try
            {
                var target = AccessTools.Method(typeof(ZDOMan), "HandleDestroyedZDO", new[] { typeof(ZDOID) });
                if (target == null)
                {
                    throw new MissingMethodException("ZDOMan.HandleDestroyedZDO(ZDOID) not found - vanilla method signature may have changed.");
                }
                harmony.Patch(target, prefix: new HarmonyMethod(typeof(HandleDestroyedZdoHook), nameof(VetoPrefix)));
                VetoPatchOk = true;
            }
            catch (Exception ex)
            {
                VetoPatchOk = false;
                PortalDebug.LogError($"[Hooks.HandleDestroyedZdo] failed to bind ZDOMan.HandleDestroyedZDO(ZDOID): {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void RpcDestroyPrefix(long sender)
        {
            _currentRpcSender = sender;
            _insideRpcDestroy = true;
        }

        private static void RpcDestroyPostfix()
        {
            _insideRpcDestroy = false;
        }

        private static bool VetoPrefix(ZDOID uid)
        {
            if (_handlers.Count == 0 || ZDOMan.instance == null)
            {
                return true;
            }

            ZDO zdo = ZDOMan.instance.GetZDO(uid);
            if (zdo == null)
            {
                // Already gone / never existed - nothing to protect, let vanilla's own null-check run and no-op.
                return true;
            }

            long sender = _insideRpcDestroy ? _currentRpcSender : 0L;
            foreach (Handler handler in _handlers.InOrder())
            {
                try
                {
                    if (!handler(zdo, sender))
                    {
                        return false;
                    }
                }
                catch (Exception ex)
                {
                    PortalDebug.LogError($"[Hooks.HandleDestroyedZdo] handler threw (treated as non-veto): {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
                }
            }
            return true;
        }
    }
}
