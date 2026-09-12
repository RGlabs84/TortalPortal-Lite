using System;
using System.Globalization;
using UnityEngine;
using TortalPortalLite.Core;
using TortalPortalLite.Core.Data;
using TortalPortalLite.Subsystems.Foundations;

namespace TortalPortalLite.Subsystems.Topology
{
    /// <summary>
    /// The generic "declare a fixed directed graph among named portal positions (+ optional fabricated
    /// Anchors), keep it durable, self-heal on drift" reassert loop. One engine, driven by
    /// TopologiesDefinitions' Shapes list, covers nine catalog options because they all reduce to the
    /// same primitive - "for each declared node, what should its ZDO connection be" - and differ only in
    /// which target each node names:
    ///
    ///   #1  Simple Pair            - 2 nodes, each Target = the other's position.
    ///   #2  Fan-In Star            - N spokes with Target = hub's position; hub Target = one chosen
    ///                                return spoke's position (or TargetAnchor).
    ///   #4  Directed Ring          - N nodes, node i Target = node (i+1 mod N)'s position.
    ///   #6  Open Chain + Terminator- like a ring but the last node's Target is whichever terminator the
    ///                                admin chose: TargetSelf (self-loop), Target = head's position
    ///                                (wrap), or TargetAnchor.
    ///   #7  Self-Loop Dead End     - 1+ nodes with TargetSelf, sharing a tag with zero or one "real" node.
    ///   #9  Directed In-Tree       - every non-root node's Target = its parent's position; the root
    ///                                uses TargetAnchor (or a designated Target). Outward "express" banks
    ///                                are just separate 2-node shape entries (see #11 below).
    ///   #11 Portal Bank            - independent 2-node shapes (or NoManagedEdge nodes if the admin
    ///                                would rather vanilla's own pass 2 do the pairing); this engine adds
    ///                                tag-hijack protection, RecordId/NetworkId bookkeeping and (opt-in)
    ///                                provisioning via TopologiesFabricationEngine.TryProvisionPortal.
    ///   #18 Black-Hole Sink        - every node TargetAnchor = true, sharing an Anchor; see the
    ///                                StashPreAnchorTarget/RestoreFromStash reversibility pair below.
    ///   #21 Asymmetric Round-Trip  - a 2-3 node chain ending in TargetAnchor, placed so the "in" and
    ///                                "out" legs sit at different physical locations.
    ///
    /// Survives vanilla's own 5s Game.ConnectPortals pass for the identical reason NetworkReassertEngine
    /// (Foundations, #69) does: pass 1 only clears a connection when the partner is missing, tag-
    /// mismatched, or itself None (SERVER decompile :100594-100606) - it never checks reciprocity, so any
    /// shape built from mutually tag-matched, non-None edges is a fixed point once this engine has
    /// asserted it once. Writes ONLY through PortalOwnership.ClaimAndWrite, per Core/Data/PortalOwnership's
    /// own doc comment; this is one of the two write paths this mod's task boundaries allow a Topologies
    /// engine (the other being a declared Foundations NetworkDefinition, which cannot express a star,
    /// tree, chain terminator, fabricated Anchor, or reversible sink - hence this engine needing its own
    /// write path instead of piggybacking on NetworkReassertEngine).
    /// </summary>
    public static class TopologiesShapeEngine
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
            bool autoProvisionGlobal = TopologiesConfig.AutoProvisionPortals?.Value == true;

