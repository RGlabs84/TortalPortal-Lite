using System;
using System.Collections.Generic;
using UnityEngine;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;
using TortalPortalLite.Subsystems.Foundations;

namespace TortalPortalLite.Subsystems.Topology
{
    /// <summary>
    /// #16 Team / Faction Networks. Same TopologiesTagShardEngine suffix mechanism as #15, but the
    /// payload is a roster-derived faction code (admin-maintained in topologies.json's Factions block)
    /// instead of a per-player code, so membership can change without rebuilding any portal. Resolution
    /// priority, exactly the catalog's own order:
    ///   1. TopologiesKeys.FactionOverride - an explicit per-portal assignment (NEEDS NEW KEY; see that
    ///      file - fully functional today via this domain's own placeholder constant).
    ///   2. s_creator looked up in the admin's roster (Rosters: factionCode -> list of ZDOVars.s_playerID).
    ///   3. The nearest PrivateArea (ward) ZDO's permitted-player list - PrivateArea.SetPermittedPlayers
    ///      writes ZDOVars.s_permitted (a count) plus raw string keys "pu_id{i}" (SERVER decompile
    ///      :137338-137363), all plain ZDO data readable server-side with no live PrivateArea component
    ///      needed (PrivateArea.CheckAccess itself is useless server-side - m_allAreas is always empty,
    ///      so it returns true vacuously, :137547-137593 - this engine never calls it).
    /// A portal already owned by a declared Shape/Foundations network is skipped, same reasoning as
    /// Private Networks. Writes ONLY the tag, ONLY via PortalOwnership.ClaimAndWrite.
    /// </summary>
    public static class TopologiesFactionEngine
    {
        private const float WardSearchRadius = 25f; // generous - PrivateArea.m_radius is a per-prefab compiled Inspector field this mod cannot read from the decompile alone (see CapabilityProbe's own ward probe), so this errs wide rather than missing a legitimately-warded portal.
        private const int MaxPermittedScan = 64; // s_permitted is player-writable data; bound the scan regardless of what it claims.

        private static float _timer;

        public static void OnUpdate(float dt)
        {
            var factions = TopologiesDefinitions.Current.Factions;
            if (factions == null || !factions.Enabled || factions.Rosters.Count == 0)
            {
                return;
            }
            _timer += dt;
            float interval = TopologiesConfig.ShapeReassertSeconds?.Value ?? 2.0f;
            if (_timer < interval)
            {
                return;
            }
            _timer = 0f;
            Normalize(factions);
        }

        private static void Normalize(TopologyFactionsConfig factions)
        {
            if (ZDOMan.instance == null || !VersionMigration.DestructivePassesAllowed)
            {
                return;
            }

            var byPlayer = new Dictionary<long, string>();
            foreach (KeyValuePair<string, List<long>> kvp in factions.Rosters)
            {
                foreach (long playerId in kvp.Value)
                {
                    byPlayer[playerId] = kvp.Key;
                }
            }
            if (byPlayer.Count == 0)
            {
                return;
            }

            int budget = TopologiesConfig.MaxWritesPerTick?.Value ?? 50;
            int written = 0;

            foreach (PortalRecord record in PortalCensus.Latest)
            {
                if (written >= budget)
                {
                    break;
                }

                ZDO zdo = ZDOMan.instance.GetZDO(record.Uid);
                if (zdo == null || !zdo.IsValid())
                {
                    continue;
                }
                if (!string.IsNullOrEmpty(PortalRecordStore.GetNetworkId(zdo)))
                {
                    continue;
                }

                string faction = ResolveFaction(zdo, record, byPlayer);
                string payload = string.IsNullOrEmpty(faction) ? "" : "f" + faction;
                string baseDisplay = TopologiesTagShardEngine.BaseOf(record.Tag);
                string desired = TopologiesTagShardEngine.FitAndShard(baseDisplay, payload);

                if (record.Tag == desired)
                {
                    continue;
                }

                try
                {
                    PortalOwnership.ClaimAndWrite(zdo, z => z.Set(ZDOVars.s_tag, desired));
                    written++;
                }
                catch (Exception ex)
                {
                    PortalDebug.LogError($"[TopologiesFactionEngine] failed to shard {record.Uid}: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        private static string ResolveFaction(ZDO zdo, PortalRecord record, Dictionary<long, string> byPlayer)
        {
            string over = zdo.GetString(TopologiesKeys.FactionOverride, "");
            if (!string.IsNullOrEmpty(over))
            {
                return over;
            }

            if (record.Creator != 0L && byPlayer.TryGetValue(record.Creator, out string viaCreator))
            {
                return viaCreator;
            }

            return ResolveViaWard(record.Position, byPlayer);
        }

        private static string ResolveViaWard(Vector3 pos, Dictionary<long, string> byPlayer)
        {
            // PrivateArea.SetPermittedPlayers' own raw-string-per-index key scheme, :137338-137363 -
            // there is no ZDOVars constant for this; each index hashes its own "pu_id{i}" string.
            List<ZDO> nearby = ZdoSpatialQuery.FindNear(pos, WardSearchRadius);
            foreach (ZDO ward in nearby)
            {
                if (!ward.IsValid())
                {
                    continue;
                }
                int permitted = ward.GetInt(ZDOVars.s_permitted, 0);
                if (permitted <= 0)
                {
                    continue;
                }
                int scan = Math.Min(permitted, MaxPermittedScan);
                for (int i = 0; i < scan; i++)
                {
                    long playerId = ward.GetLong(("pu_id" + i).GetStableHashCode(), 0L);
                    if (playerId != 0L && byPlayer.TryGetValue(playerId, out string viaWard))
                    {
                        return viaWard;
                    }
                }
            }
            return "";
        }
    }
}
