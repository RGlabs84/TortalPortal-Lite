using System.Collections.Generic;
using TortalPortalLite.Core;

namespace TortalPortalLite.Subsystems.Topology
{
    /// <summary>One rule inside an Event-Driven Retarget definition - the first rule whose condition currently holds wins.</summary>
    public sealed class RoutingEventRule
    {
        /// <summary>Matches when ZoneSystem.GetGlobalKey(GlobalKey) is true. Leave blank to use RandomEventNameEquals instead.</summary>
        public string? GlobalKey;

        /// <summary>Matches when RandEventSystem's currently active event's name equals this (case-insensitive). Reads the same private m_randomEvent the catalog names as reachable only by publicizer/reflection - available here via TortalPortalLite.csproj's Publicize=true.</summary>
        public string? RandomEventNameEquals;

        public RoutingPosition? Destination;
        public string? Tag;
    }

    /// <summary>Admin declaration for one Event-Driven Retarget portal (routing.json section "eventRetargets").</summary>
    public sealed class RoutingEventRetargetDefinition
    {
        public string Name = "";
        public RoutingPosition Position = new RoutingPosition();
        public List<RoutingEventRule> Rules = new List<RoutingEventRule>();
        public RoutingPosition? DefaultDestination;
        public string? DefaultTag;

        /// <summary>If true, GlobalKeys.activeBosses &gt; 0 forces this gate dark regardless of every rule above - catalog #30's "blackout a network during a boss fight" use, and its own leak-guard note (a crashed boss-owning client can leave this stuck &gt; 0).</summary>
        public bool BlackoutWhileActiveBosses = false;
    }

    /// <summary>
    /// #30 Event-Driven Retarget. Three server-readable event sources, most-to-least reliable:
    ///  (1) Global key state (`ZoneSystem.GetGlobalKey`, :115948-115997) - boss defeats, world
    ///      modifiers, custom mod flags. Zero latency, evaluated fresh every tick (same
    ///      no-boot-replay-hazard reasoning as RoutingSealedGateEngine - polling current state needs no
    ///      GlobalKeyAdd patch at all).
    ///  (2) The currently active random event's name (`RandEventSystem.m_randomEvent.m_name`,
    ///      :106749/:107435-107437 - RandomEvent itself is a fully public class, only the field holding
    ///      the CURRENT instance is private, reachable via this csproj's Publicize=true). RandEventSystem
    ///      genuinely runs server-side (its update path is gated on ZNet.instance.IsServer(),
    ///      :106793-106798) so this is a real signal, not a client-only illusion.
    ///  (3) `GlobalKeys.activeBosses` (:115958) - a float key the boss-owning CLIENT increments/decrements
    ///      (BaseAI.SetAlerted, :27258-27259) - read here only as an optional whole-network blackout,
    ///      never as a per-rule condition, because the catalog's own verified finding is that it can leak
    ///      above zero forever if that client crashes mid-fight; an admin who doesn't want that risk simply
    ///      leaves BlackoutWhileActiveBosses off.
    ///
    /// Explicitly NOT implemented: reading `RandEventSystem.GetBossEvent()` as a condition - the catalog
    /// itself proves this always returns null server-side (EnemyHud.instance.GetActiveBoss(), EnemyHud's
    /// m_huds dictionary is filled relative to Player.m_localPlayer, permanently null headless,
    /// :106939-106950) - implementing a rule kind for it would silently never fire.
    ///
    /// Budgeted like #38's mass sweep (RoutingConfig.SweepWriteBudgetPerTick) since one event can flip
    /// every managed portal in the same tick.
    /// </summary>
    public static class RoutingEventRetargetEngine
    {
        private static float _timer;
        private static int _lastRegistryVersion = -1;
        private static List<RoutingEventRetargetDefinition> _defs = new List<RoutingEventRetargetDefinition>();

        public static void OnUpdate(float dt)
        {
            if (RoutingConfig.TakeoverEnabled?.Value == false || ZDOMan.instance == null || ZoneSystem.instance == null)
            {
                return;
            }
            _timer += dt;
            float interval = RoutingConfig.EventRetargetEvalSeconds?.Value ?? 1.0f;
            if (_timer < interval)
            {
                return;
            }
            _timer = 0f;

            if (RoutingManagedPortalRegistry.Version != _lastRegistryVersion)
            {
                _lastRegistryVersion = RoutingManagedPortalRegistry.Version;
                _defs = RoutingManagedPortalRegistry.Section<RoutingEventRetargetDefinition>("eventRetargets");
            }
            if (_defs.Count == 0)
            {
                return;
            }

            bool activeBossesPresent = ZoneSystem.instance.GetGlobalKey(GlobalKeys.activeBosses, out float activeBossValue) && activeBossValue > 0f;
            string? currentEventName = RandEventSystem.instance?.m_randomEvent?.m_name;
            var budget = new RoutingWriteBudget(RoutingConfig.SweepWriteBudgetPerTick?.Value ?? 16);

            foreach (RoutingEventRetargetDefinition def in _defs)
            {
                if (!RoutingPairingAuthorityEngine.TryClaim(def.Position, $"eventretarget:{def.Name}"))
                {
                    continue;
                }
                ZDO? zdo = RoutingWriteOps.ResolveLive(def.Position);
                if (zdo == null)
                {
                    continue;
                }

                string? desiredTag;
                ZDOID desiredConnection;
                if (def.BlackoutWhileActiveBosses && activeBossesPresent)
                {
                    desiredTag = def.DefaultTag;
                    desiredConnection = ZDOID.None;
                }
                else
                {
                    RoutingEventRule? match = FindMatch(def, currentEventName);
                    RoutingPosition? destPos = match?.Destination ?? def.DefaultDestination;
                    desiredTag = match?.Tag ?? def.DefaultTag;
                    desiredConnection = ZDOID.None;
                    if (destPos != null)
                    {
                        ZDO? destZdo = RoutingWriteOps.ResolveLive(destPos);
                        if (destZdo != null)
                        {
                            desiredConnection = destZdo.m_uid;
                        }
                    }
                }

                RoutingPairingAuthorityEngine.Publish(zdo.m_uid, desiredTag, desiredConnection);
                if (budget.TryConsume())
                {
                    RoutingWriteOps.Reassert(zdo, desiredTag, desiredConnection);
                }
            }
        }

        private static RoutingEventRule? FindMatch(RoutingEventRetargetDefinition def, string? currentEventName)
        {
            foreach (RoutingEventRule rule in def.Rules)
            {
                if (!string.IsNullOrEmpty(rule.GlobalKey) && ZoneSystem.instance!.GetGlobalKey(rule.GlobalKey))
                {
                    return rule;
                }
                if (!string.IsNullOrEmpty(rule.RandomEventNameEquals) && currentEventName != null &&
                    string.Equals(rule.RandomEventNameEquals, currentEventName, System.StringComparison.OrdinalIgnoreCase))
                {
                    return rule;
                }
            }
            return null;
        }
    }
}
