using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;
using TortalPortalLite.Core.Hooks;
using TortalPortalLite.Subsystems.Foundations;

namespace TortalPortalLite.Subsystems.Enforcement
{
    /// <summary>
    /// #252 Placement Policy Engine. Generalises a per-player-cap idea into a rule chain evaluated at
    /// the ZDO-arrival hook: spacing, biome bans, interior ban, and ward-permission - all pure ZDO/
    /// geometry reads, rejection by SetOwner+DestroyZDO (with a ground-drop refund, #253, so a
    /// server-side rejection never costs materials).
    ///
    /// The catalog's own prescribed hook pair is ZDOMan.CreateNewZDO(ZDOID,Vector3,int) postfix +
    /// ZDO.Deserialize postfix (Wonderland's SpawnGovernor precedent) - neither is covered by
    /// Core/Hooks/. As with #254 Portal Sanctuary, this is built on RpcZdoDataHook's existing postfix
    /// instead: it already fires for every incoming client-authored ZDO write, and "first time this
    /// engine has seen this ZDOID" is an equally valid arrival signal for a portal (created once, then
    /// only its tag/connection change) without a second Harmony patch pair.
    ///
    /// Ward-permission reads PrivateArea's own ZDO schema directly (all public ZDO.Get* calls, confirmed
    /// against the decompile - ZDOVars.s_permitted count + "pu_id{i}" long entries, ZDOVars.s_enabled,
    /// ZDOVars.s_creator) rather than via PrivateArea.GetPermittedPlayers() (private instance method) -
    /// avoids both a Publicize-only call on a MonoBehaviour instance this engine never has a live
    /// reference to, and a Publicize dependency for something the ZDO layer already exposes directly.
    /// </summary>
    public static class LockdownPlacementPolicyEngine
    {
        private const int SeenCap = 20000;
        private static readonly HashSet<ZDOID> _seen = new HashSet<ZDOID>();

        public static void Initialize()
        {
            RpcZdoDataHook.RegisterPostfix(400, OnZdoDataArrived);
        }

        private static void OnZdoDataArrived(ZNetPeer? sender, ZDOID zdoid)
        {
            if (LockdownConfig.PlacementPolicyEnabled?.Value != true)
            {
                return;
            }
            if (_seen.Count >= SeenCap)
            {
                _seen.Clear();
            }
            if (!_seen.Add(zdoid) || ZDOMan.instance == null || Game.instance == null)
            {
                return;
            }
            ZDO zdo = ZDOMan.instance.GetZDO(zdoid);
            if (zdo == null || !zdo.IsValid() || !Game.instance.PortalPrefabHash.Contains(zdo.GetPrefab()))
            {
                return; // Not a portal, or an update to an already-known one.
            }
            Evaluate(zdo);
        }

        private static void Evaluate(ZDO zdo)
        {
            Vector3 pos = zdo.GetPosition();
            long creatorId = zdo.GetLong(ZDOVars.s_creator, 0L);

            string reason = CheckSpacing(zdo, pos)
                ?? CheckBiomeBan(pos)
                ?? CheckInterior(pos)
                ?? CheckWards(pos, creatorId)
                ?? CheckCap(creatorId);

            if (reason == null)
            {
                return; // Accepted.
            }

            Reject(zdo, pos, creatorId, reason);
        }

        private static string CheckSpacing(ZDO zdo, Vector3 pos)
        {
            float minSpacing = LockdownConfig.PlacementMinSpacingMeters?.Value ?? 0f;
            if (minSpacing <= 0f)
            {
                return null;
            }
            float sqr = minSpacing * minSpacing;
            foreach (PortalRecord rec in PortalCensus.Latest)
            {
                if (rec.Uid == zdo.m_uid)
                {
                    continue; // Not yet added to the census this tick in the common case, but guard anyway.
                }
                if ((rec.Position - pos).sqrMagnitude < sqr)
                {
                    return $"portals must be at least {minSpacing:F0}m apart";
                }
            }
            return null;
        }

