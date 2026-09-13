using UnityEngine;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;
using TortalPortalLite.Subsystems.Foundations;

namespace TortalPortalLite.Subsystems.WorldOps
{
    /// <summary>
    /// Shared low-level write/lookup helpers for the economy domain - this domain's own copy of the
    /// pattern Subsystems/Topology/RoutingWriteOps.cs already established (read, not reused directly:
    /// per that file's sibling RoutingManagedPortalRegistry's own reasoning, each domain's plumbing
    /// should never break if a concurrently-edited sibling domain's internals change shape mid-wave).
    /// Every actual s_tag/ConnectionType.Portal write still goes through Core/Data/PortalOwnership.
    /// ClaimAndWrite - this class never bypasses it.
    /// </summary>
    public static class EconomyWriteOps
    {
        /// <summary>
        /// Writes <paramref name="tag"/> (if non-null) and/or <paramref name="connection"/> (if given)
        /// on <paramref name="zdo"/> ONLY if at least one differs from its current value. Returns true if
        /// a write was actually issued.
        /// </summary>
        public static bool Reassert(ZDO zdo, string? tag, ZDOID? connection)
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
            // ZDO.SetConnection sets DirtyPortalObjects itself, but a tag-only change does NOT (catalog
            // #69/#89's own citation: AddIfPortal's early-return means a tag-only write on an
            // already-registered portal never dirties the portal chunk on its own) - always set it
            // explicitly here so neither write path can be silently lost at the next world save.
            ZDOMan.instance?.SetDirtyPortals();
            return true;
        }

        public static bool Disconnect(ZDO zdo, string? tag = null) => Reassert(zdo, tag, ZDOID.None);

        /// <summary>Resolves a declared economy.json position to its LIVE portal ZDO via PortalCensus (never a cached ZDO reference - ZDOPool recycles objects). Null if no portal currently sits at that rounded position.</summary>
        public static ZDO? ResolveLivePortal(EconomyPosition pos)
        {
            if (ZDOMan.instance == null || pos == null || !PortalCensus.TryGetByPosition(pos.ToVector3(), out PortalRecord record))
            {
                return null;
            }
            ZDO zdo = ZDOMan.instance.GetZDO(record.Uid);
            return zdo != null && zdo.IsValid() ? zdo : null;
        }

        /// <summary>Force-resends an existing ZDO to one specific peer - guarantees TeleportWorld.TargetFound's "client must hold the destination ZDO" gate passes before that peer ever tries to use it (catalog #89's own failure-mode citation).</summary>
        public static void PrewarmToPeer(long peerUid, ZDOID targetZdoId)
        {
            ZDOMan.instance?.ForceSendZDO(peerUid, targetZdoId);
        }

        /// <summary>Prewarms every connected character within <paramref name="radius"/> of <paramref name="sourcePos"/> with <paramref name="targetZdoId"/>.</summary>
        public static void PrewarmToPeersNear(Vector3 sourcePos, float radius, ZDOID targetZdoId)
        {
            if (ZDOMan.instance == null || targetZdoId == ZDOID.None)
            {
                return;
            }
            float radiusSqr = radius * radius;
            foreach (ConnectedCharacter character in ConnectedCharacters.All())
            {
                if ((character.Position - sourcePos).sqrMagnitude <= radiusSqr)
                {
                    PrewarmToPeer(character.Peer.m_uid, targetZdoId);
                }
            }
        }

        /// <summary>Toasts every connected character within <paramref name="radius"/> of <paramref name="sourcePos"/> - catalog #89's "pair every park with a reason" guidance.</summary>
        public static void NotifyNear(Vector3 sourcePos, float radius, string message)
        {
            float radiusSqr = radius * radius;
            foreach (ConnectedCharacter character in ConnectedCharacters.All())
            {
                if ((character.Position - sourcePos).sqrMagnitude <= radiusSqr)
                {
                    PlayerNotify.Toast(character, message);
                }
            }
        }
    }
}
