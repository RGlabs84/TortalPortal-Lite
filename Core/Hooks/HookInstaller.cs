using System.Collections.Generic;
using HarmonyLib;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;

namespace TortalPortalLite.Core.Hooks
{
    /// <summary>
    /// Installs every Core/Hooks/ broker exactly once, from Plugin.Awake, before any subsystem
    /// registers a handler against one. Adding a NEW broker (a later wave discovers it needs one) means
    /// adding both the broker class and one line here - an orchestrator action between waves, same rule
    /// as PortalKeys.cs/TagCodec.cs.
    /// </summary>
    public static class HookInstaller
    {
        public static void InstallAll(Harmony harmony)
        {
            ConnectPortalsHook.Install(harmony);
            SetConnectionHook.Install(harmony);
            FindRandomUnconnectedPortalHook.Install(harmony);
            RpcZdoDataHook.Install(harmony);
            HandleDestroyedZdoHook.Install(harmony);
            SenderContext.Install(harmony);

            PatchSelfTest.Run();
        }

        /// <summary>Name -> PatchOk, for PatchSelfTest and any engine that wants to check before relying on a broker.</summary>
        internal static IEnumerable<(string name, bool ok)> Status()
        {
            yield return ("Game.ConnectPortals", ConnectPortalsHook.PatchOk);
            yield return ("Game.SetConnection", SetConnectionHook.PatchOk);
            yield return ("Game.FindRandomUnconnectedPortal", FindRandomUnconnectedPortalHook.PatchOk);
            yield return ("ZDOMan.RPC_ZDOData", RpcZdoDataHook.PatchOk);
            yield return ("ZDOMan.RPC_DestroyZDO / HandleDestroyedZDO", HandleDestroyedZdoHook.PatchOk);
            yield return ("ZRoutedRpc.RPC_RoutedRPC (SenderContext)", SenderContext.PatchOk);
        }
    }
}
