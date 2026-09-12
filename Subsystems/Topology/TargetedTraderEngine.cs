using UnityEngine;
using TortalPortalLite.Core;
using TortalPortalLite.Subsystems.Foundations;

namespace TortalPortalLite.Subsystems.Topology
{
    /// <summary>
    /// #183 Traders (Haldor, Hildir, Bog Witch) - portal to a named trader camp, resolved from
    /// ZoneSystem's own location registry (TargetedLocationRegistry). Trader camps are Locations flagged
    /// m_iconPlaced; vanilla shows their map icon only once a peer's reference position has ghost-
    /// generated the zone (GetLocationIcons rule `m_iconAlways || (m_iconPlaced &amp;&amp; m_placed)`,
    /// SERVER decompile :115450-115467) - "discovered only" mode matches that reveal rule exactly by
    /// checking LocationInstance.m_placed before offering the destination; "always" mode (per-route
    /// DiscoveredOnly=false) targets the instance position regardless, leading players TO an undiscovered
    /// camp (a design choice the catalog itself names as valid, not a bug).
    /// </summary>
    public static class TargetedTraderEngine
    {
        private const string Kind = "Trader";
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
            float offset = TargetedConfig.TraderOffsetMeters?.Value ?? 6f;
            bool defaultDiscoveredOnly = TargetedConfig.TraderDiscoveredOnly?.Value != false;

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

                bool discoveredOnly = route.GetBool("DiscoveredOnly", defaultDiscoveredOnly);
                string mode = route.Get("Mode", "specific").ToLowerInvariant();

                bool found;
                ZoneSystem.LocationInstance inst;
                string label;

                if (mode == "nearest")
                {
                    found = TryNearest(route, source.GetPosition(), out inst, out label);
                }
                else
                {
                    string name = route.Get("Name", "");
                    label = string.IsNullOrEmpty(route.Label) ? name : route.Label;
                    found = TargetedLocationRegistry.TryFindClosest(name, source.GetPosition(), out inst);
                }

                if (!found)
                {
                    TargetedPhantomPortalFactory.MarkWaiting(source, TargetedTagFormat.Named((route.Label ?? "Trader") + " (?)"));
                    continue;
                }
                if (discoveredOnly && !inst.m_placed)
                {
                    // #183's own mode (a): "discovered only" - deliberately leave the hub unconnected
                    // until some player has generated the camp's zone, matching vanilla's own reveal rule.
                    TargetedPhantomPortalFactory.MarkWaiting(source, TargetedTagFormat.Named(label + " (unfound)"));
                    continue;
                }

                if (!discoveredOnly)
                {
                    // #206 - only in "always" mode: forcing generation in "discovered only" mode would
                    // defeat its own purpose (generation reveals the map pin immediately).
                    TargetedMaterialiserEngine.EnsureMaterialized(inst.m_position);
                }
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

        private static bool TryNearest(TargetedRoute route, Vector3 from, out ZoneSystem.LocationInstance best, out string bestLabel)
        {
            best = default;
            bestLabel = null;
            string spec = route.Get("Names", "");
            if (string.IsNullOrEmpty(spec))
            {
                return false;
            }

            float bestDist = float.MaxValue;
            bool found = false;
            foreach (string pair in spec.Split(','))
            {
                string[] parts = pair.Split(':');
                string label = parts[0].Trim();
                string name = parts.Length == 2 ? parts[1].Trim() : parts[0].Trim();
                if (!TargetedLocationRegistry.TryFindClosest(name, from, out ZoneSystem.LocationInstance inst))
                {
                    continue;
                }
                float d = Vector3.Distance(inst.m_position, from);
                if (d < bestDist)
                {
                    bestDist = d;
                    best = inst;
                    bestLabel = label;
                    found = true;
                }
            }
            return found;
        }
    }
}
