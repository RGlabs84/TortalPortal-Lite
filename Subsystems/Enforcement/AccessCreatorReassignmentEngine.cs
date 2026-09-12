using System.Collections.Generic;
using UnityEngine;
using Splatform;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;
using TortalPortalLite.Subsystems.Foundations;

namespace TortalPortalLite.Subsystems.Enforcement
{
    /// <summary>
    /// #215 Creator Reassignment. `Piece.SetCreator` only ever writes when the field is still 0 - vanilla
    /// never overwrites a non-zero creator again, so nothing stops a SERVER write from being
    /// authoritative once delivered: `ZDO.Set` performs no ownership check and `ZDO.Deserialize` is
    /// additive. Scoped strictly to `Game.instance.PortalPrefabHash` per the option's own warning
    /// (applying this to a guard stone would silently flip its ward creatorship instead).
    ///
    /// `s_creator` is the target's own `PlayerProfile.m_playerID` - a per-CHARACTER value that only
    /// exists in this server's memory for a CURRENTLY CONNECTED account (their own character ZDO's
    /// `s_playerID`, the same field Player.SetPlayerID stamps and ConnectedCharacters.PlayerId reads).
    /// There is no server-side record of an offline account's PlayerProfile.m_playerID (player history
    /// only stores PlatformUserID + display name, not this), so this engine only supports reassigning TO
    /// a currently-connected account - reassigning to someone offline is refused with a clear reason
    /// rather than guessing or inventing a value.
    ///
    /// Also bridges into Portal Record Store's own ownership (#50) at the same time, since "transfer
    /// this gate" naturally means both the vanilla-visible creator AND this mod's ACL owner change
    /// together - not two separate admin actions.
    /// </summary>
    public static class AccessCreatorReassignmentEngine
    {
        public static string ReassignToConnectedAccount(Vector3 portalPos, string targetPlatformUserId)
        {
            if (ZDOMan.instance == null)
            {
                return "ZDOMan not ready.";
            }
            if (!PortalCensus.TryGetByPosition(portalPos, out PortalRecord record))
            {
                return "no portal at that position.";
            }
            ZDO? zdo = ZDOMan.instance.GetZDO(record.Uid);
            if (zdo == null || !zdo.IsValid())
            {
                return "portal ZDO no longer resolvable.";
            }
            if (!PortalRegistry.IsPortalPrefabHash(zdo.GetPrefab()))
            {
                return "that ZDO is not a portal prefab - refusing (this would silently flip ward/other creatorship if it were, see #215's own warning).";
            }

            ConnectedCharacter? target = null;
            foreach (ConnectedCharacter candidate in ConnectedCharacters.All())
            {
                if (string.Equals(SenderContext.HostNameOf(candidate.Peer), targetPlatformUserId, System.StringComparison.OrdinalIgnoreCase))
                {
                    target = candidate;
                    break;
                }
            }
            if (target == null)
            {
                return $"'{targetPlatformUserId}' is not currently connected - this engine can only resolve a live PlayerProfile.m_playerID, not an offline one.";
            }

            long newProfileId = target.Value.Zdo.GetLong(ZDOVars.s_playerID, 0L);
            if (newProfileId == 0L)
            {
                return "target's own s_playerID has not landed on the server yet - try again shortly.";
            }

            int trueIdx = -1;
            List<ZNet.CrossNetworkUserInfo> history = ZNet.World?.m_playerHistory ?? new List<ZNet.CrossNetworkUserInfo>();
            string wantId = new PlatformUserID(targetPlatformUserId).ToString();
            trueIdx = history.FindIndex(h => h.m_id.ToString() == wantId);

            PortalOwnership.ClaimAndWrite(zdo, z =>
            {
                z.Set(ZDOVars.s_creator, newProfileId);
                if (trueIdx >= 0)
                {
                    z.Set(ZDOVars.s_creatorIndex, trueIdx);
                }
            });
            ZDOMan.instance.SetDirtyPortals();
            PortalRecordStore.SetOwnerPlatformId(zdo, targetPlatformUserId);

            PortalDebug.LogAlways($"[AccessCreatorReassignmentEngine] reassigned {record.Uid} to {targetPlatformUserId} (profileId={newProfileId}, historyIdx={trueIdx}).");
            return $"reassigned {record.Uid} to {targetPlatformUserId}.";
        }
    }
}
