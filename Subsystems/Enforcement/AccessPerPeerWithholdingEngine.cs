using System;
using System.Collections;
using System.Reflection;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;

namespace TortalPortalLite.Subsystems.Enforcement
{
    /// <summary>
    /// #211 Per-Peer ZDO Withholding + #212 Withholding Revocation &amp; Re-grant, combined - #212 is the
    /// mid-session half of the same mechanism #211 defines.
    ///
    /// HONESTY NOTE: the actual SEND-SIDE FILTER (a postfix on the private `ZDOMan.CreateSyncList` that
    /// strips a withheld ZDO from a specific peer's sync list, and injects nothing extra) has no
    /// Core/Hooks/ broker, and this wave's rules require flagging that rather than patching it directly:
    ///
    /// NEEDS NEW HOOK BROKER on ZDOMan.CreateSyncList(ZDOMan.ZDOPeer peer, List&lt;ZDO&gt; toSync): purpose -
    /// strip any ZDO for which AccessPerPeerWithholdingEngine.ShouldWithhold(viewer, zdoid) is true from
    /// `toSync` before it is serialised, and remove the id from `peer.m_forceSend` in the same pass so
    /// `AddForceSendZdos` does not immediately re-insert it. Until this hook exists, the DECISION logic
    /// below is fully implemented and ready for it to call, but nothing actually removes a withheld
    /// portal from a non-member's view - #211 as shipped in this wave is a policy engine, not yet an
    /// enforced one. (Also shares this same hook need with #213 Destination Pre-Delivery's ORIGINAL
    /// design - but #213 turned out to be fully achievable a different way, via the public
    /// peer-targeted `ForceSendZDO(long,ZDOID)` overload, so it needed no new hook after all; only the
    /// withholding DIRECTION - removing something already delivered - still does.)
    ///
    /// What #212's "pull it back out" half genuinely does not need a new hook for: sending a
    /// peer-TARGETED `DestroyZDO` routed RPC is just invoking an RPC name vanilla itself already
    /// registers on every peer (`ZDOMan`'s constructor registers "DestroyZDO" globally) - not a patch,
    /// an ordinary call. `Revoke(...)` below does exactly that, with the same wire shape
    /// `ZDOMan.SendDestroyed` uses (`int count` then that many `ZDOID`s). It best-effort purges the
    /// server's own per-peer send cache (`ZDOMan.ZDOPeer.m_zdos`, a private nested field) via reflection
    /// so a later re-grant is not silently refused by `ShouldSend` - reflection, not a Harmony patch, so
    /// it does not need a broker or a flag; it is simply best-effort against a private field that could
    /// rename, wrapped in try/catch.
    /// </summary>
    public static class AccessPerPeerWithholdingEngine
    {
        /// <summary>True if <paramref name="viewerPlatformId"/> should NOT be able to hold/see the portal ZDO <paramref name="zdoid"/> - the decision the flagged CreateSyncList hook would consult.</summary>
        public static bool ShouldWithhold(string viewerPlatformId, ZDOID zdoid)
        {
            if (ZDOMan.instance == null)
            {
                return false;
            }
            ZDO zdo = ZDOMan.instance.GetZDO(zdoid);
            if (zdo == null || !zdo.IsValid() || !PortalRegistry.IsPortalPrefabHash(zdo.GetPrefab()))
            {
                return false;
            }
            // Only ever withhold a portal this mod actively locks down - an ordinary unlocked/unclaimed
            // portal is never hidden from anyone, matching vanilla's own default visibility.
            if (!AccessAclStore.TryGet(zdo.GetPosition(), out AccessAclEntry entry) || entry.Lock != AccessLockLevel.Full)
            {
                return false;
            }
            AccessPortalAclEngine.Decision decision = AccessPortalAclEngine.Evaluate(zdo, viewerPlatformId, 0L, false, false, out _, allowMutation: false);
            return decision == AccessPortalAclEngine.Decision.Deny;
        }

        /// <summary>
        /// #212 - forcibly pulls an already-delivered ZDO back out of one specific peer's world. Safe to
        /// call even if that peer never actually held it (HandleDestroyedZDO no-ops on an unknown id).
        /// </summary>
        public static void Revoke(ZNetPeer peer, ZDOID zdoid)
        {
            if (peer == null || ZRoutedRpc.instance == null)
            {
                return;
            }
            try
            {
                var pkg = new ZPackage();
                pkg.Write(1);
                pkg.Write(zdoid);
                ZRoutedRpc.instance.InvokeRoutedRPC(peer.m_uid, "DestroyZDO", pkg);
                PurgePeerSendCache(peer.m_uid, zdoid);
            }
            catch (Exception ex)
            {
                PortalDebug.LogWarning($"[AccessPerPeerWithholdingEngine] revoke failed for {zdoid} on peer {peer.m_uid}: {ex.Message}");
            }
        }

        /// <summary>Best-effort removal of ZDOMan.ZDOPeer.m_zdos[id] via reflection, so a future re-grant is not refused by ShouldSend (it would otherwise think the peer already has this DataRevision). Never throws outward.</summary>
        private static void PurgePeerSendCache(long peerUid, ZDOID zdoid)
        {
            try
            {
                if (ZDOMan.instance == null)
                {
                    return;
                }
                MethodInfo? getPeer = typeof(ZDOMan).GetMethod("GetPeer", BindingFlags.NonPublic | BindingFlags.Instance, null, new[] { typeof(long) }, null);
                object? zdoPeer = getPeer?.Invoke(ZDOMan.instance, new object[] { peerUid });
                if (zdoPeer == null)
                {
                    return;
                }
                FieldInfo? zdosField = zdoPeer.GetType().GetField("m_zdos", BindingFlags.Public | BindingFlags.Instance);
                if (zdosField?.GetValue(zdoPeer) is IDictionary dict)
                {
                    dict.Remove(zdoid);
                }
            }
            catch (Exception ex)
            {
                PortalDebug.LogWarning($"[AccessPerPeerWithholdingEngine] send-cache purge failed (non-fatal, ShouldSend may refuse a re-grant until DataRevision moves): {ex.Message}");
            }
        }
    }
}
