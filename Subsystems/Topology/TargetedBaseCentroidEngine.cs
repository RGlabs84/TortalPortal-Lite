using System;
using System.Collections.Generic;
using UnityEngine;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;
using TortalPortalLite.Subsystems.Foundations;

namespace TortalPortalLite.Subsystems.Topology
{
    /// <summary>
    /// #195 Geometric Centre of a Base - portal to the centroid of a player's largest build cluster,
    /// computed from ZDOVars.s_creator on every piece ZDO (Piece.SetCreator, SERVER decompile
    /// :136415-136424). Piece prefabs are discovered by component (TargetedPrefabDiscovery.PieceHashes)
    /// and indexed via a resumable ZdoSpatialQuery.PrefabSetSweeper, the same incremental-discovery
    /// pattern #187/#189/#192 use, rather than a one-shot GetAllZDOIDsWithHash(Long, s_creator) scan
    /// (which would also match every OTHER creator-stamped ZDO in the world, not just pieces).
    ///
    /// Clustering: bucket each player's pieces by 64 m zone (ZoneSystem.GetZone) and flood-fill
    /// 4-connected zones into clusters; "the base" is the cluster with the most pieces. Height comes
    /// from the median Y of nearby floor-like pieces (name contains "floor") rather than
    /// WorldGenerator's pre-flattening height, which is wrong inside a raised base (#195's own note).
    /// Structure clearance is approximate (no server-side colliders): a handful of candidate points near
    /// the centroid are scored by "fewest piece ZDOs nearby" rather than validated against real geometry.
    /// </summary>
    public static class TargetedBaseCentroidEngine
    {
        private const string Kind = "BaseCentroid";
        private const int MaxTrackedPiecesPerPlayer = 3000;

        private static float _timer;
        private static float _recomputeTimer;
        private static ZdoSpatialQuery.PrefabSetSweeper _sweeper;
        private static readonly Dictionary<long, List<Vector3>> _piecesByCreator = new Dictionary<long, List<Vector3>>();

        public static void Initialize()
        {
            EmoteSignals.Register(OnEmote);
        }

        public static void OnUpdate(float dt)
        {
            SweepTick();

            _timer += dt;
            float interval = TargetedConfig.MaintenanceIntervalSeconds?.Value ?? 2f;
            if (_timer < interval)
            {
                return;
            }
            _timer = 0f;
            ReassertAll(interval);
        }

        private static void SweepTick()
        {
            if (TargetedPrefabDiscovery.PieceHashes.Count == 0 || ZDOMan.instance == null)
            {
                return;
            }
            _sweeper ??= new ZdoSpatialQuery.PrefabSetSweeper(TargetedPrefabDiscovery.PieceHashes);

            var results = new List<ZDO>();
            _sweeper.Advance(60, results);
            foreach (ZDO piece in results)
            {
                if (piece == null || !piece.IsValid())
                {
                    continue;
                }
                long creator = piece.GetLong(ZDOVars.s_creator, 0L);
                if (creator == 0L)
                {
                    continue;
                }
                if (!_piecesByCreator.TryGetValue(creator, out List<Vector3> list))
                {
                    list = new List<Vector3>();
                    _piecesByCreator[creator] = list;
                }
                if (list.Count < MaxTrackedPiecesPerPlayer)
                {
                    list.Add(piece.GetPosition());
                }
            }
        }

        private static void OnEmote(ConnectedCharacter who, string emote)
        {
            // Same simple claim gesture as #187 Bed (TargetedConfig.ClaimBedEmote, default "wave") -
            // this domain does not add a dedicated config knob per "claim my own X" hub kind.
            if (!EmoteSignals.Is(emote, TargetedConfig.ClaimBedEmote?.Value, "wave"))
            {
                return;
            }

            ZDO hub = FindNearestHub(who.Position, 10f);
            if (hub == null)
            {
                return;
            }

            if (!TryComputeCentroid(who.PlayerId, out Vector3 centroid, out int pieceCount))
            {
                PlayerNotify.Toast(who, "No base found yet - keep building and try again.");
                return;
            }

            PlaceFor(hub, who.PlayerId, who.Name, centroid);
            PlayerNotify.Toast(who, $"Base found: {pieceCount} pieces near ({centroid.x:F0}, {centroid.z:F0}).");
        }

