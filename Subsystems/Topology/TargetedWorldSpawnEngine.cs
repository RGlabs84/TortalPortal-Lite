using UnityEngine;
using TortalPortalLite.Core;
using TortalPortalLite.Subsystems.Foundations;

namespace TortalPortalLite.Subsystems.Topology
{
    /// <summary>
    /// #181 World Spawn / Start Temple - portals to the exact spot vanilla respawns new characters,
    /// read from the server's own already-computed LocationIcons source
    /// (TargetedLocationRegistry.TryGetStartTemple -&gt; ZoneSystem.GetLocationIcon, SERVER decompile
    /// :115422-115447). Works at OnWorldReady with zero players connected: m_locationInstances is
    /// populated by ZNet.ServerLoadWorld -&gt; GenerateLocationsIfNeeded before any peer connects.
    /// Declared via targeted_routes.json (Kind="WorldSpawn") like every other static destination in
    /// this domain - see TargetedRouteStore.
    /// </summary>
    public static class TargetedWorldSpawnEngine
    {
        private const string Kind = "WorldSpawn";
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
            if (ZDOMan.instance == null || !TargetedLocationRegistry.TryGetStartTemple(out Vector3 templePos))
            {
                return;
            }

            float offset = TargetedConfig.WorldSpawnOffsetMeters?.Value ?? 7f;
            int index = 0;
            foreach (TargetedRoute route in TargetedRouteStore.RoutesOfKind(Kind))
            {
                if (!PortalCensus.TryGetByPosition(route.SourcePosition, out PortalRecord sourceRec))
                {
                    index++;
                    continue;
                }
                ZDO source = ZDOMan.instance.GetZDO(sourceRec.Uid);
                if (source == null || !source.IsValid())
                {
                    index++;
                    continue;
                }

                // Spread multiple hubs around the temple ring so their exits don't overlap (#181's own
                // note) - a non-90-degree stride so a handful of hubs don't alias onto the same points.
                float angle = index * 47f;
                Vector3 dir = Quaternion.Euler(0f, angle, 0f) * Vector3.forward;
                TargetedWorldGenValidation.Result result = TargetedWorldGenValidation.ValidateGroundPoint(
                    templePos.x + dir.x * offset, templePos.z + dir.z * offset, allowUnderwater: true);
                if (!result.Ok)
                {
                    result = TargetedWorldGenValidation.BestCompassOffset(templePos, offset, samples: 8, allowUnderwater: true);
                }
                if (!result.Ok)
                {
                    index++;
                    continue;
                }

                Quaternion rot = TargetedWorldGenValidation.FacingTowards(result.Position, templePos);
                string tag = TargetedTagFormat.Truncate(string.IsNullOrEmpty(route.Label) ? "Spawn" : route.Label);
                TargetedPhantomPortalFactory.CreateOrRetarget(source, result.Position, rot, tag, Kind);
                index++;
            }
        }
    }
}
