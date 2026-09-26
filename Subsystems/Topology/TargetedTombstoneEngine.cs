using System;
using System.Collections.Generic;
using UnityEngine;
using TortalPortalLite.Core.Data;

namespace TortalPortalLite.Subsystems.Topology
{
    /// <summary>
    /// #189 Player's Death Spot / Tombstone, trimmed to just its lookup substrate: #207 Corpse-Run Gate's
    /// own death detection reuses this sweep rather than re-implementing tombstone discovery a second
    /// time. The full #189 feature (claiming a "Grave" hub portal via emote, keeping it reasserted) is not
    /// part of this build's wanted set and has been cut.
    ///
    /// Read straight off the TombStone container ZDO: `s_ownerName`/`s_owner` (TombStone.Setup, SERVER
    /// decompile :19845-19853) and `s_timeOfDeath` (TombStone.Awake, :19743-19746, written once by the
    /// owning client). s_timeOfDeath is DateTime TICKS of ZNet.instance.GetTime() - not seconds - so
    /// "newest tombstone" needs no extra first-seen bookkeeping (just the max over a player's owned
    /// tombstones) and a real age is available for free.
    ///
    /// Two discovery paths, because one is not enough for #207's "a gate ALWAYS appears" promise:
    ///  - The background sweep, for completeness: one resumable walk of every sector, so a tombstone that
    ///    was standing before this server booted, or one belonging to an offline player, is still found.
    ///    It is paced, so on a large map a full pass takes tens of seconds - far too slow on its own to
    ///    answer "this player just died".
    ///  - ObserveNear, for latency: the corpse-run engine calls it with the last known position of a
    ///    player whose character ZDO just went away (a death or a logout), which is exactly where a fresh
    ///    tombstone is. That resolves a death in one tick instead of one sweep.
    ///
    /// Tombstone prefabs are discovered by component (TargetedPrefabDiscovery), not by a guessed name.
    /// Filtered on the TombStone component specifically because s_owner is shared with Bed/GrapplingPoint.
    /// </summary>
    public static class TargetedTombstoneEngine
    {
        /// <summary>
        /// Non-empty sectors walked per tick by the background sweep. Each visited ZDO costs one
        /// int-hash-set lookup, so this is cheap; it was 20 through 1.0.7, which on a built-up map put a
        /// full pass in the tens of seconds and was the main reason a gate could lag a death badly.
        /// </summary>
        private const int SweepSectorsPerTick = 48;

        private static ZdoSpatialQuery.PrefabSetSweeper? _sweeper;
        private static readonly Dictionary<long, ZDOID> _newestTombstoneByPlayer = new Dictionary<long, ZDOID>();
        private static readonly List<ZDO> _results = new List<ZDO>();
        private static readonly List<ZDO> _nearBuffer = new List<ZDO>();
        private static HashSet<int>? _tombstoneHashes;

        public static void OnUpdate(float dt)
        {
            if (TargetedPrefabDiscovery.TombstoneHashes.Count == 0 || ZDOMan.instance == null)
            {
                return;
            }
            _sweeper ??= new ZdoSpatialQuery.PrefabSetSweeper(TargetedPrefabDiscovery.TombstoneHashes);

            _results.Clear();
            _sweeper.Advance(SweepSectorsPerTick, _results);
            Fold(_results);
        }

        /// <summary>
        /// Folds every tombstone within <paramref name="radius"/> of <paramref name="pos"/> into the
        /// newest-tombstone table immediately, without waiting for the background sweep's cursor to come
        /// round. Sector data only (ZdoSpatialQuery.FindNear) - no physics, nothing instantiated.
        /// Returns how many tombstones were seen, so a caller can tell "looked and found nothing" from
        /// "could not look".
        /// </summary>
        public static int ObserveNear(Vector3 pos, float radius)
        {
            if (TargetedPrefabDiscovery.TombstoneHashes.Count == 0 || ZDOMan.instance == null)
            {
                return 0;
            }
            if (_tombstoneHashes == null)
            {
                _tombstoneHashes = new HashSet<int>(TargetedPrefabDiscovery.TombstoneHashes);
            }

            List<ZDO> near = ZdoSpatialQuery.FindNear(pos, radius, _nearBuffer);
            _results.Clear();
            for (int i = 0; i < near.Count; i++)
            {
                ZDO zdo = near[i];
                if (zdo != null && zdo.IsValid() && _tombstoneHashes.Contains(zdo.GetPrefab()))
                {
                    _results.Add(zdo);
                }
            }
            Fold(_results);
            return _results.Count;
        }

        /// <summary>Shared with #207 Corpse-Run Gate, which builds on this sweep rather than re-implementing tombstone discovery a second time.</summary>
        public static bool TryGetNewestTombstone(long playerId, out ZDOID tombId) => _newestTombstoneByPlayer.TryGetValue(playerId, out tombId);

        /// <summary>
        /// Every (player, newest tombstone) pair known right now, as a snapshot safe to iterate while the
        /// caller raises gates. #207 drives off this rather than off the connected-player list, so a
        /// player who died and disconnected before a gate could be raised still gets one - standing and
        /// waiting when they log back in.
        /// </summary>
        public static void Tracked(List<KeyValuePair<long, ZDOID>> into)
        {
            into.Clear();
            foreach (KeyValuePair<long, ZDOID> kvp in _newestTombstoneByPlayer)
            {
                into.Add(kvp);
            }
        }

        /// <summary>
        /// How long ago this tombstone was raised, from the client-written s_timeOfDeath (DateTime ticks
        /// of ZNet.GetTime()). TimeSpan.MaxValue when the timestamp is missing or the network clock is not
        /// up yet - a caller applying a maximum age must treat that as "unknown", never as "ancient".
        /// </summary>
        public static TimeSpan AgeOf(ZDO tomb)
        {
            if (tomb == null || !tomb.IsValid() || ZNet.instance == null)
            {
                return TimeSpan.MaxValue;
            }
            long ticks = tomb.GetLong(ZDOVars.s_timeOfDeath, 0L);
            if (ticks <= 0L)
            {
                return TimeSpan.MaxValue;
            }
            try
            {
                return ZNet.instance.GetTime() - new DateTime(ticks);
            }
            catch (ArgumentOutOfRangeException)
            {
                // A corrupt/foreign timestamp outside DateTime's range - unknown, not ancient.
                return TimeSpan.MaxValue;
            }
        }

        private static void Fold(List<ZDO> found)
        {
            for (int i = 0; i < found.Count; i++)
            {
                ZDO tomb = found[i];
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
                if (currentId == tomb.m_uid)
                {
                    continue;
                }
                ZDO? current = ZDOMan.instance.GetZDO(currentId);
                bool currentGone = current == null || !current.IsValid();
                long currentTime = currentGone ? -1L : current!.GetLong(ZDOVars.s_timeOfDeath, 0L);
                if (currentGone || timeOfDeath >= currentTime)
                {
                    _newestTombstoneByPlayer[owner] = tomb.m_uid;
                }
            }
        }
    }
}
