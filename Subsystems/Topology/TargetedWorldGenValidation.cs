using System.Collections.Generic;
using UnityEngine;

namespace TortalPortalLite.Subsystems.Topology
{
    /// <summary>
    /// "Where, near this point, can a phantom portal stand" - the placement scanner behind #207
    /// Corpse-Run Gate's two ends (grave side and bed side).
    ///
    /// Pure data only - deliberately never touches ZoneSystem.GetGroundHeight/FindFloor/IsBlocked or any
    /// Physics.* call: those raycast against a live terrain collider, and Game.FixedUpdate pins the
    /// dedicated server's own reference position at (1e6,0,1e6) every physics tick (SERVER decompile
    /// :100506), so no collider ever exists near a real player coordinate server-side - every one of
    /// those calls would silently return its no-hit fallback. Ground comes from TargetedGroundProbe
    /// (the client's own heightmap recipe re-run headless, terraforming included) and obstacles from
    /// TargetedObstacleField (collider footprints off the prefab assets, plus generated locations).
    ///
    /// Search order, each stage only reached when the previous found nothing:
    ///  1. Fully clear: honours the configured clearance against everything, on dry, level, contiguous
    ///     ground. Rings scan outward; once a ring yields a hit, three more rings are scanned and the
    ///     best-scored spot (flattest, most open, nearest) wins.
    ///  2. Same again with the level-ground rules relaxed (steeper hillsides).
    ///  3. Most open spot: no point inside the search radius honours the clearance, so take the
    ///     candidate with the largest edge-to-edge room that still stands on dry ground - it is as far
    ///     from everything as this patch of map allows. Logged as a compromise so an admin can raise
    ///     SearchRadiusMeters if it keeps happening.
    ///  4. Water surface, only when the anchor itself is in water (an ocean death): float the gate.
    ///  5. Last resort: the most open candidate on the ground regardless of slope.
    /// A death inside a dungeon (interior stacked at y &gt; 1000) is re-anchored to the dungeon's
    /// exterior entrance location, so the gate opens outside the crypt rather than inside a wall.
    /// </summary>
    public static class TargetedWorldGenValidation
    {
        /// <summary>Half-width of the playable map along one axis, beyond which WorldGenerator.GetHeight returns the fixed -400 ocean-edge value (SERVER decompile :151978-151981, waterEdgeSqr :151122).</summary>
        private const float WaterEdgeRadius = 10500f;
        /// <summary>Anything above this is an interior (dungeons are assembled ~5000 m up); no surface terrain reaches it.</summary>
        private const float InteriorHeight = 1000f;
        /// <summary>Radial spacing between rings and target arc spacing between candidates on a ring.</summary>
        private const float RingStep = 1f;
        private const float ArcSpacing = 2f;
        private const int MinBearings = 8;
        private const int MaxBearings = 64;
        /// <summary>After the first ring with a fully clear hit, how many further rings are still scored before settling.</summary>
        private const int ExtraRingsAfterHit = 3;
        /// <summary>Cap on ground evaluations spent in the "most open spot" stage.</summary>
        private const int MaxCompromiseChecks = 400;
        /// <summary>Portal pivot sits on the ground; a couple of centimetres of lift only avoids z-fighting on a perfectly flat mesh.</summary>
        private const float GroundLift = 0.03f;
        /// <summary>Base corners of a portal frame, in its own space: +-x across the frame, +-z through it.</summary>
        private const float FrameHalfWidth = 1.2f;
        private const float FrameHalfDepth = 0.5f;
        /// <summary>Ground must sit at least this far above the water surface.</summary>
        private const float DryMargin = 0.3f;

        public enum Quality
        {
            /// <summary>Configured clearance honoured against everything, level dry ground.</summary>
            Clear,
            /// <summary>Nothing in range honoured the clearance; this is the most open dry spot found.</summary>
            Compromise,
            /// <summary>Anchor is in open water; gate floats on the surface.</summary>
            WaterSurface,
            /// <summary>Only steep or otherwise poor ground was available.</summary>
            LastResort,
            /// <summary>WorldGenerator not ready; fixed offset from the anchor.</summary>
            Unplaced,
        }

