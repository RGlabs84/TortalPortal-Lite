using System.Collections.Generic;
using UnityEngine;
using TortalPortalLite.Core.Data;
using TortalPortalLite.Subsystems.Foundations;

namespace TortalPortalLite.Subsystems.WorldOps
{
    /// <summary>
    /// #99 Physical Tiers - Structures and Wards as the Upgrade Currency. A pure DATA PROVIDER, not a
    /// kernel participant: this engine never publishes to EconomyRoutingKernel itself. Other mechanism
    /// engines (Cooldown, Charge Cells, Distance Pricing, ...) query <see cref="TierOf"/>/
    /// <see cref="IsWarded"/> to scale THEIR OWN parameters (a tier-3 gate's cooldown is shorter, a
    /// tier-3 gate's range cap is larger) - composing tiers into the open/closed decision is each of
    /// those engines' own job, exactly as the catalog frames it ("what tier buys... fuel burn rate,
    /// cooldown length, quota").
    ///
    /// Signal 1 (adjacent structure): a Fireplace and a CraftingStation (the "Workbench" family) within
    /// scan radius escalate the tier - the single most legible signal per the catalog's own framing
    /// ("everyone can see whose gate is tier 3 by looking at it"). Signal 2 (the ward): an ENABLED
    /// PrivateArea nearby is read directly off its ZDO (ZDOVars.s_enabled, catalog's own citation :78423)
    /// - never through PrivateArea.CheckAccess, which the catalog's own citation proves is a server-side
    /// vacuous `true` (m_allAreas is empty), not a real permission check.
    ///
    /// Rescanned on a slow cadence (EconomyConfig.TierScanSeconds, default 10s) per the catalog's own
    /// perf warning ("a tier scan across every portal is O(portals x sector objects)... never every
    /// tick") - a spatial query per managed portal is cheap in isolation but adds up across the whole
    /// census if run at kernel/detector cadence.
    /// </summary>
    public static class EconomyTierEngine
    {
        private static readonly Dictionary<Vector3, (int tier, bool warded)> _cache = new Dictionary<Vector3, (int, bool)>();
        private static float _timer;

        public static void OnUpdate(float dt)
        {
            _timer += dt;
            float interval = EconomyConfig.TierScanSeconds?.Value ?? 10f;
            if (_timer < interval)
            {
                return;
            }
            _timer = 0f;
            Rescan();
        }

        public static int TierOf(Vector3 portalPos) => _cache.TryGetValue(Round(portalPos), out var v) ? v.tier : 0;

        public static bool IsWarded(Vector3 portalPos) => _cache.TryGetValue(Round(portalPos), out var v) && v.warded;

        private static void Rescan()
        {
            float radius = EconomyConfig.TierScanRadius?.Value ?? 12f;
            foreach (PortalRecord record in PortalCensus.Latest)
            {
                bool hasFireplace = EconomyBindingRegistry.FindNearest(record.Position, radius, EconomyBindingRegistry.FixtureKind.Fireplace) != null;
                bool hasWorkbench = HasNearbyCraftingStation(record.Position, radius);
                bool warded = HasNearbyEnabledWard(record.Position, radius);

                int tier = 0;
                if (hasFireplace) tier = 1;
                if (hasFireplace && hasWorkbench) tier = 2;
                if (tier == 2 && warded) tier = 3;

                _cache[Round(record.Position)] = (tier, warded);
            }
        }

        private static bool HasNearbyCraftingStation(Vector3 pos, float radius)
        {
            var buf = ZdoSpatialQuery.FindNear(pos, radius);
            foreach (ZDO zdo in buf)
            {
                GameObject? prefab = ZNetScene.instance?.GetPrefab(zdo.GetPrefab());
                if (prefab != null && prefab.GetComponent<CraftingStation>() != null)
                {
                    return true;
                }
            }
            return false;
        }

        private static bool HasNearbyEnabledWard(Vector3 pos, float radius)
        {
            var buf = ZdoSpatialQuery.FindNear(pos, radius);
            foreach (ZDO zdo in buf)
            {
                GameObject? prefab = ZNetScene.instance?.GetPrefab(zdo.GetPrefab());
                if (prefab != null && prefab.GetComponent<PrivateArea>() != null && zdo.GetBool(ZDOVars.s_enabled))
                {
                    return true;
                }
            }
            return false;
        }

        private static Vector3 Round(Vector3 v) => new Vector3(Mathf.Round(v.x * 2f) / 2f, Mathf.Round(v.y * 2f) / 2f, Mathf.Round(v.z * 2f) / 2f);
    }
}
