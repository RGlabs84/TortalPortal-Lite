using UnityEngine;
using TortalPortalLite.Core;
using TortalPortalLite.Subsystems.Foundations;

namespace TortalPortalLite.Subsystems.Topology
{
    /// <summary>
    /// #184 Dungeon / Crypt Entrances - portal to the SURFACE entrance of a crypt/dungeon location only.
    /// Interior targeting is deliberately NOT implemented: the catalog's own verified spec rules it out
    /// ("RULED OUT in practice... do not ship") - floor height inside a DungeonGenerator-built interior
    /// is unknowable without colliders (the client's own FindFloor raycast only works if the arrival
    /// point happens to sit above a real generated room), and bypassing GlobalKeys.DungeonBuild's
    /// portal-placement gate to even stand a phantom there is a gameplay-policy decision this mod does
    /// not take on unilaterally. Crypt doors themselves (`class Teleport`, SERVER decompile :143316) have
    /// no ZDO, no RPC and a compiled `m_targetPoint` component reference - a server-only mod cannot
    /// enumerate or re-route them, so this engine only ever targets the exterior.
    ///
    /// Entrance location prefab NAMES are Unity asset data outside the decompile, so - same discipline as
    /// #182/#183 - they come from the admin's targeted_routes.json declaration, never guessed.
    /// </summary>
    public static class TargetedDungeonEngine
    {
        private const string Kind = "Dungeon";
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
            float offset = TargetedConfig.DungeonOffsetMeters?.Value ?? 5f;

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
                if (!TargetedLocationRegistry.TryFindClosest(locationName, source.GetPosition(), out ZoneSystem.LocationInstance inst))
                {
                    TargetedPhantomPortalFactory.MarkWaiting(source, TargetedTagFormat.Named((label ?? "Crypt") + " (?)"));
                    continue;
                }

                TargetedMaterialiserEngine.EnsureMaterialized(inst.m_position); // #206 - the entrance ruin and its rooms always exist
                float radius = TargetedLocationRegistry.ExteriorRadiusOf(inst);
                // Sunken/swamp entrances sit near sea level - allow underwater (#184's own "wet but
                // acceptable" note) rather than rejecting the whole location as invalid.
                TargetedWorldGenValidation.Result result = TargetedWorldGenValidation.BestCompassOffset(inst.m_position, radius + offset, samples: 8, allowUnderwater: true);
                if (!result.Ok)
                {
                    continue;
                }

                Quaternion rot = TargetedWorldGenValidation.FacingTowards(result.Position, inst.m_position);
                TargetedPhantomPortalFactory.CreateOrRetarget(source, result.Position, rot, TargetedTagFormat.Named(label), Kind);
            }
        }
    }
}
