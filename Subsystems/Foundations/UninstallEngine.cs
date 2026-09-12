using System;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;

namespace TortalPortalLite.Subsystems.Foundations
{
    /// <summary>
    /// #79 (world-save footprint / key cleanup) + #80 (graceful uninstall), combined - #80's design is
    /// "ship an explicit uninstall command that lands the world in a clean, vanilla-stable state", and
    /// #79 is exactly the cleanup that command performs.
    ///
    /// Traced end to end: mod-created portal-prefab ZDOs are loaded and paired by vanilla like any other
    /// gate with no special handling needed - no phantoms, nothing to clean up there. What genuinely
    /// needs cleaning is this mod's own custom ZDO keys (PortalKeys.*), which ZDO.RemoveInt/RemoveLong/
    /// RemoveString (SERVER decompile :74022-74074, confirmed public) can actually delete - not just
    /// reset to a default value. After removal, an explicit SetDirtyPortals() ensures the cleaned state
    /// is what gets saved next, rather than leaving the dirty flag to chance.
    ///
    /// Managed topologies are NOT restored to a fully "vanilla-authored" random pairing by this pass -
    /// the catalog is explicit that this happens naturally at the next world load anyway (the on-disk
    /// connection-hash slot only expresses a pairwise perfect matching, so anything this mod built
    /// beyond simple pairs unravels on its own once NetworkReassertEngine stops running). This command
    /// only needs to remove the mod's own bookkeeping so nothing is left half-attributed to a plugin
    /// that's no longer installed.
    /// </summary>
    public static class UninstallEngine
    {
        public static string CleanKeys()
        {
            if (ZDOMan.instance == null)
            {
                return "tpl: ZDOMan not ready.";
            }

            int touched = 0;
            int failed = 0;
            foreach (PortalRecord record in PortalCensus.Latest)
            {
                ZDO zdo = ZDOMan.instance.GetZDO(record.Uid);
                if (zdo == null || !zdo.IsValid())
                {
                    continue;
                }
                try
                {
                    PortalOwnership.ClaimAndWrite(zdo, z =>
                    {
                        z.RemoveLong(PortalKeys.RecordId);
                        z.RemoveString(PortalKeys.RecordOwner);
                        z.RemoveString(PortalKeys.NetworkId);
                        z.RemoveInt(PortalKeys.RecordLocked);
                        z.RemoveLong(PortalKeys.RecordClaimedTicks);
                        z.RemoveString(PortalKeys.DataStoreNamespace);
                        z.RemoveString(PortalKeys.DataStorePayload);
                        z.RemoveLong(PortalKeys.VerifiedAuthorId);
                        z.RemoveInt(PortalKeys.SchemaVersion);
                        z.RemoveInt(PortalKeys.CommandSeq);
                    });
                    touched++;
                }
                catch (Exception ex)
                {
                    failed++;
                    PortalDebug.LogWarning($"[UninstallEngine] failed to clean keys on {record.Uid}: {ex.Message}");
                }
            }
            ZDOMan.instance.SetDirtyPortals();
            return $"tpl: cleaned mod keys from {touched} portal(s){(failed > 0 ? $", {failed} failed" : "")}.";
        }
    }
}
