using System;
using System.Collections.Generic;
using UnityEngine;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;

namespace TortalPortalLite.Subsystems.Topology
{
    /// <summary>
    /// #204 Terminal Architecture - builds the PLACE around a managed destination with vanilla world-gen
    /// primitives, at an arbitrary mod-chosen position (not restricted to wherever world-gen happened to
    /// roll a LocationInstance, unlike #206 Target Materialiser which only accelerates an EXISTING one).
    /// Master-switched off by default (TargetedConfig.TerminalArchitectureEnabled) - the catalog's own
    /// enforcement tier for this option is "client-honoured" and it names this as the heaviest, most
    /// invasive primitive in the domain.
    ///
    /// Two composable mechanisms, both real (no stub):
    ///
    ///  (a) PROXY SHELL ONLY - mint a LocationProxy ZDO directly: `zdo.SetPrefab(proxyHash);
    ///      zdo.Set(ZDOVars.s_location, location-name-hash); zdo.Set(ZDOVars.s_seed, seed)`. On every
    ///      vanilla client, LocationProxy.Awake -&gt; SpawnLocation(Client mode) (client decompile
    ///      :103092-103158) resolves the hash via GetLocation(int) and instantiates ONLY the location's
    ///      non-ZNetView content (static meshes/colliders/EnvZone/Location component) - a real no-build/
    ///      no-terraform ring with zero placed pieces. Cheap, but per the catalog's own failure-mode
    ///      list, may show only bare terrain if the location's walls/geometry are themselves ZNetView
    ///      pieces rather than static meshes (per-location Inspector composition, not decompile-visible).
    ///
    ///  (b)+(c) REAL PIECES, via the REGISTRY primitive rather than a hand-rolled SpawnLocation call:
    ///      `ZoneSystem.RegisterLocation(location, pos, generated:false)` (SERVER decompile
    ///      :115364-115382, publicized) adds a LocationInstance for this exact position as if world-gen
    ///      itself had rolled it there (refuses only if the zone already holds a different location -
    ///      ZoneHasLocation-equivalent check performed here first) - and then #205's own shared SpawnZone
    ///      driver (TargetedZoneGovernorEngine.RequestGhostGenerate, already built for #205/#206) takes
    ///      it from there: SpawnZone -&gt; PlaceLocations -&gt; SpawnLocation(Ghost) instantiates every real
    ///      persistent ZNetView child (walls, altar bowls, dungeon rooms via DungeonGenerator.Generate)
    ///      and creates the proxy itself. This deliberately reuses vanilla's OWN terrain-readiness
    ///      gating/retry/zone-prefab-instantiation dance (SnapToGround/GetGroundData need a live terrain
    ///      collider - the catalog's own explicit warning against calling SpawnLocation directly without
    ///      it) instead of re-implementing it a second time in this file.
    ///
    /// Declared via targeted_routes.json (Kind="Terminal"): SourcePosition here means "where to build the
    /// location", not a hub portal (a Terminal route composes with a separate AdminCoord/etc. route that
    /// also targets the same position if a hub portal should lead there).
    /// </summary>
    public static class TargetedTerminalArchitectureEngine
    {
        private const string Kind = "Terminal";
        private static readonly HashSet<Vector3> _processed = new HashSet<Vector3>();
        private static float _timer;

        public static void OnUpdate(float dt)
        {
            _timer += dt;
            float interval = TargetedConfig.MaintenanceIntervalSeconds?.Value ?? 2f;
            if (_timer < interval)
            {
                return;
            }
            _timer = 0f;
            Tick();
        }

