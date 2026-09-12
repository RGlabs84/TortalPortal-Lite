using System.Collections.Generic;
using UnityEngine;
using TortalPortalLite.Core.Compat;

namespace TortalPortalLite.Core.Data
{
    /// <summary>
    /// Nearby-object lookups against ZDOMan's own sector data - never Physics.OverlapSphere, since no
    /// collider exists server-side for anything outside the single point Game.FixedUpdate pins the
    /// world-loading reference position to. Two distinct query shapes:
    ///  - FindNear: anchored to one known position (a portal, a player). Cheap - a handful of sectors -
    ///    safe to call every tick for every tracked object.
    ///  - PrefabSetScanner: unanchored ("every portal_wood on the map"). Time-budgeted and resumable so
    ///    a full-map pass never costs a frame spike; for a portal mod (1-2 tracked prefab names) this
    ///    is the right tool, not PrefabSetSweeper below.
    /// Forked from Wonderland's Core/Data/ZdoSpatialQuery.cs (namespace renamed only).
    /// </summary>
    public static class ZdoSpatialQuery
    {
        /// <summary>
        /// Every ZDO within roughly radiusMeters of worldPos. FindSectorObjects only narrows to whole
        /// 64m sectors (area is rounded up and area 0 still scans the containing sector), so this
        /// always post-filters to the real distance.
        /// </summary>
        public static List<ZDO> FindNear(Vector3 worldPos, float radiusMeters, List<ZDO> reuseBuffer = null)
        {
            List<ZDO> result = reuseBuffer ?? new List<ZDO>();
            result.Clear();
            if (ZDOMan.instance == null || ZoneSystem.instance == null)
            {
                return result;
            }

            int area = Mathf.Max(1, Mathf.CeilToInt(radiusMeters / ZoneSystem.c_ZoneSize));
            var raw = new List<ZDO>();

            // Split into separate methods, not an if/else inline, deliberately - see Core/Compat/GameShape.cs.
            switch (GameShape.Detected)
            {
                case GameShape.Build.Release10_SimulationDistance:
                    FindNear_Native(worldPos, area, raw);
                    break;
                case GameShape.Build.Legacy_FiveArgSectors:
                    GameShape.FindSectorObjectsLegacy(worldPos, area, raw);
                    break;
                default:
                    return result;
            }

            float radiusSqr = radiusMeters * radiusMeters;
            foreach (ZDO zdo in raw)
            {
                if (!zdo.IsValid())
                {
                    continue;
                }
                if ((zdo.GetPosition() - worldPos).sqrMagnitude <= radiusSqr)
                {
                    result.Add(zdo);
                }
            }
            return result;
        }

        private static void FindNear_Native(Vector3 worldPos, int area, List<ZDO> raw)
        {
            Vector2s sector = ZoneSystem.GetZone(worldPos);
            ZDOMan.instance.FindSectorObjects(sector, new SimulationDistance(area, 0, classic: true), raw);
        }

        /// <summary>
        /// Round-robins ZDOMan.GetAllZDOsWithPrefabIterative across a fixed set of prefab names, one
        /// chunk (that method's own internal ~400-sector budget) per Advance() call. Never blocks, never
        /// "completes" - it cycles the whole set forever, which is exactly what a background integrity
        /// sweep or census wants.
        /// </summary>
        public sealed class PrefabSetScanner
        {
            private readonly List<string> _prefabNames;
            private int _prefabIndex;
            private int _sectorCursor;

            public PrefabSetScanner(IEnumerable<string> prefabNames)
            {
                _prefabNames = new List<string>(prefabNames);
            }

            public int TrackedPrefabCount => _prefabNames.Count;

            /// <summary>Appends whatever this chunk found to <paramref name="results"/> (not cleared first).</summary>
            public void Advance(List<ZDO> results)
            {
                if (_prefabNames.Count == 0 || ZDOMan.instance == null)
                {
                    return;
                }

                string name = _prefabNames[_prefabIndex];
                bool done = ZDOMan.instance.GetAllZDOsWithPrefabIterative(name, results, ref _sectorCursor);
                if (done)
                {
                    _sectorCursor = 0;
                    _prefabIndex = (_prefabIndex + 1) % _prefabNames.Count;
                }
            }
        }

        /// <summary>
        /// One resumable walk of ZDOMan's whole sector array matching every ZDO against a SET of prefab
        /// hashes in a single pass. Reads the publicised ZDOMan.m_objectsBySector directly - this is why
        /// the csproj needs Publicize=true.
        /// </summary>
        public sealed class PrefabSetSweeper
        {
            private readonly HashSet<int> _prefabHashes;
            private int _sectorCursor;

            public PrefabSetSweeper(IEnumerable<int> prefabHashes)
            {
                _prefabHashes = new HashSet<int>(prefabHashes);
            }

            public int TrackedPrefabCount => _prefabHashes.Count;

            /// <summary>
            /// Walks up to <paramref name="sectorBudget"/> NON-EMPTY sectors from where the last call
            /// stopped, appending matches to <paramref name="results"/> (not cleared first). Returns
            /// true when the cursor ran off the end of the array - a full map pass completed - and has
            /// been reset to 0 for the next call.
            /// </summary>
            public bool Advance(int sectorBudget, List<ZDO> results)
            {
                if (_prefabHashes.Count == 0 || ZDOMan.instance == null)
                {
                    return false;
                }
                List<ZDO>[] sectors = ZDOMan.instance.m_objectsBySector;
                if (sectors == null)
                {
                    return false;
                }

                int visited = 0;
                while (_sectorCursor < sectors.Length)
                {
                    List<ZDO> sector = sectors[_sectorCursor];
                    _sectorCursor++;
                    if (sector == null)
                    {
                        continue;
                    }
                    for (int i = 0; i < sector.Count; i++)
                    {
                        ZDO zdo = sector[i];
                        if (zdo != null && zdo.IsValid() && _prefabHashes.Contains(zdo.GetPrefab()))
                        {
                            results.Add(zdo);
                        }
                    }
                    if (++visited >= sectorBudget)
                    {
                        return false;
                    }
                }
                _sectorCursor = 0;
                return true;
            }
        }
    }
}
