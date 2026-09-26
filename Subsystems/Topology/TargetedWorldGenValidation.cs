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
    /// :100340), so no collider ever exists near a real player coordinate server-side - every one of
    /// those calls would silently return its no-hit fallback. Ground comes from TargetedGroundProbe
    /// (the client's own heightmap recipe re-run headless, terraforming included) and obstacles from
    /// TargetedObstacleField (collider footprints off the prefab assets, plus generated locations).
    ///
    /// The configured clearance is a PREFERENCE, scanned in descending tiers, not an all-or-nothing
    /// requirement - which is the lesson of the 1.0.4/1.0.7 live logs, where 40 of 40 gates came back
    /// "Compromise": at ClearanceMeters=15 (and at the 10 m default) there is simply nowhere in a Valheim
    /// forest with that much room from every tree, rock and building piece, so every single gate fell
    /// through to the "most open spot" fallback. That fallback sorts by room and treats a metre of room as
    /// worth thirty metres of walking, so gates landed 26-40 m from the bed they were supposed to be
    /// beside, and the warning it logs fired every time and therefore meant nothing.
    ///
    /// Search order, each stage only reached when the previous found nothing:
    ///  1. Tiered clearance: the configured clearance first, then progressively smaller requirements down
    ///     to MinRoomMeters - the hard floor of real edge-to-edge room the frame must have. At every tier
    ///     the scan is the same one: rings outward on dry, level, contiguous ground, first hit wins its
    ///     neighbourhood, best-scored spot in it settles it - so the result is the CLOSEST spot that meets
    ///     the best requirement this patch of map can actually offer. Each tier is tried with strict then
    ///     relaxed ground rules.
    ///  2. Widen and repeat: if not even the hard floor is met anywhere inside the search radius, the
    ///     radius is widened and stage 1 runs again. A gate 80 m away that the player can walk into beats
    ///     one 20 m away wedged inside a boulder.
    ///  3. Most open spot: nowhere in the widened radius clears the hard floor, so take the candidate with
    ///     the largest room that still stands on dry ground. Logged as a compromise - now a genuinely rare
    ///     signal worth acting on.
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
        /// <summary>After the first ring with a hit at the tier being tried, how many further rings are still scored before settling.</summary>
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
        /// <summary>How much wider each escalation looks when the hard floor is unmet everywhere, and how many times.</summary>
        private const float RadiusEscalationFactor = 2f;
        private const int MaxRadiusEscalations = 2;
        /// <summary>Absolute ceiling on an escalated search radius - past this the walk is worse than a poor spot.</summary>
        private const float MaxEscalatedRadius = 200f;
        /// <summary>Fallback hard floor of real edge-to-edge room, when a caller does not pass one.</summary>
        public const float DefaultMinRoom = 2f;

        public enum Quality
        {
            /// <summary>Configured clearance honoured against everything, level dry ground.</summary>
            Clear,
            /// <summary>The configured clearance was not available here, but a reduced requirement was honoured in full - still genuinely clear of everything, just with less room to spare.</summary>
            Reduced,
            /// <summary>Not even the hard floor of room was available anywhere in the widened radius; this is the most open dry spot found.</summary>
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
            /// <summary>Real edge-to-edge room the frame has here, from its own edge to the nearest obstacle's edge. 1000 when nothing was in reach.</summary>
            public readonly float Room;
            /// <summary>The clearance requirement this spot actually satisfied (the tier that won), or the hard floor it failed to reach.</summary>
            public readonly float Required;
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

            public Placement(Vector3 position, Quaternion rotation, Quality quality, float room, float required, float distance, float normalY, string nearest, bool reanchored, string obstacles = "", float anchorAboveGround = 0f)
            {
                Position = position;
                Rotation = rotation;
                Quality = quality;
                Room = room;
                Required = required;
                Distance = distance;
                NormalY = normalY;
                Nearest = nearest;
                ReanchoredFromInterior = reanchored;
                Obstacles = obstacles;
                AnchorAboveGround = anchorAboveGround;
            }

            /// <summary>A spot that honours a clearance requirement in full - the frame is genuinely clear of everything. Reduced counts: it met a smaller requirement, it did not overlap anything.</summary>
            public bool IsGood => Quality == Quality.Clear || Quality == Quality.Reduced;

            public string Describe()
            {
                string room = Room >= 999f
                    ? "nothing in range"
                    : $"{Room:0.0}m room" + (Nearest.Length > 0 ? $" vs {Nearest}" : "") + $" (needed {Required:0.0}m)";
                string extra = (ReanchoredFromInterior ? ", re-anchored from interior" : $", anchor {AnchorAboveGround:+0.00;-0.00}m above modelled ground") + (Obstacles.Length > 0 ? ", " + Obstacles : "");
                return $"{Quality} at {Position.x:F1},{Position.y:F1},{Position.z:F1} ({Distance:F1}m out, slope normal {NormalY:F2}, {room}{extra})";
            }
        }

        private struct Candidate
        {
            public float X;
            public float Z;
            public float R;
            /// <summary>Edge-to-edge room to the nearest ordinary obstacle / nearest sub-metre prop. float.MaxValue when none in reach.</summary>
            public float NormalEdge;
            public float SmallEdge;
            public string NearestNormal;
            public string NearestSmall;

            /// <summary>Room to spare beyond <paramref name="clearance"/> here (negative = short by that much) - O(1), the obstacle list was already walked once.</summary>
            public float MarginAt(float clearance, out string nearest) =>
                TargetedObstacleField.MarginFrom(NormalEdge, SmallEdge, NearestNormal, NearestSmall, clearance, out nearest);

            /// <summary>The real room here, against no requirement at all.</summary>
            public float Room(out string nearest) => MarginAt(0f, out nearest);
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
        /// and <paramref name="maxRadius"/> out, with as close to <paramref name="clearance"/> metres
        /// edge-to-edge from every solid thing as this patch of map allows but never less than
        /// <paramref name="minRoom"/>, on the real ground. Always returns a position; Quality says how good
        /// it is, and IsGood is true whenever the frame is genuinely clear of everything.
        /// </summary>
        public static Placement FindPortalPlacement(Vector3 anchor, float minRadius, float maxRadius, float clearance, ZDOID ignoreZdo, IReadOnlyList<Vector3>? avoid = null, float minRoom = DefaultMinRoom)
        {
            minRadius = Mathf.Max(0.5f, minRadius);
            maxRadius = Mathf.Max(minRadius + RingStep, maxRadius);
            clearance = Mathf.Max(0f, clearance);
            minRoom = Mathf.Clamp(minRoom, 0f, clearance);

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

            float water = TargetedGroundProbe.WaterLevel;
            float anchorAbove = reanchored ? 0f : anchor.y - probe.HeightAt(anchor.x, anchor.z);
            List<float> tiers = Tiers(clearance, minRoom);

            // Stages 1 + 2: the tiered scan, widening the radius only if not even the hard floor is met.
            var candidates = new List<Candidate>();
            string census = "";
            float radius = maxRadius;
            for (int attempt = 0; ; attempt++)
            {
                TargetedObstacleField field = TargetedObstacleField.Build(scanAnchor, radius, clearance, ignoreZdo, avoid);
                census = $"{field.ZdoObstacles} objects + {field.LocationObstacles} locations in reach" + (field.UnreadablePrefabs > 0 ? $", {field.UnreadablePrefabs} with assumed footprints" : "");
                candidates = EnumerateCandidates(scanAnchor, minRadius, field.EffectiveSearchRadius, field);

                for (int t = 0; t < tiers.Count; t++)
                {
                    if (TryTier(probe, candidates, scanAnchor, water, tiers[t], out GroundFit fit, out Candidate won))
                    {
                        float room = won.Room(out string nearest);
                        Quality quality = t == 0 ? Quality.Clear : Quality.Reduced;
                        return new Placement(fit.Position, fit.Rotation, quality, room, tiers[t], won.R, fit.NormalY, nearest, reanchored, census, anchorAbove);
                    }
                }

                if (attempt >= MaxRadiusEscalations)
                {
                    break;
                }
                float widened = Mathf.Min(radius * RadiusEscalationFactor, MaxEscalatedRadius);
                if (widened <= radius + 0.001f)
                {
                    break;
                }
                radius = widened;
            }

            // 3: most open dry spot. Nothing in the widened radius clears even the hard floor.
            var crowded = new List<Candidate>(candidates.Count);
            for (int i = 0; i < candidates.Count; i++)
            {
                crowded.Add(candidates[i]);
            }
            // Most open first; a metre of room is worth roughly thirty metres of extra walk.
            crowded.Sort((a, b) => (b.Room(out _) - (b.R * 0.03f)).CompareTo(a.Room(out _) - (a.R * 0.03f)));
            for (int pass = 0; pass < 2; pass++)
            {
                bool strict = pass == 0;
                int checks = 0;
                for (int i = 0; i < crowded.Count && checks < MaxCompromiseChecks; i++, checks++)
                {
                    Candidate c = crowded[i];
                    if (TryFitGround(probe, c.X, c.Z, water, scanAnchor, strict, out GroundFit fit))
                    {
                        float room = c.Room(out string nearest);
                        return new Placement(fit.Position, fit.Rotation, Quality.Compromise, room, minRoom, c.R, fit.NormalY, nearest, reanchored, census, anchorAbove);
                    }
                }
            }

            // 4: anchor in open water and no dry ground in range - float on the surface.
            float anchorGround = probe.HeightAt(scanAnchor.x, scanAnchor.z);
            if (anchorGround < water && candidates.Count > 0)
            {
                Candidate best = MostOpen(candidates);
                Vector3 pos = new Vector3(best.X, water + 0.1f, best.Z);
                return new Placement(pos, FacingTowards(pos, scanAnchor), Quality.WaterSurface, best.Room(out string nearest), minRoom, best.R, 1f, nearest, reanchored, census, anchorAbove);
            }

            // 5: whatever is most open, on the ground, slope be damned.
            if (candidates.Count > 0)
            {
                Candidate best = MostOpen(candidates);
                float h = Mathf.Max(probe.HeightAt(best.X, best.Z), water + 0.1f);
                Vector3 pos = new Vector3(best.X, h + GroundLift, best.Z);
                return new Placement(pos, FacingTowards(pos, scanAnchor), Quality.LastResort, best.Room(out string nearest), minRoom, best.R, probe.NormalYAt(best.X, best.Z), nearest, reanchored, census, anchorAbove);
            }

            Vector3 edge = scanAnchor + (Vector3.forward * minRadius);
            edge.y = Mathf.Max(probe.HeightAt(edge.x, edge.z), water + 0.1f) + GroundLift;
            return new Placement(edge, FacingTowards(edge, scanAnchor), Quality.LastResort, 0f, minRoom, minRadius, 1f, "", reanchored, census, anchorAbove);
        }

        /// <summary>The no-information fallback: a fixed step from the anchor at the anchor's own height. Only for when the world is not ready or the scan itself faulted.</summary>
        public static Placement FixedOffset(Vector3 anchor, float radius)
        {
            Vector3 pos = anchor + (Vector3.forward * Mathf.Max(0.5f, radius));
            return new Placement(pos, FacingTowards(pos, anchor), Quality.Unplaced, 0f, 0f, radius, 1f, "", false);
        }

        /// <summary>
        /// The clearance requirements to try, largest first, always ending at the hard floor. Geometric
        /// rather than a fixed step so the shape holds whether an admin asks for 3 m or 30.
        /// </summary>
        private static List<float> Tiers(float clearance, float minRoom)
        {
            var tiers = new List<float>(5);
            void Add(float value)
            {
                value = Mathf.Max(value, minRoom);
                if (tiers.Count == 0 || value < tiers[tiers.Count - 1] - 0.05f)
                {
                    tiers.Add(value);
                }
            }
            Add(clearance);
            Add(clearance * 0.7f);
            Add(clearance * 0.5f);
            Add(clearance * 0.35f);
            Add(minRoom);
            return tiers;
        }

        /// <summary>
        /// One tier of the clearance ladder, strict ground rules then relaxed: the closest ring holding a
        /// candidate that honours <paramref name="required"/> wins its neighbourhood, and the best-scored
        /// spot within ExtraRingsAfterHit of it settles it.
        /// </summary>
        private static bool TryTier(TargetedGroundProbe probe, List<Candidate> candidates, Vector3 scanAnchor, float water, float required, out GroundFit bestFit, out Candidate bestCandidate)
        {
            bestFit = default;
            bestCandidate = default;
            for (int pass = 0; pass < 2; pass++)
            {
                bool strict = pass == 0;
                bool found = false;
                float bestScore = float.MinValue;
                float firstHitRing = -1f;
                for (int i = 0; i < candidates.Count; i++)
                {
                    Candidate c = candidates[i];
                    float margin = c.MarginAt(required, out _);
                    if (margin < 0f)
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
                    float score = (fit.NormalY * 20f) + (Mathf.Min(margin, 5f) * 0.6f) + (fit.OpenSides * 0.5f) - (c.R * 0.35f) - (fit.Deviation * 4f);
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
                    return true;
                }
            }
            return false;
        }

        private static List<Candidate> EnumerateCandidates(Vector3 anchor, float minRadius, float maxRadius, TargetedObstacleField field)
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
                    field.Probe(x, z, out float normalEdge, out float smallEdge, out string normalName, out string smallName);
                    list.Add(new Candidate
                    {
                        X = x,
                        Z = z,
                        R = r,
                        NormalEdge = normalEdge,
                        SmallEdge = smallEdge,
                        NearestNormal = normalName,
                        NearestSmall = smallName,
                    });
                }
            }
            return list;
        }

        private static Candidate MostOpen(List<Candidate> candidates)
        {
            Candidate best = candidates[0];
            float bestRoom = best.Room(out _);
            for (int i = 1; i < candidates.Count; i++)
            {
                float room = candidates[i].Room(out _);
                // Ties (typically "nothing in range" everywhere) resolve toward the anchor.
                if (room > bestRoom + 0.01f || (Mathf.Abs(room - bestRoom) <= 0.01f && candidates[i].R < best.R))
                {
                    best = candidates[i];
                    bestRoom = room;
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