        private static void Tick()
        {
            if (TargetedConfig.TerminalArchitectureEnabled?.Value != true || ZoneSystem.instance == null)
            {
                return;
            }

            foreach (TargetedRoute route in TargetedRouteStore.RoutesOfKind(Kind))
            {
                if (_processed.Contains(route.SourcePosition))
                {
                    continue; // one-shot per declared position - a location is built once, never re-rolled
                }

                string locationName = route.Get("LocationName", "");
                string mode = route.Get("Mode", "real").ToLowerInvariant();
                float yaw = route.GetFloat("Yaw", 0f);
                Quaternion rot = Quaternion.Euler(0f, yaw, 0f);

                bool ok = mode == "proxy"
                    ? TrySpawnProxyShell(locationName, route.SourcePosition, rot, out _)
                    : TryMaterializeRealLocation(locationName, route.SourcePosition, rot);

                if (ok)
                {
                    _processed.Add(route.SourcePosition);
                }
            }
        }

        /// <summary>Mechanism (a) - lightweight static-geometry-only shell, no real pieces.</summary>
        public static bool TrySpawnProxyShell(string locationName, Vector3 pos, Quaternion rot, out ZDO proxyZdo)
        {
            proxyZdo = null;
            if (!TargetedLocationRegistry.TryGetZoneLocation(locationName, out ZoneSystem.ZoneLocation location) || ZoneSystem.instance.m_locationProxyPrefab == null || ZDOMan.instance == null)
            {
                return false;
            }

            int proxyHash = ZoneSystem.instance.m_locationProxyPrefab.name.GetStableHashCode();
            int locationHash = locationName.GetStableHashCode();
            int seed = UnityEngine.Random.Range(0, 99999);

            ZDO zdo;
            try
            {
                zdo = ZDOMan.instance.CreateNewZDO(pos, proxyHash);
            }
            catch (Exception ex)
            {
                PortalDebug.LogError($"[TargetedTerminalArchitectureEngine] CreateNewZDO failed for proxy shell '{locationName}': {ex.Message}");
                return false;
            }
            if (zdo == null)
            {
                return false;
            }

            PortalOwnership.ClaimAndWrite(zdo, z =>
            {
                z.Persistent = true;
                z.Type = ZDO.ObjectType.Default;
                z.Distant = false;
                z.SetPrefab(proxyHash);
                z.SetRotation(rot);
                z.Set(ZDOVars.s_location, locationHash);
                z.Set(ZDOVars.s_seed, seed);

                // LoadFields overrides consumed by LocationProxy.Awake's spawned Location component
                // AFTER parenting (client decompile :103149-103150) - the client-honoured no-build ring.
                z.Set("HasFields", true);
                z.Set("HasFieldsLocation", true);
                z.Set("Location.m_noBuild", true);
                z.Set("Location.m_exteriorRadius", location.m_exteriorRadius);
            });

            proxyZdo = zdo;
            return true;
        }

        /// <summary>Mechanisms (b)+(c) - real pieces via RegisterLocation + the shared SpawnZone driver.</summary>
        public static bool TryMaterializeRealLocation(string locationName, Vector3 pos, Quaternion rot)
        {
            if (!TargetedLocationRegistry.TryGetZoneLocation(locationName, out ZoneSystem.ZoneLocation location))
            {
                PortalDebug.LogWarning($"[TargetedTerminalArchitectureEngine] unknown location name '{locationName}'.");
                return false;
            }

            Vector2s zone = ZoneSystem.GetZone(pos);
            if (ZoneSystem.instance.m_locationInstances.ContainsKey(zone))
            {
                PortalDebug.LogWarning($"[TargetedTerminalArchitectureEngine] zone {zone} already holds a location - refusing to place '{locationName}' there (RegisterLocation would silently no-op).");
                return false;
            }

            try
            {
                ZoneSystem.instance.RegisterLocation(location, pos, generated: false);
            }
            catch (Exception ex)
            {
                PortalDebug.LogError($"[TargetedTerminalArchitectureEngine] RegisterLocation failed for '{locationName}': {ex.Message}");
                return false;
            }

            // Hand off to #205's shared, budgeted, retrying SpawnZone driver rather than calling
            // SpawnLocation directly here - it already implements the terrain-ready gate this mechanism
            // needs (a bare SpawnLocation call without a live zone root leaves pieces floating/sinking,
            // #204's own documented failure mode).
            TargetedZoneGovernorEngine.RequestGhostGenerate(pos);
            return true;
        }
    }
}
