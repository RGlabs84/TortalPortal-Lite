using System.Collections.Generic;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;

namespace TortalPortalLite.Subsystems.WorldOps
{
    /// <summary>One directed edge (economy.json section "oneWayRoutes") - A -> B with no implied return leg.</summary>
    public sealed class EconomyOneWayEdge
    {
        public EconomyPosition From = new EconomyPosition();
        public EconomyPosition To = new EconomyPosition();
    }

    /// <summary>
    /// #101 One-Way Trade Routes. A -> B with no return leg - runtime-stable in vanilla because
    /// `Game.ConnectPortals` pass 1 never checks reciprocity (catalog's own citation :100600-100604), so
    /// A -> B while B -> C is stable indefinitely, as are rings and self-loops, provided every
    /// participant shares one byte-identical tag and no participant's own connection is None.
    ///
    /// THE failure mode this engine exists to close: `ZDOExtraData.RegenerateConnectionHashData` gives
    /// each ZDO exactly one hash slot, so `ZDOMan.ConnectPortals`'s load-time relink (run from
    /// `ZDOMan.LoadChunks`, before any peer connects) silently converts a one-way link back to two-way,
    /// collapses a ring, or erases a self-loop. Reasserting every edge on a steady cadence (well under
    /// vanilla's own 5s pass) - published into EconomyRoutingKernel exactly like every other mechanism in
    /// this domain, so a portal can be BOTH a one-way trade-route node AND (say) tolled, without two
    /// independent writers fighting over its connection.
    /// </summary>
    public static class EconomyOneWayRouteEngine
    {
        private static int _lastVersion = -1;
        private static List<EconomyOneWayEdge> _edges = new List<EconomyOneWayEdge>();
        private static float _timer;

        public static void OnUpdate(float dt)
        {
            RefreshIfNeeded();
            _timer += dt;
            float interval = EconomyConfig.OneWayReassertSeconds?.Value ?? 2f;
            if (_timer < interval || _edges.Count == 0)
            {
                return;
            }
            _timer = 0f;
            foreach (EconomyOneWayEdge edge in _edges)
            {
                Evaluate(edge);
            }
        }

        private static void RefreshIfNeeded()
        {
            if (EconomyRegistry.Version != _lastVersion)
            {
                _lastVersion = EconomyRegistry.Version;
                _edges = EconomyRegistry.Section<EconomyOneWayEdge>("oneWayRoutes");
            }
        }

        private static void Evaluate(EconomyOneWayEdge edge)
        {
            ZDO? fromZdo = EconomyWriteOps.ResolveLivePortal(edge.From);
            if (fromZdo == null)
            {
                return;
            }
            ZDOID fromUid = fromZdo.m_uid;

            ZDO? toZdo = EconomyWriteOps.ResolveLivePortal(edge.To);
            if (toZdo == null)
            {
                EconomyRoutingKernel.Publish(fromUid, "oneway", false, null, 10);
                return;
            }

            EconomyRoutingKernel.SetDestination(fromUid, toZdo.m_uid);
            EconomyRoutingKernel.Publish(fromUid, "oneway", true, null, 10);
        }
    }
}
