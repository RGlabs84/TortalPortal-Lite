using System;
using System.Collections.Generic;
using TortalPortalLite.Core.Data;

namespace TortalPortalLite.Subsystems.Topology
{
    /// <summary>
    /// #187 Player's Bed, trimmed to just its lookup substrate: #207 Corpse-Run Gate's own origin
    /// resolution reuses this sweep (TryGetMostRecentBed) rather than re-implementing bed discovery a
    /// second time. The full #187 feature (claiming a "Home" hub portal via emote, keeping it reasserted)
    /// is not part of this build's wanted set and has been cut.
    ///
    /// Found by scanning bed ZDOs for ZDOVars.s_owner == the player's stable profile ID (Bed.RPC_SetOwner
    /// writes s_owner/s_ownerName, SERVER decompile :119813-119819 - persistent, offline-readable). Bed
    /// prefabs are discovered by component (TargetedPrefabDiscovery), not by guessed name, and indexed
    /// via a resumable ZdoSpatialQuery.PrefabSetSweeper rather than a one-shot scan, since s_owner is a
    /// long field shared with TombStone/GrapplingPoint.
    ///
    /// Ambiguity rule (vanilla lets a player own unlimited beds and never clears s_owner): this engine
    /// tracks the tick each bed ZDO's s_owner was FIRST observed to match a given player and, on lookup,
    /// returns that player's most-recently-first-seen-owned bed.
    /// </summary>
    public static class TargetedBedEngine
    {
        private static ZdoSpatialQuery.PrefabSetSweeper _sweeper;
        private static readonly Dictionary<long, ZDOID> _mostRecentBedByPlayer = new Dictionary<long, ZDOID>();
        private static readonly Dictionary<ZDOID, long> _firstSeenOwnerTickByBed = new Dictionary<ZDOID, long>();

        public static void OnUpdate(float dt)
        {
            if (TargetedPrefabDiscovery.BedHashes.Count == 0 || ZDOMan.instance == null)
            {
                return;
            }
            _sweeper ??= new ZdoSpatialQuery.PrefabSetSweeper(TargetedPrefabDiscovery.BedHashes);

            var results = new List<ZDO>();
            _sweeper.Advance(20, results);
            foreach (ZDO bed in results)
            {
                if (bed == null || !bed.IsValid())
                {
                    continue;
                }
                long owner = bed.GetLong(ZDOVars.s_owner, 0L);
                if (owner == 0L)
                {
                    continue;
                }

                if (!_firstSeenOwnerTickByBed.TryGetValue(bed.m_uid, out long firstSeen))
                {
                    firstSeen = DateTime.UtcNow.Ticks;
                    _firstSeenOwnerTickByBed[bed.m_uid] = firstSeen;
                }

                bool replace = !_mostRecentBedByPlayer.TryGetValue(owner, out ZDOID current)
                    || !_firstSeenOwnerTickByBed.TryGetValue(current, out long currentTick)
                    || firstSeen >= currentTick;
                if (replace)
                {
                    _mostRecentBedByPlayer[owner] = bed.m_uid;
                }
            }
        }

        /// <summary>Shared with #207 Corpse-Run Gate, which resolves a death's origin from this claimed-bed tracking.</summary>
        public static bool TryGetMostRecentBed(long playerId, out ZDOID bedId) => _mostRecentBedByPlayer.TryGetValue(playerId, out bedId);
    }
}