        private static bool TryComputeCentroid(long playerId, out Vector3 centroid, out int pieceCount)
        {
            centroid = default;
            pieceCount = 0;
            int minPieces = TargetedConfig.BaseCentroidMinPieces?.Value ?? 10;
            if (!_piecesByCreator.TryGetValue(playerId, out List<Vector3> pieces) || pieces.Count < minPieces)
            {
                return false;
            }

            var zoneOf = new Dictionary<Vector2s, List<Vector3>>();
            foreach (Vector3 p in pieces)
            {
                Vector2s z = ZoneSystem.GetZone(p);
                if (!zoneOf.TryGetValue(z, out List<Vector3> list))
                {
                    list = new List<Vector3>();
                    zoneOf[z] = list;
                }
                list.Add(p);
            }

            var visited = new HashSet<Vector2s>();
            List<Vector3> bestCluster = null;
            foreach (Vector2s start in zoneOf.Keys)
            {
                if (visited.Contains(start))
                {
                    continue;
                }
                var clusterZones = new List<Vector2s>();
                var stack = new Stack<Vector2s>();
                stack.Push(start);
                visited.Add(start);
                while (stack.Count > 0)
                {
                    Vector2s z = stack.Pop();
                    clusterZones.Add(z);
                    Vector2s[] neighbours =
                    {
                        new Vector2s((short)(z.x + 1), z.y),
                        new Vector2s((short)(z.x - 1), z.y),
                        new Vector2s(z.x, (short)(z.y + 1)),
                        new Vector2s(z.x, (short)(z.y - 1)),
                    };
                    foreach (Vector2s n in neighbours)
                    {
                        if (zoneOf.ContainsKey(n) && visited.Add(n))
                        {
                            stack.Push(n);
                        }
                    }
                }

                var clusterPieces = new List<Vector3>();
                foreach (Vector2s z in clusterZones)
                {
                    clusterPieces.AddRange(zoneOf[z]);
                }
                if (bestCluster == null || clusterPieces.Count > bestCluster.Count)
                {
                    bestCluster = clusterPieces;
                }
            }

            if (bestCluster == null)
            {
                return false;
            }
            pieceCount = bestCluster.Count;

            Vector3 sum = Vector3.zero;
            foreach (Vector3 p in bestCluster)
            {
                sum += p;
            }
            Vector3 mean = sum / bestCluster.Count;

            float floorY = MedianFloorHeight(mean, 5f) ?? mean.y;
            centroid = new Vector3(mean.x, floorY + 0.3f, mean.z);
            return true;
        }

