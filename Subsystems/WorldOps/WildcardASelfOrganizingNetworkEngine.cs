using System;
using System.Collections.Generic;
using UnityEngine;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;
using TortalPortalLite.Core.Hooks;
using TortalPortalLite.Subsystems.Foundations;

namespace TortalPortalLite.Subsystems.WorldOps
{
    /// <summary>
    /// #140 Self-Organising Networks From Player Behaviour. Rewires topology from usage data the server
    /// can already see for free: sampled character positions (a lightweight reimplementation of
    /// Wonderland's PositionWatch.IsPortalTransit heuristic - two consecutive samples straddling a
    /// portal and its live partner counts as one transit) persisted per-portal as a plain ZDO int
    /// (WildcardAZdoKeys.TransitCount), never through Core/Data/DataStore.cs's single-slot namespace
    /// (that engine is reserved for #142's puzzle-state payload on the same host portals this engine
    /// also touches - a numeric counter needs its own dedicated key, not a shared string slot).
    ///
    /// Rewiring is exposed as an OPT-IN override on the shared FindRandomUnconnectedPortalHook
    /// (Game.FindRandomUnconnectedPortal, :100664-100679) rather than a Harmony prefix/postfix of our
    /// own: first-true-wins, so if another wave-3 domain's handler at a lower priority number already
    /// decided (e.g. Topology's own phantom-exclusion handler, priority 100), this engine never runs.
    /// Off by default (WildcardAConfig.SelfOrganizeRewireEnabled) since it changes live pairing behaviour
    /// for every same-tag group with 2+ candidates the instant it is turned on.
    ///
    /// #140's own structural warning, preserved rather than "fixed": three or more portals sharing one
    /// tag permanently strand exactly one member (the surviving pair keeps re-confirming itself), and
    /// which member gets stranded is dictionary-bucket-order dependent, not stable across restarts - this
    /// engine does not attempt to fix that, only to bias WHICH of the eligible candidates gets picked.
    /// </summary>
    public static class WildcardASelfOrganizingNetworkEngine
    {
        // playerId -> last sample's nearest portal uid (ZDOID.None if not near any portal last sample).
        private static readonly Dictionary<long, ZDOID> _lastNearPortal = new Dictionary<long, ZDOID>();
        private static float _sampleTimer;
        private static float _rewireTimer;
        private static bool _hookRegistered;

        public static void Initialize()
        {
            if (_hookRegistered)
            {
                return;
            }
            _hookRegistered = true;
            // Priority 300: deliberately after Topology's own phantom-exclusion override (registered at
            // 100 in a prior, already-committed wave) so that handler's "never randomly pair a phantom"
            // guarantee is never bypassed by this one.
            FindRandomUnconnectedPortalHook.Register(300, TryPickMostUsed);
        }

        public static void OnUpdate(float dt)
        {
            if (WildcardAConfig.Enabled?.Value == false || WildcardAConfig.SelfOrganizeEnabled?.Value == false)
            {
                return;
            }
            _sampleTimer += dt;
            float sampleInterval = WildcardAConfig.SelfOrganizeSampleSeconds?.Value ?? 3.0f;
            if (_sampleTimer >= sampleInterval)
            {
                _sampleTimer = 0f;
                SampleTransits();
            }

            if (WildcardAConfig.SelfOrganizeRewireEnabled?.Value != true)
            {
                return;
            }
            _rewireTimer += dt;
            float rewireInterval = WildcardAConfig.SelfOrganizeRewireSeconds?.Value ?? 60f;
            if (_rewireTimer >= rewireInterval)
            {
                _rewireTimer = 0f;
                // Nothing to precompute per-sweep today - candidate transit counts are read live from
                // each portal's own ZDO at pairing time (TryPickMostUsed), so this tick only exists to
                // bound how often that (cheap) read happens; kept for symmetry with every other engine's
                // timer shape and as the natural place to add a periodic log/report later.
            }
        }

        public static int GetTransitCount(ZDO portal) => portal != null && portal.IsValid() ? portal.GetInt(WildcardAZdoKeys.TransitCount, 0) : 0;

        private static void SampleTransits()
        {
            float radius = WildcardAConfig.SelfOrganizeProximityRadius?.Value ?? 40f;
            float radiusSqr = radius * radius;

            foreach (ConnectedCharacter cc in ConnectedCharacters.All())
            {
                ZDOID nearestNow = ZDOID.None;
                float bestSqr = radiusSqr;
                foreach (PortalRecord rec in PortalCensus.Latest)
                {
                    float sqr = (rec.Position - cc.Position).sqrMagnitude;
                    if (sqr <= bestSqr)
                    {
                        bestSqr = sqr;
                        nearestNow = rec.Uid;
                    }
                }

                long playerId = cc.PlayerId;
                if (playerId == 0L)
                {
                    continue;
                }
                _lastNearPortal.TryGetValue(playerId, out ZDOID before);

                // A transit: was near portal A last sample, now near A's CURRENT partner (not A itself,
                // and not "near nothing" -> "near something", which is just walking, not teleporting).
                if (before != ZDOID.None && nearestNow != ZDOID.None && before != nearestNow
                    && PortalCensus.TryGet(before, out PortalRecord beforeRec)
                    && beforeRec.Connection == nearestNow)
                {
                    RecordTransit(before);
                }

                _lastNearPortal[playerId] = nearestNow;
            }
        }

        private static void RecordTransit(ZDOID sourcePortalUid)
        {
            if (ZDOMan.instance == null)
            {
                return;
            }
            ZDO zdo = ZDOMan.instance.GetZDO(sourcePortalUid);
            if (zdo == null || !zdo.IsValid())
            {
                return;
            }
            int count = zdo.GetInt(WildcardAZdoKeys.TransitCount, 0) + 1;
            PortalOwnership.ClaimAndWrite(zdo, z =>
            {
                z.Set(WildcardAZdoKeys.TransitCount, count);
                z.Set(WildcardAZdoKeys.LastTransitTicks, DateTime.UtcNow.Ticks);
            });
            WildcardACrossModIntegrationEngine.RaiseTransitDetected(sourcePortalUid);
        }

        /// <summary>
        /// Replicates Game.FindRandomUnconnectedPortal's own candidate filter (:100664-100677: not skip,
        /// same tag, connection is None, not IsCurrentlyConnectingPortal) and, among eligible candidates,
        /// picks the one with the highest recorded transit count instead of vanilla's uniform random -
        /// declining (false) whenever there are zero or one eligible candidates, since there is nothing
        /// to bias between.
        /// </summary>
        private static bool TryPickMostUsed(List<ZDO> portals, ZDO skip, string tag, out ZDO? result)
        {
            result = null;
            if (Game.instance == null)
            {
                return false;
            }

            ZDO best = null;
            int bestCount = -1;
            int eligible = 0;
            foreach (ZDO candidate in portals)
            {
                if (candidate == skip || candidate.GetString(ZDOVars.s_tag) != tag
                    || candidate.GetConnectionZDOID(ZDOExtraData.ConnectionType.Portal) != ZDOID.None
                    || Game.instance.IsCurrentlyConnectingPortal(candidate))
                {
                    continue;
                }
                eligible++;
                int count = candidate.GetInt(WildcardAZdoKeys.TransitCount, 0);
                if (count > bestCount)
                {
                    bestCount = count;
                    best = candidate;
                }
            }

            if (eligible < 2 || best == null || bestCount <= 0)
            {
                return false; // nothing to bias between, or no usage data yet - let vanilla's own random pick run.
            }
            result = best;
            return true;
        }
    }
}
