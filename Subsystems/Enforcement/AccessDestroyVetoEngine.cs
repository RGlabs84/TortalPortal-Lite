using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;
using TortalPortalLite.Core.Hooks;
using TortalPortalLite.Subsystems.Foundations;

namespace TortalPortalLite.Subsystems.Enforcement
{
    /// <summary>
    /// #56 Destroy Veto. `ZDOMan.RPC_DestroyZDO` performs NO sender/ownership/prefab check at all - any
    /// client speaking the vanilla protocol can broadcast a forged destroy for any portal in the world.
    /// HandleDestroyedZdoHook already installs the real veto point for this (a Harmony PREFIX on the
    /// private `ZDOMan.HandleDestroyedZDO(ZDOID)`, the actual per-ZDO removal - see that broker's own
    /// doc comment for why patching RPC_DestroyZDO itself would have shipped a veto that did nothing).
    /// Because that broker vetoes PER ZDOID inside vanilla's own destroy loop, refusing one protected id
    /// in a batch automatically lets every other id in the same packet through unaffected - no manual
    /// packet-splitting/re-dispatch is needed the way the catalog's own howItWorks worried about; the
    /// hook broker already made that simpler.
    ///
    /// Identity for the ACL decision comes from the Authentic Sender Context's ambient `Current`, not
    /// the raw `sender` long HandleDestroyedZdoHook also hands the veto - `RPC_RoutedRPC`'s prefix/
    /// postfix bracket the ENTIRE synchronous call stack down to `HandleDestroyedZDO`, so `Current` is
    /// still the socket-verified peer at this point (SenderContext's own doc comment names
    /// `ZDOMan.RPC_DestroyZDO` explicitly as one of the handlers it brackets). The raw `sender` argument
    /// is only used for the audit log when `Current` could not be resolved (e.g. a genuinely
    /// server-internal destroy, sender 0).
    ///
    /// Decision reuses AccessPortalAclEngine.Evaluate with BOTH change flags false - a destroy is
    /// neither a tag nor a connection change, so lock level Full still blocks it, Retag/Relink alone do
    /// not (matching those levels' own stated scope), owner/co-owner/network/ward all still apply, and
    /// an unclaimed portal auto-adopts whoever destroys it first (harmless - the portal is gone either
    /// way). This is the SAME chain #53 already defines, not a parallel one.
    /// </summary>
    public static class AccessDestroyVetoEngine
    {
        public static void Initialize()
        {
            HandleDestroyedZdoHook.Register(100, VetoIfProtected);
        }

        private static bool VetoIfProtected(ZDO zdo, long rawSender)
        {
            if (AccessConfig.DestroyVetoEnabled?.Value == false)
            {
                return true;
            }
            if (zdo == null || !zdo.IsValid() || !PortalRegistry.IsPortalPrefabHash(zdo.GetPrefab()))
            {
                return true; // not our concern - only portals are in scope for this domain's veto.
            }

            ZNetPeer? peer = SenderContext.Current;
            string actorHost = SenderContext.HostNameOf(peer) ?? "";
            long actorProfileId = 0L;
            if (peer != null && !peer.m_characterID.IsNone() && ZDOMan.instance != null)
            {
                ZDO charZdo = ZDOMan.instance.GetZDO(peer.m_characterID);
                if (charZdo != null && charZdo.IsValid())
                {
                    actorProfileId = charZdo.GetLong(ZDOVars.s_playerID, 0L);
                }
            }

            AccessPortalAclEngine.Decision decision = AccessPortalAclEngine.Evaluate(zdo, actorHost, actorProfileId, isTagChange: false, isConnectionChange: false, out string reason);
            if (decision == AccessPortalAclEngine.Decision.Allow)
            {
                return true;
            }

            PortalDebug.LogAlways($"[AccessDestroyVetoEngine] REFUSED destroy of {zdo.m_uid} ({PortalRecordStore.GetNetworkId(zdo)}): {reason} (actor: {(string.IsNullOrEmpty(actorHost) ? $"raw-sender:{rawSender}" : actorHost)}).");

            if (peer != null)
            {
                foreach (ConnectedCharacter who in ConnectedCharacters.All())
                {
                    if (who.Peer == peer)
                    {
                        PlayerNotify.Toast(who, $"You may not destroy this gate ({reason}).");
                        break;
                    }
                }
            }

            // Force-push the still-live ZDO back to the destroying client - it already destroyed its
            // OWN local GameObject when it queued the destroy, and refusing the removal here would
            // otherwise leave that one client looking at a hole until its zone reloads (#56's own
            // failure mode).
            ZDOMan.instance?.ForceSendZDO(zdo.m_uid);
            return false;
        }
    }
}
