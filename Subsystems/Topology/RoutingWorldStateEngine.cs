using System.Collections.Generic;
using UnityEngine;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;

namespace TortalPortalLite.Subsystems.Topology
{
    /// <summary>Admin declaration for one World-State Conditional gate (routing.json section "worldStateRules").</summary>
    public sealed class RoutingWorldStateRuleDefinition
    {
        public string Name = "";
        public RoutingPosition Position = new RoutingPosition();

        /// <summary>"busiest" (route to whichever candidate has the most nearby players) or "leastcrowded" (fewest) - both require Candidates.Count &gt;= 1; with exactly one candidate they degenerate to the same result (a population on/off gate to one fixed destination).</summary>
        public string Kind = "busiest";

        public int MinPopulation = 1;
        public List<RoutingPosition> Candidates = new List<RoutingPosition>();
        public List<string>? CandidateNames;
        public float CrowdRadius = 100f;
        public string? ClosedTag;
    }

    /// <summary>
    /// #39 World-State Conditional Routing - the population/occupancy half the catalog itself calls
    /// "fully feasible". `ZNet.instance.GetPeers()` (:81630) plus each peer's character ZDO position
    /// (Core/Data/ConnectedCharacters, already this mod's non-negotiable server-side player list) gives
    /// a live, cheap read of "how many players are online" and "how many are near candidate X" - a
    /// muster gate that opens above a population floor and routes to whichever declared candidate
    /// currently has the most (or least) people near it.
    ///
    /// WEATHER IS DELIBERATELY NOT IMPLEMENTED, matching the catalog's own negative finding:
    /// `EnvMan.FixedUpdate` computes environment from `Game.GetBiome()`, which on a dedicated server
    /// resolves at the reference position PINNED to (1e6,0,1e6) (:100458-100466 + :95719-95721) - the
    /// server's own idea of "current weather" describes a coordinate no player will ever stand on, and
    /// weather is biome-LOCAL per client besides, so there is no single coherent "server weather" to
    /// condition on even in principle. Only time-of-day (RoutingScheduledEngine, #28) is actually
    /// server-coherent among EnvMan-derived state.
    ///
    /// Hysteresis (RoutingConfig.WorldStatePopulationHysteresis) prevents one login/logout from flapping
    /// a population-gated portal open and shut.
    /// </summary>
    public static class RoutingWorldStateEngine
    {
        private static float _timer;
        private static int _lastRegistryVersion = -1;
        private static List<RoutingWorldStateRuleDefinition> _rules = new List<RoutingWorldStateRuleDefinition>();
        private static readonly Dictionary<string, bool> _wasOpen = new Dictionary<string, bool>();

        public static void OnUpdate(float dt)
        {
            if (RoutingConfig.TakeoverEnabled?.Value == false || ZDOMan.instance == null || ZNet.instance == null)
            {
                return;
            }
            _timer += dt;
            float interval = RoutingConfig.WorldStateEvalSeconds?.Value ?? 1.0f;
            if (_timer < interval)
            {
                return;
            }
            _timer = 0f;

            if (RoutingManagedPortalRegistry.Version != _lastRegistryVersion)
            {
                _lastRegistryVersion = RoutingManagedPortalRegistry.Version;
                _rules = RoutingManagedPortalRegistry.Section<RoutingWorldStateRuleDefinition>("worldStateRules");
            }
            if (_rules.Count == 0)
            {
                return;
            }

            List<ConnectedCharacter> characters = ConnectedCharacters.All();
            int population = characters.Count;
            int hysteresis = RoutingConfig.WorldStatePopulationHysteresis?.Value ?? 1;

            foreach (RoutingWorldStateRuleDefinition rule in _rules)
            {
                if (rule.Candidates.Count == 0 || !RoutingPairingAuthorityEngine.TryClaim(rule.Position, $"worldstate:{rule.Name}"))
                {
                    continue;
                }
                ZDO? zdo = RoutingWriteOps.ResolveLive(rule.Position);
                if (zdo == null)
                {
                    continue;
                }

                _wasOpen.TryGetValue(rule.Name, out bool wasOpen);
                bool open = wasOpen
                    ? population >= rule.MinPopulation - hysteresis
                    : population >= rule.MinPopulation;
                _wasOpen[rule.Name] = open;

                string? desiredTag;
                ZDOID desiredConnection;
                if (!open)
                {
                    desiredTag = rule.ClosedTag;
                    desiredConnection = ZDOID.None;
                }
                else
                {
                    int bestIndex = PickCandidate(rule, characters);
                    RoutingPosition bestPos = rule.Candidates[bestIndex];
                    ZDO? destZdo = RoutingWriteOps.ResolveLive(bestPos);
                    desiredConnection = destZdo?.m_uid ?? ZDOID.None;
                    string? name = rule.CandidateNames != null && bestIndex < rule.CandidateNames.Count ? rule.CandidateNames[bestIndex] : null;
                    desiredTag = string.IsNullOrEmpty(name) ? rule.ClosedTag : Truncate(name!);
                }

                RoutingWriteOps.Reassert(zdo, desiredTag, desiredConnection);
                RoutingPairingAuthorityEngine.Publish(zdo.m_uid, desiredTag, desiredConnection);
            }
        }

        private static int PickCandidate(RoutingWorldStateRuleDefinition rule, List<ConnectedCharacter> characters)
        {
            float radiusSqr = rule.CrowdRadius * rule.CrowdRadius;
            int bestIndex = 0;
            int bestCount = rule.Kind == "leastcrowded" ? int.MaxValue : -1;
            for (int i = 0; i < rule.Candidates.Count; i++)
            {
                Vector3 pos = rule.Candidates[i].ToVector3();
                int count = 0;
                foreach (ConnectedCharacter character in characters)
                {
                    if ((character.Position - pos).sqrMagnitude <= radiusSqr)
                    {
                        count++;
                    }
                }
                bool better = rule.Kind == "leastcrowded" ? count < bestCount : count > bestCount;
                if (better)
                {
                    bestCount = count;
                    bestIndex = i;
                }
            }
            return bestIndex;
        }

        private static string Truncate(string s) => s.Length <= 10 ? s : s.Substring(0, 10);
    }
}
