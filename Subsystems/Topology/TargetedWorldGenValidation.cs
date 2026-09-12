using UnityEngine;
using TortalPortalLite.Core.Data;

namespace TortalPortalLite.Subsystems.Topology
{
    /// <summary>
    /// Shared "is this coordinate a sane place to drop a phantom portal" validator - option #180 Admin
    /// Coordinates' own prerequisite, reused by every other option that picks a computed point
    /// (#182 Boss Altars, #183 Traders, #184 Dungeons, #185 Biomes, #186 Locations, #189 Tombstone,
    /// #193 Map Ping, #195 Base Centroid).
    ///
    /// Pure math only - deliberately never touches ZoneSystem.GetGroundHeight/FindFloor/IsBlocked or any
    /// Physics.* call: those raycast against a live terrain collider, and Game.FixedUpdate pins the
    /// dedicated server's own reference position at (1e6,0,1e6) every physics tick (SERVER decompile
    /// :100458-100466), so no collider ever exists near a real player coordinate server-side - every one
    /// of those calls would silently return its no-hit fallback. WorldGenerator's procedural noise
    /// functions are the only terrain facts available headless, exactly as Core/Data/PortalCensus.cs's
    /// own BiomeAt already relies on for the same reason.
    /// </summary>
    public static class TargetedWorldGenValidation
    {
        /// <summary>Half-width of the playable map along one axis, beyond which WorldGenerator.GetHeight returns the fixed -400 ocean-edge value (SERVER decompile :151978-151981, waterEdgeSqr :151122).</summary>
        private const float WaterEdgeRadius = 10500f;

        public readonly struct Result
        {
            public readonly bool Ok;
            public readonly string Reason;
            public readonly Vector3 Position;

            public Result(bool ok, string reason, Vector3 position)
            {
                Ok = ok;
                Reason = reason;
                Position = position;
            }

            public static Result Pass(Vector3 pos) => new Result(true, "", pos);
            public static Result Fail(string reason) => new Result(false, reason, Vector3.zero);
        }

        /// <summary>
        /// Validates an (x,z) world coordinate and resolves its exact ground height. allowUnderwater
        /// mirrors TargetedConfig.AllowUnderwaterCoordinates (Admin Coordinates' own knob) but is a
        /// parameter here so every caller can decide for itself (a swamp-crypt caller, e.g., wants
        /// h&gt;=~30 acceptance without touching the global admin-only switch).
        /// </summary>
        public static Result ValidateGroundPoint(float x, float z, bool allowUnderwater = false, float maxSlopeNormalY = 0.7f)
        {
            if (WorldGenerator.instance == null)
            {
                return Result.Fail("WorldGenerator not ready");
            }

            if ((x * x) + (z * z) > WaterEdgeRadius * WaterEdgeRadius)
            {
                return Result.Fail("beyond the playable world edge");
            }

            float h = WorldGenerator.instance.GetHeight(x, z);
            if (!allowUnderwater && h < 30f)
            {
                return Result.Fail($"underwater/below sea level (h={h:F1})");
            }

            Vector3 pos = new Vector3(x, h, z);
            if (ZoneSystem.IsLavaPreHeightmap(pos))
            {
                return Result.Fail("lava");
            }

            Vector3 normal = WorldGenerator.instance.GetNormal(new Vector2(x, z), 1f);
            if (normal.y < maxSlopeNormalY)
            {
                return Result.Fail($"too steep (normal.y={normal.y:F2})");
            }

            return Result.Pass(new Vector3(x, h + 0.2f, z));
        }

        /// <summary>Convenience overload that also rejects a piece-crowded point (imprecise without colliders - a ZDO-proximity approximation only, per #180's own caveat).</summary>
        public static Result ValidateGroundPointClear(float x, float z, float clearanceRadius, bool allowUnderwater = false)
        {
            Result baseResult = ValidateGroundPoint(x, z, allowUnderwater);
            if (!baseResult.Ok)
            {
                return baseResult;
            }
            if (IsCrowdedByPieces(baseResult.Position, clearanceRadius))
            {
                return Result.Fail("too close to existing player-placed pieces");
            }
            return baseResult;
        }

        /// <summary>Approximate-only: no colliders exist server-side, so this is "any ZDO nearby" rather than "any solid geometry nearby" - see #180/#195's own documented caveat.</summary>
        public static bool IsCrowdedByPieces(Vector3 pos, float radius)
        {
            var nearby = ZdoSpatialQuery.FindNear(pos, radius);
            return nearby.Count > 0;
        }

        /// <summary>
        /// Samples N compass directions at <paramref name="distance"/> from <paramref name="center"/> and
        /// returns the flattest/driest validated candidate (or Fail if none of the samples validate) -
        /// the "sample 4/8 offsets, keep the flattest" recipe #182/#183/#184/#186 all specify for
        /// standing a phantom outside a location's exterior radius.
        /// </summary>
        public static Result BestCompassOffset(Vector3 center, float distance, int samples = 8, bool allowUnderwater = false)
        {
            Result best = default;
            float bestNormalY = -2f;
            for (int i = 0; i < samples; i++)
            {
                float angle = (360f / samples) * i;
                Vector3 dir = Quaternion.Euler(0f, angle, 0f) * Vector3.forward;
                float x = center.x + dir.x * distance;
                float z = center.z + dir.z * distance;
                Result candidate = ValidateGroundPoint(x, z, allowUnderwater);
                if (!candidate.Ok)
                {
                    continue;
                }
                Vector3 normal = WorldGenerator.instance.GetNormal(new Vector2(x, z), 1f);
                if (normal.y > bestNormalY)
                {
                    bestNormalY = normal.y;
                    best = candidate;
                }
            }
            return bestNormalY > -2f ? best : Result.Fail("no valid compass offset found");
        }

        /// <summary>Rotation facing FROM the offset position back TOWARD the centre it was offset from - the "forward toward the altar/camp/door" convention every location-adjacent option wants.</summary>
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
    }
}