        public readonly struct Placement
        {
            public readonly Vector3 Position;
            public readonly Quaternion Rotation;
            public readonly Quality Quality;
            /// <summary>Edge-to-edge room to spare beyond the configured clearance (negative = short by that much).</summary>
            public readonly float Margin;
            /// <summary>Horizontal distance from the anchor actually scanned around (the entrance location for an interior death).</summary>
            public readonly float Distance;
            public readonly float NormalY;
            public readonly string Nearest;
            /// <summary>True when the anchor was an interior point re-anchored to its exterior location.</summary>
            public readonly bool ReanchoredFromInterior;
            /// <summary>Obstacle census the search ran against - for log diagnostics.</summary>
            public readonly string Obstacles;
            /// <summary>Anchor's own height minus the modelled ground under it - a sanity readout of the ground model (a tombstone on open ground reads within ~0.5 m; a bed on a raised floor reads its floor height).</summary>
            public readonly float AnchorAboveGround;

            public Placement(Vector3 position, Quaternion rotation, Quality quality, float margin, float distance, float normalY, string nearest, bool reanchored, string obstacles = "", float anchorAboveGround = 0f)
            {
                Position = position;
                Rotation = rotation;
                Quality = quality;
                Margin = margin;
                Distance = distance;
                NormalY = normalY;
                Nearest = nearest;
                ReanchoredFromInterior = reanchored;
                Obstacles = obstacles;
                AnchorAboveGround = anchorAboveGround;
            }

            public bool IsGood => Quality == Quality.Clear;

            public string Describe()
            {
                string margin = Margin >= 999f ? "nothing in range" : $"margin {Margin:+0.0;-0.0}m" + (Nearest.Length > 0 ? $" vs {Nearest}" : "");
                string extra = (ReanchoredFromInterior ? ", re-anchored from interior" : $", anchor {AnchorAboveGround:+0.00;-0.00}m above modelled ground") + (Obstacles.Length > 0 ? ", " + Obstacles : "");
                return $"{Quality} at {Position.x:F1},{Position.y:F1},{Position.z:F1} ({Distance:F1}m out, slope normal {NormalY:F2}, {margin}{extra})";
            }
        }

        private struct Candidate
        {
            public float X;
            public float Z;
            public float R;
            public float Margin;
            public string Nearest;
        }

        private struct GroundFit
        {
            public Vector3 Position;
            public Quaternion Rotation;
            public float NormalY;
            public float Deviation;
            public int OpenSides;
        }

        /// <summary>Rotation facing FROM the offset position back TOWARD the centre it was offset from - the gate faces the grave / the bed.</summary>
        public static Quaternion FacingTowards(Vector3 from, Vector3 towards)
        {
            Vector3 dir = towards - from;
            dir.y = 0f;
            if (dir.sqrMagnitude < 0.0001f)
            {
                return Quaternion.identity;
            }
            return Quaternion.LookRotation(dir.normalized, Vector3.up);
        }

