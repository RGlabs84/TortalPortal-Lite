using System.Linq;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Hooks;

namespace TortalPortalLite.Subsystems.Enforcement
{
    /// <summary>
    /// #256 Managed-Portal Relay Shield. Of the three vanilla RPCs the catalog names as able to alter a
    /// managed portal from any client - DestroyZDO, Game.RPC_SetConnection, TeleportWorld's
    /// RPC_SetConnected - only the first is coverable with a true veto through EXISTING Core/Hooks/
    /// infrastructure: HandleDestroyedZdoHook is a genuine first-veto-wins prefix on the real ZDO-removal
    /// path (ZDOMan.HandleDestroyedZDO), reached once per id from RPC_DestroyZDO's own loop - so a handler
    /// here protects a managed portal per-ZDO, with no need to rewrite the incoming batch package the
    /// catalog's own howItWorks describes (that rewrite recipe was written against a broker this mod
    /// does not have; the existing per-ZDO veto achieves the same outcome more simply).
    ///
    /// RPC_SetConnection and RPC_SetConnected are NOT vetoable through this mod's existing brokers -
    /// SetConnectionHook is deliberately postfix/observe-only (catalog's own architecture choice), and
    /// neither RPC has any other broker:
    /// NEEDS NEW HOOK BROKER on Game.RPC_SetConnection(long,ZDOID,ZDOID) /
    /// TeleportWorld.RPC_SetConnected(long,ZDOID): purpose - true veto of a client-originated rewire of a
    /// managed portal's connection, mirroring HandleDestroyedZdoHook's shape. Until either exists, the
    /// mitigation is the SAME detect-and-revert re-assert tick LockdownForceDisconnectEngine/
    /// LockdownInvariantHarness already run off SetConnectionHook's postfix - covered there, not
    /// duplicated here.
    /// </summary>
    public static class LockdownRelayShieldEngine
    {
        private static bool _installed;

        public static void Initialize()
        {
            if (_installed)
            {
                return;
            }
            _installed = true;
            HandleDestroyedZdoHook.Register(20, VetoDestroyOfProtectedPortal);
        }

        private static bool VetoDestroyOfProtectedPortal(ZDO zdo, long sender)
        {
            if (LockdownConfig.RelayShieldProtectDestroy?.Value != true)
            {
                return true; // Feature off - never interferes with vanilla's own destroy path.
            }
            if (Game.instance == null || !Game.instance.PortalPrefabHash.Contains(zdo.GetPrefab()))
            {
                return true; // Not a portal.
            }
            if (!LockdownVault.IsLocked(zdo.GetPosition()))
            {
                return true; // Not currently managed/locked - never protects an ordinary player portal.
            }
            if (sender == 0L)
            {
                return true; // Server-internal destroy (this mod's own engines, e.g. a phantom reap) - never veto ourselves.
            }

            if (LockdownConfig.RelayShieldAllowCreatorDestroy?.Value != false)
            {
                long creatorId = zdo.GetLong(ZDOVars.s_creator, 0L);
                ZNetPeer peer = ZNet.instance?.GetPeers().FirstOrDefault(p => p != null && p.m_uid == sender);
                if (peer != null)
                {
                    if (creatorId != 0L && ZDOMan.instance != null)
                    {
                        ZDO characterZdo = ZDOMan.instance.GetZDO(peer.m_characterID);
                        long senderPlayerId = characterZdo != null && characterZdo.IsValid() ? characterZdo.GetLong(ZDOVars.s_playerID, 0L) : 0L;
                        if (senderPlayerId != 0L && senderPlayerId == creatorId)
                        {
                            return true; // The portal's own creator may still demolish it.
                        }
                    }
                    string hostName = peer.m_socket?.GetHostName();
                    if (!string.IsNullOrEmpty(hostName) && ZNet.instance.IsAdmin(hostName))
                    {
                        return true; // An admin may always demolish a managed portal.
                    }
                }
            }

            PortalDebug.LogWarning($"[SECURITY:ManagedPortalDestroyBlocked] peer-uid {sender} tried to destroy managed portal {zdo.m_uid} at {zdo.GetPosition():F0} - vetoed.");
            return false;
        }
    }
}