            foreach (TopologyShapeDefinition shape in TopologiesDefinitions.Current.Shapes)
            {
                if (written >= budget)
                {
                    break;
                }
                written += ReassertShape(shape, budget - written, autoProvisionGlobal);
            }
        }

        /// <summary>
        /// Public so other Topologies engines that need "a fixed set of nodes with declared targets,
        /// kept durable" but aren't themselves a plain admin-declared Shape (Vestibule's two composed
        /// layers, #10; Switchboard/Carousel's star-shaped inbound legs, #12/#13) can reuse this exact
        /// reassert algorithm instead of re-implementing the pass-1-survival argument a second time.
        /// </summary>
        public static int ReassertShape(TopologyShapeDefinition shape, int budget, bool autoProvisionGlobal)
        {
            if (shape == null || string.IsNullOrEmpty(shape.Tag) || shape.Nodes == null || shape.Nodes.Count == 0)
            {
                return 0;
            }

            bool haveAnchor = false;
            ZDOID anchorUid = ZDOID.None;
            if (shape.Anchor != null)
            {
                haveAnchor = TopologiesFabricationEngine.TryEnsureAnchor(shape.Anchor, shape.Tag, out anchorUid);
            }

            int written = 0;
            foreach (TopologyNode node in shape.Nodes)
            {
                if (written >= budget)
                {
                    break;
                }
                if (ReassertNode(shape, node, haveAnchor, anchorUid, autoProvisionGlobal))
                {
                    written++;
                }
            }
            return written;
        }

        private static bool ReassertNode(TopologyShapeDefinition shape, TopologyNode node, bool haveAnchor, ZDOID anchorUid, bool autoProvisionGlobal)
        {
            Vector3 pos = node.Position.ToVector3();
            if (!PortalCensus.TryGetByPosition(pos, out PortalRecord record))
            {
                if (shape.AutoProvisionPortals && autoProvisionGlobal)
                {
                    TopologiesFabricationEngine.TryProvisionPortal(pos, node.FacingYaw);
                }
                return false; // resolves on a later census tick, once provisioned or once an admin places it
            }

            ZDO zdo = ZDOMan.instance.GetZDO(record.Uid);
            if (zdo == null || !zdo.IsValid())
            {
                return false;
            }

            if (node.RestoreFromStash)
            {
                return ReassertRestoreFromStash(shape, zdo, record);
            }

            bool wantEdge;
            ZDOID desiredTarget;
            bool isAnchorEdge = false;

            if (node.NoManagedEdge)
            {
                wantEdge = false;
                desiredTarget = ZDOID.None;
            }
            else if (node.TargetSelf)
            {
                wantEdge = true;
                desiredTarget = record.Uid;
            }
            else if (node.TargetAnchor)
            {
                if (!haveAnchor)
                {
                    return false;
                }
                wantEdge = true;
                desiredTarget = anchorUid;
                isAnchorEdge = true;
            }
            else if (node.Target != null)
            {
                if (!PortalCensus.TryGetByPosition(node.Target.ToVector3(), out PortalRecord targetRecord))
                {
                    return false; // target not resolvable yet - skip, don't null the edge out from under it
                }
                wantEdge = true;
                desiredTarget = targetRecord.Uid;
            }
            else
            {
                wantEdge = false;
                desiredTarget = ZDOID.None;
            }

            bool tagWrong = record.Tag != shape.Tag;
            bool connWrong = wantEdge && record.Connection != desiredTarget;

            if (isAnchorEdge && shape.StashPreAnchorTarget && record.Connection != anchorUid)
            {
                StashPreAnchorTarget(zdo, record.Connection);
            }

            if (!tagWrong && !connWrong)
            {
                return false;
            }

            try
            {
                PortalOwnership.ClaimAndWrite(zdo, z =>
                {
                    if (tagWrong)
                    {
                        z.Set(ZDOVars.s_tag, shape.Tag);
                    }
                    if (connWrong)
                    {
                        z.SetConnection(ZDOExtraData.ConnectionType.Portal, desiredTarget);
                    }
                });
                PortalRecordStore.EnsureRecordId(zdo);
                PortalRecordStore.SetNetworkId(zdo, "topology:" + shape.Name);
                return true;
            }
            catch (Exception ex)
            {
                PortalDebug.LogError($"[TopologiesShapeEngine] failed to reassert shape '{shape.Name}' node at {pos}: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        /// <summary>Stashes the WORLD POSITION (never the ZDOID - renumbers every world load, ZDO.Load :74552) of whatever this node pointed at right before it is first swallowed into the shape's Anchor.</summary>
        private static void StashPreAnchorTarget(ZDO zdo, ZDOID previousTarget)
        {
            if (previousTarget == ZDOID.None || zdo.GetString(TopologiesKeys.SinkPreRepointPos, "").Length > 0)
            {
                return; // nothing to stash, or already stashed from an earlier sink - never overwrite the first capture
            }
            if (!PortalCensus.TryGet(previousTarget, out PortalRecord previousRecord))
            {
                return;
            }
            Vector3 p = previousRecord.Position;
            string encoded = p.x.ToString(CultureInfo.InvariantCulture) + ";" + p.y.ToString(CultureInfo.InvariantCulture) + ";" + p.z.ToString(CultureInfo.InvariantCulture);
            PortalOwnership.ClaimAndWrite(zdo, z => z.Set(TopologiesKeys.SinkPreRepointPos, encoded));
        }

        private static bool ReassertRestoreFromStash(TopologyShapeDefinition shape, ZDO zdo, PortalRecord record)
        {
            string stashed = zdo.GetString(TopologiesKeys.SinkPreRepointPos, "");
            if (string.IsNullOrEmpty(stashed) || !TryParsePos(stashed, out Vector3 restorePos))
            {
                return false;
            }
            if (!PortalCensus.TryGetByPosition(restorePos, out PortalRecord restoreTarget))
            {
                return false; // original destination isn't currently resolvable - stay as-is rather than null the edge
            }

            // Mirror the ORIGINAL destination's own CURRENT tag, never shape.Tag: pass 1 tears down any
            // edge whose two ends disagree (:100601), so restoring the connection while this node still
            // carried the sink's tag would just be re-broken by vanilla's own 5s pass and re-patched by
            // this engine forever, flickering the connection null for up to that 5s every cycle. This is
            // the one place in this engine where a node's tag intentionally does NOT match shape.Tag -
            // it is being handed back to whatever tag group it belonged to before it was sunk.
            string desiredTag = restoreTarget.Tag;
            bool tagWrong = record.Tag != desiredTag;
            bool connWrong = record.Connection != restoreTarget.Uid;
            if (!tagWrong && !connWrong)
            {
                return false;
            }

            try
            {
                PortalOwnership.ClaimAndWrite(zdo, z =>
                {
                    if (tagWrong)
                    {
                        z.Set(ZDOVars.s_tag, desiredTag);
                    }
                    if (connWrong)
                    {
                        z.SetConnection(ZDOExtraData.ConnectionType.Portal, restoreTarget.Uid);
                    }
                });
                return true;
            }
            catch (Exception ex)
            {
                PortalDebug.LogError($"[TopologiesShapeEngine] failed to restore-from-stash for shape '{shape.Name}': {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        private static bool TryParsePos(string encoded, out Vector3 pos)
        {
            pos = default;
            string[] parts = encoded.Split(';');
            if (parts.Length != 3)
            {
                return false;
            }
            if (!float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x) ||
                !float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float y) ||
                !float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float z))
            {
                return false;
            }
            pos = new Vector3(x, y, z);
            return true;
        }
    }
}