        /// <summary>
        /// Finds where to stand a portal near <paramref name="anchor"/>: between <paramref name="minRadius"/>
        /// and <paramref name="maxRadius"/> out, with <paramref name="clearance"/> metres edge-to-edge from
        /// every solid thing, on the real ground. Always returns a position; Quality says how good it is.
        /// </summary>
        public static Placement FindPortalPlacement(Vector3 anchor, float minRadius, float maxRadius, float clearance, ZDOID ignoreZdo, IReadOnlyList<Vector3>? avoid = null)
        {
            minRadius = Mathf.Max(0.5f, minRadius);
            maxRadius = Mathf.Max(minRadius + RingStep, maxRadius);
            clearance = Mathf.Max(0f, clearance);

            if (!TargetedGroundProbe.Ready)
            {
                return FixedOffset(anchor, minRadius);
            }

            var probe = new TargetedGroundProbe();

            // Interior death: scan around the exterior entrance instead, on the real ground there.
            bool reanchored = false;
            Vector3 scanAnchor = anchor;
            if (anchor.y > InteriorHeight)
            {
                reanchored = true;
                if (TargetedObstacleField.TryFindLocationInZoneOf(anchor, out Vector3 exterior, out _))
                {
                    scanAnchor = exterior;
                }
                scanAnchor.y = probe.HeightAt(scanAnchor.x, scanAnchor.z);
            }

            TargetedObstacleField field = TargetedObstacleField.Build(scanAnchor, maxRadius, clearance, ignoreZdo, avoid);
            maxRadius = field.EffectiveSearchRadius;
            float water = TargetedGroundProbe.WaterLevel;
            string census = $"{field.ZdoObstacles} objects + {field.LocationObstacles} locations in reach" + (field.UnreadablePrefabs > 0 ? $", {field.UnreadablePrefabs} with assumed footprints" : "");
            float anchorAbove = reanchored ? 0f : anchor.y - probe.HeightAt(anchor.x, anchor.z);

            // Every candidate with its clearance margin, cheapest test first: rings of points, then
            // ground evaluation only for those still in the running.
            List<Candidate> candidates = EnumerateCandidates(scanAnchor, minRadius, maxRadius, clearance, field);

            // 1 + 2: fully clear, strict then relaxed ground rules.
            for (int pass = 0; pass < 2; pass++)
            {
                bool strict = pass == 0;
                bool found = false;
                float bestScore = float.MinValue;
                GroundFit bestFit = default;
                Candidate bestCandidate = default;
                float firstHitRing = -1f;
                for (int i = 0; i < candidates.Count; i++)
                {
                    Candidate c = candidates[i];
                    if (c.Margin < 0f)
                    {
                        continue;
                    }
                    if (firstHitRing >= 0f && c.R > firstHitRing + (ExtraRingsAfterHit * RingStep) + 0.001f)
                    {
                        break;
                    }
                    if (!TryFitGround(probe, c.X, c.Z, water, scanAnchor, strict, out GroundFit fit))
                    {
                        continue;
                    }
                    if (firstHitRing < 0f)
                    {
                        firstHitRing = c.R;
                    }
                    float score = (fit.NormalY * 20f) + (Mathf.Min(c.Margin, 5f) * 0.6f) + (fit.OpenSides * 0.5f) - (c.R * 0.35f) - (fit.Deviation * 4f);
                    if (!found || score > bestScore)
                    {
                        found = true;
                        bestScore = score;
                        bestFit = fit;
                        bestCandidate = c;
                    }
                }
                if (found)
                {
                    return new Placement(bestFit.Position, bestFit.Rotation, Quality.Clear, bestCandidate.Margin, bestCandidate.R, bestFit.NormalY, bestCandidate.Nearest, reanchored, census, anchorAbove);
                }
            }

            // 3: most open dry spot.
            var crowded = new List<Candidate>(candidates.Count);
            for (int i = 0; i < candidates.Count; i++)
            {
                if (candidates[i].Margin < 0f)
                {
                    crowded.Add(candidates[i]);
                }
            }
            // Most open first; a metre of room is worth roughly thirty metres of extra walk.
            crowded.Sort((a, b) => (b.Margin - (b.R * 0.03f)).CompareTo(a.Margin - (a.R * 0.03f)));
            for (int pass = 0; pass < 2; pass++)
            {
                bool strict = pass == 0;
                int checks = 0;
                for (int i = 0; i < crowded.Count && checks < MaxCompromiseChecks; i++, checks++)
                {
                    Candidate c = crowded[i];
                    if (TryFitGround(probe, c.X, c.Z, water, scanAnchor, strict, out GroundFit fit))
                    {
                        return new Placement(fit.Position, fit.Rotation, Quality.Compromise, c.Margin, c.R, fit.NormalY, c.Nearest, reanchored, census, anchorAbove);
                    }
                }
            }

            // 4: anchor in open water and no dry ground in range - float on the surface.
            float anchorGround = probe.HeightAt(scanAnchor.x, scanAnchor.z);
            if (anchorGround < water && candidates.Count > 0)
            {
                Candidate best = MostOpen(candidates);
                Vector3 pos = new Vector3(best.X, water + 0.1f, best.Z);
                return new Placement(pos, FacingTowards(pos, scanAnchor), Quality.WaterSurface, best.Margin, best.R, 1f, best.Nearest, reanchored, census, anchorAbove);
            }

            // 5: whatever is most open, on the ground, slope be damned.
            if (candidates.Count > 0)
            {
                Candidate best = MostOpen(candidates);
                float h = Mathf.Max(probe.HeightAt(best.X, best.Z), water + 0.1f);
                Vector3 pos = new Vector3(best.X, h + GroundLift, best.Z);
                return new Placement(pos, FacingTowards(pos, scanAnchor), Quality.LastResort, best.Margin, best.R, probe.NormalYAt(best.X, best.Z), best.Nearest, reanchored, census, anchorAbove);
            }

            Vector3 edge = scanAnchor + (Vector3.forward * minRadius);
            edge.y = Mathf.Max(probe.HeightAt(edge.x, edge.z), water + 0.1f) + GroundLift;
            return new Placement(edge, FacingTowards(edge, scanAnchor), Quality.LastResort, 0f, minRadius, 1f, "", reanchored, census, anchorAbove);
        }

        /// <summary>The no-information fallback: a fixed step from the anchor at the anchor's own height. Only for when the world is not ready or the scan itself faulted.</summary>
        public static Placement FixedOffset(Vector3 anchor, float radius)
        {
            Vector3 pos = anchor + (Vector3.forward * Mathf.Max(0.5f, radius));
            return new Placement(pos, FacingTowards(pos, anchor), Quality.Unplaced, 0f, radius, 1f, "", false);
        }