        private static string CheckBiomeBan(Vector3 pos)
        {
            string list = LockdownConfig.PlacementBiomeBanList?.Value ?? "";
            if (string.IsNullOrWhiteSpace(list))
            {
                return null;
            }
            Heightmap.Biome biome = PortalCensus.BiomeAt(pos);
            foreach (string entry in list.Split(','))
            {
                if (System.Enum.TryParse(entry.Trim(), true, out Heightmap.Biome banned) && banned == biome)
                {
                    return $"no portals in {biome}";
                }
            }
            return null;
        }

        private static string CheckInterior(Vector3 pos)
        {
            if (LockdownConfig.PlacementBanInteriors?.Value == true && pos.y > 3000f)
            {
                return "no portals inside dungeon interiors";
            }
            return null;
        }

        private static string CheckWards(Vector3 pos, long creatorId)
        {
            if (LockdownConfig.PlacementRespectWards?.Value != true)
            {
                return null;
            }
            List<ZDO> nearby = ZdoSpatialQuery.FindNear(pos, 20f); // generous - narrowed to actual ward radius per-candidate below
            foreach (ZDO ward in nearby)
            {
                if (ward == null || !ward.IsValid() || ZNetScene.instance == null)
                {
                    continue;
                }
                GameObject prefab = ZNetScene.instance.GetPrefab(ward.GetPrefab());
                PrivateArea area = prefab != null ? prefab.GetComponent<PrivateArea>() : null;
                if (area == null)
                {
                    continue;
                }
                float radius = area.m_radius;
                if ((ward.GetPosition() - pos).sqrMagnitude > radius * radius)
                {
                    continue;
                }
                if (!ward.GetBool(ZDOVars.s_enabled, true))
                {
                    continue;
                }
                long wardOwner = ward.GetLong(ZDOVars.s_creator, 0L);
                if (creatorId != 0L && wardOwner == creatorId)
                {
                    continue; // The ward's own owner may always build inside it.
                }
                if (IsPermitted(ward, creatorId))
                {
                    continue;
                }
                return "not permitted on this ward";
            }
            return null;
        }

        private static bool IsPermitted(ZDO ward, long playerId)
        {
            if (playerId == 0L)
            {
                return false;
            }
            int count = ward.GetInt(ZDOVars.s_permitted);
            for (int i = 0; i < count; i++)
            {
                if (ward.GetLong("pu_id" + i, 0L) == playerId)
                {
                    return true;
                }
            }
            return false;
        }

        private static string CheckCap(long creatorId)
        {
            int max = LockdownConfig.PlacementMaxPerPlayer?.Value ?? 0;
            if (max <= 0 || creatorId == 0L)
            {
                return null;
            }
            int count = PortalCensus.Latest.Count(r => r.Creator == creatorId);
            return count >= max ? $"maximum {max} portal(s) per player already reached" : null;
        }

        private static void Reject(ZDO zdo, Vector3 pos, long creatorId, string reason)
        {
            if (ZDOMan.instance == null)
            {
                return;
            }
            int prefabHash = zdo.GetPrefab();
            try
            {
                zdo.SetOwner(ZDOMan.GetSessionID());
                ZDOMan.instance.DestroyZDO(zdo);
            }
            catch (System.Exception ex)
            {
                PortalDebug.LogWarning($"[LockdownPlacementPolicyEngine] reject-destroy failed: {ex.Message}");
                return;
            }

            PortalDebug.LogInfo($"[LockdownPlacementPolicyEngine] rejected portal placement at {pos:F0} (creator {creatorId}): {reason}.");

            if (LockdownConfig.GroundDropRefundEnabled?.Value != false)
            {
                LockdownGroundDropRefund.Refund(prefabHash, pos, creatorId);
            }

            if (creatorId != 0L)
            {
                foreach (ConnectedCharacter c in ConnectedCharacters.All())
                {
                    if (c.PlayerId == creatorId)
                    {
                        LockdownAnnouncementEngine.ToastPlayer(c, $"Portal placement refused: {reason}", center: false);
                        break;
                    }
                }
            }
        }
    }
}
