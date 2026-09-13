using System.Collections.Generic;
using UnityEngine;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;

namespace TortalPortalLite.Subsystems.WorldOps
{
    /// <summary>Admin declaration for one range-capped route (economy.json section "rangeCaps") - the catalog's own preferred "stronger form" of distance pricing: forbid a hop outright rather than price it.</summary>
    public sealed class EconomyRangeCapDeclaration
    {
        public EconomyPosition Portal = new EconomyPosition();
        public EconomyPosition Destination = new EconomyPosition();
        public string Label = "GATE";
        public float MaxRangeMeters = 400f;
    }

    /// <summary>
    /// #102 Distance and Biome Pricing. Both endpoints of every managed route are ZDOs whose position the
    /// server holds permanently (portals are never unloaded - `ZDOMan.AddIfPortal` files them in
    /// `m_portalObjects`, catalog's own citation), so `Vector3.Distance` is always available for free,
    /// with zero bookkeeping and no player online.
    ///
    /// This engine implements the RANGE CAP variant directly (pure topology - the route's edge is simply
    /// never emitted past the cap, completely unexploitable, catalog's own "single most interesting
    /// map-level consequence available here: a network of short hops means relay stations"), and exposes
    /// <see cref="ComputePrice"/>/<see cref="BiomeAt"/> as pure helper functions any OTHER economy engine
    /// (e.g. a toll declaration that wants to scale its price by distance instead of a flat number) can
    /// call directly - this class never reads or writes ZDOs on their behalf.
    /// </summary>
    public static class EconomyDistancePricingEngine
    {
        private static int _lastVersion = -1;
        private static List<EconomyRangeCapDeclaration> _caps = new List<EconomyRangeCapDeclaration>();
        private static float _timer;

        public static void OnUpdate(float dt)
        {
            RefreshIfNeeded();
            _timer += dt;
            if (_timer < 1f || _caps.Count == 0)
            {
                return;
            }
            _timer = 0f;
            foreach (EconomyRangeCapDeclaration decl in _caps)
            {
                Evaluate(decl);
            }
        }

        private static void RefreshIfNeeded()
        {
            if (EconomyRegistry.Version != _lastVersion)
            {
                _lastVersion = EconomyRegistry.Version;
                _caps = EconomyRegistry.Section<EconomyRangeCapDeclaration>("rangeCaps");
            }
        }

        private static void Evaluate(EconomyRangeCapDeclaration decl)
        {
            ZDO? gateZdo = EconomyWriteOps.ResolveLivePortal(decl.Portal);
            ZDO? destZdo = EconomyWriteOps.ResolveLivePortal(decl.Destination);
            if (gateZdo == null)
            {
                return;
            }
            ZDOID gateUid = gateZdo.m_uid;

            if (destZdo == null)
            {
                EconomyRoutingKernel.Publish(gateUid, "rangecap", false, $"{decl.Label} no dest", 10);
                return;
            }

            // #99 Physical Tiers feeds directly into this option's own "what tier buys" list ("maximum
            // hop distance") - a tier-1 gate (bare arch) gets the declared cap as-is, a tier-2 (+
            // workbench) gate doubles it, a tier-3 (+ enabled ward) gate triples it. EconomyTierEngine
            // never writes to the kernel itself; this is the consuming side of that data-provider
            // contract (see EconomySubsystem's own doc comment on the split).
            int tier = EconomyTierEngine.TierOf(gateZdo.GetPosition());
            float effectiveRange = decl.MaxRangeMeters * (1 + tier);

            float distance = Vector3.Distance(gateZdo.GetPosition(), destZdo.GetPosition());
            bool withinRange = distance <= effectiveRange;
            if (withinRange)
            {
                EconomyRoutingKernel.SetDestination(gateUid, destZdo.m_uid);
            }
            string tag = withinRange ? decl.Label : $"{decl.Label} out of range";
            EconomyRoutingKernel.Publish(gateUid, "rangecap", withinRange, tag, 10);
        }

        /// <summary>Pure price function - base + per-metre + biome surcharge, no ZDO access. Callers resolve positions and biome surcharges themselves.</summary>
        public static float ComputePrice(Vector3 a, Vector3 b, float basePrice, float? perMeterOverride = null)
        {
            float perMeter = perMeterOverride ?? (EconomyConfig.DistancePricePerMeter?.Value ?? 0.01f);
            return basePrice + perMeter * Vector3.Distance(a, b);
        }

        /// <summary>Pure procedural noise - safe headless, unlike any ZoneSystem collider helper (Game.FixedUpdate pins the reference position every tick so no terrain collider exists near real coordinates server-side).</summary>
        public static Heightmap.Biome BiomeAt(Vector3 pos)
        {
            return WorldGenerator.instance != null ? WorldGenerator.instance.GetBiome(pos) : Heightmap.Biome.None;
        }
    }
}
