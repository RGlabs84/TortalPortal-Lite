using System;
using System.Collections.Generic;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;
using TortalPortalLite.Core.Hooks;
using TortalPortalLite.Subsystems.Foundations;

namespace TortalPortalLite.Subsystems.Topology
{
    /// <summary>
    /// #19 Roulette / Periodic Shuffle. The "zero-mod version" the catalog describes (N same-tagged
    /// portals, vanilla's own pass 2 pairs them with UnityEngine.Random.Range and pass 1 freezes the
    /// result until something breaks) needs no code at all - an admin gets it just by tagging portals
    /// identically in-game. This engine implements the two OPT-IN enhancements the catalog names:
    ///
    ///  1. Forced re-roll (TopologiesConfig.RouletteForcedRerollEnabled): on a timer, null every declared
    ///     roulette-tag portal's connection via PortalOwnership.ClaimAndWrite directly (NOT
    ///     Game.SetConnection, whose deferred-to-owning-peer branch, :100641-100650, is exactly the
    ///     catalog's own cited bug - "a portal a player is standing next to takes two or more ticks to
    ///     re-roll"; ClaimAndWrite always claims ownership itself first, forcing the immediate local
    ///     path), then calls the public Game.instance.ConnectPortals() (:100589) once to re-pair
    ///     immediately rather than waiting up to 5s.
    ///  2. Deterministic pairing (TopologiesConfig.RouletteDeterministic): registers a
    ///     FindRandomUnconnectedPortalHook handler - the "minimal, lowest-risk intervention that leaves
    ///     the two-phase commit, ownership handling and logging intact" the catalog itself recommends -
    ///     replacing vanilla's UnityEngine.Random.Range pick with the lowest ZDOID among eligible
    ///     candidates for declared roulette tags only. The eligibility test is copied verbatim from
    ///     Game.FindRandomUnconnectedPortal's own body (read directly at :100664-100679: `portal != skip
    ///     &amp;&amp; tag matches &amp;&amp; connection is None &amp;&amp; !IsCurrentlyConnectingPortal(portal)`) so this
    ///     override cannot double-pair a portal vanilla's own pass 2 already committed earlier in the
    ///     same sweep. Game.IsCurrentlyConnectingPortal is private on Game but directly callable here
    ///     because assembly_valheim is referenced with Publicize=true (TortalPortalLite.csproj).
    /// </summary>
    public static class TopologiesRouletteEngine
    {
        private static float _timer;

        public static void Initialize()
        {
            FindRandomUnconnectedPortalHook.Register(100, DeterministicPick);
        }

        public static void OnUpdate(float dt)
        {
            if (TopologiesConfig.RouletteForcedRerollEnabled?.Value != true)
            {
                return;
            }
            _timer += dt;
            float period = Math.Max(10f, TopologiesConfig.RouletteForcedRerollSeconds?.Value ?? 3600f);
            if (_timer < period)
            {
                return;
            }
            _timer = 0f;
            ForceReroll();
        }

        private static void ForceReroll()
        {
            if (ZDOMan.instance == null || !VersionMigration.DestructivePassesAllowed)
            {
                return;
            }
            var tags = CollectRouletteTags();
            if (tags.Count == 0)
            {
                return;
            }

            int budget = TopologiesConfig.MaxWritesPerTick?.Value ?? 50;
            int written = 0;
            bool any = false;

            foreach (PortalRecord record in PortalCensus.Latest)
            {
                if (written >= budget)
                {
                    break;
                }
                if (!tags.Contains(record.Tag) || record.Connection == ZDOID.None)
                {
                    continue;
                }
                ZDO zdo = ZDOMan.instance.GetZDO(record.Uid);
                if (zdo == null || !zdo.IsValid())
                {
                    continue;
                }

                try
                {
                    PortalOwnership.ClaimAndWrite(zdo, z => z.SetConnection(ZDOExtraData.ConnectionType.Portal, ZDOID.None));
                    written++;
                    any = true;
                }
                catch (Exception ex)
                {
                    PortalDebug.LogError($"[TopologiesRouletteEngine] failed to null {record.Uid} for reroll: {ex.GetType().Name}: {ex.Message}");
                }
            }

            if (any && Game.instance != null)
            {
                Game.instance.ConnectPortals();
            }
        }

        private static HashSet<string> CollectRouletteTags()
        {
            var tags = new HashSet<string>(StringComparer.Ordinal);
            foreach (TopologyRouletteGroup g in TopologiesDefinitions.Current.RouletteGroups)
            {
                if (!string.IsNullOrEmpty(g.Tag))
                {
                    tags.Add(g.Tag);
                }
            }
            return tags;
        }

        private static bool DeterministicPick(List<ZDO> portals, ZDO skip, string tag, out ZDO result)
        {
            result = null;
            if (TopologiesConfig.RouletteDeterministic?.Value != true || !IsRouletteTag(tag))
            {
                return false;
            }

            ZDO best = null;
            foreach (ZDO candidate in portals)
            {
                if (candidate == skip)
                {
                    continue;
                }
                if (candidate.GetString(ZDOVars.s_tag) != tag)
                {
                    continue;
                }
                if (candidate.GetConnectionZDOID(ZDOExtraData.ConnectionType.Portal) != ZDOID.None)
                {
                    continue;
                }
                if (Game.instance != null && Game.instance.IsCurrentlyConnectingPortal(candidate))
                {
                    continue;
                }
                if (best == null || candidate.m_uid.CompareTo(best.m_uid) < 0)
                {
                    best = candidate;
                }
            }

            if (best == null)
            {
                return false;
            }
            result = best;
            return true;
        }

        private static bool IsRouletteTag(string tag)
        {
            foreach (TopologyRouletteGroup g in TopologiesDefinitions.Current.RouletteGroups)
            {
                if (g.Tag == tag)
                {
                    return true;
                }
            }
            return false;
        }
    }
}
