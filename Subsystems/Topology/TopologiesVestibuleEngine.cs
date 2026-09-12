using System;
using System.Collections.Generic;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;
using TortalPortalLite.Subsystems.Foundations;

namespace TortalPortalLite.Subsystems.Topology
{
    /// <summary>
    /// #10 Nested / Layered Airlock (vestibule pattern) - composes two independently-tagged layers that
    /// vanilla's own reconciler can never bridge: FindRandomUnconnectedPortal only considers candidates
    /// whose s_tag is string-equal to the orphan's (SERVER decompile :100669), so an outer portal that
    /// loses its connection can only ever be re-paired with another outer portal, never with anything
    /// inside the vestibule - the closest thing the ZDO layer offers to an access-control boundary.
    ///
    ///  - Outer layer: every outer portal -> the vestibule's own arrival portal -> a fabricated Anchor at
    ///    the vestibule floor (a Fan-In Star with an anchor terminator, #2 + #8) - built here as a
    ///    transient TopologyShapeDefinition and handed to TopologiesShapeEngine.ReassertShape, the exact
    ///    same algorithm #2 itself uses, rather than re-deriving the pass-1-survival argument twice.
    ///  - Inner layer: one independent mutually-targeting pair per "wing" (Portal Bank / Departures Hall,
    ///    #11), each on its own tag namespace so wings can never bridge into each other either.
    ///
    /// Containment falls straight out of this engine only ever resolving EXPLICITLY DECLARED positions
    /// (never "every portal currently carrying tag T") - the catalog's own failure mode ("a player
    /// building their own portal and naming it 'gate' joins the outer star's tag group... filter managed
    /// membership by position, not by tag alone") is structurally impossible here: an impostor portal is
    /// simply never a member of any Nodes list this engine builds, so it is never touched, adopted, or
    /// reasserted, regardless of what tag a player gives it.
    ///
    /// Closing a wing (admin-edited ClosedWingTags in topologies.json): preferred mode repoints the
    /// wing's INSIDE slot at a jail Anchor ("stable and needs no tag surgery" - the catalog's own
    /// preferred alternative); if no ClosedWingAnchor is configured, falls back to the other cited
    /// mitigation (a unique invisible suffix + null connection, matching the catalog's own
    /// "[$piece_portal_unconnected]" playerExperience beat exactly).
    /// </summary>
    public static class TopologiesVestibuleEngine
    {
        private static float _timer;

        public static void OnUpdate(float dt)
        {
            _timer += dt;
            float interval = TopologiesConfig.ShapeReassertSeconds?.Value ?? 2.0f;
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

            int budget = TopologiesConfig.MaxWritesPerTick?.Value ?? 50;
            int written = 0;

            foreach (TopologyVestibuleDefinition v in TopologiesDefinitions.Current.Vestibules)
            {
                if (written >= budget)
                {
                    break;
                }
                written += ReassertOne(v, budget - written);
            }
        }

        private static int ReassertOne(TopologyVestibuleDefinition v, int budget)
        {
            if (v == null || string.IsNullOrEmpty(v.OuterTag))
            {
                return 0;
            }
            int written = 0;

            var outerShape = new TopologyShapeDefinition
            {
                Name = v.Name + ":outer",
                Tag = v.OuterTag,
                Anchor = new TopologyAnchorSpec { Position = v.VestibuleAnchor, FacingYaw = 0f },
                Nodes = new List<TopologyNode>()
            };
            foreach (TopologyVec3 outerPos in v.OuterPortals)
            {
                outerShape.Nodes.Add(new TopologyNode { Position = outerPos, Target = v.VestibulePortal });
            }
            outerShape.Nodes.Add(new TopologyNode { Position = v.VestibulePortal, TargetAnchor = true });

            written += TopologiesShapeEngine.ReassertShape(outerShape, budget - written, autoProvisionGlobal: false);
            if (written >= budget)
            {
                return written;
            }

            var closed = new HashSet<string>(v.ClosedWingTags ?? new List<string>(), StringComparer.Ordinal);
            foreach (TopologyVestibuleWing wing in v.Wings)
            {
                if (written >= budget)
                {
                    break;
                }
                written += ReassertWing(v, wing, closed.Contains(wing.Tag), budget - written);
            }
            return written;
        }

        private static int ReassertWing(TopologyVestibuleDefinition v, TopologyVestibuleWing wing, bool isClosed, int budget)
        {
            if (wing == null || string.IsNullOrEmpty(wing.Tag) || budget <= 0)
            {
                return 0;
            }

            if (isClosed && v.ClosedWingAnchor != null)
            {
                var wingShape = new TopologyShapeDefinition
                {
                    Name = v.Name + ":wing:" + wing.Tag,
                    Tag = wing.Tag,
                    Anchor = new TopologyAnchorSpec { Position = v.ClosedWingAnchor, FacingYaw = 0f },
                    Nodes = new List<TopologyNode>
                    {
                        new TopologyNode { Position = wing.InsideSlot, TargetAnchor = true },
                        // The outside slot is deliberately left unmanaged here (not NoManagedEdge on
                        // purpose, simply absent) - vanilla's own pass 1 will find its target (the inside
                        // slot) now tag-mismatched and null it within 5s, which is exactly the desired
                        // "outside now leads nowhere" outcome, with zero extra writes from this engine.
                    }
                };
                return TopologiesShapeEngine.ReassertShape(wingShape, budget, autoProvisionGlobal: false);
            }

            if (isClosed)
            {
                return ReassertClosedWingViaScramble(wing);
            }

            var openShape = new TopologyShapeDefinition
            {
                Name = v.Name + ":wing:" + wing.Tag,
                Tag = wing.Tag,
                Nodes = new List<TopologyNode>
                {
                    new TopologyNode { Position = wing.InsideSlot, Target = wing.OutsideSlot },
                    new TopologyNode { Position = wing.OutsideSlot, Target = wing.InsideSlot }
                }
            };
            return TopologiesShapeEngine.ReassertShape(openShape, budget, autoProvisionGlobal: false);
        }

        private static int ReassertClosedWingViaScramble(TopologyVestibuleWing wing)
        {
            if (ZDOMan.instance == null || !PortalCensus.TryGetByPosition(wing.InsideSlot.ToVector3(), out PortalRecord record))
            {
                return 0;
            }
            ZDO zdo = ZDOMan.instance.GetZDO(record.Uid);
            if (zdo == null || !zdo.IsValid())
            {
                return 0;
            }

            string scrambled = TopologiesTagShardEngine.FitAndShard(TopologiesTagShardEngine.BaseOf(record.Tag), "c" + TopologiesTagShardEngine.ShortCode(zdo.m_uid, 5));
            bool tagWrong = record.Tag != scrambled;
            bool connWrong = record.Connection != ZDOID.None;
            if (!tagWrong && !connWrong)
            {
                return 0;
            }

            try
            {
                PortalOwnership.ClaimAndWrite(zdo, z =>
                {
                    if (tagWrong)
                    {
                        z.Set(ZDOVars.s_tag, scrambled);
                    }
                    if (connWrong)
                    {
                        z.SetConnection(ZDOExtraData.ConnectionType.Portal, ZDOID.None);
                    }
                });
                return 1;
            }
            catch (Exception ex)
            {
                PortalDebug.LogError($"[TopologiesVestibuleEngine] failed to close wing '{wing.Tag}': {ex.GetType().Name}: {ex.Message}");
                return 0;
            }
        }
    }
}
