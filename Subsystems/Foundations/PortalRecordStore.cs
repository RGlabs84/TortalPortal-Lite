using System;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;

namespace TortalPortalLite.Subsystems.Foundations
{
    /// <summary>
    /// #50 Portal Record Store. Where per-portal ownership, network membership and lock state actually
    /// live: mod-private ZDO keys (PortalKeys.RecordId/RecordOwner/NetworkId/RecordLocked/
    /// RecordClaimedTicks), NOT vanilla's s_creator (a bare long with no room for the rest of this
    /// record) or s_tagauthor (overwritten by every legitimate RPC_SetTag). ZDO.Set has no ownership
    /// check on any overload, so any code can write these - but only PortalOwnership.ClaimAndWrite
    /// (which also handles the SetDirtyPortals()/ForceSendZDO bookkeeping) should actually call it.
    ///
    /// RecordId is a mod-minted stable identity independent of the ZDO's own m_uid, which regenerates
    /// on every world load (ZDO.Load re-mints it via ZDOID.m_loadID) - this is what networks.json
    /// persists membership by, never a raw ZDOID.
    /// </summary>
    public static class PortalRecordStore
    {
        public static bool HasRecord(ZDO zdo) => zdo.GetLong(PortalKeys.RecordId, 0L) != 0L;

        public static long GetRecordId(ZDO zdo) => zdo.GetLong(PortalKeys.RecordId, 0L);

        public static string GetNetworkId(ZDO zdo) => zdo.GetString(PortalKeys.NetworkId, "");

        public static bool IsLocked(ZDO zdo) => zdo.GetInt(PortalKeys.RecordLocked, 0) != 0;

        public static string GetOwnerPlatformId(ZDO zdo) => zdo.GetString(PortalKeys.RecordOwner, "");

        /// <summary>Mints a RecordId if this portal doesn't already have one - idempotent, safe to call every reassert tick.</summary>
        public static void EnsureRecordId(ZDO zdo)
        {
            if (HasRecord(zdo))
            {
                return;
            }
            PortalOwnership.ClaimAndWrite(zdo, z => z.Set(PortalKeys.RecordId, MintRecordId(z)));
        }

        public static void SetNetworkId(ZDO zdo, string networkId)
        {
            PortalOwnership.ClaimAndWrite(zdo, z =>
            {
                z.Set(PortalKeys.NetworkId, networkId ?? "");
                z.Set(PortalKeys.RecordClaimedTicks, DateTime.UtcNow.Ticks);
            });
        }

        public static void SetLocked(ZDO zdo, bool locked)
        {
            PortalOwnership.ClaimAndWrite(zdo, z => z.Set(PortalKeys.RecordLocked, locked ? 1 : 0));
        }

        public static void SetOwnerPlatformId(ZDO zdo, string platformUserId)
        {
            PortalOwnership.ClaimAndWrite(zdo, z => z.Set(PortalKeys.RecordOwner, platformUserId ?? ""));
        }

        /// <summary>
        /// A RecordId only needs to be unique among portals that exist at the same time, and stable for
        /// the rest of that portal's life - it is never looked up by value across a restart the way a
        /// database primary key would be (networks.json instead keys by network name -> the CURRENT
        /// resolution described in NetworkModel), so a timestamp+random pair is sufficient.
        /// </summary>
        private static long MintRecordId(ZDO zdo)
        {
            long ticks = DateTime.UtcNow.Ticks;
            long salt = zdo.m_uid.ID;
            return (ticks << 16) ^ salt;
        }
    }
}
