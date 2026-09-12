using System;
using System.Collections.Generic;
using UnityEngine;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;
using TortalPortalLite.Subsystems.Foundations;

namespace TortalPortalLite.Subsystems.Topology
{
    /// <summary>
    /// #185 Biome Targets - nearest edge, centroid, nearest-N, or a random point inside a chosen biome,
    /// resolved entirely from the server's precomputed 12 m biome/height grid (`ZNet.World.m_biomeData`,
    /// an AltBiomeWorldData populated by ZNet.ServerLoadWorld's VerifyBiomeData, SERVER decompile
    /// :79273-79285/:92401-92406) - no ZDO scan, no player needed.
    ///
    /// "Centroid" re-validates against WorldGenerator.GetBiome before trusting a BiomeSector's own
    /// `Center` field: AltBiomeWorldData.GenerateSectors' centroid is a PERIMETER mean (SERVER decompile
    /// :92527-92535, summed only over edge cells) which for a crescent-shaped region can fall outside the
    /// region entirely (#185's own documented bug) - this engine walks from Center toward the sector's
    /// Min/Max box when that happens. BiomeSector.MinZone/MaxZone are never read (both computed from Min
    /// with z=0 in GenerateSectors - confirmed garbage per the catalog's own verified finding, :92577-92578).
    ///
    /// Search results are cached per (source, mode, biome): the spiral/sector scan is not cheap (up to a
    /// full 2048x2048 grid walk) and a biome's geography never changes within a session, except "random"
    /// mode which deliberately re-rolls on TargetedConfig.BiomeRandomRerollSeconds and only when nobody
    /// is within 100 m of the previous phantom (#185's own "avoid a visible pop" rule).
    /// </summary>
    public static class TargetedBiomeEngine
    {
        private const string Kind = "Biome";
        private static float _timer;
        private static readonly Dictionary<string, Vector3> _resolvedCache = new Dictionary<string, Vector3>();
        private static readonly Dictionary<string, float> _rerollTimers = new Dictionary<string, float>();

        public static void OnUpdate(float dt)
        {
            _timer += dt;
            float interval = TargetedConfig.MaintenanceIntervalSeconds?.Value ?? 2f;
            if (_timer < interval)
            {
                return;
            }
            float elapsed = _timer;
            _timer = 0f;
            Tick(elapsed);
        }

        private static void Tick(float dt)
        {
            if (ZDOMan.instance == null || WorldGenerator.instance == null || ZNet.World?.m_biomeData == null)
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

                string biomeName = route.Get("Biome", "Meadows");
                if (!Enum.TryParse(biomeName, ignoreCase: true, out Heightmap.Biome biome))
                {
                    PortalDebug.LogWarning($"[TargetedBiomeEngine] route '{route.Label}': unrecognised biome '{biomeName}'.");
                    continue;
                }
                string mode = route.Get("Mode", "nearest-edge").ToLowerInvariant();
                string cacheKeyBase = $"{route.SourcePosition}|{mode}|{biome}";

                if (mode == "nearest-n")
                {
                    ResolveNearestN(route, source, biome, cacheKeyBase);
                    continue;
                }

                bool haveCached = _resolvedCache.TryGetValue(cacheKeyBase, out Vector3 target);
                bool needsRoll = !haveCached;

                if (mode == "random")
                {
                    float t = (_rerollTimers.TryGetValue(cacheKeyBase, out float existing) ? existing : 0f) + dt;
                    float rerollInterval = TargetedConfig.BiomeRandomRerollSeconds?.Value ?? 1800f;
                    if (t >= rerollInterval)
                    {
                        t = 0f;
                        needsRoll = true;
                    }
                    _rerollTimers[cacheKeyBase] = t;
                }

                if (needsRoll)
                {
                    bool ok = mode switch
                    {
                        "centroid" => TryResolveCentroid(biome, out Vector3 rolled) && Assign(ref target, rolled),
                        "random" => TryResolveRandom(biome, out Vector3 rolled2) && Assign(ref target, rolled2),
                        _ => TryResolveNearestEdge(source.GetPosition(), biome, out Vector3 rolled3) && Assign(ref target, rolled3),
                    };
                    if (!ok)
                    {
                        continue;
                    }

                    if (mode == "random" && haveCached && AnyPlayerNear(_resolvedCache[cacheKeyBase], 100f))
                    {
                        target = _resolvedCache[cacheKeyBase]; // don't pop a visible destination while someone is standing at it
                    }
                    else
                    {
                        _resolvedCache[cacheKeyBase] = target;
                    }
                }

                string label = string.IsNullOrEmpty(route.Label) ? biome.ToString() : route.Label;
                string tag = TargetedTagFormat.Biome($"{label} ({mode})");
                TargetedPhantomPortalFactory.CreateOrRetarget(source, target, Quaternion.identity, tag, Kind);
            }
        }

