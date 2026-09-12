using TortalPortalLite.Core.Data;

namespace TortalPortalLite.Subsystems.Foundations
{
    /// <summary>
    /// #137 Portals As A Data Store. Portal ZDOs are the only always-resident, always-saved,
    /// distance-independent ZDO collection in the game (never released, saved wholesale to their own
    /// chunk with no Persistent filter, loaded straight back at world load) - so an arbitrary
    /// mod-private string payload piggybacked on a portal ZDO that already exists for a real reason
    /// round-trips through both network and save for free. ZDO.Set has no ownership check on any
    /// overload, but writes still go through PortalOwnership.ClaimAndWrite for the SetDirtyPortals/
    /// ForceSendZDO/revision-tie discipline every other write in this mod uses.
    ///
    /// Namespaced so two engines sharing a host portal ZDO don't overwrite each other's payload -
    /// callers should use a stable per-engine prefix (e.g. "economy.toll", "wildcard.puzzle").
    /// </summary>
    public static class DataStore
    {
        public static bool TryGet(ZDO zdo, string ns, out string payload)
        {
            payload = "";
            if (zdo == null || !zdo.IsValid())
            {
                return false;
            }
            string storedNs = zdo.GetString(PortalKeys.DataStoreNamespace, "");
            if (storedNs != ns)
            {
                return false;
            }
            payload = zdo.GetString(PortalKeys.DataStorePayload, "");
            return true;
        }

        public static void Set(ZDO zdo, string ns, string payload)
        {
            PortalOwnership.ClaimAndWrite(zdo, z =>
            {
                z.Set(PortalKeys.DataStoreNamespace, ns ?? "");
                z.Set(PortalKeys.DataStorePayload, payload ?? "");
            });
        }
    }
}
