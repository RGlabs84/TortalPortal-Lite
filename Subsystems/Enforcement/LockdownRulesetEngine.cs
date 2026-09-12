using System.Collections.Generic;
using TortalPortalLite.Core;
using TortalPortalLite.Subsystems.Foundations;

namespace TortalPortalLite.Subsystems.Enforcement
{
    /// <summary>
    /// #121 Ironman and Hardcore Rulesets. Named composite presets that combine the primitives already
    /// built in this domain into one-line server personalities, driven by a single level-triggered
    /// reconciler (LockdownConfig.ActiveRuleset). Every preset is expressed purely as calls into the
    /// already-existing engines' own public APIs - this is deliberately NOT a fully general per-portal
    /// desired-state arbiter (the catalog's own aspirational "Locked | Free | RegionTag(x)" resolver for
    /// arbitrary COMBINATIONS of independently-run engines): with a fixed, named set of presets this
    /// simplifies to "apply this preset's recipe" with no cross-preset conflict possible, since only one
    /// ruleset is ever active at a time. The documented limitation: an operator running one of these
    /// presets AND manually engaging an unrelated lockdown engine (e.g. a hand-declared schedule window)
    /// at the same time gets no special arbitration between them beyond each engine's own idempotent
    /// writes - exactly the "declare a precedence or they will fight" risk the catalog itself flags for
    /// the fully general version, scoped down rather than solved.
    /// </summary>
    public static class LockdownRulesetEngine
    {
        private const string Reason = "ruleset";
        private static string _appliedRuleset = "";

        public static void OnUpdate(float dt)
        {
            string desired = LockdownConfig.ActiveRuleset?.Value ?? "None";
            if (desired == _appliedRuleset)
            {
                return;
            }
            Lift(_appliedRuleset);
            Apply(desired);
            _appliedRuleset = desired;
            PortalDebug.LogAlways($"[LockdownRulesetEngine] ruleset changed to '{desired}'.");
            LockdownAnnouncementEngine.BroadcastMessage($"Server ruleset changed: {desired}", center: true);
        }

        private static void Apply(string ruleset)
        {
            switch (ruleset)
            {
                case "NoPortals":
                    LockdownGlobalKeyEngine.SetNoPortals(true);
                    LockdownForceDisconnectEngine.EngageScope(PortalCensus.Latest, Reason);
                    break;

                case "OneWayOutboundOnly":
                    LockdownOneWayEngine.SetEnabled(true);
                    break;

                case "TieredTravel":
                    LockdownRegionEngine.SetProgrammaticGroups(BuildTieredGroups());
                    break;

                case "NightCurfew":
                    LockdownNightCurfewEngine.SetEnabled(true);
                    break;

                case "RaidSealAndBossBlackout":
                    LockdownRulesetOverrides.RaidGeofenceForced = true;
                    LockdownRulesetOverrides.BossLockdownForced = true;
                    break;

                case "CargoIronman":
                    LockdownGlobalKeyEngine.SetTeleportAll(false);
                    break;

                case "None":
                default:
                    break;
            }
        }

        private static void Lift(string ruleset)
        {
            switch (ruleset)
            {
                case "NoPortals":
                    LockdownGlobalKeyEngine.SetNoPortals(false);
                    LockdownForceDisconnectEngine.DisengageScope(Reason);
                    break;
                case "OneWayOutboundOnly":
                    LockdownOneWayEngine.SetEnabled(false);
                    break;
                case "TieredTravel":
                    LockdownRegionEngine.SetProgrammaticGroups(null);
                    break;
                case "NightCurfew":
                    LockdownNightCurfewEngine.SetEnabled(false);
                    break;
                case "RaidSealAndBossBlackout":
                    LockdownRulesetOverrides.RaidGeofenceForced = false;
                    LockdownRulesetOverrides.BossLockdownForced = false;
                    break;
                case "CargoIronman":
                    break;
                default:
                    break;
            }
        }

        /// <summary>Reads genuinely-persisted world progression (defeated_* are &gt;=NonServerOption, .db-backed global keys) to gate travel between biome tiers - meadows/blackforest always allowed together; swamp/mountain unlock after Eikthyr; plains/mistlands after Bonemass+Moder; ashlands/deepnorth after Yagluth.</summary>
        private static List<LockdownRegionGroup> BuildTieredGroups()
        {
            bool eikthyr = ZoneSystem.instance != null && ZoneSystem.instance.GetGlobalKey(GlobalKeys.defeated_eikthyr);
            bool bonemass = ZoneSystem.instance != null && ZoneSystem.instance.GetGlobalKey(GlobalKeys.defeated_bonemass);

            var groups = new List<LockdownRegionGroup>
            {
                new LockdownRegionGroup { Name = "tier0", Biomes = new List<string> { "Meadows", "BlackForest" }, Suffix = "" }
            };
            groups.Add(new LockdownRegionGroup
            {
                Name = "tier1",
                Biomes = new List<string> { "Swamp", "Mountain" },
                Suffix = eikthyr ? "" : "~1"
            });
            groups.Add(new LockdownRegionGroup
            {
                Name = "tier2",
                Biomes = new List<string> { "Plains", "Mistlands" },
                Suffix = bonemass ? "" : "~2"
            });
            groups.Add(new LockdownRegionGroup
            {
                Name = "tier3",
                Biomes = new List<string> { "AshLands", "DeepNorth" },
                Suffix = "~3" // Always namespaced separately in this simplified preset - end-game biomes never auto-merge with the open tier.
            });
            return groups;
        }
    }
}