        private static List<Candidate> EnumerateCandidates(Vector3 anchor, float minRadius, float maxRadius, float clearance, TargetedObstacleField field)
        {
            var list = new List<Candidate>(1024);
            int ring = 0;
            for (float r = minRadius; r <= maxRadius + 0.001f; r += RingStep, ring++)
            {
                int bearings = Mathf.Clamp(Mathf.RoundToInt(2f * Mathf.PI * r / ArcSpacing), MinBearings, MaxBearings);
                float phase = (ring & 1) == 1 ? 0.5f : 0f;
                for (int i = 0; i < bearings; i++)
                {
                    float angle = (i + phase) * (2f * Mathf.PI / bearings);
                    float x = anchor.x + (Mathf.Cos(angle) * r);
                    float z = anchor.z + (Mathf.Sin(angle) * r);
                    if ((x * x) + (z * z) > WaterEdgeRadius * WaterEdgeRadius)
                    {
                        continue;
                    }
                    float margin = field.Margin(x, z, clearance, out string nearest);
                    list.Add(new Candidate { X = x, Z = z, R = r, Margin = margin, Nearest = nearest });
                }
            }
            return list;
        }

        private static Candidate MostOpen(List<Candidate> candidates)
        {
            Candidate best = candidates[0];
            for (int i = 1; i < candidates.Count; i++)
            {
                // Ties (typically "nothing in range" everywhere) resolve toward the anchor.
                if (candidates[i].Margin > best.Margin + 0.01f || (Mathf.Abs(candidates[i].Margin - best.Margin) <= 0.01f && candidates[i].R < best.R))
                {
                    best = candidates[i];
                }
            }
            return best;
        }

        /// <summary>
        /// Is this dry, level, contiguous ground a portal frame can sit on? Checks the pivot, the four
        /// base corners of the frame (oriented to face the anchor) and a 2 m ring of surroundings so
        /// the frame neither floats off a downhill edge, sinks into an uphill one, nor wedges against
        /// a terrain cliff or hole.
        /// </summary>
        private static bool TryFitGround(TargetedGroundProbe probe, float x, float z, float water, Vector3 faceTarget, bool strict, out GroundFit fit)
        {
            fit = default;
            float minNormalY = strict ? 0.87f : 0.72f;       // 30 / 44 degrees
            float maxDeviation = strict ? 0.45f : 0.9f;      // frame corner vs pivot height
            float surroundStep = strict ? 1.0f : 1.8f;       // surroundings vs pivot height
            int minOpenSides = strict ? 6 : 4;               // of 8, at 2 m

            float h = probe.HeightAt(x, z);
            if (h < water + DryMargin)
            {
                return false;
            }
            Vector3 pos = new Vector3(x, h, z);
            if (TargetedGroundProbe.IsLava(pos))
            {
                return false;
            }
            float normalY = probe.NormalYAt(x, z);
            if (normalY < minNormalY)
            {
                return false;
            }

            Quaternion rot = FacingTowards(pos, faceTarget);
            Vector3 right = rot * Vector3.right;
            Vector3 fwd = rot * Vector3.forward;
            float deviation = 0f;
            for (int sx = -1; sx <= 1; sx += 2)
            {
                for (int sz = -1; sz <= 1; sz += 2)
                {
                    float cx = x + (right.x * FrameHalfWidth * sx) + (fwd.x * FrameHalfDepth * sz);
                    float cz = z + (right.z * FrameHalfWidth * sx) + (fwd.z * FrameHalfDepth * sz);
                    float hc = probe.HeightAt(cx, cz);
                    if (hc < water + (DryMargin * 0.5f))
                    {
                        return false;
                    }
                    deviation = Mathf.Max(deviation, Mathf.Abs(hc - h));
                }
            }
            if (deviation > maxDeviation)
            {
                return false;
            }

            int openSides = 0;
            for (int i = 0; i < 8; i++)
            {
                float a = i * (Mathf.PI / 4f);
                float hs = probe.HeightAt(x + (Mathf.Cos(a) * 2f), z + (Mathf.Sin(a) * 2f));
                if (Mathf.Abs(hs - h) <= surroundStep && hs >= water)
                {
                    openSides++;
                }
            }
            if (openSides < minOpenSides)
            {
                return false;
            }

            fit = new GroundFit
            {
                Position = new Vector3(x, h + GroundLift, z),
                Rotation = rot,
                NormalY = normalY,
                Deviation = deviation,
                OpenSides = openSides,
            };
            return true;
        }
    }
}
