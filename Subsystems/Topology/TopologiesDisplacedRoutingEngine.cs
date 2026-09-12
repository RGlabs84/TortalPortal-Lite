using System;
using System.Globalization;
using UnityEngine;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;
using TortalPortalLite.Core.Hooks;
using TortalPortalLite.Subsystems.Foundations;

namespace TortalPortalLite.Subsystems.Topology
{
    /// <summary>
    /// #20 Displaced Routing - "the master enabler": own the pairing, free s_tag for pure display. Every
    /// constraint elsewhere in this catalogue (one tag per network, identical hover names across a tier,
    /// invisible suffixes, 10-character budgets, a player accidentally retagging a hub out of existence)
    /// exists only because vanilla routes on s_tag. This engine reads routing from a mod-private key
    /// instead (TopologiesKeys.DisplacedRoute - NEEDS NEW KEY, see that file; fully functional today via
    /// this domain's own placeholder) so s_tag can carry an arbitrary-length, arbitrary-content display
    /// label with zero effect on where the portal actually leads.
    ///
    /// IMPORTANT DEVIATION FROM THE CATALOG'S OWN IDEAL, and why: the catalog's reference implementation
    /// is `[HarmonyPatch(typeof(Game), nameof(Game.ConnectPortals))] static bool Prefix() => false;` -
    /// a PREFIX that fully suppresses vanilla's tag-based reconciliation pass. This mod's task rules
    /// forbid writing a second, independent Harmony patch on a vanilla method Core/Hooks/ already patches
    /// exactly once, and ConnectPortalsHook (Core/Hooks/ConnectPortalsHook.cs) only exposes
    /// RegisterPostfix - there is no broker capability here for a prefix that can return false and skip
    /// vanilla's own method body.
    /// // NEEDS NEW HOOK BROKER on Game.ConnectPortals: purpose - a prefix-capable variant (able to
    /// // return false and suppress vanilla's own pass 1/pass 2 entirely) would let Displaced Routing
    /// // match the catalog's reference implementation exactly, with zero risk of a same-tick teardown.
    ///
    /// What this engine does instead, and why it is still correct: it registers a POSTFIX (priority 200,
    /// i.e. late) on the same broker, so its own re-assert runs immediately after vanilla's pass
    /// finishes, within the SAME synchronous call - Unity's single-threaded frame model means no
    /// Update()/render ever runs between "vanilla just tore down/re-paired a displaced-routing portal
    /// because its unique display tag differed from its neighbour's" and "this postfix immediately
    /// overwrites that with the declared route", so no client ever observably sees the intermediate
    /// vanilla-imposed state. It ALSO reasserts on its own independent timer
    /// (TopologiesConfig.DisplacedRoutingReassertSeconds), so a route is corrected long before the next
    /// 5s vanilla pass even if something else changed it in between.
    ///
    /// Two-phase-commit note (the catalog's own "your pairer now owns the two-phase-commit problem
    /// vanilla solved" warning): does not apply here. Every write in this mod goes through
    /// PortalOwnership.ClaimAndWrite, which claims ownership and writes the connection directly and
    /// synchronously - it never calls Game.SetConnection's deferred-to-owning-peer branch
    /// (:100641-100650), so this engine never produces the "portal in flight to a peer" state
    /// Game.IsCurrentlyConnectingPortal exists to guard against, and never needs to reproduce it.
    /// Never caches a ZDO reference across ticks either (ZDOPool recycles them on destruction, ZDO.Reset
    /// clears m_uid, :78286-78311/:73536-73545) - every tick re-resolves both ends fresh from
    /// PortalCensus, by position, exactly like every other engine in this domain.
    /// </summary>
    public static class TopologiesDisplacedRoutingEngine
    {
        private static float _timer;

        public static void Initialize()
        {
            ConnectPortalsHook.RegisterPostfix(200, Reassert);
        }

        public static void OnUpdate(float dt)
        {
            _timer += dt;
            float interval = TopologiesConfig.DisplacedRoutingReassertSeconds?.Value ?? 2.0f;
            if (_timer < interval)
            {
                return;
            }
            _timer = 0f;
            Reassert();
        }

        private static void Reassert()
        {
            if (ZDOMan.instance == null || !VersionMigration.DestructivePassesAllowed)
            {
                return;
            }
            var routes = TopologiesDefinitions.Current.DisplacedRoutes;
            if (routes.Count == 0)
            {
                return;
            }

            int budget = TopologiesConfig.MaxWritesPerTick?.Value ?? 50;
            int written = 0;
            foreach (TopologyDisplacedRoute route in routes)
            {
                if (written >= budget)
                {
                    break;
                }
                if (ReassertOne(route))
                {
                    written++;
                }
            }
        }

        private static bool ReassertOne(TopologyDisplacedRoute route)
        {
            if (route == null || !PortalCensus.TryGetByPosition(route.From.ToVector3(), out PortalRecord record))
            {
                return false;
            }
            ZDO zdo = ZDOMan.instance.GetZDO(record.Uid);
            if (zdo == null || !zdo.IsValid())
            {
                return false;
            }

            ZDOID desired;
            string routeEncoded;
            if (route.ToSelf)
            {
                desired = record.Uid;
                routeEncoded = "self";
            }
            else if (route.To != null)
            {
                if (!PortalCensus.TryGetByPosition(route.To.ToVector3(), out PortalRecord targetRecord))
                {
                    return false; // target not resolvable yet - skip, self-heals once it reappears
                }
                desired = targetRecord.Uid;
                Vector3 p = route.To.ToVector3();
                routeEncoded = p.x.ToString(CultureInfo.InvariantCulture) + ";" + p.y.ToString(CultureInfo.InvariantCulture) + ";" + p.z.ToString(CultureInfo.InvariantCulture);
            }
            else
            {
                return false;
            }

            bool connWrong = record.Connection != desired;
            bool tagWrong = !string.IsNullOrEmpty(route.DisplayTag) && record.Tag != route.DisplayTag;
            bool routeKeyWrong = zdo.GetString(TopologiesKeys.DisplacedRoute, "") != routeEncoded;

            if (!connWrong && !tagWrong && !routeKeyWrong)
            {
                return false;
            }

            try
            {
                PortalOwnership.ClaimAndWrite(zdo, z =>
                {
                    if (tagWrong)
                    {
                        z.Set(ZDOVars.s_tag, route.DisplayTag);
                    }
                    if (connWrong)
                    {
                        z.SetConnection(ZDOExtraData.ConnectionType.Portal, desired);
                    }
                    if (routeKeyWrong)
                    {
                        z.Set(TopologiesKeys.DisplacedRoute, routeEncoded);
                    }
                });
                PortalRecordStore.EnsureRecordId(zdo);
                PortalRecordStore.SetNetworkId(zdo, "topology:displaced");
                return true;
            }
            catch (Exception ex)
            {
                PortalDebug.LogError($"[TopologiesDisplacedRoutingEngine] failed to reassert route from {route.From.ToVector3()}: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }
    }
}
