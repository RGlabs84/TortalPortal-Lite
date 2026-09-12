using System;
using TortalPortalLite.Core;

namespace TortalPortalLite.Core.Data
{
    /// <summary>
    /// The write primitive every engine in this mod uses to change a portal ZDO - never a bare
    /// zdo.Set(...) from Subsystems code. Grounded directly in the catalog's adversarially-verified
    /// corrections (OPTION-CATALOG-VERIFIED.md, option #1 "Simple Pair"), not a guess:
    ///
    ///  - Claiming ownership (SetOwner) does NOT win a DataRevision race by itself -
    ///    ZDOMan.RPC_ZDOData accepts any incoming packet with a HIGHER DataRevision regardless of who
    ///    owns the ZDO, and SetOwner only bumps OwnerRevision, never DataRevision. What ownership
    ///    actually buys: a routed RPC (RPC_SetTag, RPC_SetConnected, ...) from a vanilla client is
    ///    dropped while the server holds ownership, because there is no TeleportWorld component
    ///    server-side to receive it (ZNetView.HandleRoutedRPC needs a registered instance) - so a
    ///    player standing at the portal can't retag it out from under a write in progress.
    ///  - ZDOMan.ReleaseNearbyZDOS (every ~2s) hands ownership back to any nearby peer, so a claim is
    ///    NOT durable - re-claim before every write, never assume a prior claim still holds.
    ///  - The real safety net against a genuine revision tie is a SECOND, distinct write immediately
    ///    after the real one (the "revision-tie hazard" correction) - it bumps DataRevision a second
    ///    time, past whatever a concurrently-writing client might have landed at.
    ///  - ForceSendZDO pushes the result immediately rather than waiting for the ordinary per-peer sync
    ///    cadence, so a reassert is visible to nearby clients within one network tick.
    /// </summary>
    public static class PortalOwnership
    {
        /// <summary>
        /// Claims ownership, runs <paramref name="writes"/> against the ZDO, performs the revision-tie
        /// safety write, then force-sends. <paramref name="writes"/> should only call ZDO.Set*/SetConnection
        /// - never SetOwner or ForceSendZDO itself, both handled here so every caller gets the same
        /// safety net.
        /// </summary>
        public static void ClaimAndWrite(ZDO zdo, Action<ZDO> writes)
        {
            if (zdo == null || !zdo.IsValid() || ZDOMan.instance == null)
            {
                return;
            }

            try
            {
                zdo.SetOwner(ZDOMan.GetSessionID());
                writes(zdo);
                // Revision-tie hazard: bump DataRevision a second time with a harmless no-op-ish write
                // so this write's revision strictly exceeds any single concurrent client write that
                // landed at the same tick.
                zdo.Set(PortalKeys.SchemaVersion, zdo.GetInt(PortalKeys.SchemaVersion, 0));
                ZDOMan.instance.ForceSendZDO(zdo.m_uid);
            }
            catch (Exception ex)
            {
                PortalDebug.LogWarning($"[PortalOwnership] claim-and-write failed for {zdo.m_uid}: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}
