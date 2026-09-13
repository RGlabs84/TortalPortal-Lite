using BepInEx.Configuration;
using HarmonyLib;
using ServerSync;
using TortalPortalLite.Core;

namespace TortalPortalLite.Subsystems.WorldOps
{
    /// <summary>
    /// The `economy` domain subsystem (Wave 3 catalog #88-#106, #273-#286 - portal-based tolls,
    /// fuel/charge systems, cooldowns, progression-gated networks, tiered portals, trade routes, server
    /// events built on portal networks). Thin dispatcher, same shape as
    /// Subsystems/Foundations/PortalOpsSubsystem.cs: real logic lives in sibling static engine classes,
    /// each with its own Initialize()/OnUpdate(float dt); this class only owns tick ORDER, which is
    /// semantics here, not taste.
    ///
    /// Tick order: hot-reload the registry and flush persisted stores first, then every mechanism engine
    /// that PUBLISHES a condition/destination into EconomyRoutingKernel (toll, turnstile, charge cell,
    /// cooldown, progression, one-way, range cap, lease, presence, escort, naming, event topology,
    /// auction, debt lien), THEN EconomyRoutingKernel itself (which composes everything published this
    /// tick into the actual tag/connection writes) - so no mechanism engine's verdict ever lags a full
    /// frame behind the kernel that applies it. Reactive/output-only engines that read the kernel's
    /// already-applied state (Door Portcullis) or that operate independently of it entirely (Treasury,
    /// Validator, Station/Smelter Credit primitives, Tier data provider) are ordered last since their
    /// correctness does not depend on this tick's publish order.
    ///
    /// EconomyStationCreditEngine (#273) and EconomySmelterCreditEngine (#274) are deliberately NOT ticked
    /// here - both are pure on-demand primitives (the catalog's own framing: "Station Credit Primitives"),
    /// with no OnUpdate of their own. They are genuinely wired into this wave's scope, not orphaned APIs:
    /// EconomyTreasuryEngine calls both directly (RewardFireplaceFuel/RewardSmelterOreAmount) to credit
    /// every online player's own station when a collective unlock fires, and remain callable by a future
    /// admin command or engine for any other refund-shaped need. EconomyTierEngine (#99) is a pure data
    /// provider other engines query (TierOf/IsWarded) - still ticked here for its own background rescan,
    /// but it never writes to the kernel itself; EconomyDistancePricingEngine's range cap and
    /// EconomyCooldownEngine's perTransit cooldown both consume it directly (tier scales max hop distance
    /// and cooldown length respectively), so it is load-bearing rather than decorative.
    /// </summary>
    public class EconomySubsystem : IPortalSubsystem
    {
        public string Name => "Economy";
        public bool IsEnabled => EconomyConfig.Enabled?.Value != false;

        public void Initialize(ConfigFile config, ConfigSync configSync, Harmony harmony)
        {
            EconomyConfig.Bind(config, configSync);

            EconomyStateStore.Initialize();
            EconomyDebtLienEngine.Initialize();
            EconomyRoutingKernel.Initialize();
            EconomyValidatorEngine.Initialize();
            EconomyBuildCapEngine.Initialize();
        }

        public void OnWorldReady()
        {
            // Nothing prefab/ObjectDB-dependent needs to run before the first tick beyond what each
            // engine's own OnUpdate already lazily resolves (PortalRegistry/ZNetScene lookups are cached
            // per-call, not pre-warmed here) - matching PortalOpsSubsystem's own "OnWorldReady only for
            // things that must run exactly once at this point" discipline. EconomyOneWayRouteEngine's own
            // periodic reassertion covers the "vanilla's load-time relink just converted a one-way route
            // back to two-way" race without needing a dedicated one-shot pass here, because it publishes
            // into the kernel every tick from the moment this subsystem starts ticking.
        }

        public void OnUpdate()
        {
            float dt = UnityEngine.Time.deltaTime;

            EconomyRegistry.OnUpdate(dt);
            EconomyStateStore.OnUpdate(dt);

            // Data providers / independent bookkeeping.
            EconomyTierEngine.OnUpdate(dt);
            EconomyDebtLienEngine.OnUpdate(dt);

            // Condition publishers (toll/fuel/access family) - order among these does not matter, each
            // owns disjoint condition keys per portal.
            EconomyTollEscrowEngine.OnUpdate(dt);
            EconomyTurnstileEngine.OnUpdate(dt);
            EconomyTransitTollEngine.OnUpdate(dt);
            EconomyChargeCellEngine.OnUpdate(dt);
            EconomyCooldownEngine.OnUpdate(dt);
            EconomyProgressionTierEngine.OnUpdate(dt);
            EconomyOneWayRouteEngine.OnUpdate(dt);
            EconomyDistancePricingEngine.OnUpdate(dt);
            EconomyLeaseEngine.OnUpdate(dt);
            EconomyPresenceEngine.OnUpdate(dt);
            EconomyEscortGateEngine.OnUpdate(dt);
            EconomyNamingRightsEngine.OnUpdate(dt);
            EconomyEventTopologyEngine.OnUpdate(dt);
            EconomyAuctionEngine.OnUpdate(dt);
            EconomyDoorEngine.OnUpdate(dt); // lever half publishes; portcullis half reads the kernel's last-applied state, which is always the most recent regardless of intra-tick order

            // Client-honoured, non-kernel writers.
            EconomyOreGateEngine.OnUpdate(dt);

            // The kernel - composes everything published above into actual ZDO writes.
            EconomyRoutingKernel.OnUpdate(dt);

            // Downstream of the kernel / independent of it.
            EconomyCustomsHouseEngine.OnUpdate(dt);
            EconomyVaultSealEngine.OnUpdate(dt);
            EconomyTreasuryEngine.OnUpdate(dt);
            EconomyTransitEffectsEngine.OnUpdate(dt);
            EconomyValidatorEngine.OnUpdate(dt);
        }

        public void Shutdown()
        {
            EconomyStateStore.Save();
            EconomyDebtLienEngine.Save();
        }
    }
}