        private static float? MedianFloorHeight(Vector3 near, float radius)
        {
            List<ZDO> nearby = ZdoSpatialQuery.FindNear(near, radius);
            var floorYs = new List<float>();
            foreach (ZDO z in nearby)
            {
                if (!TargetedPrefabDiscovery.IsPiece(z.GetPrefab()))
                {
                    continue;
                }
                GameObject prefab = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(z.GetPrefab()) : null;
                if (prefab != null && prefab.name.IndexOf("floor", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    floorYs.Add(z.GetPosition().y);
                }
            }
            if (floorYs.Count == 0)
            {
                return null;
            }
            floorYs.Sort();
            return floorYs[floorYs.Count / 2];
        }

        private static void PlaceFor(ZDO hub, long playerId, string playerName, Vector3 centroid)
        {
            Vector3 best = centroid;
            int bestCount = CountNearbyPieces(centroid, 1.5f);
            foreach (Vector3 candidate in CandidateOffsets(centroid))
            {
                int count = CountNearbyPieces(candidate, 1.5f);
                if (count < bestCount)
                {
                    bestCount = count;
                    best = candidate;
                }
            }

            string tag = TargetedTagFormat.Named($"Base: {playerName}");
            TargetedPhantomPortalFactory.CreateOrRetarget(hub, best, Quaternion.identity, tag, $"basecentroid:{playerId}");
        }

        private static IEnumerable<Vector3> CandidateOffsets(Vector3 centre)
        {
            for (int i = 0; i < 8; i++)
            {
                float angle = i * 45f;
                Vector3 dir = Quaternion.Euler(0f, angle, 0f) * Vector3.forward;
                yield return centre + dir * 3f;
                yield return centre + dir * 6f;
            }
        }

        private static int CountNearbyPieces(Vector3 pos, float radius)
        {
            List<ZDO> nearby = ZdoSpatialQuery.FindNear(pos, radius);
            int count = 0;
            foreach (ZDO z in nearby)
            {
                if (TargetedPrefabDiscovery.IsPiece(z.GetPrefab()))
                {
                    count++;
                }
            }
            return count;
        }

        private static void ReassertAll(float dt)
        {
            if (ZDOMan.instance == null)
            {
                return;
            }
            _recomputeTimer += dt;
            float recomputeInterval = TargetedConfig.BaseCentroidRecomputeSeconds?.Value ?? 300f;
            bool recompute = _recomputeTimer >= recomputeInterval;
            if (recompute)
            {
                _recomputeTimer = 0f;
            }
            if (!recompute)
            {
                return;
            }

            foreach (TargetedRoute route in TargetedRouteStore.RoutesOfKind(Kind))
            {
                if (!PortalCensus.TryGetByPosition(route.SourcePosition, out PortalRecord sourceRec))
                {
                    continue;
                }
                ZDO source = ZDOMan.instance.GetZDO(sourceRec.Uid);
                if (source == null || !source.IsValid())
                {
                    continue;
                }

                ZDO phantom = TargetedPhantomPortalFactory.ResolveExistingPhantom(source);
                if (phantom == null)
                {
                    continue; // nobody has claimed this hub yet
                }
                string kind = TargetedPhantomPortalFactory.GetKind(phantom);
                string[] parts = kind.Split(':');
                if (parts.Length != 2 || parts[0] != "basecentroid" || !long.TryParse(parts[1], out long playerId))
                {
                    continue;
                }

                if (!TryComputeCentroid(playerId, out Vector3 centroid, out _))
                {
                    continue;
                }

                // Only re-target when the centre moved meaningfully AND nobody is standing near the old
                // phantom (#195's own "avoid a visible pop" rule, shared with #185 Biome random mode).
                Vector3 oldPos = phantom.GetPosition();
                if ((centroid - oldPos).sqrMagnitude < 64f || AnyPlayerNear(oldPos, 100f))
                {
                    continue;
                }

                PlaceFor(source, playerId, ResolveName(playerId), centroid);
            }
        }

        private static bool AnyPlayerNear(Vector3 pos, float radius)
        {
            float radiusSqr = radius * radius;
            foreach (ConnectedCharacter cc in ConnectedCharacters.All())
            {
                if ((cc.Position - pos).sqrMagnitude <= radiusSqr)
                {
                    return true;
                }
            }
            return false;
        }

        private static string ResolveName(long playerId)
        {
            foreach (ConnectedCharacter cc in ConnectedCharacters.All())
            {
                if (cc.PlayerId == playerId)
                {
                    return cc.Name;
                }
            }
            return "Player";
        }

        private static ZDO FindNearestHub(Vector3 pos, float radius)
        {
            if (ZDOMan.instance == null)
            {
                return null;
            }
            ZDO best = null;
            float bestDistSqr = radius * radius;
            foreach (TargetedRoute route in TargetedRouteStore.RoutesOfKind(Kind))
            {
                float d = (route.SourcePosition - pos).sqrMagnitude;
                if (d > bestDistSqr)
                {
                    continue;
                }
                if (!PortalCensus.TryGetByPosition(route.SourcePosition, out PortalRecord rec))
                {
                    continue;
                }
                ZDO z = ZDOMan.instance.GetZDO(rec.Uid);
                if (z == null || !z.IsValid())
                {
                    continue;
                }
                bestDistSqr = d;
                best = z;
            }
            return best;
        }
    }
}