        private static bool Assign(ref Vector3 slot, Vector3 value)
        {
            slot = value;
            return true;
        }

        private static void ResolveNearestN(TargetedRoute route, ZDO source, Heightmap.Biome biome, string cacheKeyBase)
        {
            int index = Mathf.Max(1, route.GetInt("Index", 1));
            string cacheKey = $"{cacheKeyBase}|{index}";

            if (!_resolvedCache.TryGetValue(cacheKey, out Vector3 target))
            {
                if (!ZNet.World.m_biomeData.Biomes.TryGetValue(biome, out BiomeTypeInfo info) || info.Sectors.Count == 0)
                {
                    return;
                }
                Vector3 from = source.GetPosition();
                var sorted = new List<BiomeSector>(info.Sectors);
                sorted.Sort((a, b) => Vector2.Distance(a.Center, new Vector2(from.x, from.z)).CompareTo(Vector2.Distance(b.Center, new Vector2(from.x, from.z))));
                int i = Mathf.Clamp(index - 1, 0, sorted.Count - 1);
                Vector3 candidate = ReconcileCentroid(sorted[i], biome);

                TargetedWorldGenValidation.Result result = TargetedWorldGenValidation.ValidateGroundPoint(candidate.x, candidate.z);
                if (!result.Ok)
                {
                    return;
                }
                target = result.Position;
                _resolvedCache[cacheKey] = target;
            }

            string label = string.IsNullOrEmpty(route.Label) ? $"{biome} {index}" : route.Label;
            TargetedPhantomPortalFactory.CreateOrRetarget(source, target, Quaternion.identity, TargetedTagFormat.Biome(label), Kind);
        }

        private static bool TryResolveCentroid(Heightmap.Biome biome, out Vector3 target)
        {
            target = default;
            if (!ZNet.World.m_biomeData.Biomes.TryGetValue(biome, out BiomeTypeInfo info) || info.Sectors.Count == 0)
            {
                return false;
            }
            BiomeSector best = null;
            int bestEdge = -1;
            foreach (BiomeSector s in info.Sectors)
            {
                if (s.EdgeCount > bestEdge)
                {
                    bestEdge = s.EdgeCount;
                    best = s;
                }
            }
            if (best == null)
            {
                return false;
            }
            Vector3 candidate = ReconcileCentroid(best, biome);
            TargetedWorldGenValidation.Result result = TargetedWorldGenValidation.ValidateGroundPoint(candidate.x, candidate.z);
            if (!result.Ok)
            {
                return false;
            }
            target = result.Position;
            return true;
        }

