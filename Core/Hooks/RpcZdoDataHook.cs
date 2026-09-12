using System;
using HarmonyLib;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;

namespace TortalPortalLite.Core.Hooks
{
    /// <summary>
    /// Postfix-only on the private ZDOMan.RPC_ZDOData(ZRpc rpc, ZPackage pkg) (SERVER decompile :77076
    /// - confirmed private, on ZDOMan, the server's own handler for every incoming client ZDO write).
    /// This is THE choke point every client-authored ZDO change passes through, regardless of whether
    /// any GameObject/ZNetView exists for it - unlike a routed RPC (dead on the server: portals never
    /// have a live ZNetScene.FindInstance to receive one), this runs unconditionally.
    ///
    /// Deliberately postfix-only, not prefix-veto: this mod has not verified that peeking further into
    /// `pkg`'s body (past the leading ZDOID) and rewinding is safe without corrupting the original
    /// method's own subsequent parse. The catalog's own verified guidance already prefers the simpler,
    /// safer alternative to a body-content veto: let the write land, then watch the resulting ZDO state
    /// and revert an unauthorized change - "detect and revert", the reactive-detection-only enforcement
    /// tier this mod's catalog names explicitly, not "prevent".
    ///
    /// The leading ZDOID IS captured safely, via a prefix that reads it and immediately restores the
    /// package's cursor with GetPos()/SetPos(int) (both confirmed present on ZPackage) before vanilla's
    /// own read of the same bytes runs - the standard Harmony "observe in prefix, use in postfix via
    /// __state" pattern. The prefix also resolves the sending peer via SenderContext.ResolvePeer - #44
    /// Authentic Sender Context names this same method as needing sender resolution, and that resolution
    /// happens HERE (piggybacked on the patch that already exists) rather than through a second,
    /// competing Harmony patch on the same vanilla method - exactly the collision Core/Hooks/ exists to
    /// prevent (see the implementation plan).
    /// </summary>
    public static class RpcZdoDataHook
    {
        public delegate void Handler(ZNetPeer? sender, ZDOID zdoid);

        private readonly struct State
        {
            public readonly ZDOID Zdoid;
            public readonly ZNetPeer? Sender;
            public State(ZDOID zdoid, ZNetPeer? sender) { Zdoid = zdoid; Sender = sender; }
        }

        private static readonly PriorityList<Handler> _postfixHandlers = new PriorityList<Handler>();
        public static bool PatchOk { get; private set; }

        public static void RegisterPostfix(int priority, Handler handler) => _postfixHandlers.Add(priority, handler);

        internal static void Install(Harmony harmony)
        {
            try
            {
                var target = AccessTools.Method(typeof(ZDOMan), "RPC_ZDOData", new[] { typeof(ZRpc), typeof(ZPackage) });
                if (target == null)
                {
                    throw new MissingMethodException("ZDOMan.RPC_ZDOData(ZRpc, ZPackage) not found - vanilla method signature may have changed.");
                }
                harmony.Patch(target,
                    prefix: new HarmonyMethod(typeof(RpcZdoDataHook), nameof(Prefix)),
                    postfix: new HarmonyMethod(typeof(RpcZdoDataHook), nameof(Postfix)));
                PatchOk = true;
            }
            catch (Exception ex)
            {
                PatchOk = false;
                PortalDebug.LogError($"[Hooks.RpcZdoData] failed to bind ZDOMan.RPC_ZDOData(...): {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void Prefix(ZRpc rpc, ZPackage pkg, out State __state)
        {
            ZDOID zdoid = ZDOID.None;
            if (_postfixHandlers.Count > 0)
            {
                int pos = pkg.GetPos();
                try
                {
                    zdoid = pkg.ReadZDOID();
                }
                catch (Exception ex)
                {
                    PortalDebug.LogWarning($"[Hooks.RpcZdoData] leading-ZDOID peek failed (non-fatal): {ex.Message}");
                }
                finally
                {
                    pkg.SetPos(pos);
                }
            }
            __state = new State(zdoid, SenderContext.ResolvePeer(rpc));
        }

        private static void Postfix(State __state)
        {
            if (__state.Zdoid == ZDOID.None)
            {
                return;
            }
            foreach (Handler handler in _postfixHandlers.InOrder())
            {
                try
                {
                    handler(__state.Sender, __state.Zdoid);
                }
                catch (Exception ex)
                {
                    PortalDebug.LogError($"[Hooks.RpcZdoData] handler threw: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
                }
            }
        }
    }
}
