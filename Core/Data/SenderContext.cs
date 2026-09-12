using System;
using System.Linq;
using HarmonyLib;
using TortalPortalLite.Core;

namespace TortalPortalLite.Core.Data
{
    /// <summary>
    /// #44 Authentic Sender Context. An ambient, [ThreadStatic] record of the socket-verified ZNetPeer
    /// for the duration of a routed-RPC dispatch - the prerequisite that turns a spoofable `sender`
    /// long argument into something checkable (peer.m_socket.GetHostName(), the platform-verified
    /// identity, same check RPC_Kick/the adminlist itself use).
    ///
    /// One prefix/postfix pair on ZRoutedRpc.RPC_RoutedRPC(ZRpc rpc, ZPackage pkg) (SERVER decompile
    /// :83632, confirmed private on ZRoutedRpc): the whole chain RPC_RoutedRPC -> HandleRoutedRPC ->
    /// the actual handler is one synchronous call stack on the main thread (Valheim's server loop is
    /// single-threaded), so a prefix resolving the peer and a postfix clearing it safely brackets every
    /// handler invoked in between - including ZDOMan.RPC_DestroyZDO, Game.RPC_SetConnection, and any
    /// handler this mod or vanilla itself registers.
    ///
    /// ZDOMan.RPC_ZDOData is NOT patched a second time here even though #44 names it too - that
    /// hook already exists (RpcZdoDataHook) and now also resolves + exposes the sender peer itself,
    /// exactly to avoid a second, competing patch on the same vanilla method (see Core/Hooks/ and the
    /// implementation plan's "Harmony patch collisions" hazard).
    ///
    /// Wave 2 integration note: this file also now owns routed-RPC CONTENT observation
    /// (RegisterObserver), added because three independent Wave-2 engines (ux's map-ping/chat-command
    /// capture, access's global-key RPC hardening, lockdown's global-key veto/detection) each separately
    /// asked for a broker on this exact method. Rather than creating a second, competing patch on
    /// RPC_RoutedRPC, this class's existing prefix ALSO safely peeks the routed-RPC envelope: per #44's
    /// own citation, `new ZPackage(pkg.GetArray())` copies the raw bytes, and RoutedRPCData's own public
    /// Deserialize (SERVER decompile :83463-83483) parses `m_methodHash`/`m_parameters` from that COPY -
    /// vanilla's own subsequent read of the original `pkg` is never touched. This is now a genuine
    /// Core/Hooks/-style broker (ordered handlers, one patch) even though it lives in Core/Data/ next to
    /// the ambient-Current API it grew from.
    /// </summary>
    public static class SenderContext
    {
        [ThreadStatic] private static ZNetPeer? _current;

        /// <summary>The socket-verified peer that sent the routed RPC currently being dispatched, or null (outside any routed-RPC dispatch, or the peer could not be resolved).</summary>
        public static ZNetPeer? Current => _current;

        /// <summary>method hash ("MethodName".GetStableHashCode()), sender peer (may be null), deserialized parameters (a fresh copy - safe to read/consume freely, it is not the live package).</summary>
        public delegate void RpcObserver(int methodHash, ZNetPeer? sender, ZPackage parameters);

        private static readonly Hooks.PriorityList<RpcObserver> _observers = new Hooks.PriorityList<RpcObserver>();
        public static void RegisterObserver(int priority, RpcObserver observer) => _observers.Add(priority, observer);

        public static bool PatchOk { get; private set; }

        public static void Install(Harmony harmony)
        {
            try
            {
                var target = AccessTools.Method(typeof(ZRoutedRpc), "RPC_RoutedRPC", new[] { typeof(ZRpc), typeof(ZPackage) });
                if (target == null)
                {
                    throw new MissingMethodException("ZRoutedRpc.RPC_RoutedRPC(ZRpc, ZPackage) not found - vanilla method signature may have changed.");
                }
                harmony.Patch(target,
                    prefix: new HarmonyMethod(typeof(SenderContext), nameof(Prefix)),
                    postfix: new HarmonyMethod(typeof(SenderContext), nameof(Postfix)));
                PatchOk = true;
            }
            catch (Exception ex)
            {
                PatchOk = false;
                PortalDebug.LogError($"[SenderContext] failed to bind ZRoutedRpc.RPC_RoutedRPC(...): {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void Prefix(ZRpc rpc, ZPackage pkg)
        {
            ZNetPeer? sender = ResolvePeer(rpc);
            _current = sender;

            if (_observers.Count > 0)
            {
                try
                {
                    var data = new ZRoutedRpc.RoutedRPCData();
                    data.Deserialize(new ZPackage(pkg.GetArray()));
                    foreach (RpcObserver observer in _observers.InOrder())
                    {
                        try
                        {
                            observer(data.m_methodHash, sender, data.m_parameters);
                        }
                        catch (Exception ex)
                        {
                            PortalDebug.LogError($"[SenderContext] observer threw: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    PortalDebug.LogWarning($"[SenderContext] routed-RPC envelope peek failed (non-fatal): {ex.Message}");
                }
            }
        }

        private static void Postfix()
        {
            _current = null;
        }

        /// <summary>Also used directly by RpcZdoDataHook, which receives the same ZRpc but patches a different method.</summary>
        public static ZNetPeer? ResolvePeer(ZRpc rpc)
        {
            if (rpc == null || ZNet.instance == null)
            {
                return null;
            }
            try
            {
                return ZNet.instance.GetPeers().FirstOrDefault(p => p != null && p.m_rpc == rpc);
            }
            catch (Exception ex)
            {
                PortalDebug.LogWarning($"[SenderContext] peer resolution failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>The one platform-verified identity on the box - the same string RPC_Kick/the adminlist check against. Null if the peer or its socket is unavailable.</summary>
        public static string? HostNameOf(ZNetPeer? peer)
        {
            try
            {
                return peer?.m_socket?.GetHostName();
            }
            catch
            {
                return null;
            }
        }
    }
}
