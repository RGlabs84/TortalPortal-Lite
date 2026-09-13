using UnityEngine;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;
using TortalPortalLite.Subsystems.Foundations;

namespace TortalPortalLite.Subsystems.WorldOps
{
    /// <summary>
    /// Shared low-level write/guard helpers for every wildcard-A engine - mirrors
    /// Subsystems/Topology/RoutingWriteOps's own idempotent-reassert shape (read, not depended on
    /// directly, to keep this domain self-contained) so each engine's file states only ITS policy.
    /// Every actual s_tag/ConnectionType.Portal write still goes through
    /// Core/Data/PortalOwnership.ClaimAndWrite - this class never bypasses it.
    /// </summary>
    public static class WildcardAWriteOps
    {
        /// <summary>
        /// Task rule #5: NetworkReassertEngine (#69) is the only engine allowed to write s_tag/
        /// ConnectionType.Portal on an EXISTING, already-network-managed portal. Every wildcard-A engine
        /// that considers a direct tag/connection write on a portal it did not itself fabricate must
        /// check this first and back off (log + no-op) when true - a managed portal's tag/connection is
        /// NetworkReassertEngine's declared intent, not this domain's to fight over.
        /// </summary>
        public static bool IsNetworkManaged(ZDO zdo) => zdo != null && zdo.IsValid() && !string.IsNullOrEmpty(PortalRecordStore.GetNetworkId(zdo));

        /// <summary>
        /// Writes <paramref name="tag"/> (if non-null) and/or <paramref name="connection"/> (if given)
        /// on <paramref name="zdo"/> only if at least one differs from its current value
        /// (ZDOExtraData.SetConnection/Set both early-return on an unchanged value, so this is safe to
        /// call every tick). Returns true if a write was actually issued. Callers are responsible for
        /// having already checked IsNetworkManaged where that matters.
        /// </summary>
        public static bool ReassertTagAndConnection(ZDO zdo, string? tag, ZDOID? connection)
        {
            if (zdo == null || !zdo.IsValid())
            {
                return false;
            }

            string currentTag = zdo.GetString(ZDOVars.s_tag, "");
            ZDOID currentConnection = zdo.GetConnectionZDOID(ZDOExtraData.ConnectionType.Portal);

            bool tagWrong = tag != null && currentTag != tag;
            bool connectionWrong = connection.HasValue && currentConnection != connection.Value;
            if (!tagWrong && !connectionWrong)
            {
                return false;
            }

            PortalOwnership.ClaimAndWrite(zdo, z =>
            {
                if (tagWrong)
                {
                    z.Set(ZDOVars.s_tag, tag);
                }
                if (connectionWrong)
                {
                    z.SetConnection(ZDOExtraData.ConnectionType.Portal, connection!.Value);
                }
            });
            return true;
        }

        /// <summary>Fabricates a real, persistent, invisible (prefab-0) anchor ZDO at a position - the same recipe TargetedAnchorFactory/RoutingPhantomAnchorEngine already use (ZNetView.Awake's own fabrication sequence, SERVER decompile :82613-82631), kept local so this domain never depends on Topology's factories for its own anchors.</summary>
        public static ZDO? FabricateVoidZdo(Vector3 pos, Quaternion rot)
        {
            if (ZDOMan.instance == null)
            {
                return null;
            }
            try
            {
                ZDO zdo = ZDOMan.instance.CreateNewZDO(pos, 0);
                if (zdo == null)
                {
                    return null;
                }
                PortalOwnership.ClaimAndWrite(zdo, z =>
                {
                    z.Persistent = true;
                    z.Type = ZDO.ObjectType.Default;
                    z.Distant = false;
                    z.SetPrefab(0);
                    z.SetRotation(rot);
                });
                return zdo;
            }
            catch (System.Exception ex)
            {
                PortalDebug.LogError($"[WildcardAWriteOps] FabricateVoidZdo failed at {pos:F0}: {ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        /// <summary>Owner-gated destroy - ZDOMan.DestroyZDO is a silent no-op unless the server owns the ZDO (SERVER decompile :76929-76935).</summary>
        public static void OwnerDestroy(ZDO zdo)
        {
            if (zdo == null || !zdo.IsValid() || ZDOMan.instance == null)
            {
                return;
            }
            try
            {
                zdo.SetOwner(ZDOMan.GetSessionID());
                ZDOMan.instance.DestroyZDO(zdo);
            }
            catch (System.Exception ex)
            {
                PortalDebug.LogWarning($"[WildcardAWriteOps] OwnerDestroy failed for {zdo.m_uid}: {ex.Message}");
            }
        }
    }
}
