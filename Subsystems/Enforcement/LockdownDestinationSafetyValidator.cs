using UnityEngine;

namespace TortalPortalLite.Subsystems.Enforcement
{
    /// <summary>
    /// #123 Destination Safety Validator. A purely procedural, admin-facing server-side check that
    /// refuses to wire a portal (or fabricate a phantom/relay lobby) at an unsafe coordinate -
    /// underwater, in lava, underground, inside a dungeon interior, or off the map edge. Built FIRST in
    /// this wave: #197/#208 (the Two-Hop Relay) explicitly depend on it for hop-2 target validation.
    ///
    /// Deliberately never touches ZoneSystem.GetGroundHeight/FindFloor/IsBlocked/GetGroundData or the
    /// instance IsLava(Vector3) - every one of those raycasts against a live Heightmap collider, and
    /// Game.FixedUpdate pins the dedicated server's own reference position at (1e6,0,1e6) every physics
    /// tick (SERVER decompile :100458-100466), so no collider ever exists near a real coordinate
    /// server-side; each of those calls silently returns its own no-hit fallback with no exception and no
    /// log line - the single easiest way to ship a validator that looks correct and is not. WorldGenerator's
    /// procedural noise functions and the static ZoneSystem.IsLavaPreHeightmap are the only terrain facts
    /// genuinely available headless (PortalCensus.BiomeAt and Topology's own TargetedWorldGenValidation.cs
    /// rely on the exact same reasoning).
    ///
    /// This is a Lockdown-domain option in its own right (world-integrity: "never let the mod itself wire
    /// a portal somewhere broken"), separate from - but written to the same standard as - Topology's own
    /// TargetedWorldGenValidation.cs (that file validates a Topology destination at ROUTE-DECLARATION
    /// time; this one is the fuller six-check admin/lockdown gate the catalog specifies, including the
    /// interior and underground checks Topology's file does not need for its own narrower purpose).
    /// </summary>
    public static class LockdownDestinationSafetyValidator
    {
        /// <summary>WorldGenerator.waterEdgeSqr's own radius (SERVER decompile citation) - beyond this GetHeight returns the fixed ocean-edge fallback, never real terrain.</summary>
        private const float WaterEdgeRadius = 10500f;

        /// <summary>Character.InInterior(Vector3)'s own threshold - dungeon interiors are instantiated at zoneCenter+5000y by Location.Awake.</summary>
        private const float InteriorYThreshold = 3000f;

        private const float SeaLevel = 30f;

        public readonly struct Result
        {
            public readonly bool Ok;
            public readonly string Reason;
            public readonly Vector3 Position;
            public readonly Quaternion FacingHint;

            public Result(bool ok, string reason, Vector3 position, Quaternion facingHint)
            {
                Ok = ok;
                Reason = reason;
                Position = position;
                FacingHint = facingHint;
            }

            public static Result Pass(Vector3 pos, Quaternion facing) => new Result(true, "", pos, facing);
            public static Result Fail(string reason) => new Result(false, reason, Vector3.zero, Quaternion.identity);

            public override string ToString() => Ok ? $"OK @ {Position:F1}" : $"REFUSED: {Reason}";
        }

