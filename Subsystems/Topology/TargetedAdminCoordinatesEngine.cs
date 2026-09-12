using UnityEngine;
using TortalPortalLite.Core;
using TortalPortalLite.Subsystems.Foundations;

namespace TortalPortalLite.Subsystems.Topology
{
    /// <summary>
    /// #180 Admin Coordinates - an operator names an X/Z (+ optional Y/Yaw) in targeted_routes.json
    /// (Kind="AdminCoord"); this engine validates it against procedural terrain
    /// (TargetedWorldGenValidation - pure WorldGenerator math, since every raycast-based ZoneSystem
    /// helper is dead server-side) and drops a phantom there.
    ///
    /// Input channel: a hot-reloaded declaration file (TargetedRouteStore), not a new console command -
    /// Core/Hooks/ has no broker for Terminal.TryRunCommand, and Foundations/CommandEngine.cs already
    /// owns that exact patch point for its own "tpl removekey" grammar and is off-limits to edit from
    /// this domain. #180's own howItWorks text offers "a hot-reloaded config entry" as an equally valid
    /// alternative to a console command, which is what this (and every other declared-destination
    /// engine in this domain) uses.
    /// </summary>
    public static class TargetedAdminCoordinatesEngine
    {
        private const string Kind = "AdminCoord";
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
            if (ZDOMan.instance == null)
            {
                return;
            }
            bool allowUnderwater = TargetedConfig.AllowUnderwaterCoordinates?.Value == true;

            foreach (TargetedRoute route in TargetedRouteStore.RoutesOfKind(Kind))
            {
                if (!PortalCensus.TryGetByPosition(route.SourcePosition, out PortalRecord sourceRec))
                {
                    continue; // no portal at the declared hub position (yet, or a typo) - nothing to do
                }
                ZDO source = ZDOMan.instance.GetZDO(sourceRec.Uid);
                if (source == null || !source.IsValid())
                {
                    continue;
                }

                float x = route.GetFloat("X", route.X);
                float z = route.GetFloat("Z", route.Z);
                float yaw = route.GetFloat("Yaw", 0f);

                TargetedWorldGenValidation.Result result = TargetedWorldGenValidation.ValidateGroundPoint(x, z, allowUnderwater);
                if (!result.Ok)
                {
                    PortalDebug.LogWarning($"[TargetedAdminCoordinatesEngine] route '{route.Label}' rejected: {result.Reason}");
                    continue;
                }

                string tag = TargetedTagFormat.Truncate(string.IsNullOrEmpty(route.Label) ? "AdminCoord" : route.Label);
                TargetedPhantomPortalFactory.CreateOrRetarget(source, result.Position, Quaternion.Euler(0f, yaw, 0f), tag, Kind);
            }
        }
    }
}
