using System;
using System.Collections.Generic;
using UnityEngine;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;
using TortalPortalLite.Subsystems.Foundations;

namespace TortalPortalLite.Subsystems.Topology
{
    /// <summary>
    /// #186 Named World-Gen Locations (generic) and LocationProxy ZDOs - any location type by name
    /// (runestone camps, villages, tar pits, shipwrecks, ...), in two modes:
    ///
    ///  "registry" (default) - ZoneSystem.instance.FindClosestLocation/FindLocations (already public,
    ///  TargetedLocationRegistry), the same vocabulary vanilla's own `find` console command uses
    ///  (SERVER decompile :45602-45631) - includes locations world-gen has rolled but never generated.
    ///
    ///  "materialized" - only locations some peer has actually ghost-generated, via
    ///  `ZDOExtraData.GetAllZDOIDsWithHash(Type.Int, ZDOVars.s_location)` (:75390-75407), an unindexed
    ///  full scan of every ZDO's int fields (LocationProxy.SetLocation writes the location's name-hash
    ///  into s_location, :102653-102659) - run once and cached per requested name, refreshed on a slow
    ///  timer rather than every maintenance tick, exactly per the catalog's own "run it once and cache"
    ///  guidance.
    /// </summary>
    public static class TargetedLocationEngine
    {
        private const string Kind = "Location";
        private const float MaterializedScanInterval = 60f;
        private static float _timer;
        private static float _scanTimer;
        private static readonly Dictionary<string, List<Vector3>> _materializedCache = new Dictionary<string, List<Vector3>>();

        public static void OnUpdate(float dt)
        {
            _timer += dt;
            float interval = TargetedConfig.MaintenanceIntervalSeconds?.Value ?? 2f;
            if (_timer < interval)
            {
                return;
            }
            _timer = 0f;

            _scanTimer += interval;
            bool refreshMaterialized = _scanTimer >= MaterializedScanInterval;
            if (refreshMaterialized)
            {
                _scanTimer = 0f;
            }

            Tick(refreshMaterialized);
        }

        private static void Tick(bool refreshMaterialized)
        {
            if (ZDOMan.instance == null)
            {
                return;
            }
            float offset = TargetedConfig.LocationOffsetMeters?.Value ?? 5f;

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

                string locationName = route.Get("LocationName", "");
                string label = string.IsNullOrEmpty(route.Label) ? locationName : route.Label;
                string mode = route.Get("Mode", "registry").ToLowerInvariant();

                if (mode == "materialized")
                {
                    if (refreshMaterialized || !_materializedCache.ContainsKey(locationName))
                    {
                        RefreshMaterialized(locationName);
                    }
                    if (!TryPickMaterialized(route, locationName, source.GetPosition(), out Vector3 pos))
                    {
                        TargetedPhantomPortalFactory.MarkWaiting(source, TargetedTagFormat.Named((label ?? "Location") + " (?)"));
                        continue;
                    }
                    TargetedWorldGenValidation.Result placed = TargetedWorldGenValidation.BestCompassOffset(pos, offset, samples: 8, allowUnderwater: true);
                    if (!placed.Ok)
                    {
                        continue;
                    }
                    Quaternion facing = TargetedWorldGenValidation.FacingTowards(placed.Position, pos);
                    TargetedPhantomPortalFactory.CreateOrRetarget(source, placed.Position, facing, TargetedTagFormat.Named(label), Kind);
                    continue;
                }

                // "registry" mode (default)
                string registryMode = route.Get("PickMode", "nearest").ToLowerInvariant();
                bool found;
                ZoneSystem.LocationInstance inst;
                if (registryMode == "random")
                {
                    found = TryPickRandomRegistryInstance(locationName, out inst);
                }
                else
                {
                    found = TargetedLocationRegistry.TryFindClosest(locationName, source.GetPosition(), out inst);
                }

                if (!found)
                {
                    TargetedPhantomPortalFactory.MarkWaiting(source, TargetedTagFormat.Named((label ?? "Location") + " (?)"));
                    continue;
                }

                TargetedMaterialiserEngine.EnsureMaterialized(inst.m_position); // #206 - registry mode always benefits from the destination existing
                float radius = TargetedLocationRegistry.ExteriorRadiusOf(inst);
                TargetedWorldGenValidation.Result result = TargetedWorldGenValidation.BestCompassOffset(inst.m_position, radius + offset, samples: 8, allowUnderwater: true);
                if (!result.Ok)
                {
                    continue;
                }
                Quaternion rot = TargetedWorldGenValidation.FacingTowards(result.Position, inst.m_position);
                TargetedPhantomPortalFactory.CreateOrRetarget(source, result.Position, rot, TargetedTagFormat.Named(label), Kind);
            }
        }

        private static bool TryPickRandomRegistryInstance(string locationName, out ZoneSystem.LocationInstance inst)
        {
            inst = default;
            if (!TargetedLocationRegistry.TryFindAll(locationName, out List<ZoneSystem.LocationInstance> all) || all.Count == 0)
            {
                return false;
            }
            inst = all[UnityEngine.Random.Range(0, all.Count)];
            return true;
        }

        private static void RefreshMaterialized(string locationName)
        {
            var positions = new List<Vector3>();
            try
            {
                if (ZDOMan.instance != null && !string.IsNullOrEmpty(locationName))
                {
                    int wantHash = locationName.GetStableHashCode();
                    List<ZDOID> ids = ZDOExtraData.GetAllZDOIDsWithHash(ZDOExtraData.Type.Int, ZDOVars.s_location);
                    foreach (ZDOID id in ids)
                    {
                        ZDO zdo = ZDOMan.instance.GetZDO(id);
                        if (zdo != null && zdo.IsValid() && zdo.GetInt(ZDOVars.s_location) == wantHash)
                        {
                            positions.Add(zdo.GetPosition());
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                PortalDebug.LogWarning($"[TargetedLocationEngine] materialized scan for '{locationName}' failed: {ex.Message}");
            }
            _materializedCache[locationName] = positions;
        }

        private static bool TryPickMaterialized(TargetedRoute route, string locationName, Vector3 near, out Vector3 pos)
        {
            pos = default;
            if (!_materializedCache.TryGetValue(locationName, out List<Vector3> positions) || positions.Count == 0)
            {
                return false;
            }
            string pickMode = route.Get("PickMode", "nearest").ToLowerInvariant();
            if (pickMode == "random")
            {
                pos = positions[UnityEngine.Random.Range(0, positions.Count)];
                return true;
            }
            float best = float.MaxValue;
            foreach (Vector3 p in positions)
            {
                float d = Vector3.SqrMagnitude(p - near);
                if (d < best)
                {
                    best = d;
                    pos = p;
                }
            }
            return true;
        }
    }
}
