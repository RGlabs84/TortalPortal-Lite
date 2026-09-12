using TortalPortalLite.Core;
using TortalPortalLite.Core.Hooks;

namespace TortalPortalLite.Subsystems.Foundations
{
    /// <summary>
    /// #200 Portal Dirty-Flag Guardian. The hole: a vanilla client's RPC_SetTag writes s_tag directly
    /// into its own ZDO copy via ZDOExtraData.Add - a dictionary assignment, not a ZDOMan call - and the
    /// change reaches the server through ZDOMan.RPC_ZDOData -> ZDO.Deserialize -> AddIfPortal, which
    /// early-returns (before setting DirtyPortalObjects) for a portal already in its bucket. So a
    /// player's own vanilla rename is never flagged for the next portal-chunk save and is silently lost
    /// at the next autosave unless something else dirties the flag first.
    ///
    /// Fix: piggyback on RpcZdoDataHook (already installed for ZDOMan.RPC_ZDOData; no second patch) and
    /// call ZDOMan.instance.SetDirtyPortals() whenever the incoming write's ZDOID is a known portal.
    /// Unconditional and cheap - SetDirtyPortals only flips a bool, and a redundant call when the write
    /// wasn't actually a rename costs nothing worth guarding against with a diff.
    /// </summary>
    public static class PortalDirtyFlagGuardian
    {
        public static void Initialize()
        {
            RpcZdoDataHook.RegisterPostfix(500, OnIncomingZdoData);
        }

        private static void OnIncomingZdoData(ZNetPeer? sender, ZDOID zdoid)
        {
            if (ZDOMan.instance == null || !PortalCensus.TryGet(zdoid, out _))
            {
                return;
            }
            ZDOMan.instance.SetDirtyPortals();
        }
    }
}
