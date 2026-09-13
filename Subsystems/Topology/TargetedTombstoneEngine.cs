using System.Collections.Generic;
using TortalPortalLite.Core.Data;

namespace TortalPortalLite.Subsystems.Topology
{
    /// <summary>
    /// #189 Player's Death Spot / Tombstone, trimmed to just its lookup substrate: #207 Corpse-Run Gate's
    /// own death detection reuses this sweep (TryGetNewestTombstone) rather than re-implementing
    /// tombstone discovery a second time. The full #189 feature (claiming a "Grave" hub portal via emote,
    /// keeping it reasserted) is not part of this build's wanted set and has been cut.
    ///
    /// Read straight off the TombStone container ZDO: `s_ownerName`/`s_owner` (TombStone.Setup, SERVER
    /// decompile :19845-19853) and `s_timeOfDeath` (TombStone.Awake, :19732-19740, written once by the
    /// owning client). s_timeOfDeath is a real timestamp, so "newest tombstone" needs no extra
    /// first-seen bookkeeping - just the max over a player's owned tombstones.
    ///
    /// Tombstone prefabs are discovered by component (TargetedPrefabDiscovery), not by a guessed name.
    /// Filtered on the TombStone component specifically because s_owner is shared with Bed/GrapplingPoint.
    /// </summary>
    public static class TargetedTombstoneEngine
    {
        private static ZdoSpatialQuery.PrefabSetSweeper _sweeper;
        private static readonly Dictionary<long, ZDOID> _newestTombstoneByPlayer = new Dictionary<long, ZDOID>();

        public static void OnUpdate(float dt)
        {
            if (TargetedPrefabDiscovery.TombstoneHashes.Count == 0 || ZDOMan.instance == null)
            {
                return;
            }
            _sweeper ??= new ZdoSpatialQuery.PrefabSetSweeper(TargetedPrefabDiscovery.TombstoneHashes);

            var results = new List<ZDO>();
            _sweeper.Advance(20, results);
            foreach (ZDO tomb in results)
            {
                if (tomb == null || !tomb.IsValid())
                {
                    continue;
                }
                long owner = tomb.GetLong(ZDOVars.s_owner, 0L);
                if (owner == 0L)
                {
                    continue;
                }
                long timeOfDeath = tomb.GetLong(ZDOVars.s_timeOfDeath, 0L);

                if (!_newestTombstoneByPlayer.TryGetValue(owner, out ZDOID currentId))
                {
                    _newestTombstoneByPlayer[owner] = tomb.m_uid;
                    continue;
                }
                ZDO current = ZDOMan.instance.GetZDO(currentId);
                long currentTime = current != null && current.IsValid() ? current.GetLong(ZDOVars.s_timeOfDeath, 0L) : -1L;
                if (current == null || !current.IsValid() || timeOfDeath >= currentTime)
                {
                    _newestTombstoneByPlayer[owner] = tomb.m_uid;
                }
            }
        }

        /// <summary>Shared with #207 Corpse-Run Gate, which builds on this sweep rather than re-implementing tombstone discovery a second time.</summary>
        public static bool TryGetNewestTombstone(long playerId, out ZDOID tombId) => _newestTombstoneByPlayer.TryGetValue(playerId, out tombId);
    }
}