        /// <summary>Corrects GenerateSectors' perimeter-mean Center when it lands outside the region (#185's own documented bug).</summary>
        private static Vector3 ReconcileCentroid(BiomeSector sector, Heightmap.Biome biome)
        {
            Vector3 candidate = new Vector3(sector.Center.x, 0f, sector.Center.y);
            if (WorldGenerator.instance.GetBiome(candidate) == biome)
            {
                return candidate;
            }
            Vector3 boxCentre = new Vector3((sector.Min.x + sector.Max.x) * 0.5f, 0f, (sector.Min.y + sector.Max.y) * 0.5f);
            if (WorldGenerator.instance.GetBiome(boxCentre) == biome)
            {
                return boxCentre;
            }
            Vector3 dir = (boxCentre - candidate);
            if (dir.sqrMagnitude > 0.01f)
            {
                dir = dir.normalized;
                for (int step = 1; step <= 20; step++)
                {
                    Vector3 probe = candidate + dir * (step * 20f);
                    if (WorldGenerator.instance.GetBiome(probe) == biome)
                    {
                        return probe;
                    }
                }
            }
            return candidate; // give up gracefully - ValidateGroundPoint may still reject it downstream
        }

        private static bool TryResolveRandom(Heightmap.Biome biome, out Vector3 target)
        {
            target = default;
            BiomePointCoordinate coord = ZNet.World.m_biomeData.GetRandomPointByBiomesAboveSeaLevel(biome);
            Vector3 world = AltBiomeWorldData.MapSpaceToWorldSpace(coord);
            TargetedWorldGenValidation.Result result = TargetedWorldGenValidation.ValidateGroundPoint(world.x, world.z);
            if (!result.Ok)
            {
                return false;
            }
            target = result.Position;
            return true;
        }

        /// <summary>
        /// Spiral (square-ring) search outward from the source's own map-space cell for the nearest cell
        /// tagged with the requested biome above sea level. One-time cost per (source,biome) - cached by
        /// the caller. Worst case (biome absent from the reachable map) walks the whole 2048x2048 grid;
        /// typical case resolves within a few hundred cells since most biomes are large contiguous regions.
        /// </summary>
        private static bool TryResolveNearestEdge(Vector3 from, Heightmap.Biome biome, out Vector3 target)
        {
            target = default;
            AltBiomeWorldData data = ZNet.World.m_biomeData;
            int size = data.Size;
            int cx = AltBiomeWorldData.WorldSpaceToMapSpace(from.x);
            int cy = AltBiomeWorldData.WorldSpaceToMapSpace(from.z);
            Heightmap.BiomeIndex targetIndex = biome.ToBiomeIndex();
            int maxRadius = size; // covers the entire grid in the worst case

            for (int radius = 0; radius <= maxRadius; radius++)
            {
                int xMin = cx - radius, xMax = cx + radius, yMin = cy - radius, yMax = cy + radius;
                for (int x = xMin; x <= xMax; x++)
                {
                    if (TryCell(data, size, x, yMin, targetIndex, out target)) return true;
                    if (radius > 0 && TryCell(data, size, x, yMax, targetIndex, out target)) return true;
                }
                for (int y = yMin + 1; y < yMax; y++)
                {
                    if (TryCell(data, size, xMin, y, targetIndex, out target)) return true;
                    if (TryCell(data, size, xMax, y, targetIndex, out target)) return true;
                }
            }
            return false;
        }

        private static bool TryCell(AltBiomeWorldData data, int size, int x, int y, Heightmap.BiomeIndex targetIndex, out Vector3 target)
        {
            target = default;
            if (x < 0 || y < 0 || x >= size || y >= size)
            {
                return false;
            }
            if (data.PointBiomes[x, y] != targetIndex || data.PointHeights[x, y] < 31f)
            {
                return false;
            }
            float wx = AltBiomeWorldData.MapSpaceToWorldSpace((float)x);
            float wz = AltBiomeWorldData.MapSpaceToWorldSpace((float)y);
            TargetedWorldGenValidation.Result result = TargetedWorldGenValidation.ValidateGroundPoint(wx, wz);
            if (!result.Ok)
            {
                return false;
            }
            target = result.Position;
            return true;
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
    }
}
