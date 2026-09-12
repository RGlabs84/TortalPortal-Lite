using System;
using System.Collections.Generic;
using UnityEngine;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;
using TortalPortalLite.Subsystems.Foundations;

namespace TortalPortalLite.Subsystems.Topology
{
    /// <summary>
    /// #13 Carousel - mechanically identical to the Switchboard (#12) minus the player input: a timer
    /// cycles the hub's outbound edge through its declared destinations instead of a player selecting
    /// one. Inbound legs (every destination -> hub) are the same reasserted Fan-In Star.
    ///
    /// The catalog's own caveat is structural, not a bug to fix: the destination is resolved at the
    /// INSTANT of transit (TeleportWorld.Teleport, :143539), not at the instant a player decides to walk
    /// in, and there is exactly one connection slot - it cannot be latched per-player. This engine
    /// implements both cited mitigations: (1) a proximity pause (TopologiesConfig.CarouselProximityPauseRadius)
    /// so the destination cannot flip out from under someone already approaching, and (2) an optional
    /// live Sign update (ZDOVars.s_text, server-writable) naming the CURRENT destination, so a long
    /// period's advertisement stays honest for practically the whole period. A rotation also broadcasts
    /// the new destination ZDO to every peer (ZDOMan.ForceSendZDO(ZDOID) - all peers, unlike Switchboard's
    /// single targeted push) so nobody's first walk-in after a flip silently no-ops at TeleportWorld's
    /// gate 1 (:143519-143522).
    /// </summary>
    public static class TopologiesCarouselEngine
    {
        private sealed class CarouselState
        {
            public int Index;
            public float RotationTimer;
        }

        private static readonly Dictionary<string, CarouselState> _state = new Dictionary<string, CarouselState>();
        private static float _reassertTimer;

        public static void OnUpdate(float dt)
        {
            foreach (TopologyCarouselDefinition c in TopologiesDefinitions.Current.Carousels)
            {
                AdvanceRotation(c, dt);
            }

            _reassertTimer += dt;
            float interval = TopologiesConfig.ShapeReassertSeconds?.Value ?? 2.0f;
            if (_reassertTimer < interval)
            {
                return;
            }
            _reassertTimer = 0f;
            Reassert();
        }

        private static void AdvanceRotation(TopologyCarouselDefinition c, float dt)
        {
            if (c == null || c.Destinations == null || c.Destinations.Count == 0)
            {
                return;
            }
            CarouselState state = GetState(c);

            if (IsAnyPlayerNear(c.Hub.ToVector3()))
            {
                return;
            }

            state.RotationTimer += dt;
            float period = Math.Max(1f, c.PeriodSeconds);
            if (state.RotationTimer < period)
            {
                return;
            }
            state.RotationTimer = 0f;
            state.Index = (state.Index + 1) % c.Destinations.Count;
            BroadcastNewTarget(c, state);
            UpdateSign(c, state);
        }

        private static bool IsAnyPlayerNear(Vector3 pos)
        {
            float radius = TopologiesConfig.CarouselProximityPauseRadius?.Value ?? 8f;
            if (radius <= 0f)
            {
                return false;
            }
            float radiusSqr = radius * radius;
            foreach (ConnectedCharacter who in ConnectedCharacters.All())
            {
                if ((who.Position - pos).sqrMagnitude <= radiusSqr)
                {
                    return true;
                }
            }
            return false;
        }

        private static void Reassert()
        {
            if (ZDOMan.instance == null || !VersionMigration.DestructivePassesAllowed)
            {
                return;
            }
            int budget = TopologiesConfig.MaxWritesPerTick?.Value ?? 50;
            int written = 0;
            foreach (TopologyCarouselDefinition c in TopologiesDefinitions.Current.Carousels)
            {
                if (written >= budget)
                {
                    break;
                }
                written += ReassertOne(c, budget - written);
            }
        }

        private static int ReassertOne(TopologyCarouselDefinition c, int budget)
        {
            if (c == null || string.IsNullOrEmpty(c.Tag) || c.Destinations == null || c.Destinations.Count == 0)
            {
                return 0;
            }
            CarouselState state = GetState(c);
            state.Index = ((state.Index % c.Destinations.Count) + c.Destinations.Count) % c.Destinations.Count;

            var shape = new TopologyShapeDefinition { Name = "carousel:" + c.Name, Tag = c.Tag, Nodes = new List<TopologyNode>() };
            foreach (TopologyDestination d in c.Destinations)
            {
                shape.Nodes.Add(new TopologyNode { Position = d.Position, Target = c.Hub });
            }
            shape.Nodes.Add(new TopologyNode { Position = c.Hub, Target = c.Destinations[state.Index].Position });
            return TopologiesShapeEngine.ReassertShape(shape, budget, autoProvisionGlobal: false);
        }

        /// <summary>Seeds a first-seen carousel's index from whatever the hub is ALREADY connected to (persisted ZDO data) instead of always defaulting to destination 0 - same reasoning as TopologiesSwitchboardEngine's own GetState.</summary>
        private static CarouselState GetState(TopologyCarouselDefinition c)
        {
            if (!_state.TryGetValue(c.Name, out CarouselState s))
            {
                s = new CarouselState { Index = ResolveInitialIndex(c.Hub, c.Destinations) };
                _state[c.Name] = s;
            }
            return s;
        }

        private static int ResolveInitialIndex(TopologyVec3 hub, List<TopologyDestination> destinations)
        {
            if (ZDOMan.instance != null && PortalCensus.TryGetByPosition(hub.ToVector3(), out PortalRecord hubRecord) && hubRecord.Connection != ZDOID.None)
            {
                for (int i = 0; i < destinations.Count; i++)
                {
                    if (PortalCensus.TryGetByPosition(destinations[i].Position.ToVector3(), out PortalRecord destRecord) && destRecord.Uid == hubRecord.Connection)
                    {
                        return i;
                    }
                }
            }
            return 0;
        }

        private static void BroadcastNewTarget(TopologyCarouselDefinition c, CarouselState state)
        {
            if (ZDOMan.instance == null)
            {
                return;
            }
            if (PortalCensus.TryGetByPosition(c.Destinations[state.Index].Position.ToVector3(), out PortalRecord destRecord))
            {
                ZDOMan.instance.ForceSendZDO(destRecord.Uid);
            }
        }

        /// <summary>Best-effort: writes the first ZDO found near the declared SignPosition, on the assumption an admin placed exactly one Sign piece there. Never fails loudly if nothing resolves - a missing sign just means the "long period + honest sign" mitigation is unavailable, not that the carousel itself stops working.</summary>
        private static void UpdateSign(TopologyCarouselDefinition c, CarouselState state)
        {
            if (c.SignPosition == null)
            {
                return;
            }
            List<ZDO> nearby = ZdoSpatialQuery.FindNear(c.SignPosition.ToVector3(), 1.5f);
            foreach (ZDO zdo in nearby)
            {
                if (!zdo.IsValid())
                {
                    continue;
                }
                string text = "Currently: " + c.Destinations[state.Index].Name;
                if (zdo.GetString(ZDOVars.s_text, "") == text)
                {
                    return;
                }
                PortalOwnership.ClaimAndWrite(zdo, z => z.Set(ZDOVars.s_text, text));
                return;
            }
        }
    }
}
