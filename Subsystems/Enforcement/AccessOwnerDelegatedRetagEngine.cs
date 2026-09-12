using System;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;

namespace TortalPortalLite.Subsystems.Enforcement
{
    /// <summary>
    /// #209 Owner-Delegated Retag. The race-free write primitive for reverting a change on a portal a
    /// CONNECTED client currently owns: instead of a local ClaimAndWrite (which only wins if this
    /// engine's own DataRevision ends up higher - the "revision-tie hazard" PortalOwnership.cs's own doc
    /// comment names), send vanilla's own `RPC_SetTag`/`RPC_SetConnected` TO the owning client via
    /// `ZRoutedRpc.instance.InvokeRoutedRPC(ownerPeerUid, portalZdo.m_uid, "RPC_SetTag", tag, authorId)`.
    /// Both handlers gate only on `IsOwner()`/`m_nview.IsOwner()` and ignore `sender` entirely, so the
    /// owning client executes the write itself and pushes the result back as an ordinary ZDOData sync -
    /// no race, because the owner authored it.
    ///
    /// Falls back to the local ClaimAndWrite path (this mod becomes the owner first) whenever the
    /// current owner is 0 or not a connected peer - exactly `Game.SetConnection`'s own deferral test.
    /// `authorId` is always the socket-verified value, never a client-supplied one (see #209's own
    /// failure-mode warning: "authorId is written verbatim - pass the socket-verified PlatformUserID or
    /// empty string, never the client-supplied value").
    /// </summary>
    public static class AccessOwnerDelegatedRetagEngine
    {
        public static void RevertTag(ZDO zdo, string approvedTag, string verifiedAuthorId)
        {
            if (zdo == null || !zdo.IsValid())
            {
                return;
            }
            try
            {
                long owner = zdo.GetOwner();
                if (owner != 0L && ZNet.instance != null && ZNet.instance.GetPeer(owner) != null && ZRoutedRpc.instance != null)
                {
                    ZRoutedRpc.instance.InvokeRoutedRPC(owner, zdo.m_uid, "RPC_SetTag", approvedTag ?? "", verifiedAuthorId ?? "");
                }
                else
                {
                    PortalOwnership.ClaimAndWrite(zdo, z => z.Set(ZDOVars.s_tag, approvedTag ?? ""));
                }
            }
            catch (Exception ex)
            {
                PortalDebug.LogWarning($"[AccessOwnerDelegatedRetagEngine] RevertTag failed for {zdo.m_uid}: {ex.Message}");
                PortalOwnership.ClaimAndWrite(zdo, z => z.Set(ZDOVars.s_tag, approvedTag ?? ""));
            }
        }

        public static void RevertConnection(ZDO zdo, ZDOID approvedTarget)
        {
            if (zdo == null || !zdo.IsValid())
            {
                return;
            }
            try
            {
                long owner = zdo.GetOwner();
                if (owner != 0L && ZNet.instance != null && ZNet.instance.GetPeer(owner) != null && ZRoutedRpc.instance != null)
                {
                    // TeleportWorld.RPC_SetConnected(long sender, ZDOID connection) - see #209's citations.
                    ZRoutedRpc.instance.InvokeRoutedRPC(owner, zdo.m_uid, "RPC_SetConnected", approvedTarget);
                }
                else
                {
                    PortalOwnership.ClaimAndWrite(zdo, z => z.SetConnection(ZDOExtraData.ConnectionType.Portal, approvedTarget));
                }
            }
            catch (Exception ex)
            {
                PortalDebug.LogWarning($"[AccessOwnerDelegatedRetagEngine] RevertConnection failed for {zdo.m_uid}: {ex.Message}");
                PortalOwnership.ClaimAndWrite(zdo, z => z.SetConnection(ZDOExtraData.ConnectionType.Portal, approvedTarget));
            }
        }
    }
}