        /// <summary>
        /// Validates a full 3D point (used for an already-known Y, e.g. a player's current position, a
        /// tombstone, a ship deck) against every check in the catalog's own cheapest-first order. Does
        /// NOT re-derive height from WorldGenerator - callers with a raw (x,z) and no known height should
        /// use <see cref="ValidateGroundColumn"/> instead, which resolves height itself.
        /// </summary>
        public static Result ValidatePoint(Vector3 pos)
        {
            if (WorldGenerator.instance == null)
            {
                return Result.Fail("WorldGenerator not ready");
            }

            // (1) OFF-MAP.
            float radialSqr = (pos.x * pos.x) + (pos.z * pos.z);
            if (radialSqr > WaterEdgeRadius * WaterEdgeRadius)
            {
                return Result.Fail($"beyond the playable world edge (r={Mathf.Sqrt(radialSqr):F0}m > {WaterEdgeRadius:F0}m)");
            }

            // (2) INTERIOR - vanilla's own Character.InInterior(Vector3) test.
            if (pos.y > InteriorYThreshold)
            {
                return Result.Fail($"inside a dungeon interior (y={pos.y:F0} > {InteriorYThreshold:F0})");
            }

            float terrainHeight = WorldGenerator.instance.GetHeight(pos.x, pos.z);

            // (3) UNDERWATER.
            if (terrainHeight < SeaLevel)
            {
                return Result.Fail($"underwater/below sea level (terrain h={terrainHeight:F1}, water={SeaLevel:F0})");
            }

            // (4) LAVA - the static pre-heightmap test only; NEVER ZoneSystem.instance.IsLava (dead raycast path, silently returns defaultTrue on a miss).
            if (ZoneSystem.IsLavaPreHeightmap(new Vector3(pos.x, terrainHeight, pos.z)))
            {
                return Result.Fail("lava (Ashlands biome mask)");
            }

            // (5) UNDERGROUND - blind to terraforming/location flattening, so a generous tolerance is mandatory.
            float tolerance = LockdownConfig.ValidatorUndergroundToleranceMeters?.Value ?? 2f;
            if (pos.y < terrainHeight - tolerance)
            {
                return Result.Fail($"underground (y={pos.y:F1} < terrain {terrainHeight:F1} - {tolerance:F1}m tolerance)");
            }

            // (6) SLOPE.
            Vector3 normal = WorldGenerator.instance.GetNormal(new Vector2(pos.x, pos.z), 1f);
            float maxSlope = LockdownConfig.ValidatorMaxSlopeNormalY?.Value ?? 0.6f;
            if (normal.y < maxSlope)
            {
                return Result.Fail($"too steep (normal.y={normal.y:F2} < {maxSlope:F2})");
            }

            Quaternion facing = Quaternion.identity;
            return Result.Pass(new Vector3(pos.x, terrainHeight + 0.2f, pos.z), facing);
        }

        /// <summary>Convenience overload for a raw (x,z) column with no known height - resolves height via WorldGenerator first, then runs the same six checks.</summary>
        public static Result ValidateGroundColumn(float x, float z)
        {
            if (WorldGenerator.instance == null)
            {
                return Result.Fail("WorldGenerator not ready");
            }
            float h = WorldGenerator.instance.GetHeight(x, z);
            return ValidatePoint(new Vector3(x, h, z));
        }

        /// <summary>
        /// Validates the EXIT point a traveller actually lands at, not just the destination portal's own
        /// anchor - catalog #123's own warning: TeleportWorld.Teleport computes
        /// `targetPos + (targetRot * forward) * m_exitDistance + Vector3.up`, so a validated anchor can
        /// still place a traveller 1m forward into a wall if the exit direction wasn't checked too.
        /// </summary>
        public static Result ValidateExitPoint(Vector3 anchorPos, Quaternion anchorRot, float exitDistance)
        {
            Vector3 exit = anchorPos + (anchorRot * Vector3.forward * exitDistance) + Vector3.up;
            return ValidatePoint(exit);
        }

        /// <summary>
        /// Samples N compass directions at <paramref name="distance"/> from <paramref name="center"/> and
        /// returns the flattest validated candidate, facing back toward the centre - the same "sample
        /// 8 offsets, keep the flattest" recipe Topology's own validator uses, duplicated here (rather than
        /// called cross-domain) so this file has zero compile-time dependency on Subsystems/Topology.
        /// </summary>
        public static Result BestCompassOffset(Vector3 center, float distance, int samples = 8)
        {
            Result best = default;
            float bestNormalY = -2f;
            for (int i = 0; i < samples; i++)
            {
                float angle = (360f / samples) * i;
                Vector3 dir = Quaternion.Euler(0f, angle, 0f) * Vector3.forward;
                float x = center.x + dir.x * distance;
                float z = center.z + dir.z * distance;
                Result candidate = ValidateGroundColumn(x, z);
                if (!candidate.Ok || WorldGenerator.instance == null)
                {
                    continue;
                }
                Vector3 normal = WorldGenerator.instance.GetNormal(new Vector2(x, z), 1f);
                if (normal.y > bestNormalY)
                {
                    bestNormalY = normal.y;
                    Vector3 facingDir = center - candidate.Position;
                    facingDir.y = 0f;
                    Quaternion facing = facingDir.sqrMagnitude > 0.0001f ? Quaternion.LookRotation(facingDir.normalized, Vector3.up) : Quaternion.identity;
                    best = new Result(true, "", candidate.Position, facing);
                }
            }
            return bestNormalY > -2f ? best : Result.Fail("no valid compass offset found");
        }
    }
}
