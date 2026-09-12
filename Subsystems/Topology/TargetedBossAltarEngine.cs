using UnityEngine;
using TortalPortalLite.Core;
using TortalPortalLite.Subsystems.Foundations;

namespace TortalPortalLite.Subsystems.Topology
{
    /// <summary>
    /// #182 Boss Altars - portal to a specific boss's altar, the nearest altar of a chosen type, or the
    /// altar of the first boss not yet marked defeated in the server's own global keys. Altar LOCATION
    /// PREFAB NAMES are Unity asset data outside the decompile (catalog's own open question) - this
    /// engine never guesses one; every name comes from the admin's own targeted_routes.json declaration,
    /// resolved through the already-public ZoneSystem.FindClosestLocation/FindLocations
    /// (TargetedLocationRegistry).
    ///
    /// "Next undefeated" re-evaluates on a slow poll (TargetedConfig.NextBossPollSeconds) rather than a
    /// ZoneSystem.GlobalKeyAdd postfix - that private method has no Core/Hooks/ broker in this codebase,
    /// and a 30 s poll is a fully-working alternative the catalog itself offers ("re-evaluates on a slow
    /// tick (30s) or from a postfix..."), not a stub standing in for the real thing.
    ///
    /// Global-key defeat check uses TargetedLocationRegistry.IsGlobalKeySet (the string overload)
    /// uniformly - confirmed against the decompile to work identically for classic enum-backed bosses
    /// (defeated_eikthyr..defeated_bonemass) and any newer boss tracked only by a raw string key.
    ///
    /// Does not and cannot override TeleportWorld.Teleport's own NoBossPortals gate (SERVER decompile
    /// :143528) - that is vanilla's transit-time check and unrelated to which coordinate this engine
    /// points a phantom at.
    /// </summary>
    public static class TargetedBossAltarEngine
    {
        private const string Kind = "BossAltar";
        private static float _timer;
        private static float _nextBossTimer;

        public static void OnUpdate(float dt)
        {
            _timer += dt;
            float interval = TargetedConfig.MaintenanceIntervalSeconds?.Value ?? 2f;
            bool generalTick = _timer >= interval;
            if (generalTick)
            {
                _timer = 0f;
            }

            _nextBossTimer += dt;
            float pollInterval = TargetedConfig.NextBossPollSeconds?.Value ?? 30f;
            bool nextBossTick = _nextBossTimer >= pollInterval;
            if (nextBossTick)
            {
                _nextBossTimer = 0f;
            }

            if (generalTick || nextBossTick)
            {
                TickAll();
            }
        }

        private static void TickAll()
        {
            if (ZDOMan.instance == null)
            {
                return;
            }
            float offset = TargetedConfig.BossAltarOffsetMeters?.Value ?? 18f;

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

                string mode = route.Get("Mode", "specific").ToLowerInvariant();
                bool resolved = mode switch
                {
                    "nearest" => TryResolveNearest(route, source, offset),
                    "next-undefeated" => TryResolveNextUndefeated(route, source, offset),
                    _ => TryResolveSpecific(route, source, offset),
                };

                if (!resolved)
                {
                    TargetedPhantomPortalFactory.MarkWaiting(source, TargetedTagFormat.Named((route.Label ?? "Boss") + " (?)"));
                }
            }
        }

        private static bool TryResolveSpecific(TargetedRoute route, ZDO source, float offset)
        {
            string locationName = route.Get("LocationName", "");
            string label = route.Get("BossLabel", route.Label);
            return TryPlaceAtLocation(source, locationName, label, offset);
        }

        private static bool TryResolveNearest(TargetedRoute route, ZDO source, float offset)
        {
            string spec = route.Get("Locations", "");
            if (string.IsNullOrEmpty(spec))
            {
                return false;
            }

            Vector3 from = source.GetPosition();
            float bestDist = float.MaxValue;
            string bestLabel = null;
            ZoneSystem.LocationInstance bestInst = default;
            bool found = false;

            foreach (string pair in spec.Split(','))
            {
                string[] parts = pair.Split(':');
                if (parts.Length != 2)
                {
                    continue;
                }
                string label = parts[0].Trim();
                string locationName = parts[1].Trim();
                if (!TargetedLocationRegistry.TryFindClosest(locationName, from, out ZoneSystem.LocationInstance inst))
                {
                    continue;
                }
                float d = Vector3.Distance(inst.m_position, from);
                if (d < bestDist)
                {
                    bestDist = d;
                    bestLabel = label;
                    bestInst = inst;
                    found = true;
                }
            }

            return found && TryPlaceAtInstance(source, bestInst, bestLabel, offset);
        }

        private static bool TryResolveNextUndefeated(TargetedRoute route, ZDO source, float offset)
        {
            string spec = route.Get("Order", "");
            if (string.IsNullOrEmpty(spec))
            {
                return false;
            }

            foreach (string triple in spec.Split(','))
            {
                string[] parts = triple.Split(':');
                if (parts.Length != 3)
                {
                    continue;
                }
                string globalKey = parts[0].Trim();
                string label = parts[1].Trim();
                string locationName = parts[2].Trim();

                if (TargetedLocationRegistry.IsGlobalKeySet(globalKey))
                {
                    continue; // already defeated - keep scanning for the first not-yet-defeated boss
                }
                return TryPlaceAtLocation(source, locationName, label, offset);
            }
            return false; // every boss in the declared order is defeated
        }

        private static bool TryPlaceAtLocation(ZDO source, string locationName, string label, float offset)
        {
            if (string.IsNullOrEmpty(locationName))
            {
                return false;
            }
            if (!TargetedLocationRegistry.TryFindClosest(locationName, source.GetPosition(), out ZoneSystem.LocationInstance inst))
            {
                return false;
            }
            return TryPlaceAtInstance(source, inst, label, offset);
        }

        private static bool TryPlaceAtInstance(ZDO source, ZoneSystem.LocationInstance inst, string label, float offset)
        {
            TargetedMaterialiserEngine.EnsureMaterialized(inst.m_position); // #206 - so the altar's real pieces (OfferingBowl etc.) always exist
            float radius = TargetedLocationRegistry.ExteriorRadiusOf(inst);
            // Wide default offset (#182's own failure-mode note): a phantom too close to the altar sits
            // inside the boss arena and can be destroyed by boss combat (WearNTear damage -> Destroy ->
            // resources drop, an exploit vector #178 already warns about).
            TargetedWorldGenValidation.Result result = TargetedWorldGenValidation.BestCompassOffset(inst.m_position, radius + offset, samples: 8, allowUnderwater: true);
            if (!result.Ok)
            {
                return false;
            }

            Quaternion rot = TargetedWorldGenValidation.FacingTowards(result.Position, inst.m_position);
            TargetedPhantomPortalFactory.CreateOrRetarget(source, result.Position, rot, TargetedTagFormat.Named(label), Kind);
            return true;
        }
    }
}
