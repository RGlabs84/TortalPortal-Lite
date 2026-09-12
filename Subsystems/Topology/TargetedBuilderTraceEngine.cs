using System.Collections.Generic;
using UnityEngine;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;
using TortalPortalLite.Subsystems.Foundations;

namespace TortalPortalLite.Subsystems.Topology
{
    /// <summary>
    /// #194 Last Place the Portal's Builder Stood - a "retreat" portal that follows its own builder
    /// (ZDOVars.s_creator on the SOURCE portal itself, already surfaced as PortalRecord.Creator by
    /// Foundations/PortalCensus.cs - Piece.SetCreator, SERVER decompile :136415-136424) rather than a
    /// claimed hub: fully automatic, no emote needed, matching the catalog's own worked example ("its
    /// hover immediately reads... connected").
    ///
    /// Variant (a) LIVE (default): a moving Invisible Anchor tracks the builder's last-known-good
    /// position every tick while they are online, frozen at their last sample after they disconnect.
    /// Variant (b) STATIC: captured once - the first maintenance tick this engine notices the source
    /// portal - and never moved again ("wherever you were when you built this"). This approximates the
    /// catalog's own "capture at the moment the portal ZDO arrives" (a SpawnGovernor Deserialize-postfix
    /// pattern this codebase has no broker for) with "captured the first tick this engine's own poll
    /// notices the portal", off by at most one MaintenanceIntervalSeconds/MovingTargetTickSeconds tick.
    ///
    /// Position sampling excludes s_dead, s_inBed, interior (y&gt;3000) and underwater samples (#194's own
    /// safety rule) and, unlike ConnectedCharacters, is never cleared on disconnect - the last good
    /// sample IS the "logout point" proxy the catalog's own text describes.
    /// </summary>
    public static class TargetedBuilderTraceEngine
    {
        private const string Kind = "BuilderTrace";
        private static float _timer;
        private static readonly Dictionary<long, Vector3> _lastGoodPosition = new Dictionary<long, Vector3>();
        private static readonly HashSet<int> _staticCaptured = new HashSet<int>();

        public static void OnUpdate(float dt)
        {
            SamplePositions();

            _timer += dt;
            float interval = TargetedMovingTargetEngine.TickSeconds;
            if (_timer < interval)
            {
                return;
            }
            _timer = 0f;
            Tick();
        }

        private static void SamplePositions()
        {
            foreach (ConnectedCharacter cc in ConnectedCharacters.All())
            {
                if (cc.PlayerId == 0L)
                {
                    continue;
                }
                if (cc.Zdo.GetBool(ZDOVars.s_dead) || cc.Zdo.GetBool(ZDOVars.s_inBed))
                {
                    continue;
                }
                if (cc.Position.y > 3000f)
                {
                    continue;
                }
                if (WorldGenerator.instance != null && cc.Position.y < 30f && WorldGenerator.instance.GetHeight(cc.Position.x, cc.Position.z) < 30f)
                {
                    continue;
                }
                _lastGoodPosition[cc.PlayerId] = cc.Position;
            }
        }

        private static void Tick()
        {
            if (ZDOMan.instance == null)
            {
                return;
            }
            bool live = TargetedConfig.BuilderTraceLiveVariant?.Value != false;

            foreach (TargetedRoute route in TargetedRouteStore.RoutesOfKind(Kind))
            {
                if (!PortalCensus.TryGetByPosition(route.SourcePosition, out PortalRecord sourceRec))
                {
                    continue;
                }
                ZDO source = ZDOMan.instance.GetZDO(sourceRec.Uid);
                if (source == null || !source.IsValid())
                {
                    continue;
                }

                long builderId = source.GetLong(ZDOVars.s_creator, 0L);
                if (builderId == 0L)
                {
                    continue; // #194's own rule: skip pre-mod/admin-spawned portals with no creator
                }

                if (!_lastGoodPosition.TryGetValue(builderId, out Vector3 pos))
                {
                    TargetedPhantomPortalFactory.MarkWaiting(source, TargetedTagFormat.Named("Back (unknown)"));
                    continue;
                }

                string tag = TargetedTagFormat.Named($"Back to {ResolveName(builderId)}");
                string kind = $"buildertrace:{builderId}";

                if (!live)
                {
                    int captureKey = source.m_uid.GetHashCode();
                    if (_staticCaptured.Contains(captureKey))
                    {
                        continue; // already pinned this session - never move again
                    }
                    TargetedAnchorFactory.CreateOrRetarget(source, pos, Quaternion.identity, tag, kind);
                    _staticCaptured.Add(captureKey);
                    continue;
                }

                ZDO existing = TargetedAnchorFactory.ResolveExistingAnchor(source);
                bool sameBuilder = existing != null && TargetedAnchorFactory.GetKind(existing) == kind;
                if (sameBuilder)
                {
                    if (TargetedMovingTargetEngine.MovedEnough(existing.GetPosition(), pos) && TargetedAnchorFactory.MoveTo(existing, pos, Quaternion.identity))
                    {
                        TargetedMovingTargetEngine.ForceSendToNearbySourcePeers(source, existing);
                    }
                }
                else
                {
                    ZDO anchor = TargetedAnchorFactory.CreateOrRetarget(source, pos, Quaternion.identity, tag, kind);
                    if (anchor != null)
                    {
                        TargetedMovingTargetEngine.ForceSendToNearbySourcePeers(source, anchor);
                    }
                }
            }
        }

        private static string ResolveName(long playerId)
        {
            foreach (ConnectedCharacter cc in ConnectedCharacters.All())
            {
                if (cc.PlayerId == playerId)
                {
                    return cc.Name;
                }
            }
            return "Builder";
        }
    }
}
